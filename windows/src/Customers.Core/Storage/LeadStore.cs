using System.Text.Json.Nodes;

namespace Customers.Core;

/// <summary>
/// Локальная база пользователя (SQLite/SQLCipher). Результаты поиска и действия пользователя хранятся рядом,
/// но поиск никогда не затирает избранное, архив, заметки и удаления. Схема таблиц совпадает с macOS-версией.
/// </summary>
public sealed partial class LeadStore : ISearchRepository, ISearchMemory, IDisposable
{
    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS leads (
            lead_id TEXT PRIMARY KEY,
            fingerprint TEXT NOT NULL DEFAULT '',
            kind TEXT NOT NULL,
            source TEXT NOT NULL,
            title TEXT NOT NULL,
            score REAL NOT NULL DEFAULT 0,
            relevance TEXT NOT NULL DEFAULT 'rejected',
            discovered_at TEXT NOT NULL,
            updated_at TEXT,
            data_json TEXT NOT NULL,
            imported_at TEXT NOT NULL,
            favorite INTEGER NOT NULL DEFAULT 0,
            archived INTEGER NOT NULL DEFAULT 0,
            deleted INTEGER NOT NULL DEFAULT 0,
            excluded INTEGER NOT NULL DEFAULT 0,
            seen INTEGER NOT NULL DEFAULT 0,
            note TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_leads_discovered ON leads(discovered_at);
        CREATE TABLE IF NOT EXISTS source_state (
            source TEXT PRIMARY KEY,
            last_success_at TEXT,
            last_attempt_at TEXT,
            consecutive_failures INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'ok',
            last_error TEXT
        );
        CREATE TABLE IF NOT EXISTS pipeline_errors (
            id INTEGER PRIMARY KEY,
            at TEXT,
            module TEXT NOT NULL,
            message TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS exclusions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL,
            value TEXT NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(kind, value)
        );
        CREATE TABLE IF NOT EXISTS lead_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            lead_id TEXT NOT NULL,
            at TEXT NOT NULL,
            action TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_lead ON lead_events(lead_id);
        CREATE TABLE IF NOT EXISTS sync_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            at TEXT NOT NULL,
            ok INTEGER NOT NULL,
            added INTEGER NOT NULL DEFAULT 0,
            updated INTEGER NOT NULL DEFAULT 0,
            message TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """;

    private const string MigrationSchema = """
        CREATE TABLE IF NOT EXISTS search_queries (
            qhash TEXT PRIMARY KEY,
            source TEXT NOT NULL,
            label TEXT NOT NULL,
            last_run_at TEXT NOT NULL,
            results INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS seen_objects (
            source TEXT NOT NULL,
            object_id TEXT NOT NULL,
            checked_at TEXT NOT NULL,
            PRIMARY KEY (source, object_id)
        );
        CREATE INDEX IF NOT EXISTS ix_leads_fingerprint ON leads(fingerprint);
        """;

    private Db _db;

    /// <summary>Ключ шифрования базы. null — база без шифрования (только тесты).</summary>
    public string? Key { get; }

    public LeadStore(string path, string? key = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        Key = key;
        _db = Db.Open(path, key);
        Prepare();
    }

    public string Path => _db.Path;
    public bool IsEncrypted => Key is not null;

    /// <summary>Записывать изменения в журнал синхронизации (включено для облачных учётных записей).</summary>
    public bool SyncTracking { get; private set; }

    internal Db Database => _db;

    private void Prepare()
    {
        _db.Exec("PRAGMA journal_mode=WAL");
        _db.Exec(Schema);
        _db.Run("INSERT OR IGNORE INTO kv(key, value) VALUES('schema_version', '1')");
        Migrate();
        SyncTracking = Value("sync.enabled") == "1";
    }

    private void Migrate()
    {
        var cols = _db.Query("PRAGMA table_info(source_state)").Select(r => r.Str("name")).ToList();
        if (!cols.Contains("cursor_json"))
        {
            _db.Exec("ALTER TABLE source_state ADD COLUMN cursor_json TEXT");
        }

        _db.Exec(MigrationSchema);
        _db.Exec(SyncSchema);
        _db.Run("INSERT INTO kv(key, value) VALUES('schema_version', '2') ON CONFLICT(key) DO UPDATE SET value='2'");
    }

    public void Dispose() => _db.Close();

    /// <summary>Включить журнал изменений для облака. Флаг хранится в самой базе.</summary>
    public void EnableSyncTracking()
    {
        SetValue("sync.enabled", "1");
        SyncTracking = true;
    }

    // MARK: - Результаты поиска

    /// <summary>Сохраняет найденный лид. Избранное, архив, заметки и удаления пользователя не трогаются.</summary>
    public SaveOutcome Save(LeadData lead, string id, string fingerprint)
    {
        var data = lead;
        var now = ISODate.Now();
        var row = _db.First("SELECT discovered_at, data_json FROM leads WHERE lead_id=?", id);
        if (row is not null)
        {
            // Дата обнаружения — первая, а не последняя.
            data.DiscoveredAt = ISODate.Parse(row.Str("discovered_at")) ?? data.DiscoveredAt;
            var json = data.ToStableJson();
            if (json == row.Str("data_json"))
            {
                return SaveOutcome.Unchanged;
            }

            _db.Transaction(() =>
            {
                _db.Run("""
                    UPDATE leads SET fingerprint=?, kind=?, source=?, title=?, score=?, relevance=?, updated_at=?, data_json=?
                    WHERE lead_id=?
                    """, fingerprint, data.Kind.Raw(), data.Source, data.Title, data.Score, data.Relevance.Raw(), now, json, id);
                MarkDirty("lead", id, ["data"]);
            });
            return SaveOutcome.Updated;
        }

        if (fingerprint.Length > 0 && _db.Scalar("SELECT 1 FROM leads WHERE fingerprint=? LIMIT 1", fingerprint) is not null)
        {
            return SaveOutcome.Duplicate; // та же заявка или организация уже есть из другого места
        }

        var newJson = data.ToStableJson();
        _db.Transaction(() =>
        {
            _db.Run("""
                INSERT INTO leads(lead_id, fingerprint, kind, source, title, score, relevance, discovered_at, updated_at, data_json, imported_at)
                VALUES(?,?,?,?,?,?,?,?,?,?,?)
                """, id, fingerprint, data.Kind.Raw(), data.Source, data.Title, data.Score, data.Relevance.Raw(),
                ISODate.Format(data.DiscoveredAt) ?? now, now, newJson, now);
            LogEvent(id, $"Найден поиском ({SourceCatalog.Title(data.Source)})");
            MarkDirty("lead", id, ["data"]);
        });
        return SaveOutcome.Added;
    }

    /// <summary>После поиска: применить исключения к новым лидам и запомнить время.</summary>
    public void SearchFinished()
    {
        ApplyExclusions();
        SetValue("last_search_at", ISODate.Now());
    }

    public string? SourceCursor(string source) => _db.Scalar("SELECT cursor_json FROM source_state WHERE source=?", source) as string;

    public DateTimeOffset? LastSuccess(string source) =>
        ISODate.Parse(_db.Scalar("SELECT last_success_at FROM source_state WHERE source=?", source) as string);

    public void MarkAttempt(string source) => _db.Run("""
        INSERT INTO source_state(source, last_attempt_at) VALUES(?,?)
        ON CONFLICT(source) DO UPDATE SET last_attempt_at=excluded.last_attempt_at
        """, source, ISODate.Now());

    public void FinishSource(string source, bool ok, bool partial, string? error, string? cursor)
    {
        var now = ISODate.Now();
        if (ok)
        {
            _db.Run("""
                UPDATE source_state SET last_success_at=?, consecutive_failures=0, status=?, last_error=?,
                    cursor_json=COALESCE(?, cursor_json)
                WHERE source=?
                """, now, partial ? "partial" : "ok", error, cursor, source);
        }
        else
        {
            _db.Run("""
                UPDATE source_state SET consecutive_failures=consecutive_failures+1, status='error', last_error=?,
                    cursor_json=COALESCE(?, cursor_json)
                WHERE source=?
                """, error, cursor, source);
        }
    }

    public void LogError(string module, string message)
    {
        _db.Run("INSERT INTO pipeline_errors(at, module, message) VALUES(?,?,?)", ISODate.Now(), module, message);
        _db.Run("DELETE FROM pipeline_errors WHERE id NOT IN (SELECT id FROM pipeline_errors ORDER BY id DESC LIMIT 500)");
    }

    public void LogRun(bool ok, int added, int updated, string message) => AddSyncLog(ok, added, updated, message);

    public DateTimeOffset? QueryLastRun(string hash) =>
        ISODate.Parse(_db.Scalar("SELECT last_run_at FROM search_queries WHERE qhash=?", hash) as string);

    public void RecordQuery(string hash, string source, string label, int results) => _db.Run("""
        INSERT INTO search_queries(qhash, source, label, last_run_at, results) VALUES(?,?,?,?,?)
        ON CONFLICT(qhash) DO UPDATE SET last_run_at=excluded.last_run_at, results=excluded.results
        """, hash, source, label, ISODate.Now(), results);

    public void ForgetQuery(string hash) => _db.Run("DELETE FROM search_queries WHERE qhash=?", hash);

    public DateTimeOffset? ObjectCheckedAt(string source, string objectId) =>
        ISODate.Parse(_db.Scalar("SELECT checked_at FROM seen_objects WHERE source=? AND object_id=?", source, objectId) as string);

    public void MarkObjectChecked(string source, string objectId) => _db.Run("""
        INSERT INTO seen_objects(source, object_id, checked_at) VALUES(?,?,?)
        ON CONFLICT(source, object_id) DO UPDATE SET checked_at=excluded.checked_at
        """, source, objectId, ISODate.Now());

    /// <summary>Сбросить память источника (курсоры, историю запросов): следующий поиск начнёт заново.</summary>
    public void ResetSourceMemory(string source)
    {
        _db.Run("UPDATE source_state SET cursor_json=NULL WHERE source=?", source);
        _db.Run("DELETE FROM search_queries WHERE source=?", source);
        _db.Run("DELETE FROM seen_objects WHERE source=?", source);
    }

    // MARK: - Чтение

    public List<LeadItem> AllLeads(bool includeDeleted = false)
    {
        var rows = _db.Query($"""
            SELECT lead_id, data_json, discovered_at, favorite, archived, excluded, seen, note
            FROM leads {(includeDeleted ? "" : "WHERE deleted=0")} ORDER BY discovered_at DESC
            """);
        var items = new List<LeadItem>(rows.Count);
        foreach (var r in rows)
        {
            var id = r.Str("lead_id");
            var data = LeadData.FromJson(r.Str("data_json"));
            if (id is null || data is null)
            {
                continue;
            }

            items.Add(new LeadItem {
                Id = id,
                Data = data,
                DiscoveredAt = ISODate.Parse(r.Str("discovered_at")) ?? data.DiscoveredAt ?? DateTimeOffset.UtcNow,
                Favorite = r.Bool("favorite"),
                Archived = r.Bool("archived"),
                Excluded = r.Bool("excluded"),
                Seen = r.Bool("seen"),
                Note = r.Str("note") ?? "",
            });
        }

        return items;
    }

    public List<SourceState> SourceStates() => _db.Query("SELECT * FROM source_state ORDER BY source").Select(r =>
        new SourceState(r.Str("source") ?? "", ISODate.Parse(r.Str("last_success_at")), ISODate.Parse(r.Str("last_attempt_at")),
            (int)(r.Int("consecutive_failures") ?? 0), r.Str("status") ?? "ok", r.Str("last_error"))).ToList();

    public List<PipelineError> PipelineErrors(int limit = 100) =>
        _db.Query("SELECT id, at, module, message FROM pipeline_errors ORDER BY id DESC LIMIT ?", limit).Select(r =>
            new PipelineError(r.Int("id") ?? 0, ISODate.Parse(r.Str("at")), r.Str("module") ?? "", r.Str("message") ?? "")).ToList();

    public List<LeadEvent> Events(string leadId) =>
        _db.Query("SELECT id, at, action FROM lead_events WHERE lead_id=? ORDER BY id DESC LIMIT 100", leadId).Select(r =>
            new LeadEvent(r.Int("id") ?? 0, ISODate.Parse(r.Str("at")), r.Str("action") ?? "")).ToList();

    public List<SyncLogEntry> SyncLog(int limit = 50) =>
        _db.Query("SELECT * FROM sync_log ORDER BY id DESC LIMIT ?", limit).Select(r =>
            new SyncLogEntry(r.Int("id") ?? 0, ISODate.Parse(r.Str("at")), r.Bool("ok"), (int)(r.Int("added") ?? 0),
                (int)(r.Int("updated") ?? 0), r.Str("message") ?? "")).ToList();

    public List<Exclusion> Exclusions()
    {
        var list = new List<Exclusion>();
        foreach (var r in _db.Query("SELECT * FROM exclusions ORDER BY id DESC"))
        {
            if (EnumText.ParseExclusion(r.Str("kind")) is { } kind)
            {
                list.Add(new Exclusion(r.Int("id") ?? 0, kind, r.Str("value") ?? "", ISODate.Parse(r.Str("created_at"))));
            }
        }

        return list;
    }

    public string? Value(string key) => _db.Scalar("SELECT value FROM kv WHERE key=?", key) as string;

    public void SetValue(string key, string value) =>
        _db.Run("INSERT INTO kv(key, value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);

    public void DeleteValue(string key) => _db.Run("DELETE FROM kv WHERE key=?", key);

    // MARK: - Действия пользователя

    private void UpdateLeads(IReadOnlyCollection<string> ids, string sql, object?[] args, string? eventText, string field)
    {
        if (ids.Count == 0)
        {
            return;
        }

        _db.Transaction(() =>
        {
            foreach (var id in ids)
            {
                if (_db.Run(sql, [.. args, id]) > 0)
                {
                    MarkDirty("lead", id, [field]);
                }

                if (eventText is not null)
                {
                    LogEvent(id, eventText);
                }
            }
        });
    }

    public void SetFavorite(IReadOnlyCollection<string> ids, bool on) => UpdateLeads(ids,
        "UPDATE leads SET favorite=? WHERE lead_id=?", [on], on ? "Добавлен в избранное" : "Убран из избранного", "favorite");

    public void SetArchived(IReadOnlyCollection<string> ids, bool on) => UpdateLeads(ids,
        "UPDATE leads SET archived=? WHERE lead_id=?", [on], on ? "Перенесён в архив" : "Возвращён из архива", "archived");

    /// <summary>Удалённые лиды остаются в базе скрытыми, чтобы следующий поиск не вернул их.</summary>
    public void Delete(IReadOnlyCollection<string> ids) =>
        UpdateLeads(ids, "UPDATE leads SET deleted=1 WHERE lead_id=?", [], "Удалён", "deleted");

    public void MarkSeen(IReadOnlyCollection<string> ids) =>
        UpdateLeads(ids, "UPDATE leads SET seen=1 WHERE lead_id=? AND seen=0", [], null, "seen");

    public void MarkAllSeen()
    {
        var ids = _db.Query("SELECT lead_id FROM leads WHERE seen=0").Select(r => r.Str("lead_id")).OfType<string>().ToList();
        MarkSeen(ids);
    }

    public void SetNote(string id, string note) => _db.Transaction(() =>
    {
        if (_db.Run("UPDATE leads SET note=? WHERE lead_id=? AND note<>?", note, id, note) > 0)
        {
            MarkDirty("lead", id, ["note"]);
        }
    });

    public void LogEvent(string id, string action) =>
        _db.Run("INSERT INTO lead_events(lead_id, at, action) VALUES(?,?,?)", id, ISODate.Now(), action);

    public void AddSyncLog(bool ok, int added = 0, int updated = 0, string message = "") =>
        _db.Run("INSERT INTO sync_log(at, ok, added, updated, message) VALUES(?,?,?,?,?)", ISODate.Now(), ok, added, updated, message);

    // MARK: - Исключения

    public void AddExclusion(ExclusionKind kind, string raw)
    {
        var value = Normalize.Value(raw, kind);
        if (value.Length == 0)
        {
            return;
        }

        _db.Transaction(() =>
        {
            if (_db.Run("INSERT OR IGNORE INTO exclusions(kind, value, created_at) VALUES(?,?,?)", kind.Raw(), value, ISODate.Now()) > 0)
            {
                MarkDirty("exclusion", $"{kind.Raw()}|{value}", ["deleted"]);
            }
        });
        ApplyExclusions();
    }

    public void RemoveExclusion(long id)
    {
        _db.Transaction(() =>
        {
            var r = _db.First("SELECT kind, value FROM exclusions WHERE id=?", id);
            if (r is not null)
            {
                MarkDirty("exclusion", $"{r.Str("kind")}|{r.Str("value")}", ["deleted"]);
            }

            _db.Run("DELETE FROM exclusions WHERE id=?", id);
        });
        ApplyExclusions();
    }

    /// <summary>Пересчитывает флаг «исключён» у всех лидов.</summary>
    public void ApplyExclusions()
    {
        var rules = Exclusions().GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Select(e => e.Value).ToHashSet());
        var leads = AllLeads(includeDeleted: true);
        _db.Transaction(() =>
        {
            foreach (var lead in leads)
            {
                var hit = Matches(lead.Data, rules);
                if (hit != lead.Excluded)
                {
                    _db.Run("UPDATE leads SET excluded=? WHERE lead_id=?", hit, lead.Id);
                }
            }
        });
    }

    public static bool Matches(LeadData d, IReadOnlyDictionary<ExclusionKind, HashSet<string>> rules)
    {
        if (rules.TryGetValue(ExclusionKind.Domain, out var domainSet))
        {
            var domains = new List<string>();
            if (d.Domain is { } dm)
            {
                domains.Add(dm);
            }

            foreach (var e in d.Emails)
            {
                var at = e.Split('@').LastOrDefault();
                if (!string.IsNullOrEmpty(at))
                {
                    domains.Add(Normalize.Domain(at));
                }
            }

            if (domains.Any(dom => domainSet.Any(rule => dom == rule || dom.EndsWith("." + rule, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        if (rules.TryGetValue(ExclusionKind.Phone, out var phoneSet) && d.Phones.Any(p => phoneSet.Contains(Normalize.Phone(p))))
        {
            return true;
        }

        if (rules.TryGetValue(ExclusionKind.Email, out var emailSet) && d.Emails.Any(e => emailSet.Contains(Normalize.Email(e))))
        {
            return true;
        }

        if (rules.TryGetValue(ExclusionKind.Name, out var nameSet))
        {
            var names = new[] { d.Title, d.CustomerName ?? "" }.Select(Normalize.Name).Where(n => n.Length > 0);
            if (names.Any(nameSet.Contains))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Импорт базы существующих клиентов: каждая ячейка с доменом, телефоном или email становится исключением.</summary>
    public int ImportExclusionsCsv(string text)
    {
        var count = 0;
        _db.Transaction(() =>
        {
            foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var cell in Csv.ParseLine(line))
                {
                    var v = cell.Trim();
                    if (v.Length == 0)
                    {
                        continue;
                    }

                    ExclusionKind? kind = null;
                    if (v.Contains('@'))
                    {
                        kind = ExclusionKind.Email;
                    }
                    else if (v.Count(char.IsNumber) >= 10 && v.All(c => char.IsNumber(c) || " +-()".Contains(c)))
                    {
                        kind = ExclusionKind.Phone;
                    }
                    else if (v.Contains('.') && !v.Contains(' '))
                    {
                        kind = ExclusionKind.Domain;
                    }

                    if (kind is { } k)
                    {
                        var norm = Normalize.Value(v, k);
                        var added = _db.Run("INSERT OR IGNORE INTO exclusions(kind, value, created_at) VALUES(?,?,?)", k.Raw(), norm, ISODate.Now());
                        if (added > 0)
                        {
                            MarkDirty("exclusion", $"{k.Raw()}|{norm}", ["deleted"]);
                        }

                        count += added;
                    }
                }
            }
        });
        ApplyExclusions();
        return count;
    }

    // MARK: - Целостность и резервные копии

    /// <summary>Проверка целостности базы. Пустой список — всё в порядке.</summary>
    public List<string> CheckIntegrity()
    {
        var problems = new List<string>();
        try
        {
            foreach (var r in _db.Query("PRAGMA integrity_check"))
            {
                var v = r.Str("integrity_check");
                if (v is not null && v != "ok")
                {
                    problems.Add(v);
                }
            }
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            problems.Add(e.Message);
        }

        return problems;
    }

    /// <summary>Консистентная копия базы. Копия зашифрованной базы шифруется тем же ключом.</summary>
    public void Backup(string target)
    {
        var full = System.IO.Path.GetFullPath(target);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        TryDelete(full);
        _db.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
        _db.Run("VACUUM INTO ?", full);
        if (Key is not null && Db.IsPlainSqlite(full))
        {
            TryDelete(full);
            throw new DatabaseException("резервная копия получилась незашифрованной и удалена");
        }
    }

    /// <summary>Проверяет, что файл — резервная копия Customers, и возвращает число лидов в ней.</summary>
    public int ProbeBackup(string file)
    {
        using var probe = OpenBackup(file);
        if (!probe.TableExists("leads"))
        {
            throw new DatabaseException("это не резервная копия Customers");
        }

        return (int)Convert.ToInt64(probe.Scalar("SELECT COUNT(*) FROM leads"), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Db OpenBackup(string file)
    {
        // Копии этого приложения зашифрованы ключом пользователя; чужие и старые — без шифрования.
        var plain = Db.IsPlainSqlite(file);
        try
        {
            return Db.Open(file, plain ? null : Key, readOnly: true);
        }
        catch (DatabaseException)
        {
            throw new DatabaseException("не удалось открыть копию: она повреждена или создана с другим ключом");
        }
    }

    /// <summary>Заменяет базу копией. Текущая база перед этим сохраняется рядом (before-restore.db).</summary>
    public void Restore(string file)
    {
        var source = System.IO.Path.GetFullPath(file);
        ProbeBackup(source);
        var current = System.IO.Path.GetFullPath(Path);
        if (string.Equals(source, current, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseException("это текущая база, восстанавливать из неё нечего");
        }

        var dir = System.IO.Path.GetDirectoryName(current)!;
        var staged = System.IO.Path.Combine(dir, $"restore-{Guid.NewGuid():N}.db");
        try
        {
            if (Db.IsPlainSqlite(source) && Key is not null)
            {
                ExportEncrypted(source, null, staged, Key);
            }
            else
            {
                File.Copy(source, staged);
            }

            var safety = System.IO.Path.Combine(dir, "before-restore.db");
            Backup(safety);
            _db.Close();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                TryDelete(current + suffix);
            }

            try
            {
                File.Move(staged, current);
            }
            catch
            {
                File.Copy(safety, current, overwrite: true);
                _db = Db.Open(current, Key);
                throw;
            }

            _db = Db.Open(current, Key);
            Prepare();

            // Восстановленная копия — осознанный выбор пользователя: её состояние уходит в облако как
            // новое изменение, а всё, что появилось в облаке позже, загрузится заново.
            if (SyncTracking)
            {
                SetValue("sync.enabled", "1");
                MarkEverythingDirty(DateTimeOffset.UtcNow);
                ResetSyncCursors();
            }
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>Копирует базу в новый файл, шифруя ключом (для импорта незашифрованных копий).</summary>
    internal static void ExportEncrypted(string sourcePath, string? sourceKey, string targetPath, string targetKey)
    {
        using var src = Db.Open(sourcePath, sourceKey, readOnly: false);
        TryDelete(targetPath);
        var quoted = targetKey.Replace("'", "''");
        src.Exec($"ATTACH DATABASE '{targetPath.Replace("'", "''")}' AS enc KEY '{quoted}'");
        src.Exec("SELECT sqlcipher_export('enc')");
        src.Exec("DETACH DATABASE enc");
    }

    /// <summary>Переносит базу в другую папку.</summary>
    public string MoveTo(string directory)
    {
        var target = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), "leads.db");
        if (string.Equals(target, System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase))
        {
            return Path;
        }

        Backup(target);
        var old = Path;
        _db.Close();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            TryDelete(old + suffix);
        }

        _db = Db.Open(target, Key);
        _db.Exec("PRAGMA journal_mode=WAL");
        return target;
    }

    public long FileSize => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
