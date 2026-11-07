using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Core;

// Экспорт лидов во внешние сервисы: Bitrix24 (входящий вебхук), Notion (интеграция и база данных) и любой JSON-вебхук
// (Make, n8n, Zapier, свой сервер). Ключи хранит приложение в защищённом хранилище, сюда они приходят параметрами
// и никуда не записываются. Запросы и сопоставления полей совпадают с macOS-версией.

public enum CrmKind
{
    Bitrix,
    Notion,
    Webhook,
}

public static class CrmKinds
{
    public static string Title(this CrmKind k) => k switch { CrmKind.Bitrix => "Bitrix24", CrmKind.Notion => "Notion", _ => "Вебхук" };

    public static string Raw(this CrmKind k) => k switch { CrmKind.Bitrix => "bitrix", CrmKind.Notion => "notion", _ => "webhook" };

    /// <summary>Имя секрета в защищённом хранилище.</summary>
    public static string SecretName(this CrmKind k) => $"export.{k.Raw()}.secret";
}

/// <summary>Понятные ошибки. Секреты в текст не попадают.</summary>
public sealed class CrmException : Exception
{
    public CrmException(CrmErrorKind kind, string message, int status = 0)
        : base(Compose(kind, message, status))
    {
        Kind = kind;
        Status = status;
    }

    public CrmErrorKind Kind { get; }
    public int Status { get; }

    /// <summary>Ошибка, после которой нет смысла отправлять остальные лиды.</summary>
    public bool StopsExport => Kind is CrmErrorKind.Unauthorized or CrmErrorKind.Forbidden or CrmErrorKind.NotFound
        or CrmErrorKind.InvalidAddress or CrmErrorKind.Network;

    private static string Compose(CrmErrorKind kind, string s, int status) => kind switch {
        CrmErrorKind.InvalidAddress => s,
        CrmErrorKind.Unauthorized => $"Доступ не подтверждён: {s}",
        CrmErrorKind.Forbidden => $"Недостаточно прав: {s}",
        CrmErrorKind.NotFound => $"Не найдено: {s}",
        CrmErrorKind.RateLimited => "Сервис просит подождать: слишком много запросов. Повторите экспорт через минуту.",
        CrmErrorKind.Server => s.Length == 0 ? $"Сервис ответил ошибкой {status}" : $"Ошибка {status}: {s}",
        CrmErrorKind.BadResponse => $"Непонятный ответ сервиса: {s}",
        _ => $"Нет связи с сервисом: {s}",
    };
}

public enum CrmErrorKind
{
    InvalidAddress,
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    Server,
    BadResponse,
    Network,
}

/// <summary>Какие лиды уже ушли в конкретное назначение: id лида → id записи на стороне сервиса. Хранится локально.</summary>
public sealed class ExportLedger
{
    public Dictionary<string, string> Sent { get; set; } = [];

    public static ExportLedger Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new ExportLedger();
        }

        try
        {
            var o = JsonNode.Parse(raw) as JsonObject;
            var ledger = new ExportLedger();
            if (o?["sent"] is JsonObject sent)
            {
                foreach (var (k, v) in sent)
                {
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        ledger.Sent[k] = s;
                    }
                }
            }

            return ledger;
        }
        catch (System.Text.Json.JsonException)
        {
            return new ExportLedger();
        }
    }

    public string Encoded() => JsonStable.Serialize(new JsonObject {
        ["sent"] = new JsonObject(Sent.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
    });

    /// <summary>Ключ kv для назначения. Адрес вебхука содержит секрет, поэтому в ключ идёт только хэш.</summary>
    public static string StorageKey(CrmKind kind, string target) => $"export.ledger.{kind.Raw()}.{Fnv1a(target.ToLowerInvariant())}";

    internal static string Fnv1a(string s)
    {
        var h = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }

        return h.ToString("x", CultureInfo.InvariantCulture);
    }
}

public sealed class CrmReport
{
    public int Exported { get; set; }
    public int Skipped { get; set; }
    public List<(string Title, string Message)> Failures { get; } = [];
    public bool StoppedEarly { get; set; }

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"Отправлено: {Exported}" };
            if (Skipped > 0)
            {
                parts.Add($"пропущено как уже отправленные: {Skipped}");
            }

            if (Failures.Count > 0)
            {
                parts.Add($"с ошибкой: {Failures.Count}");
            }

            return string.Join(", ", parts);
        }
    }
}

/// <summary>Одно назначение экспорта.</summary>
public interface ICrmDestination
{
    CrmKind Kind { get; }

    /// <summary>Что-то, однозначно определяющее место назначения (портал, база, адрес). Идёт в ключ журнала.</summary>
    string Target { get; }

    /// <summary>Пауза между запросами, чтобы уложиться в лимиты сервиса.</summary>
    TimeSpan Pause { get; }

    /// <summary>Отправить один лид. Возвращает id записи на стороне сервиса.</summary>
    Task<string> SendAsync(LeadItem item, ICrmHttp http, CancellationToken ct);
}

/// <summary>HTTP для экспорта (в тестах подменяется).</summary>
public interface ICrmHttp
{
    Task<(byte[] Data, int Status)> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

public sealed class CrmHttp : ICrmHttp, IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => _client.Dispose();

    public async Task<(byte[] Data, int Status)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var resp = await _client.SendAsync(request, ct).ConfigureAwait(false);
            return (await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), (int)resp.StatusCode);
        }
        catch (HttpRequestException e)
        {
            throw new CrmException(CrmErrorKind.Network, e.Message);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new CrmException(CrmErrorKind.Network, e.Message);
        }
    }
}

public static class CrmExporter
{
    /// <summary>Отправляет лиды по одному. Уже отправленные (по журналу) пропускаются, если не <paramref name="resend"/>.</summary>
    public static async Task<(CrmReport Report, ExportLedger Ledger)> RunAsync(IEnumerable<LeadItem> items, ICrmDestination destination,
        ExportLedger ledger, ICrmHttp http, bool resend = false, Action<int, int>? progress = null, CancellationToken ct = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= (t, c) => Task.Delay(t, c);
        var list = items.ToList();
        var report = new CrmReport();
        var first = true;
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            progress?.Invoke(i, list.Count);
            if (ct.IsCancellationRequested)
            {
                report.StoppedEarly = true;
                break;
            }

            if (!resend && ledger.Sent.ContainsKey(item.Id))
            {
                report.Skipped++;
                continue;
            }

            if (!first && destination.Pause > TimeSpan.Zero)
            {
                await delay(destination.Pause, ct).ConfigureAwait(false);
            }

            first = false;
            try
            {
                ledger.Sent[item.Id] = await SendRetryingAsync(item, destination, http, ct, delay).ConfigureAwait(false);
                report.Exported++;
            }
            catch (OperationCanceledException)
            {
                report.StoppedEarly = true;
                break;
            }
            catch (Exception e)
            {
                var ce = e as CrmException ?? new CrmException(CrmErrorKind.Network, e.Message);
                report.Failures.Add((item.Data.Title, ce.Message));
                if (ce.StopsExport)
                {
                    report.StoppedEarly = true;
                    break;
                }
            }
        }

        progress?.Invoke(list.Count, list.Count);
        return (report, ledger);
    }

    /// <summary>Один повтор после «слишком много запросов».</summary>
    private static async Task<string> SendRetryingAsync(LeadItem item, ICrmDestination d, ICrmHttp http, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        try
        {
            return await d.SendAsync(item, http, ct).ConfigureAwait(false);
        }
        catch (CrmException e) when (e.Kind == CrmErrorKind.RateLimited)
        {
            await delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            return await d.SendAsync(item, http, ct).ConfigureAwait(false);
        }
    }

    internal static JsonObject? Object(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Число или строка в идентификатор.</summary>
    internal static string? IdString(JsonNode? v)
    {
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s))
            {
                return s.Length == 0 ? null : s;
            }

            if (jv.TryGetValue<long>(out var l))
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }

            if (jv.TryGetValue<double>(out var d) && Math.Round(d) == d)
            {
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>Текст лида для поля «Комментарий» / тела страницы.</summary>
    public static string Comment(LeadItem item)
    {
        var d = item.Data;
        var lines = new List<string>();
        if (d.Description.Length > 0)
        {
            lines.Add(d.Description);
        }

        if (d.Url.Length > 0)
        {
            lines.Add($"Ссылка: {d.Url}");
        }

        lines.Add($"Источник: {SourceCatalog.Title(d.Source)}");
        lines.Add($"Оценка: {(long)Math.Round(d.Score, MidpointRounding.AwayFromZero)} ({d.Relevance.Title()})");
        if (ExportField.Budget.Value(item).NilIfEmpty() is { } b)
        {
            lines.Add($"Бюджет: {b}");
        }

        if (d.ScoreReasons.Count > 0)
        {
            lines.Add("Почему: " + string.Join("; ", d.ScoreReasons));
        }

        if (item.Note.Length > 0)
        {
            lines.Add($"Заметка: {item.Note}");
        }

        return string.Join("\n", lines);
    }

    internal static string? NilIfEmpty(this string s)
    {
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    internal static HttpRequestMessage Json(HttpMethod method, Uri url, JsonNode? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return req;
    }
}

// MARK: - Bitrix24

/// <summary>Bitrix24 через входящий вебхук (бесплатный тариф тоже). Создаёт лиды методом <c>crm.lead.add</c>.</summary>
public sealed class Bitrix24Destination : ICrmDestination
{
    public Bitrix24Destination(string webhook) => Base = Normalize(webhook);

    public CrmKind Kind => CrmKind.Bitrix;

    /// <summary>Адрес вида <c>https://portal.bitrix24.ru/rest/1/abc123/</c> (с завершающим «/»).</summary>
    public Uri Base { get; }

    public string Target => Base.Host;

    /// <summary>Лимит Bitrix24: около 2 запросов в секунду.</summary>
    public TimeSpan Pause => TimeSpan.FromSeconds(0.6);

    /// <summary>Проверяет и приводит адрес вебхука к виду <c>https://host/rest/&lt;user&gt;/&lt;code&gt;/</c>.</summary>
    public static Uri Normalize(string raw)
    {
        var bad = new CrmException(CrmErrorKind.InvalidAddress,
            "Вставьте адрес входящего вебхука целиком, например https://ваш-портал.bitrix24.ru/rest/1/abc123def456/");
        var s = raw.Trim();
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host))
        {
            throw bad;
        }

        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var rest = parts.IndexOf("rest");
        if (rest < 0 || parts.Count < rest + 3 || !int.TryParse(parts[rest + 1], out _) || parts[rest + 2].Length == 0)
        {
            throw bad;
        }

        var path = "/" + string.Join("/", parts.Take(rest + 3)) + "/";
        var b = new UriBuilder(Uri.UriSchemeHttps, u.Host, u.IsDefaultPort ? -1 : u.Port, path);
        return b.Uri;
    }

    /// <summary>Поля лида для <c>crm.lead.add</c>.</summary>
    public static JsonObject Fields(LeadItem item)
    {
        var d = item.Data;
        var f = new JsonObject {
            ["TITLE"] = SearchText.Prefix(d.Title, 250),
            ["SOURCE_ID"] = "OTHER",
            ["SOURCE_DESCRIPTION"] = $"Customers: {SourceCatalog.Title(d.Source)}",
            ["COMMENTS"] = CrmExporter.Comment(item),
            ["OPENED"] = "Y",

            // Связь с исходной записью: по ней видно, откуда пришёл лид.
            ["ORIGINATOR_ID"] = "lead-generator",
            ["ORIGIN_ID"] = item.Id,
        };
        var name = (d.CustomerName ?? "").NilIfEmpty() ?? (d.Kind == LeadKind.Organization ? d.Title.NilIfEmpty() : null);
        if (name is not null)
        {
            f["COMPANY_TITLE"] = name;
        }

        JsonObject Val(string v) => new() { ["VALUE"] = v, ["VALUE_TYPE"] = "WORK" };
        if (d.Phones.Count > 0)
        {
            f["PHONE"] = new JsonArray(d.Phones.Select(p => (JsonNode?)Val(p)).ToArray());
        }

        if (d.Emails.Count > 0)
        {
            f["EMAIL"] = new JsonArray(d.Emails.Select(p => (JsonNode?)Val(p)).ToArray());
        }

        if ((d.Website ?? "").NilIfEmpty() is { } w)
        {
            f["WEB"] = new JsonArray(Val(w));
        }

        if ((d.Address ?? "").NilIfEmpty() is { } a)
        {
            f["ADDRESS"] = a;
        }

        if ((d.Region ?? "").NilIfEmpty() is { } region)
        {
            f["ADDRESS_CITY"] = region;
        }

        if (d.Budget is { } budget)
        {
            f["OPPORTUNITY"] = budget;
            if ((d.Currency ?? "").NilIfEmpty() is { } c)
            {
                f["CURRENCY_ID"] = c == "₽" ? "RUB" : c.ToUpperInvariant();
            }
        }

        return f;
    }

    /// <summary>Разбор ответа Bitrix24: <c>{"result": 123}</c> или <c>{"error": "...", "error_description": "..."}</c>.</summary>
    public static JsonNode Parse(byte[] data, int status)
    {
        var obj = CrmExporter.Object(data);
        if (obj?["error"] is JsonValue ev && ev.TryGetValue<string>(out var err))
        {
            var text = obj["error_description"]?.ToString().NilIfEmpty() ?? err;
            switch (err.ToUpperInvariant())
            {
                case "QUERY_LIMIT_EXCEEDED":
                    throw new CrmException(CrmErrorKind.RateLimited, "");
                case "INVALID_CREDENTIALS" or "WRONG_AUTH_TYPE" or "NO_AUTH_FOUND" or "EXPIRED_TOKEN" or "INVALID_TOKEN":
                    throw new CrmException(CrmErrorKind.Unauthorized, $"Bitrix24 не принял вебхук. Проверьте адрес или создайте вебхук заново. ({text})");
                case "INSUFFICIENT_SCOPE" or "ACCESS_DENIED":
                    throw new CrmException(CrmErrorKind.Forbidden, $"у вебхука нет права «CRM (crm)». Откройте его настройки в Bitrix24 и отметьте CRM. ({text})");
                case "ERROR_METHOD_NOT_FOUND":
                    throw new CrmException(CrmErrorKind.NotFound, $"метод не найден. Возможно, у вебхука нет права «CRM (crm)». ({text})");
                default:
                    throw new CrmException(CrmErrorKind.Server, text, status);
            }
        }

        if (status is 429 or 503)
        {
            throw new CrmException(CrmErrorKind.RateLimited, "");
        }

        if (status == 401)
        {
            throw new CrmException(CrmErrorKind.Unauthorized, "Bitrix24 не принял вебхук.");
        }

        if (status == 404)
        {
            throw new CrmException(CrmErrorKind.NotFound, "портал или вебхук. Проверьте адрес.");
        }

        if (status is < 200 or >= 300)
        {
            throw new CrmException(CrmErrorKind.Server, "", status);
        }

        return obj?["result"] ?? throw new CrmException(CrmErrorKind.BadResponse, "нет поля result");
    }

    /// <summary>Проверка подключения: вебхук действует и у него есть доступ к CRM.</summary>
    public async Task CheckAsync(ICrmHttp http, CancellationToken ct = default)
    {
        using var req = CrmExporter.Json(HttpMethod.Post, new Uri(Base, "crm.lead.fields.json"), new JsonObject());
        var (data, status) = await http.SendAsync(req, ct).ConfigureAwait(false);
        Parse(data, status);
    }

    public async Task<string> SendAsync(LeadItem item, ICrmHttp http, CancellationToken ct)
    {
        var body = new JsonObject {
            ["fields"] = Fields(item),
            ["params"] = new JsonObject { ["REGISTER_SONET_EVENT"] = "N" },
        };
        using var req = CrmExporter.Json(HttpMethod.Post, new Uri(Base, "crm.lead.add.json"), body);
        var (data, status) = await http.SendAsync(req, ct).ConfigureAwait(false);
        var result = Parse(data, status);
        return CrmExporter.IdString(result) ?? throw new CrmException(CrmErrorKind.BadResponse, "в ответе нет номера лида");
    }
}

// MARK: - Notion

public sealed record NotionProperty(string Name, string Type)
{
    /// <summary>Типы, в которые экспорт умеет писать.</summary>
    public static readonly IReadOnlySet<string> Writable = new HashSet<string>
    {
        "title", "rich_text", "url", "email", "phone_number", "number", "select", "multi_select", "checkbox", "date",
    };

    public bool IsWritable => Writable.Contains(Type);

    public string TypeTitle => Type switch {
        "title" => "заголовок",
        "rich_text" => "текст",
        "url" => "ссылка",
        "email" => "email",
        "phone_number" => "телефон",
        "number" => "число",
        "select" => "выбор",
        "multi_select" => "теги",
        "checkbox" => "флажок",
        "date" => "дата",
        _ => Type,
    };
}

public sealed record NotionDatabase(string Id, string Title, IReadOnlyList<NotionProperty> Properties);

/// <summary>Notion через внутреннюю интеграцию (бесплатно на любом тарифе): каждая страница базы — один лид.</summary>
public sealed class NotionDestination : ICrmDestination
{
    public static readonly Uri Api = new("https://api.notion.com/v1/");
    public const string Version = "2022-06-28";

    public NotionDestination(string token, string databaseId, IReadOnlyList<NotionProperty> properties, IReadOnlyDictionary<string, ExportField> mapping)
    {
        Token = token.Trim();
        DatabaseId = databaseId;
        Properties = properties;
        Mapping = mapping;
    }

    public CrmKind Kind => CrmKind.Notion;
    public string Token { get; }
    public string DatabaseId { get; }

    /// <summary>Свойство базы → поле лида.</summary>
    public IReadOnlyDictionary<string, ExportField> Mapping { get; }

    public IReadOnlyList<NotionProperty> Properties { get; }
    public string Target => DatabaseId;

    /// <summary>Лимит Notion: в среднем 3 запроса в секунду.</summary>
    public TimeSpan Pause => TimeSpan.FromSeconds(0.4);

    /// <summary>ID базы из ссылки или из самого ID: последняя группа из 32 шестнадцатеричных знаков.</summary>
    public static string ParseDatabaseId(string raw)
    {
        var s = raw.Trim();
        var path = Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.AbsolutePath : s;
        var compact = path.Replace("-", "");
        var run = 0;
        int? end = null;
        for (var i = 0; i < compact.Length; i++)
        {
            run = Uri.IsHexDigit(compact[i]) ? run + 1 : 0;
            if (run >= 32)
            {
                end = i;
            }
        }

        if (end is not { } e)
        {
            throw new CrmException(CrmErrorKind.InvalidAddress,
                "Вставьте ссылку на базу данных Notion (Поделиться → Скопировать ссылку) или её ID из 32 символов.");
        }

        var id = compact.Substring(e - 31, 32).ToLowerInvariant();
        return $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..]}";
    }

    internal static HttpRequestMessage Request(string path, string token, HttpMethod? method = null, JsonNode? body = null)
    {
        var req = CrmExporter.Json(method ?? HttpMethod.Get, new Uri(Api, path), body);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("Notion-Version", Version);
        return req;
    }

    /// <summary>Ошибки Notion: <c>{"object":"error","status":401,"code":"unauthorized","message":"..."}</c>.</summary>
    public static JsonObject Check(byte[] data, int status)
    {
        var obj = CrmExporter.Object(data) ?? [];
        if (status is >= 200 and < 300 && obj["object"]?.ToString() != "error")
        {
            return obj;
        }

        var code = obj["code"]?.ToString() ?? "";
        var message = obj["message"]?.ToString() ?? "";
        if (status == 401 || code == "unauthorized")
        {
            throw new CrmException(CrmErrorKind.Unauthorized, "Notion не принял ключ интеграции. Скопируйте его заново на notion.so/my-integrations.");
        }

        if (status == 403 || code == "restricted_resource")
        {
            throw new CrmException(CrmErrorKind.Forbidden, "у интеграции нет доступа к базе. Откройте базу в Notion → ••• → Подключения → добавьте интеграцию.");
        }

        if (status == 404 || code == "object_not_found")
        {
            throw new CrmException(CrmErrorKind.NotFound, "база не найдена или к ней не подключена интеграция. Откройте базу → ••• → Подключения → добавьте интеграцию.");
        }

        if (status == 429 || code == "rate_limited")
        {
            throw new CrmException(CrmErrorKind.RateLimited, "");
        }

        if (status == 400 || code == "validation_error")
        {
            throw new CrmException(CrmErrorKind.Server, $"Notion отклонил данные: {message}. Проверьте сопоставление полей.", 400);
        }

        throw new CrmException(CrmErrorKind.Server, message, status);
    }

    /// <summary>Схема базы: название и свойства.</summary>
    public static async Task<NotionDatabase> FetchDatabaseAsync(string token, string id, ICrmHttp http, CancellationToken ct = default)
    {
        using var req = Request($"databases/{id}", token.Trim());
        var (data, status) = await http.SendAsync(req, ct).ConfigureAwait(false);
        return ParseDatabase(Check(data, status), id);
    }

    public static NotionDatabase ParseDatabase(JsonObject obj, string id)
    {
        if (obj["properties"] is not JsonObject props)
        {
            throw new CrmException(CrmErrorKind.BadResponse, "в базе нет свойств");
        }

        var title = string.Concat((obj["title"] as JsonArray ?? []).OfType<JsonObject>().Select(t => t["plain_text"]?.ToString() ?? ""));
        var list = new List<NotionProperty>();
        foreach (var (name, v) in props)
        {
            if (v is JsonObject po && po["type"]?.ToString() is { } type)
            {
                list.Add(new NotionProperty(name, type));
            }
        }

        // Заголовок первым, остальные по имени.
        var sorted = list.OrderBy(p => p.Type == "title" ? 0 : 1).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        return new NotionDatabase(id, title.Length == 0 ? "Без названия" : title, sorted);
    }

    /// <summary>Сопоставление по умолчанию: заголовок ← название, остальные — по похожему имени свойства.</summary>
    public static Dictionary<string, ExportField> AutoMapping(IEnumerable<NotionProperty> properties)
    {
        var props = properties.ToList();
        var output = new Dictionary<string, ExportField>();
        var used = new HashSet<ExportField>();
        var synonyms = new Dictionary<ExportField, string[]> {
            [ExportField.Title] = ["название", "name", "title", "имя", "лид"],
            [ExportField.Url] = ["ссылка", "url", "link", "источник ссылка"],
            [ExportField.Phone] = ["телефон", "phone", "тел"],
            [ExportField.Emails] = ["email", "e-mail", "почта", "mail"],
            [ExportField.Website] = ["сайт", "website", "web", "site"],
            [ExportField.Address] = ["адрес", "address"],
            [ExportField.Region] = ["регион", "город", "region", "city"],
            [ExportField.Category] = ["категория", "category", "тип проекта"],
            [ExportField.Source] = ["источник", "source"],
            [ExportField.Score] = ["оценка", "score", "балл"],
            [ExportField.Relevance] = ["релевантность", "relevance", "приоритет"],
            [ExportField.Kind] = ["тип", "kind", "type"],
            [ExportField.Budget] = ["бюджет", "budget", "сумма"],
            [ExportField.DiscoveredAt] = ["обнаружен", "дата", "date", "найден"],
            [ExportField.Description] = ["описание", "description"],
            [ExportField.Note] = ["заметка", "note", "комментарий"],
            [ExportField.Reasons] = ["причины оценки", "причины", "reasons"],
            [ExportField.Favorite] = ["избранное", "favorite"],
        };
        foreach (var p in props.Where(p => p.Type == "title"))
        {
            output[p.Name] = ExportField.Title;
            used.Add(ExportField.Title);
        }

        foreach (var p in props.Where(p => p.IsWritable && !output.ContainsKey(p.Name)))
        {
            var n = p.Name.ToLowerInvariant().Trim();
            ExportField? match = null;
            foreach (var f in ExportFields.All)
            {
                if (!used.Contains(f) && ((synonyms.TryGetValue(f, out var syn) && syn.Contains(n)) || f.Title().ToLowerInvariant() == n))
                {
                    match = f;
                    break;
                }
            }

            if (match is { } m)
            {
                output[p.Name] = m;
                used.Add(m);
            }
        }

        return output;
    }

    /// <summary>Значение свойства Notion из поля лида. <c>null</c> — пропустить (пустое или неподходящее).</summary>
    public static JsonObject? PropertyValue(ExportField field, LeadItem item, string type)
    {
        var raw = field switch {
            ExportField.Phone => item.Data.Phones.FirstOrDefault() ?? "",
            ExportField.Emails => item.Data.Emails.FirstOrDefault() ?? "",
            _ => field.Value(item),
        };
        if (type == "checkbox")
        {
            return new JsonObject { ["checkbox"] = field == ExportField.Favorite ? item.Favorite : raw.Length > 0 };
        }

        var text = SearchText.Prefix(raw, 2000);
        if (text.Length == 0)
        {
            return null;
        }

        static JsonArray Rich(string s) => new(new JsonObject { ["text"] = new JsonObject { ["content"] = s } });
        switch (type)
        {
            case "title":
                return new JsonObject { ["title"] = Rich(text) };
            case "rich_text":
                return new JsonObject {
                    ["rich_text"] = Rich(field == ExportField.Phone ? string.Join(", ", item.Data.Phones)
                        : field == ExportField.Emails ? string.Join(", ", item.Data.Emails) : text),
                };
            case "url":
                var u = field == ExportField.Emails ? $"mailto:{text}"
                    : field == ExportField.Phone ? "tel:" + new string(text.Where(c => char.IsNumber(c) || c == '+').ToArray()) : text;
                return new JsonObject { ["url"] = u };
            case "email":
                return text.Contains('@') ? new JsonObject { ["email"] = text } : null;
            case "phone_number":
                return new JsonObject { ["phone_number"] = text };
            case "number":
                double? n = field switch {
                    ExportField.Score => Math.Round(item.Data.Score, MidpointRounding.AwayFromZero),
                    ExportField.Budget => item.Data.Budget,
                    _ => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                };
                return n is null ? null : new JsonObject { ["number"] = n };
            case "select":
                return new JsonObject { ["select"] = new JsonObject { ["name"] = SearchText.Prefix(text, 100).Replace(",", " ") } };
            case "multi_select":
                var tags = text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => SearchText.Prefix(t.Trim(), 100)).Where(t => t.Length > 0).Take(20).ToList();
                return tags.Count == 0 ? null : new JsonObject { ["multi_select"] = new JsonArray(tags.Select(t => (JsonNode?)new JsonObject { ["name"] = t }).ToArray()) };
            case "date":
                var date = field == ExportField.DiscoveredAt ? item.DiscoveredAt : ISODate.Parse(text);
                return ISODate.Format(date) is { } start ? new JsonObject { ["date"] = new JsonObject { ["start"] = start } } : null;
            default:
                return null;
        }
    }

    public JsonObject PageBody(LeadItem item)
    {
        var props = new JsonObject();
        foreach (var p in Properties)
        {
            if (Mapping.TryGetValue(p.Name, out var field) && PropertyValue(field, item, p.Type) is { } v)
            {
                props[p.Name] = v;
            }
        }

        // Текст лида — в тело страницы, абзацами по 2000 символов (лимит Notion).
        var text = CrmExporter.Comment(item);
        var chunks = new List<string>();
        var rest = text;
        while (rest.Length > 0 && chunks.Count < 20)
        {
            chunks.Add(rest[..Math.Min(2000, rest.Length)]);
            rest = rest[Math.Min(2000, rest.Length)..];
        }

        var children = new JsonArray(chunks.Select(c => (JsonNode?)new JsonObject {
            ["object"] = "block",
            ["type"] = "paragraph",
            ["paragraph"] = new JsonObject { ["rich_text"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = c } }) },
        }).ToArray());
        return new JsonObject {
            ["parent"] = new JsonObject { ["database_id"] = DatabaseId },
            ["properties"] = props,
            ["children"] = children,
        };
    }

    public async Task<string> SendAsync(LeadItem item, ICrmHttp http, CancellationToken ct)
    {
        using var req = Request("pages", Token, HttpMethod.Post, PageBody(item));
        var (data, status) = await http.SendAsync(req, ct).ConfigureAwait(false);
        var obj = Check(data, status);
        return obj["id"]?.ToString() ?? throw new CrmException(CrmErrorKind.BadResponse, "в ответе нет id страницы");
    }
}

// MARK: - Вебхук

/// <summary>
/// Любой адрес, принимающий JSON методом POST: сценарии Make, n8n, Zapier, Pipedream или свой сервер.
/// Через них лиды попадают в amoCRM, Google Таблицы, Airtable и т. д.
/// </summary>
public sealed class WebhookDestination : ICrmDestination
{
    public WebhookDestination(string url) => Url = Normalize(url);

    public CrmKind Kind => CrmKind.Webhook;
    public Uri Url { get; }
    public string Target => Url.AbsoluteUri;
    public TimeSpan Pause => TimeSpan.FromSeconds(0.25);

    /// <summary>Адрес вебхука секретный (в нём ключ сценария), поэтому принимается только https.</summary>
    public static Uri Normalize(string raw)
    {
        var bad = new CrmException(CrmErrorKind.InvalidAddress, "Нужен адрес вебхука, начинающийся с https://");
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host))
        {
            throw bad;
        }

        return u;
    }

    /// <summary>Тело запроса: один лид, все поля экспорта, плюс телефоны и email списками.</summary>
    public static JsonObject Payload(LeadItem item)
    {
        var lead = new JsonObject();
        foreach (var f in ExportFields.All)
        {
            lead[f.RawName()] = f.Value(item);
        }

        lead["phones"] = new JsonArray(item.Data.Phones.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        lead["email_list"] = new JsonArray(item.Data.Emails.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        lead["score"] = Math.Round(item.Data.Score, MidpointRounding.AwayFromZero);
        lead["favorite"] = item.Favorite;
        return new JsonObject {
            ["event"] = "lead.exported",
            ["source"] = "lead-generator",
            ["lead_id"] = item.Id,
            ["lead"] = lead,
        };
    }

    public async Task<string> SendAsync(LeadItem item, ICrmHttp http, CancellationToken ct)
    {
        using var req = CrmExporter.Json(HttpMethod.Post, Url, Payload(item));
        req.Headers.TryAddWithoutValidation("X-LeadGen-Lead-Id", item.Id);
        var (data, code) = await http.SendAsync(req, ct).ConfigureAwait(false);
        switch (code)
        {
            case >= 200 and < 300:
                return CrmExporter.IdString(CrmExporter.Object(data)?["id"]) ?? item.Id;
            case 401:
                throw new CrmException(CrmErrorKind.Unauthorized, "адрес вебхука отклонил запрос.");
            case 403:
                throw new CrmException(CrmErrorKind.Forbidden, "адрес вебхука отклонил запрос.");
            case 404 or 410:
                throw new CrmException(CrmErrorKind.NotFound, "адрес вебхука не существует или сценарий выключен.");
            case 429:
                throw new CrmException(CrmErrorKind.RateLimited, "");
            default:
                var text = Encoding.UTF8.GetString(data, 0, Math.Min(200, data.Length));
                throw new CrmException(CrmErrorKind.Server, text.Trim(), code);
        }
    }
}
