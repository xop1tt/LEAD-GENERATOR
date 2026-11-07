using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Customers.Core;

/// <summary>RSS/Atom-ленты (FL.ru и любые другие публичные ленты заказов).</summary>
public sealed class FeedSourceSettings
{
    public bool Enabled { get; set; } = true;
    public double IntervalHours { get; set; } = 2;
    public List<string> Feeds { get; set; } = ["https://www.fl.ru/rss/all.xml"];

    /// <summary>Пусто — в оценку идут все записи.</summary>
    public List<string> Keywords { get; set; } = [];
}

/// <summary>Публичные Telegram-каналы через веб-превью t.me/s/&lt;канал&gt;.</summary>
public sealed class TelegramSourceSettings
{
    public bool Enabled { get; set; } = true;
    public double IntervalHours { get; set; } = 3;
    public List<string> Channels { get; set; } = ["teleworko", "vacanciesbest", "freetasks", "theyseeku"];
    public int MaxPages { get; set; } = 3;
    public List<string> Keywords { get; set; } = [.. SearchDefaults.OrderKeywords];
}

/// <summary>Открытые сообщества ВКонтакте (wall.get с бесплатным сервисным ключом).</summary>
public sealed class VKSourceSettings
{
    public bool Enabled { get; set; }
    public double IntervalHours { get; set; } = 3;
    public List<string> Communities { get; set; } = [];
    public int PageSize { get; set; } = 50;
    public int MaxPages { get; set; } = 2;

    public List<string> Keywords { get; set; } =
        ["сайт", "бот", "приложени", "разработ", "программист", "автоматизац", "crm", "парсер", "интеграц"];
}

/// <summary>Категория организаций OpenStreetMap.</summary>
public sealed class OSMCategory
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Group { get; set; } = "";
    public List<string> Filters { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public double Weight { get; set; } = 10;
}

/// <summary>Организации из OpenStreetMap через Overpass API.</summary>
public sealed class OSMSourceSettings
{
    public bool Enabled { get; set; } = true;
    public double IntervalHours { get; set; } = 120;
    public List<string> Regions { get; set; } = ["Ереван"];
    public List<OSMCategory> Categories { get; set; } = SearchDefaults.OsmCategories();
    public bool CheckWebsites { get; set; } = true;

    /// <summary>Одинаковый запрос регион × категория не чаще, чем раз в N дней.</summary>
    public double RefreshDays { get; set; } = 7;

    /// <summary>Повторная проверка уже известной организации.</summary>
    public double RecheckDays { get; set; } = 60;

    public int MaxNewPerRun { get; set; } = 40;
    public string Endpoint { get; set; } = "https://overpass-api.de/api/interpreter";
}

/// <summary>
/// Все настройки поиска пользователя. Хранятся в базе как JSON с теми же именами полей, что у macOS-версии
/// (camelCase): так настройки одинаково читаются на обеих платформах и синхронизируются через облако.
/// </summary>
public sealed class SearchSettings
{
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("rss")]
    public FeedSourceSettings Rss { get; set; } = new();

    [JsonPropertyName("telegram")]
    public TelegramSourceSettings Telegram { get; set; } = new();

    [JsonPropertyName("vk")]
    public VKSourceSettings Vk { get; set; } = new();

    [JsonPropertyName("osm")]
    public OSMSourceSettings Osm { get; set; } = new();

    public bool IsEnabled(string key) => key switch {
        SourceKey.Rss => Rss.Enabled,
        SourceKey.Telegram => Telegram.Enabled,
        SourceKey.Vk => Vk.Enabled,
        SourceKey.Osm => Osm.Enabled,
        _ => false,
    };

    public double IntervalHours(string key) => key switch {
        SourceKey.Rss => Rss.IntervalHours,
        SourceKey.Telegram => Telegram.IntervalHours,
        SourceKey.Vk => Vk.IntervalHours,
        SourceKey.Osm => Osm.IntervalHours,
        _ => 24,
    };

    /// <summary>Читает настройки; любой сбой — настройки по умолчанию (как в macOS-версии).</summary>
    public static SearchSettings Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SearchSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<SearchSettings>(json, Options) ?? new SearchSettings();
        }
        catch (JsonException)
        {
            return new SearchSettings();
        }
    }

    public string Encoded() => JsonStable.Serialize(JsonSerializer.SerializeToNode(this, Options));

    public SearchSettings Clone() => Decode(Encoded());
}

/// <summary>Секреты, которые нужны источникам. Хранит их приложение (защищённое хранилище), а не настройки.</summary>
public sealed record SearchSecrets(string? VkToken = null);

public static class SearchDefaults
{
    public static readonly string[] OrderKeywords =
    [
        "сайт", "бот", "приложени", "разработ", "программист", "автоматизац", "crm", "срм", "парсер", "интеграц",
        "лендинг", "верстк", "доработ", "интернет-магазин", "telegram", "телеграм", "python", "api",
    ];

    public static List<OSMCategory> OsmCategories() =>
    [
        new() { Key = "beauty", Label = "Салоны красоты", Group = "beauty", Filters = ["[\"shop\"=\"beauty\"]", "[\"shop\"=\"hairdresser\"]"], Weight = 20 },
        new() { Key = "dance", Label = "Танцевальные студии", Group = "dance", Filters = ["[\"leisure\"=\"dance\"]", "[\"amenity\"=\"dancing_school\"]"], Weight = 20 },
        new() { Key = "restaurant", Label = "Рестораны", Group = "restaurant", Filters = ["[\"amenity\"=\"restaurant\"]"], Weight = 12 },
        new() { Key = "cafe", Label = "Кафе", Group = "restaurant", Filters = ["[\"amenity\"=\"cafe\"]"], Weight = 10 },
        new() { Key = "education", Label = "Образовательные центры", Group = "education", Filters = ["[\"amenity\"=\"language_school\"]", "[\"amenity\"=\"training\"]", "[\"amenity\"=\"music_school\"]"], Weight = 15 },
        new() { Key = "medical", Label = "Медицина и косметология", Group = "medical", Filters = ["[\"amenity\"=\"dentist\"]", "[\"healthcare\"=\"clinic\"]", "[\"amenity\"=\"clinic\"]"], Weight = 15 },
        new() { Key = "fitness", Label = "Спорт и фитнес", Group = "fitness", Filters = ["[\"leisure\"=\"fitness_centre\"]", "[\"leisure\"=\"sports_centre\"]"], Weight = 15 },
        new() { Key = "shops", Label = "Магазины", Group = "shop", Filters = ["[\"shop\"=\"clothes\"]", "[\"shop\"=\"gift\"]", "[\"shop\"=\"florist\"]"], Weight = 10 },
    ];
}

/// <summary>Приведение того, что вводит пользователь, к виду, который понимает источник.</summary>
public static partial class SourceInput
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{3,31}$")]
    private static partial Regex ChannelRx();

    [GeneratedRegex(@"^[A-Za-z0-9_.]{2,64}$")]
    private static partial Regex VkRx();

    private static string StripPrefixes(string raw, string[] first, string[] second)
    {
        var s = raw.Trim();
        foreach (var p in first)
        {
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                s = s[p.Length..];
            }
        }

        foreach (var p in second)
        {
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                s = s[p.Length..];
            }
        }

        if (s.StartsWith('@'))
        {
            s = s[1..];
        }

        var cut = s.IndexOfAny(['/', '?']);
        return cut >= 0 ? s[..cut] : s;
    }

    /// <summary>«@name», «t.me/name», «https://t.me/s/name» → «name».</summary>
    public static string? TelegramChannel(string raw)
    {
        var s = StripPrefixes(raw, ["https://", "http://"], ["www.", "t.me/s/", "t.me/", "telegram.me/"]);
        return ChannelRx().IsMatch(s) ? s : null;
    }

    /// <summary>«https://vk.com/name» → «name».</summary>
    public static string? VkCommunity(string raw)
    {
        var s = StripPrefixes(raw, ["https://", "http://"], ["www.", "m.vk.com/", "vk.com/", "vk.ru/"]);
        return VkRx().IsMatch(s) ? s : null;
    }

    /// <summary>Адрес ленты: только публичный http(s).</summary>
    public static string? FeedUrl(string raw)
    {
        var s = raw.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            s = "https://" + s;
        }

        return SiteChecker.IsPublicHttpUrl(s) ? s : null;
    }

    public static string? Keyword(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }

    public static string? Region(string raw)
    {
        var s = raw.Trim().Replace("\"", "");
        return s.Length == 0 ? null : s;
    }

    /// <summary>Разбивает ввод на элементы: по запятым, точкам с запятой и строкам.</summary>
    public static List<string> Split(string text) =>
        text.Split([',', ';', '\n', '\r', '\t'], StringSplitOptions.None)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}
