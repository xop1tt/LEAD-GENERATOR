using System.Globalization;

namespace Customers.Core;

/// <summary>
/// Даты в формате Python <c>datetime.isoformat()</c> и Swift <c>ISO8601DateFormatter</c>:
/// «2026-11-05T09:42:00Z», «2026-11-05T09:42:00+00:00», иногда с микросекундами или без пояса.
/// Тот же формат читает и пишет macOS-версия, поэтому общие данные не расходятся.
/// </summary>
public static class ISODate
{
    public static DateTimeOffset? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        // Без часового пояса Python-версия писала UTC.
        var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, styles, out var d))
        {
            return d;
        }

        return null;
    }

    /// <summary>«2026-11-05T09:42:00Z» — секунды, всегда UTC.</summary>
    public static string? Format(DateTimeOffset? d) =>
        d is null ? null : d.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>С миллисекундами — для журнала синхронизации: два изменения в одну секунду различаются.</summary>
    public static string FormatPrecise(DateTimeOffset d) =>
        d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Now() => Format(DateTimeOffset.UtcNow)!;
}
