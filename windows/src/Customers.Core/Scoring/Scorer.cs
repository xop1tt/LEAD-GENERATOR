using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Customers.Core;

/// <summary>Правила оценки заказов. Значения по умолчанию совпадают с config.yaml Python-версии и macOS-приложением.</summary>
public sealed class OrderScoring
{
    public double MaxAgeDays { get; set; } = 14;
    public double MinBudgetRub { get; set; } = 3000;
    public double High { get; set; } = 65;
    public double Medium { get; set; } = 45;
    public double Low { get; set; } = 25;
    public double WTopic { get; set; } = 30;
    public double WIntent { get; set; } = 20;
    public double WOfferPenalty { get; set; } = 35;
    public double WBudget { get; set; } = 15;
    public double WLowBudgetPenalty { get; set; } = 10;
    public double WUrgency { get; set; } = 5;
    public double WCompleteness { get; set; } = 10;
    public double WTooShortPenalty { get; set; } = 10;
    public double WCustomerType { get; set; } = 5;
    public double WFeasibility { get; set; } = 5;
    public double WFeasibilityPenalty { get; set; } = 15;
    public double WSpamPenalty { get; set; } = 40;
    public double WFreshness { get; set; } = 10;

    public List<(string Type, string[] Words)> ProjectTypes { get; set; } =
    [
        ("site", ["сайт", "лендинг", "landing", "интернет-магазин", "wordpress", "tilda", "тильда", "битрикс", "верстк", "одностраничник"]),
        ("telegram_bot", ["телеграм бот", "telegram бот", "телеграм-бот", "telegram-бот", "бот для телеграм", "бот в телеграм", "чат-бот", "чат бот", "tg бот", "aiogram", "бота"]),
        ("mobile_app", ["мобильное приложение", "мобильного приложения", "приложение для ios", "приложение для android", "flutter", "react native", "android", "ios"]),
        ("web_app", ["веб-приложение", "веб приложение", "web-приложение", "веб-сервис", "личный кабинет", "react", "vue", "django", "fastapi", "nextjs", "next js"]),
        ("automation", ["автоматизац", "парсер", "парсинг", "скрипт", "интеграц", "api", "google таблиц", "google sheets", "webhook", "n8n", "zapier"]),
        ("crm", ["crm", "срм", "amocrm", "битрикс24", "учетн систем", "внутренн систем", "erp"]),
        ("support", ["доработ", "исправить", "баг", "модерниз", "редизайн", "поддержка сайта", "перенести сайт"]),
    ];

    public string[] Intent { get; set; } =
    [
        "ищу", "ищем", "нужен", "нужна", "нужно", "требуется", "требуются", "кто может", "кто сделает", "кто возьмется",
        "хочу заказать", "заказать", "техническое задание", "тз",
    ];

    public string[] Offers { get; set; } =
    [
        "сделаю", "разработаю", "создам", "предлагаю услуги", "мои услуги", "ищу заказы", "ищу заказчиков", "беру заказы",
        "возьму заказ", "мое портфолио", "моё портфолио",
    ];

    public string[] StopWords { get; set; } =
    [
        "в штат", "оклад", "полный рабочий день", "резюме", "стажировк", "казино", "ставки на спорт", "букмекер", "закладк",
        "обнал", "накрутк", "без оплаты", "за отзыв", "даркнет", "взлом", "18+",
    ];

    public string[] Spam { get; set; } =
    [
        "заработок от", "доход от", "пассивный доход", "подписывайся", "розыгрыш", "промокод", "арбитраж трафика", "реферал",
    ];

    public string[] Urgency { get; set; } = ["срочно", "сегодня", "до завтра", "в кратчайшие сроки", "asap", "горит"];

    public string[] Business { get; set; } =
    [
        "компания", "компании", "ооо", "бизнес", "магазин", "студия", "салон", "клиника", "ресторан", "школа", "агентство", "производство",
    ];

    public string[] Hard { get; set; } =
    [
        "1с", "1c", "unity", "unreal", "микроконтроллер", "arduino", "embedded", "fpga", "plc", "solidity", "смарт-контракт", "в офис",
    ];
}

/// <summary>Правила оценки организаций.</summary>
public sealed class OrganizationScoring
{
    public double High { get; set; } = 60;
    public double Medium { get; set; } = 40;
    public double Low { get; set; } = 25;
    public double Base { get; set; } = 10;

    public List<(string Key, string Label, double Weight)> Signals { get; set; } =
    [
        ("no_website", "сайт не указан в источнике", 35),
        ("site_unreachable", "сайт недоступен", 30),
        ("no_https", "нет HTTPS", 10),
        ("no_viewport", "нет мобильной вёрстки (meta viewport)", 15),
        ("outdated", "сайт выглядит устаревшим", 15),
        ("no_booking", "нет онлайн-записи (предположение о потребности)", 10),
    ];

    public double HasContacts { get; set; } = 10;
    public double NoContactsPenalty { get; set; } = 20;
}

/// <summary>Собственная оценка без AI: ключевые слова и веса. Это оценка системы, а не факт.</summary>
public sealed class Scorer
{
    private readonly ConcurrentDictionary<string, Regex> _cache = new();

    public Scorer(OrderScoring? orders = null, OrganizationScoring? organizations = null)
    {
        Orders = orders ?? new OrderScoring();
        Organizations = organizations ?? new OrganizationScoring();
    }

    public OrderScoring Orders { get; }
    public OrganizationScoring Organizations { get; }

    internal static Relevance Classify(double score, double high, double medium, double low) =>
        score >= high ? Relevance.High : score >= medium ? Relevance.Medium : score >= low ? Relevance.Low : Relevance.Rejected;

    /// <summary>Слова, найденные в нормализованном тексте (по началу слова).</summary>
    internal List<string> ContainsAny(string text, IEnumerable<string> words)
    {
        var found = new List<string>();
        foreach (var w in words)
        {
            var wn = SearchText.Normalize(w);
            if (wn.Length == 0)
            {
                continue;
            }

            var rx = _cache.GetOrAdd(wn, k => new Regex(@"(?<!\w)" + Regex.Escape(k), RegexOptions.CultureInvariant));
            if (rx.IsMatch(text))
            {
                found.Add(w);
            }
        }

        return found;
    }

    /// <summary>«%.0f» как в Swift/C: округление к чётному только для точных половин.</summary>
    private static string Pts(double v) => Math.Round(v, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);

    private static double Round1(double v) => Math.Round(v * 10, MidpointRounding.AwayFromZero) / 10;

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }

        return n;
    }

    public void ScoreOrder(LeadData lead, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var o = Orders;
        var text = SearchText.Normalize($"{lead.Title} {lead.Description}");
        var reasons = new List<string>();
        var total = 0.0;

        var stop = ContainsAny(text, o.StopWords);
        if (stop.Count > 0)
        {
            lead.Score = 0;
            lead.Relevance = Relevance.Rejected;
            lead.ScoreReasons = [$"стоп-слова: {string.Join(", ", stop.Take(3))}"];
            return;
        }

        // 1. Тематика разработки и тип проекта
        string? bestType = null;
        var bestHits = 0;
        foreach (var (type, words) in o.ProjectTypes)
        {
            var hits = ContainsAny(text, words).Count;
            if (hits > bestHits)
            {
                bestType = type;
                bestHits = hits;
            }
        }

        if (bestType is null || bestHits == 0)
        {
            lead.Score = 0;
            lead.Relevance = Relevance.Rejected;
            lead.ScoreReasons = ["нет признаков разработки"];
            return;
        }

        lead.ProjectType ??= bestType;
        var topic = o.WTopic * Math.Min(1.0, 0.6 + (0.2 * bestHits));
        total += topic;
        reasons.Add($"тематика «{bestType}» (+{Pts(topic)})");

        // 2. Конкретный запрос
        var intent = ContainsAny(text, o.Intent);
        if (intent.Count > 0)
        {
            total += o.WIntent;
            reasons.Add($"явный запрос: «{intent[0]}» (+{Pts(o.WIntent)})");
        }

        if (ContainsAny(text, o.Offers).Count > 0)
        {
            total -= o.WOfferPenalty;
            reasons.Add("похоже на предложение услуг, а не заказ");
        }

        // 3. Бюджет
        if (lead.Budget is null)
        {
            var b = Budget.Parse($"{lead.Title} {lead.Description}");
            if (b is { } parsed)
            {
                lead.Budget = parsed.Value;
                lead.Currency = parsed.Currency;
            }
        }

        if (lead.Budget is > 0)
        {
            var budget = lead.Budget.Value;
            total += o.WBudget;
            reasons.Add($"указан бюджет (+{Pts(o.WBudget)})");
            if (lead.Currency == "RUB" && o.MinBudgetRub > 0 && budget < o.MinBudgetRub)
            {
                total -= o.WLowBudgetPenalty;
                reasons.Add($"бюджет ниже {Pts(o.MinBudgetRub)} ₽");
            }
        }

        // 4. Срочность
        if (ContainsAny(text, o.Urgency).Count > 0)
        {
            total += o.WUrgency;
            reasons.Add("срочно");
        }

        // 5. Полнота описания
        var words5 = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words5 >= 40)
        {
            total += o.WCompleteness;
            reasons.Add("подробное описание");
        }
        else if (words5 < 8)
        {
            total -= o.WTooShortPenalty;
            reasons.Add("слишком короткое описание");
        }

        // 6. Тип заказчика
        if (ContainsAny(text, o.Business).Count > 0)
        {
            total += o.WCustomerType;
            reasons.Add("заказчик — бизнес");
        }

        // 7. Выполнимость силами одного разработчика
        var hard = ContainsAny(text, o.Hard);
        if (hard.Count > 0)
        {
            total -= o.WFeasibilityPenalty;
            reasons.Add($"сложно выполнить удалённо/в одиночку: {hard[0]}");
        }
        else
        {
            total += o.WFeasibility;
        }

        // 8. Спам
        var links = Count(lead.Description, "http://") + Count(lead.Description, "https://");
        if (ContainsAny(text, o.Spam).Count > 0 || links > 5)
        {
            total -= o.WSpamPenalty;
            reasons.Add("признаки спама");
        }

        // 9. Свежесть
        if (lead.PublishedAt is { } published)
        {
            var age = (current - published).TotalSeconds;
            if (age > o.MaxAgeDays * 86400)
            {
                lead.Score = 0;
                lead.Relevance = Relevance.Rejected;
                lead.ScoreReasons = [.. reasons, "устарело"];
                return;
            }

            if (age <= 86400)
            {
                total += o.WFreshness;
                reasons.Add("свежая публикация");
            }
        }

        lead.Score = Round1(Math.Max(0, Math.Min(100, total)));
        lead.Relevance = Classify(lead.Score, o.High, o.Medium, o.Low);
        lead.ScoreReasons = reasons;
    }

    /// <summary>Организация: опирается только на проверенные сигналы из проверки сайта.</summary>
    public void ScoreOrganization(LeadData lead, double? categoryWeight, IReadOnlyDictionary<string, bool> signals)
    {
        var g = Organizations;
        var reasons = new List<string>();
        var total = categoryWeight ?? g.Base;
        reasons.Add($"категория «{lead.Category ?? "—"}» (+{Pts(total)})");
        foreach (var (key, label, w) in g.Signals)
        {
            if (signals.TryGetValue(key, out var on) && on)
            {
                total += w;
                reasons.Add($"{label} (+{Pts(w)})");
            }
        }

        if (!string.IsNullOrEmpty(lead.Phone) || lead.Contacts.Count > 0)
        {
            total += g.HasContacts;
            reasons.Add("есть публичный контакт");
        }
        else
        {
            total -= g.NoContactsPenalty;
            reasons.Add("нет контактов");
        }

        lead.Score = Round1(Math.Max(0, Math.Min(100, total)));
        lead.Relevance = Classify(lead.Score, g.High, g.Medium, g.Low);
        lead.ScoreReasons = reasons;
    }
}

/// <summary>Бюджет в тексте заказа.</summary>
public static partial class Budget
{
    private const string Num = @"(\d[\d\s.,]*\d|\d)";

    private static readonly Regex WithCurrency = new(
        Num + @"\s*(тыс\.?|тысяч|к|k|млн)?\s*(руб(?:лей|\.|ля)?|р\.|₽|rub|\$|usd|долл\w*|€|eur)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WithPrefix = new(
        @"бюджет[:\s—-]*(?:до|от|около)?\s*" + Num + @"\s*(тыс\.?|тысяч|к|k|млн)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^\d{1,3}([.,]\d{3})+$")]
    private static partial Regex ThousandsRx();

    internal static double? Number(string raw, string? mult)
    {
        var s = new string(raw.Replace(" ", "").Replace(" ", "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        s = ThousandsRx().IsMatch(s) ? s.Replace(".", "").Replace(",", "") : s.Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var m = (mult ?? "").ToLowerInvariant().TrimEnd('.');
        if (m is "тыс" or "тысяч" or "к" or "k")
        {
            value *= 1000;
        }
        else if (m == "млн")
        {
            value *= 1_000_000;
        }

        return value;
    }

    public static (double Value, string Currency)? Parse(string text)
    {
        var m = WithCurrency.Match(text);
        if (m.Success)
        {
            var raw = m.Groups[1].Value;
            var cur = m.Groups[3].Value.ToLowerInvariant();
            string currency;
            switch (cur)
            {
                case "$" or "usd":
                    currency = "USD";
                    break;
                case "€" or "eur":
                    currency = "EUR";
                    break;
                default:
                    currency = cur.StartsWith("долл", StringComparison.Ordinal) ? "USD" : "RUB";
                    break;
            }

            var mult = m.Groups[2].Success ? m.Groups[2].Value : null;
            if (Number(raw, mult) is { } v && v > 0 && (v >= 100 || currency != "RUB"))
            {
                return (v, currency);
            }
        }

        var p = WithPrefix.Match(text);
        if (p.Success)
        {
            var mult = p.Groups[2].Success ? p.Groups[2].Value : null;
            if (Number(p.Groups[1].Value, mult) is { } v && v >= 100)
            {
                return (v, "RUB");
            }
        }

        return null;
    }
}
