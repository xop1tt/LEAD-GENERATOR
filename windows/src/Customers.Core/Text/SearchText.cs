using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Customers.Core;

/// <summary>Работа с текстом: нормализация, HTML, контакты, идентификаторы и отпечатки. Результаты совпадают с macOS-версией.</summary>
public static partial class SearchText
{
    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex PunctRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRx();

    /// <summary>Как <c>normalize_text</c> в Python: регистр, «ё», без ссылок и пунктуации.</summary>
    public static string Normalize(string text)
    {
        var t = text.ToLowerInvariant().Replace('ё', 'е');
        t = UrlRx().Replace(t, " ");
        t = PunctRx().Replace(t, " ");
        t = SpaceRx().Replace(t, " ");
        return t.Trim();
    }

    /// <summary>Быстрый предфильтр по ключевым словам (подстрока). Пустой список пропускает всё.</summary>
    public static bool Prefilter(string text, IEnumerable<string> keywords)
    {
        var words = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        if (words.Count == 0)
        {
            return true;
        }

        var low = text.ToLowerInvariant().Replace('ё', 'е');
        return words.Any(w => low.Contains(w.ToLowerInvariant().Replace('ё', 'е'), StringComparison.Ordinal));
    }

    // MARK: HTML

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRx();

    [GeneratedRegex(@"<br\s*/?>|</p\s*>|</div\s*>|</li\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRx();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRx();

    [GeneratedRegex(@"&(#[xX][0-9a-fA-F]+|#[0-9]+|[a-zA-Z]+);")]
    private static partial Regex EntityRx();

    /// <summary>HTML в текст. <paramref name="lines"/> — переносы строк сохраняются (посты Telegram), иначе всё в одну строку (RSS).</summary>
    public static string HtmlToText(string html, bool lines = false)
    {
        var s = ScriptRx().Replace(html, " ");
        s = BrRx().Replace(s, lines ? "\n" : " ");
        s = TagRx().Replace(s, lines ? "" : " ");
        s = DecodeEntities(s);
        if (lines)
        {
            return string.Join('\n', s.Split('\n')
                .Select(l => SpaceRx().Replace(l, " ").Trim())
                .Where(l => l.Length > 0));
        }

        return SpaceRx().Replace(s, " ").Trim();
    }

    private static readonly Dictionary<string, string> Named = new() {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = " ",
        ["laquo"] = "«",
        ["raquo"] = "»",
        ["mdash"] = "—",
        ["ndash"] = "–",
        ["hellip"] = "…",
        ["copy"] = "©",
        ["reg"] = "®",
        ["trade"] = "™",
        ["bull"] = "•",
        ["rsquo"] = "’",
        ["lsquo"] = "‘",
        ["rdquo"] = "”",
        ["ldquo"] = "“",
        ["bdquo"] = "„",
        ["times"] = "×",
        ["deg"] = "°",
    };

    public static string DecodeEntities(string s)
    {
        if (!s.Contains('&'))
        {
            return s;
        }

        return EntityRx().Replace(s, m =>
        {
            var body = m.Groups[1].Value;
            string? rep = null;
            if (body.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(body.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                {
                    rep = FromCodePoint(cp);
                }
            }
            else if (body.StartsWith('#'))
            {
                if (int.TryParse(body.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var cp))
                {
                    rep = FromCodePoint(cp);
                }
            }
            else
            {
                Named.TryGetValue(body.ToLowerInvariant(), out rep);
            }

            return rep ?? m.Value;
        });
    }

    private static string? FromCodePoint(int cp) =>
        cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(cp) : null;

    // MARK: Контакты

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"(?:\+7|8)[\s(-]*\d{3}[\s)-]*\d{3}[\s-]*\d{2}[\s-]*\d{2}")]
    private static partial Regex PhoneRx();

    [GeneratedRegex(@"(?<![\w/])@([A-Za-z][A-Za-z0-9_]{4,31})\b|t\.me/([A-Za-z][A-Za-z0-9_]{4,31})")]
    private static partial Regex TgRx();

    /// <summary>Только контакты, опубликованные в самом тексте. Ничего не додумываем.</summary>
    public static Dictionary<string, string> Contacts(string text)
    {
        var output = new Dictionary<string, string>();
        var email = EmailRx().Match(text);
        if (email.Success)
        {
            output["email"] = email.Value.TrimEnd('.');
        }

        var phone = PhoneRx().Match(text);
        if (phone.Success)
        {
            output["phone"] = new string(phone.Value.Where(c => char.IsNumber(c) || c == '+').ToArray());
        }

        var tg = TgRx().Match(text);
        if (tg.Success)
        {
            var name = tg.Groups[1].Success ? tg.Groups[1].Value : tg.Groups[2].Value;
            output["telegram"] = "@" + name;
        }

        return output;
    }

    // MARK: Идентификаторы

    /// <summary>Первые 24 символа SHA-256 в hex — так же, как в Python- и macOS-версиях.</summary>
    public static string ShortHash(string s)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(digest)[..24];
    }

    public static string LeadId(string source, string externalId) => ShortHash($"{source}|{externalId}");

    [GeneratedRegex(@"^(https?://)?(www\.)?")]
    private static partial Regex WwwRx();

    /// <summary>Первые <paramref name="count"/> символов (текстовых элементов, как <c>String.prefix</c> в Swift).</summary>
    internal static string Prefix(string s, int count)
    {
        if (s.Length <= count)
        {
            return s;
        }

        var e = StringInfo.GetTextElementEnumerator(s);
        var n = 0;
        var end = 0;
        while (n < count && e.MoveNext())
        {
            n++;
            end = e.ElementIndex + ((string)e.Current).Length;
        }

        return s[..end];
    }

    /// <summary>Отпечаток содержимого: одна и та же заявка или организация из разных мест.</summary>
    public static string Fingerprint(LeadData d)
    {
        if (d.IsOrganization)
        {
            var domain = "";
            if (!string.IsNullOrEmpty(d.Website))
            {
                domain = WwwRx().Replace(d.Website.ToLowerInvariant(), "").Split('/')[0];
            }

            var digits = new string((d.Phone ?? "").Where(char.IsNumber).ToArray());
            var phone = digits.Length > 10 ? digits[^10..] : digits;
            var key = domain.Length > 0 ? domain
                : phone.Length > 0 ? phone
                : $"{Normalize(d.Title)}|{Normalize(d.Address ?? "")}";
            return ShortHash("org|" + key);
        }

        var body = Prefix(Normalize($"{d.Title} {d.Description}"), 400);
        return ShortHash("order|" + body);
    }
}
