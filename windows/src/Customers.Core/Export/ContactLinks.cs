using System.Text;

namespace Customers.Core;

/// <summary>Письмо-черновик: тема отдельно от текста.</summary>
public sealed record EmailDraft(string Subject, string Body);

/// <summary>
/// Ссылки для звонка и письма из карточки лида: tel: и mailto:. Правила те же, что у ContactLinks в macOS-версии;
/// систему, которая откроет ссылку (Связь с телефоном, почтовое приложение), выбирает Windows.
/// </summary>
public static class ContactLinks
{
    /// <summary>Номер в виде «+» и цифры. Добавочный отбрасывается, «8 XXX…» (11 цифр) превращается в «+7 XXX…». Меньше 5 цифр — не номер.</summary>
    public static string? NormalizedPhone(string raw)
    {
        var text = raw.ToLowerInvariant();
        foreach (var marker in new[] { "доб", "ext", "вн.", "#", ",", ";" })
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0)
            {
                text = text[..i];
            }
        }

        var trimmed = text.Trim();
        var plus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(c => c is >= '0' and <= '9').ToArray());
        if (digits.Length is < 5 or > 15)
        {
            return null;
        }

        if (!plus && digits.Length == 11 && digits[0] == '8')
        {
            return "+7" + digits[1..];
        }

        if (!plus && digits.Length == 11 && digits[0] == '7')
        {
            return "+" + digits;
        }

        return (plus ? "+" : "") + digits;
    }

    public static Uri? TelUri(string phone) => NormalizedPhone(phone) is { } p ? new Uri("tel:" + p) : null;

    /// <summary>Простая проверка адреса: одна «@», непустые части, точка в домене, без пробелов и управляющих символов (перевод строки открыл бы подстановку заголовков письма).</summary>
    public static bool IsEmail(string raw)
    {
        var s = raw.Trim(' ', '\t');
        if (s.Length is 0 or > 254 || s.Any(c => char.IsWhiteSpace(c) || "<>()[]\\,;:\"".Contains(c) || c < 0x20 || c == 0x7F))
        {
            return false;
        }

        var parts = s.Split('@');
        if (parts.Length != 2 || parts[0].Length is 0 or > 64)
        {
            return false;
        }

        var domain = parts[1];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') && !domain.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>mailto:адрес?subject=…&amp;body=… с кодированием по RFC 6068 (пробел — %20, перевод строки — %0D%0A).</summary>
    public static Uri? MailtoUri(string address, string subject = "", string body = "")
    {
        var to = address.Trim(' ', '\t');
        if (!IsEmail(to))
        {
            return null;
        }

        var query = new List<string>();
        if (subject.Length > 0)
        {
            query.Add("subject=" + Encode(subject, ""));
        }

        if (body.Length > 0)
        {
            query.Add("body=" + Encode(NormalizeNewlines(body), ""));
        }

        var text = "mailto:" + Encode(to, "@!$'*+") + (query.Count == 0 ? "" : "?" + string.Join('&', query));
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>Только латиница, цифры и «-._~» (и допустимые дополнительно знаки) остаются как есть; остальное, включая кириллицу и «&amp;=+#», кодируется.</summary>
    private static string Encode(string s, string extraAllowed)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-._~".Contains(c) || extraAllowed.Contains(c)))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>Черновик письма: первая строка «Тема: …» шаблона становится темой.</summary>
    public static EmailDraft EmailFor(LeadData lead, string signature) => Split(DraftWriter.Draft(lead, DraftWriter.Channel.Email, signature));

    /// <summary>Делит текст «Тема: …\n\nТекст» на тему и текст. Без строки темы тема пустая.</summary>
    public static EmailDraft Split(string text)
    {
        const string prefix = "Тема:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return new EmailDraft("", text);
        }

        var lines = text.Split('\n', 2);
        var subject = lines[0][prefix.Length..].Trim(' ', '\t');
        var body = lines.Length > 1 ? lines[1].Trim('\n', '\r') : "";
        return new EmailDraft(subject, body);
    }
}
