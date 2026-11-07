using System.Text.Json.Nodes;

namespace Customers.Core;

// Формат обмена — тот же, что у SQL-функции sync_push и таблиц public.leads / exclusions / user_settings
// (см. docs/cloud.md в корне репозитория). Его реализуют macOS и Windows одинаково.

public sealed class LeadChange
{
    public required string LeadId { get; init; }
    public string Fingerprint { get; init; } = "";
    public string Kind { get; init; } = "order";
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public double Score { get; init; }
    public string Relevance { get; init; } = "rejected";
    public string DiscoveredAt { get; init; } = "";
    public JsonNode? Data { get; init; }
    public bool Favorite { get; init; }
    public bool Archived { get; init; }
    public bool Deleted { get; init; }
    public bool Seen { get; init; }
    public string Note { get; init; } = "";

    /// <summary>Время изменения только тех полей, что менялись на этом устройстве.</summary>
    public required Dictionary<string, string> Ts { get; init; }

    public JsonObject ToJson() => new() {
        ["lead_id"] = LeadId,
        ["fingerprint"] = Fingerprint,
        ["kind"] = Kind,
        ["source"] = Source,
        ["title"] = Title,
        ["score"] = Score,
        ["relevance"] = Relevance,
        ["discovered_at"] = DiscoveredAt,
        ["data"] = Data?.DeepClone(),
        ["favorite"] = Favorite,
        ["archived"] = Archived,
        ["deleted"] = Deleted,
        ["seen"] = Seen,
        ["note"] = Note,
        ["ts"] = new JsonObject(Ts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
    };
}

public sealed record ExclusionChange(string Kind, string Value, bool Deleted, string ChangedAt)
{
    public JsonObject ToJson() => new() { ["kind"] = Kind, ["value"] = Value, ["deleted"] = Deleted, ["changed_at"] = ChangedAt };
}

public sealed record SettingChange(string Key, JsonNode? Value, string ChangedAt)
{
    public JsonObject ToJson() => new() { ["key"] = Key, ["value"] = Value?.DeepClone(), ["changed_at"] = ChangedAt };
}

public sealed class PendingChanges
{
    public List<LeadChange> Leads { get; init; } = [];
    public List<ExclusionChange> Exclusions { get; init; } = [];
    public List<SettingChange> Settings { get; init; } = [];
    public bool IsEmpty => Leads.Count == 0 && Exclusions.Count == 0 && Settings.Count == 0;
    public int Count => Leads.Count + Exclusions.Count + Settings.Count;
}

public sealed class RemoteLead
{
    public string LeadId { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Kind { get; init; } = "order";
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public double Score { get; init; }
    public string Relevance { get; init; } = "rejected";
    public string DiscoveredAt { get; init; } = "";
    public JsonNode? Data { get; init; }
    public bool Favorite { get; init; }
    public bool Archived { get; init; }
    public bool Deleted { get; init; }
    public bool Seen { get; init; }
    public string Note { get; init; } = "";
    public Dictionary<string, string> FieldTs { get; init; } = [];
    public long ServerSeq { get; init; }

    internal static RemoteLead FromJson(JsonObject o)
    {
        string S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        bool B(string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var ts = new Dictionary<string, string>();
        if (o["field_ts"] is JsonObject fo)
        {
            foreach (var (k, v) in fo)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                {
                    ts[k] = s;
                }
            }
        }

        return new RemoteLead {
            LeadId = S("lead_id"),
            Fingerprint = S("fingerprint"),
            Kind = S("kind"),
            Source = S("source"),
            Title = S("title"),
            Score = o["score"] is JsonValue sv && sv.TryGetValue<double>(out var d) ? d : 0,
            Relevance = S("relevance"),
            DiscoveredAt = S("discovered_at"),
            Data = o["data"]?.DeepClone(),
            Favorite = B("favorite"),
            Archived = B("archived"),
            Deleted = B("deleted"),
            Seen = B("seen"),
            Note = S("note"),
            FieldTs = ts,
            ServerSeq = o["server_seq"] is JsonValue q && q.TryGetValue<long>(out var l) ? l : 0,
        };
    }
}

public sealed class RemoteExclusion
{
    public string Kind { get; init; } = "";
    public string Value { get; init; } = "";
    public bool Deleted { get; init; }
    public string ChangedAt { get; init; } = "";
    public long ServerSeq { get; init; }

    internal static RemoteExclusion FromJson(JsonObject o) => new() {
        Kind = o["kind"]?.ToString() ?? "",
        Value = o["value"]?.ToString() ?? "",
        Deleted = o["deleted"] is JsonValue d && d.TryGetValue<bool>(out var b) && b,
        ChangedAt = o["changed_at"]?.ToString() ?? "",
        ServerSeq = o["server_seq"] is JsonValue q && q.TryGetValue<long>(out var l) ? l : 0,
    };
}

public sealed class RemoteSetting
{
    public string Key { get; init; } = "";
    public JsonNode? Value { get; init; }
    public string ChangedAt { get; init; } = "";
    public long ServerSeq { get; init; }

    internal static RemoteSetting FromJson(JsonObject o) => new() {
        Key = o["key"]?.ToString() ?? "",
        Value = o["value"]?.DeepClone(),
        ChangedAt = o["changed_at"]?.ToString() ?? "",
        ServerSeq = o["server_seq"] is JsonValue q && q.TryGetValue<long>(out var l) ? l : 0,
    };
}

/// <summary>
/// Локальное хранилище с журналом несинхронизированных изменений. Изменения записываются в журнал в той же
/// транзакции SQLite, что и сами данные, поэтому переживают аварийное завершение и уходят в облако при следующей синхронизации.
/// </summary>
public interface ISyncLocalStore
{
    PendingChanges PendingChanges(int limit);
    void MarkPushed(PendingChanges changes);
    void ApplyRemote(IReadOnlyList<RemoteLead> leads, IReadOnlyList<RemoteExclusion> exclusions, IReadOnlyList<RemoteSetting> settings);
    long SyncCursor(string table);
    void SetSyncCursor(string table, long value);
}

public sealed record SyncReport(int Pushed, int Pulled, DateTimeOffset FinishedAt);

/// <summary>
/// Двусторонняя синхронизация: сначала выгрузка журнала, затем загрузка всего, что новее локальных курсоров.
/// Конфликты решает сервер по времени изменения каждого поля.
/// </summary>
public sealed class SyncEngine(CloudAuth auth, ISyncLocalStore store, string deviceId, int batch = 200, int pageSize = 500)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pushed = await PushAsync(ct).ConfigureAwait(false);
            var pulled = await PullAsync(ct).ConfigureAwait(false);

            // Изменения, сделанные во время загрузки, уходят сразу же.
            pushed += await PushAsync(ct).ConfigureAwait(false);
            return new SyncReport(pushed, pulled, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<int> PushAsync(CancellationToken ct)
    {
        var total = 0;
        for (var i = 0; i < 1000; i++)
        {
            var pending = store.PendingChanges(batch);
            if (pending.IsEmpty)
            {
                break;
            }

            var body = new JsonObject {
                ["p_device"] = deviceId,
                ["p_changes"] = new JsonObject {
                    ["leads"] = new JsonArray(pending.Leads.Select(l => (JsonNode?)l.ToJson()).ToArray()),
                    ["exclusions"] = new JsonArray(pending.Exclusions.Select(l => (JsonNode?)l.ToJson()).ToArray()),
                    ["settings"] = new JsonArray(pending.Settings.Select(l => (JsonNode?)l.ToJson()).ToArray()),
                },
            };
            var res = await auth.AuthorizedAsync("POST", "/rest/v1/rpc/sync_push", body, ct).ConfigureAwait(false);
            if (!res.Ok)
            {
                throw RestError(res.Status, res.Data);
            }

            store.MarkPushed(pending);
            total += pending.Count;
            if (pending.Count < batch)
            {
                break;
            }
        }

        return total;
    }

    internal async Task<int> PullAsync(CancellationToken ct)
    {
        var total = 0;
        total += await PullTableAsync("leads", RemoteLead.FromJson, rows => store.ApplyRemote(rows, [], []), x => x.ServerSeq, ct).ConfigureAwait(false);
        total += await PullTableAsync("exclusions", RemoteExclusion.FromJson, rows => store.ApplyRemote([], rows, []), x => x.ServerSeq, ct).ConfigureAwait(false);
        total += await PullTableAsync("user_settings", RemoteSetting.FromJson, rows => store.ApplyRemote([], [], rows), x => x.ServerSeq, ct).ConfigureAwait(false);
        return total;
    }

    private async Task<int> PullTableAsync<T>(string table, Func<JsonObject, T> parse, Action<IReadOnlyList<T>> apply,
        Func<T, long> seq, CancellationToken ct)
    {
        var total = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var cursor = store.SyncCursor(table);
            var path = $"/rest/v1/{table}?select=*&server_seq=gt.{cursor}&order=server_seq.asc&limit={pageSize}";
            var res = await auth.AuthorizedAsync("GET", path, null, ct).ConfigureAwait(false);
            if (!res.Ok)
            {
                throw RestError(res.Status, res.Data);
            }

            var rows = (JsonNode.Parse(res.Data) as JsonArray ?? []).OfType<JsonObject>().Select(parse).ToList();
            if (rows.Count == 0)
            {
                break;
            }

            apply(rows);
            store.SetSyncCursor(table, rows.Max(seq));
            total += rows.Count;
            if (rows.Count < pageSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Сводка в облаке — для проверки первоначального переноса.</summary>
    public async Task<Dictionary<string, int>> RemoteStatsAsync(CancellationToken ct = default)
    {
        var res = await auth.AuthorizedAsync("POST", "/rest/v1/rpc/sync_stats", new JsonObject(), ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            throw RestError(res.Status, res.Data);
        }

        var output = new Dictionary<string, int>();
        if (JsonNode.Parse(res.Data) is JsonObject o)
        {
            foreach (var (k, v) in o)
            {
                if (v is JsonValue jv && jv.TryGetValue<double>(out var d))
                {
                    output[k] = (int)d;
                }
            }
        }

        return output;
    }

    internal static CloudException RestError(int status, byte[] data)
    {
        if (status is 401 or 403)
        {
            return new CloudException(CloudErrorKind.SessionExpired);
        }

        string? message = null;
        try
        {
            message = (JsonNode.Parse(data) as JsonObject)?["message"]?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new CloudException(CloudErrorKind.Server, message is null ? $"HTTP {status}" : message[..Math.Min(200, message.Length)]);
    }
}
