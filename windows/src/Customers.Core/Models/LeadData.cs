using System.Text.Json;
using System.Text.Json.Nodes;

namespace Customers.Core;

/// <summary>Результат внешнего анализатора. Хранится отдельно от исходных данных.</summary>
public sealed record ExternalResult(string Service, DateTimeOffset? ProcessedAt, bool Ok, double? Score, string? Label, string? Error);

/// <summary>
/// Лид. JSON-формат совместим с Python-версией (<c>Lead.to_json</c>) и с macOS-приложением:
/// те же имена полей (snake_case) и такое же терпимое чтение — неизвестное или повреждённое поле
/// заменяется значением по умолчанию, а не ломает всю запись.
/// </summary>
public sealed class LeadData
{
    public LeadKind Kind { get; set; } = LeadKind.Order;
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? DiscoveredAt { get; set; }
    public double? Budget { get; set; }
    public string? Currency { get; set; }
    public string? Region { get; set; }
    public Dictionary<string, string> Contacts { get; set; } = [];
    public string? Category { get; set; }
    public string? ProjectType { get; set; }
    public string? CustomerName { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public List<string> Facts { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public double Score { get; set; }
    public Relevance Relevance { get; set; } = Relevance.Rejected;
    public List<string> ScoreReasons { get; set; } = [];
    public string Status { get; set; } = "new";
    public List<ExternalResult> ExternalResults { get; set; } = [];

    public bool IsOrganization => Kind == LeadKind.Organization;

    /// <summary>Домен сайта без протокола и www (для исключений и дедупликации).</summary>
    public string? Domain => string.IsNullOrEmpty(Website) ? null : Normalize.Domain(Website);

    /// <summary>Все email из контактов.</summary>
    public IReadOnlyList<string> Emails =>
        Contacts.Values.Where(v => v.Contains('@') && v.Contains('.') && !v.StartsWith('@')).ToList();

    /// <summary>Все телефоны: поле phone и контакты.</summary>
    public IReadOnlyList<string> Phones
    {
        get
        {
            var output = new List<string>();
            if (!string.IsNullOrEmpty(Phone))
            {
                output.Add(Phone);
            }

            foreach (var (k, v) in Contacts)
            {
                if ((k.Contains("phone", StringComparison.OrdinalIgnoreCase) || k.Contains("тел", StringComparison.Ordinal))
                    && !output.Contains(v))
                {
                    output.Add(v);
                }
            }

            return output;
        }
    }

    public LeadData Clone() => FromJson(ToJson())!;

    // MARK: JSON

    /// <summary>Объект JSON с такими же полями, как у macOS-версии (необязательные — только если заданы).</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject {
            ["kind"] = Kind.Raw(),
            ["source"] = Source,
            ["external_id"] = ExternalId,
            ["title"] = Title,
            ["url"] = Url,
            ["description"] = Description,
            ["contacts"] = new JsonObject(Contacts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            ["facts"] = Strings(Facts),
            ["assumptions"] = Strings(Assumptions),
            ["score"] = Score,
            ["relevance"] = Relevance.Raw(),
            ["score_reasons"] = Strings(ScoreReasons),
            ["status"] = Status,
            ["external_results"] = new JsonArray(ExternalResults.Select(ResultToJson).ToArray<JsonNode?>()),
        };
        Put(o, "published_at", ISODate.Format(PublishedAt));
        Put(o, "discovered_at", ISODate.Format(DiscoveredAt));
        if (Budget is { } b)
        {
            o["budget"] = b;
        }

        Put(o, "currency", Currency);
        Put(o, "region", Region);
        Put(o, "category", Category);
        Put(o, "project_type", ProjectType);
        Put(o, "customer_name", CustomerName);
        Put(o, "address", Address);
        Put(o, "phone", Phone);
        Put(o, "website", Website);
        return o;
    }

    /// <summary>JSON-текст с отсортированными ключами: стабильный при повторной записи (иначе каждый лид считался бы изменённым).</summary>
    public string ToStableJson() => JsonStable.Serialize(ToJson());

    public static LeadData? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return FromJson(JsonNode.Parse(json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static LeadData? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        return new LeadData {
            Kind = EnumText.ParseKind(Str(o, "kind")) ?? LeadKind.Order,
            Source = Str(o, "source") ?? "",
            ExternalId = Str(o, "external_id") ?? "",
            Title = Str(o, "title") ?? "",
            Url = Str(o, "url") ?? "",
            Description = Str(o, "description") ?? "",
            PublishedAt = ISODate.Parse(Str(o, "published_at")),
            DiscoveredAt = ISODate.Parse(Str(o, "discovered_at")),
            Budget = Num(o, "budget"),
            Currency = Str(o, "currency"),
            Region = Str(o, "region"),
            Contacts = ReadContacts(o["contacts"]),
            Category = Str(o, "category"),
            ProjectType = Str(o, "project_type"),
            CustomerName = Str(o, "customer_name"),
            Address = Str(o, "address"),
            Phone = Str(o, "phone"),
            Website = Str(o, "website"),
            Facts = StrList(o, "facts"),
            Assumptions = StrList(o, "assumptions"),
            Score = Num(o, "score") ?? 0,
            Relevance = EnumText.ParseRelevance(Str(o, "relevance")) ?? Relevance.Rejected,
            ScoreReasons = StrList(o, "score_reasons"),
            Status = Str(o, "status") ?? "new",
            ExternalResults = ReadResults(o["external_results"]),
        };
    }

    // MARK: helpers

    private static void Put(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    internal static string? Str(JsonObject o, string key)
    {
        try
        {
            return o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static double? Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<double>(out var d))
        {
            return d;
        }

        return v.TryGetValue<long>(out var l) ? l : null;
    }

    private static List<string> StrList(JsonObject o, string key) =>
        o[key] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];

    private static Dictionary<string, string> ReadContacts(JsonNode? node)
    {
        var output = new Dictionary<string, string>();
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                {
                    output[k] = s;
                }
            }
        }

        return output;
    }

    private static JsonObject ResultToJson(ExternalResult r)
    {
        var o = new JsonObject { ["service"] = r.Service, ["ok"] = r.Ok };
        Put(o, "processed_at", ISODate.Format(r.ProcessedAt));
        if (r.Score is { } sc)
        {
            o["score"] = sc;
        }

        Put(o, "label", r.Label);
        Put(o, "error", r.Error);
        return o;
    }

    private static List<ExternalResult> ReadResults(JsonNode? node)
    {
        var list = new List<ExternalResult>();
        if (node is JsonArray a)
        {
            foreach (var item in a.OfType<JsonObject>())
            {
                var service = Str(item, "service");
                if (service is null)
                {
                    continue;
                }

                list.Add(new ExternalResult(service, ISODate.Parse(Str(item, "processed_at")),
                    item["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && b, Num(item, "score"),
                    Str(item, "label"), Str(item, "error")));
            }
        }

        return list;
    }
}

/// <summary>JSON с отсортированными ключами на всех уровнях.</summary>
public static class JsonStable
{
    private static readonly JsonSerializerOptions Options = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode? node) => (Sort(node) ?? JsonValue.Create("null"))?.ToJsonString(Options) ?? "null";

    public static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var sorted = new JsonObject();
                foreach (var key in o.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    sorted[key] = Sort(o[key]?.DeepClone());
                }

                return sorted;
            case JsonArray a:
                return new JsonArray(a.Select(n => Sort(n?.DeepClone())).ToArray());
            default:
                return node?.DeepClone();
        }
    }
}

/// <summary>Лид в локальной базе: данные конвейера + то, что пользователь сделал в приложении.</summary>
public sealed class LeadItem
{
    public required string Id { get; init; }
    public required LeadData Data { get; init; }
    public DateTimeOffset DiscoveredAt { get; init; }
    public bool Favorite { get; init; }
    public bool Archived { get; init; }
    public bool Excluded { get; init; }
    public bool Seen { get; init; }
    public string Note { get; init; } = "";

    public string Title => Data.Title;
    public double Score => Data.Score;
    public string Source => Data.Source;
    public string KindTitle => Data.Kind.Title();
    public int RelevanceRank => Data.Relevance.Rank();
    public string RegionText => Data.Region ?? "";
    public string CategoryText => Data.Category ?? Data.ProjectType ?? "";
    public bool IsNew => !Seen;
}

/// <summary>Состояние источника: последние запуски и ошибки.</summary>
public sealed record SourceState(string Source, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt,
    int ConsecutiveFailures, string Status, string? LastError);

/// <summary>Ошибка или предупреждение из журнала поиска.</summary>
public sealed record PipelineError(long Id, DateTimeOffset? At, string Module, string Message);

public sealed record Exclusion(long Id, ExclusionKind Kind, string Value, DateTimeOffset? CreatedAt);

/// <summary>Запись журнала запусков поиска.</summary>
public sealed record SyncLogEntry(long Id, DateTimeOffset? At, bool Ok, int Added, int Updated, string Message);

/// <summary>Событие истории лида.</summary>
public sealed record LeadEvent(long Id, DateTimeOffset? At, string Action);
