namespace Customers.Core;

/// <summary>Нормализация значений для исключений и сравнения (те же правила, что в macOS-версии).</summary>
public static class Normalize
{
    public static string Domain(string raw)
    {
        var s = raw.ToLowerInvariant().Trim();
        foreach (var p in new[] { "https://", "http://" })
        {
            if (s.StartsWith(p, StringComparison.Ordinal))
            {
                s = s[p.Length..];
            }
        }

        if (s.StartsWith("www.", StringComparison.Ordinal))
        {
            s = s[4..];
        }

        var cut = s.IndexOfAny(['/', '?', '#']);
        return cut >= 0 ? s[..cut] : s;
    }

    /// <summary>Последние 10 цифр: +7 (843) 212-48-90 и 8 843 2124890 совпадают.</summary>
    public static string Phone(string raw)
    {
        var digits = new string(raw.Where(char.IsNumber).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    public static string Email(string raw) => raw.ToLowerInvariant().Trim();

    /// <summary>Название без кавычек, регистра, «ё» и лишних пробелов.</summary>
    public static string Name(string raw)
    {
        var lowered = raw.ToLowerInvariant().Replace('ё', 'е');
        var cleaned = new string(lowered.Select(c => char.IsLetter(c) || char.IsNumber(c) ? c : ' ').ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string Value(string raw, ExclusionKind kind) => kind switch {
        ExclusionKind.Domain => Domain(raw),
        ExclusionKind.Phone => Phone(raw),
        ExclusionKind.Email => Email(raw),
        _ => Name(raw),
    };
}
