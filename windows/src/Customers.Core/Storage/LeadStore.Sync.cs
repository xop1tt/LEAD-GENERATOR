using System.Text.Json.Nodes;

namespace Customers.Core;

/// <summary>
/// Журнал изменений для облачной синхронизации и применение изменений с сервера.
/// Синхронизируются: лиды (данные и отметки пользователя: избранное, архив, удаление, просмотр, заметка),
/// исключения и настройки из <see cref="SyncedKeys"/>. Остаётся на компьютере: память источников поиска,
/// журнал поиска, история действий, пути к файлам, ключи, оформление.
/// </summary>
public sealed partial class LeadStore : ISyncLocalStore
{
    /// <summary>Ключи kv, которые уходят в облако. Значения — JSON-текст.</summary>
    public static readonly IReadOnlySet<string> SyncedKeys = new HashSet<string> { "search.settings", "draft.signature", "search.auto" };

    private static readonly string[] LeadFields = ["data", "favorite", "archived", "deleted", "seen", "note"];

    private const string SyncSchema = """
        CREATE TABLE IF NOT EXISTS sync_dirty (
            entity TEXT NOT NULL,
            key TEXT NOT NULL,
            field TEXT NOT NULL,
            changed_at TEXT NOT NULL,
            PRIMARY KEY (entity, key, field)
        );
        """;

    /// <summary>Время перенесённых данных: раньше любых изменений в облаке, поэтому перенос не затирает правки с других устройств.</summary>
    public static readonly DateTimeOffset ImportStamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Время с миллисекундами: два изменения в одну секунду различаются.</summary>
    internal static string Stamp(DateTimeOffset? d = null) => ISODate.FormatPrecise(d ?? DateTimeOffset.UtcNow);

    internal void MarkDirty(string entity, string key, IEnumerable<string> fields, string? at = null)
    {
        if (!SyncTracking)
        {
            return;
        }

        var ts = at ?? Stamp();
        foreach (var f in fields)
        {
            _db.Run("""
                INSERT INTO sync_dirty(entity, key, field, changed_at) VALUES(?,?,?,?)
                ON CONFLICT(entity, key, field) DO UPDATE SET changed_at=excluded.changed_at
                """, entity, key, f, ts);
        }
    }

    /// <summary>Отметить все данные как изменённые — для первой выгрузки (перенос локальной базы в облако).</summary>
    public void MarkEverythingDirty(DateTimeOffset at)
    {
        var ts = Stamp(at);
        _db.Transaction(() =>
        {
            foreach (var row in _db.Query("SELECT lead_id FROM leads"))
            {
                if (row.Str("lead_id") is { } id)
                {
                    MarkDirty("lead", id, LeadFields, ts);
                }
            }

            foreach (var e in Exclusions())
            {
                MarkDirty("exclusion", $"{e.Kind.Raw()}|{e.Value}", ["deleted"], ts);
            }

            foreach (var k in SyncedKeys.Where(k => Value(k) is not null))
            {
                MarkDirty("setting", k, ["value"], ts);
            }
        });
    }

    public int PendingSyncCount => (int)Convert.ToInt64(_db.Scalar("SELECT COUNT(DISTINCT entity || key) FROM sync_dirty") ?? 0L);

    // MARK: ISyncLocalStore

    public PendingChanges PendingChanges(int limit)
    {
        var output = new PendingChanges();
        var rows = _db.Query("""
            SELECT entity, key, field, changed_at FROM sync_dirty
            WHERE (entity, key) IN (SELECT entity, key FROM sync_dirty GROUP BY entity, key ORDER BY MIN(changed_at) LIMIT ?)
            """, limit);
        var grouped = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
        foreach (var r in rows)
        {
            if (r.Str("entity") is not { } e || r.Str("key") is not { } k || r.Str("field") is not { } f || r.Str("changed_at") is not { } at)
            {
                continue;
            }

            if (!grouped.TryGetValue(e, out var byKey))
            {
                grouped[e] = byKey = [];
            }

            if (!byKey.TryGetValue(k, out var fields))
            {
                byKey[k] = fields = [];
            }

            fields[f] = at;
        }

        if (grouped.TryGetValue("lead", out var leads))
        {
            foreach (var (id, ts) in leads)
            {
                var r = _db.First("""
                    SELECT lead_id, fingerprint, kind, source, title, score, relevance, discovered_at, data_json,
                           favorite, archived, deleted, seen, note FROM leads WHERE lead_id=?
                    """, id);
                JsonNode? data = null;
                try
                {
                    data = r is null ? null : JsonNode.Parse(r.Str("data_json") ?? "");
                }
                catch (System.Text.Json.JsonException)
                {
                }

                if (r is null || data is null)
                {
                    // Лида больше нет локально — изменение отправлять некуда.
                    _db.Run("DELETE FROM sync_dirty WHERE entity='lead' AND key=?", id);
                    continue;
                }

                output.Leads.Add(new LeadChange {
                    LeadId = id,
                    Fingerprint = r.Str("fingerprint") ?? "",
                    Kind = r.Str("kind") ?? "order",
                    Source = r.Str("source") ?? "",
                    Title = r.Str("title") ?? "",
                    Score = r.Dbl("score") ?? 0,
                    Relevance = r.Str("relevance") ?? "rejected",
                    DiscoveredAt = ISODate.Format(ISODate.Parse(r.Str("discovered_at"))) ?? ISODate.Now(),
                    Data = data,
                    Favorite = r.Bool("favorite"),
                    Archived = r.Bool("archived"),
                    Deleted = r.Bool("deleted"),
                    Seen = r.Bool("seen"),
                    Note = r.Str("note") ?? "",
                    Ts = ts,
                });
            }
        }

        if (grouped.TryGetValue("exclusion", out var exclusions))
        {
            foreach (var (key, ts) in exclusions)
            {
                var parts = key.Split('|', 2);
                if (parts.Length != 2 || !ts.TryGetValue("deleted", out var at))
                {
                    continue;
                }

                var exists = _db.Scalar("SELECT 1 FROM exclusions WHERE kind=? AND value=?", parts[0], parts[1]) is not null;
                output.Exclusions.Add(new ExclusionChange(parts[0], parts[1], !exists, at));
            }
        }

        if (grouped.TryGetValue("setting", out var settings))
        {
            foreach (var (key, ts) in settings)
            {
                if (!ts.TryGetValue("value", out var at))
                {
                    continue;
                }

                JsonNode? v = null;
                try
                {
                    v = Value(key) is { } raw ? JsonNode.Parse(raw) : null;
                }
                catch (System.Text.Json.JsonException)
                {
                }

                output.Settings.Add(new SettingChange(key, v, at));
            }
        }

        output.Leads.Sort((a, b) => string.CompareOrdinal(a.LeadId, b.LeadId));
        return output;
    }

    public void MarkPushed(PendingChanges changes) => _db.Transaction(() =>
    {
        void Clear(string entity, string key, string field, string at) => _db.Run(
            "DELETE FROM sync_dirty WHERE entity=? AND key=? AND field=? AND changed_at=?", entity, key, field, at);

        foreach (var l in changes.Leads)
        {
            foreach (var (f, at) in l.Ts)
            {
                Clear("lead", l.LeadId, f, at);
            }
        }

        foreach (var e in changes.Exclusions)
        {
            Clear("exclusion", $"{e.Kind}|{e.Value}", "deleted", e.ChangedAt);
        }

        foreach (var s in changes.Settings)
        {
            Clear("setting", s.Key, "value", s.ChangedAt);
        }
    });

    private Dictionary<string, DateTimeOffset> DirtyAt(string entity, string key)
    {
        var output = new Dictionary<string, DateTimeOffset>();
        foreach (var r in _db.Query("SELECT field, changed_at FROM sync_dirty WHERE entity=? AND key=?", entity, key))
        {
            if (r.Str("field") is { } f && ISODate.Parse(r.Str("changed_at")) is { } d)
            {
                output[f] = d;
            }
        }

        return output;
    }

    /// <summary>
    /// Применяет изменения с сервера. Поле, изменённое здесь позже серверного, не трогается: оно уйдёт на сервер
    /// при следующей выгрузке. Применённые изменения в журнал не пишутся, поэтому эхо-цикла не возникает.
    /// </summary>
    public void ApplyRemote(IReadOnlyList<RemoteLead> leads, IReadOnlyList<RemoteExclusion> exclusions, IReadOnlyList<RemoteSetting> settings)
    {
        _db.Transaction(() =>
        {
            foreach (var r in leads)
            {
                var local = DirtyAt("lead", r.LeadId);
                bool RemoteWins(string f)
                {
                    if (!local.TryGetValue(f, out var mine))
                    {
                        return true;
                    }

                    var theirs = ISODate.Parse(r.FieldTs.GetValueOrDefault(f)) ?? DateTimeOffset.MinValue;
                    return theirs > mine;
                }

                // Данные лида храним в том же стабильном виде, что и поиск.
                var lead = LeadData.FromJson(r.Data);
                var json = lead?.ToStableJson() ?? JsonStable.Serialize(r.Data);
                var discovered = ISODate.Format(ISODate.Parse(r.DiscoveredAt)) ?? ISODate.Now();
                var exists = _db.Scalar("SELECT 1 FROM leads WHERE lead_id=?", r.LeadId) is not null;
                if (!exists)
                {
                    _db.Run("""
                        INSERT INTO leads(lead_id, fingerprint, kind, source, title, score, relevance, discovered_at, updated_at,
                                          data_json, imported_at, favorite, archived, deleted, seen, note)
                        VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                        """, r.LeadId, r.Fingerprint, r.Kind, r.Source, r.Title, r.Score, r.Relevance, discovered, ISODate.Now(), json,
                        ISODate.Now(), r.Favorite, r.Archived, r.Deleted, r.Seen, r.Note);
                    continue;
                }

                if (RemoteWins("data"))
                {
                    _db.Run("""
                        UPDATE leads SET fingerprint=?, kind=?, source=?, title=?, score=?, relevance=?, data_json=?,
                            discovered_at=MIN(discovered_at, ?) WHERE lead_id=?
                        """, r.Fingerprint, r.Kind, r.Source, r.Title, r.Score, r.Relevance, json, discovered, r.LeadId);
                }

                foreach (var (f, v) in new (string, object)[]
                         {
                             ("favorite", r.Favorite), ("archived", r.Archived), ("deleted", r.Deleted), ("seen", r.Seen), ("note", r.Note),
                         })
                {
                    if (RemoteWins(f))
                    {
                        _db.Run($"UPDATE leads SET {f}=? WHERE lead_id=?", v, r.LeadId);
                    }
                }
            }

            foreach (var e in exclusions)
            {
                var key = $"{e.Kind}|{e.Value}";
                if (DirtyAt("exclusion", key).TryGetValue("deleted", out var mine) && mine >= (ISODate.Parse(e.ChangedAt) ?? DateTimeOffset.MinValue))
                {
                    continue;
                }

                if (e.Deleted)
                {
                    _db.Run("DELETE FROM exclusions WHERE kind=? AND value=?", e.Kind, e.Value);
                }
                else if (EnumText.ParseExclusion(e.Kind) is not null)
                {
                    _db.Run("INSERT OR IGNORE INTO exclusions(kind, value, created_at) VALUES(?,?,?)", e.Kind, e.Value, ISODate.Now());
                }
            }

            foreach (var s in settings.Where(s => SyncedKeys.Contains(s.Key)))
            {
                if (DirtyAt("setting", s.Key).TryGetValue("value", out var mine) && mine >= (ISODate.Parse(s.ChangedAt) ?? DateTimeOffset.MinValue))
                {
                    continue;
                }

                SetValue(s.Key, JsonStable.Serialize(s.Value));
            }
        });
        if (exclusions.Count > 0 || leads.Count > 0)
        {
            ApplyExclusions();
        }
    }

    public long SyncCursor(string table) => long.TryParse(Value($"sync.cursor.{table}"), out var v) ? v : 0;

    public void SetSyncCursor(string table, long value) => SetValue($"sync.cursor.{table}", value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Начать синхронизацию заново (например, после восстановления из резервной копии).</summary>
    public void ResetSyncCursors() => _db.Run("DELETE FROM kv WHERE key LIKE 'sync.cursor.%'");

    /// <summary>
    /// Копирует в эту базу лиды, исключения и синхронизируемые настройки из другой базы и отмечает их для выгрузки.
    /// Лиды, которые уже есть здесь (тот же id или тот же отпечаток), не дублируются. Возвращает число добавленных лидов.
    /// </summary>
    public int ImportFrom(LeadStore src)
    {
        var ts = Stamp(ImportStamp);
        var added = 0;
        var leads = src._db.Query("""
            SELECT lead_id, fingerprint, kind, source, title, score, relevance, discovered_at, updated_at, data_json,
                   imported_at, favorite, archived, deleted, excluded, seen, note FROM leads
            """);
        var exclusions = src.Exclusions();
        _db.Transaction(() =>
        {
            string[] cols =
            [
                "lead_id", "fingerprint", "kind", "source", "title", "score", "relevance", "discovered_at", "updated_at", "data_json",
                "imported_at", "favorite", "archived", "deleted", "excluded", "seen", "note",
            ];
            foreach (var r in leads)
            {
                if (r.Str("lead_id") is not { } id)
                {
                    continue;
                }

                var fp = r.Str("fingerprint") ?? "";
                if (_db.Query("SELECT 1 FROM leads WHERE lead_id=? OR (?<>'' AND fingerprint=?)", id, fp, fp).Count > 0)
                {
                    continue;
                }

                _db.Run($"INSERT INTO leads({string.Join(",", cols)}) VALUES({string.Join(",", cols.Select(_ => "?"))})",
                    cols.Select(c => r[c]).ToArray());
                MarkDirty("lead", id, LeadFields, ts);
                added++;
            }

            foreach (var e in exclusions)
            {
                _db.Run("INSERT OR IGNORE INTO exclusions(kind, value, created_at) VALUES(?,?,?)", e.Kind.Raw(), e.Value, ISODate.Now());
                MarkDirty("exclusion", $"{e.Kind.Raw()}|{e.Value}", ["deleted"], ts);
            }

            foreach (var k in SyncedKeys.Where(k => Value(k) is null))
            {
                if (src.Value(k) is { } v)
                {
                    _db.Run("INSERT INTO kv(key, value) VALUES(?,?)", k, v);
                    MarkDirty("setting", k, ["value"], ts);
                }
            }
        });
        ApplyExclusions();
        return added;
    }

    /// <summary>Записать синхронизируемую настройку (JSON-текст) и отметить её для выгрузки.</summary>
    public void SetSyncedValue(string key, string json) => _db.Transaction(() =>
    {
        SetValue(key, json);
        if (SyncedKeys.Contains(key))
        {
            MarkDirty("setting", key, ["value"]);
        }
    });
}
