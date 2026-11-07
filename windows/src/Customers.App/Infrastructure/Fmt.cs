using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Customers.Ui;

/// <summary>Мелкие функции показа значений: используются в XAML через {x:Bind local:Fmt.…()} и в коде.</summary>
public static class Fmt
{
    public static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility NotVis(bool hidden) => hidden ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static string Size(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} Б" : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[i]}");
    }

    public static string Date(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) : "—";

    /// <summary>«5 мин назад», «вчера», дата — как в macOS-версии.</summary>
    public static string Ago(DateTimeOffset? t)
    {
        if (t is not { } v)
        {
            return "никогда";
        }

        var d = DateTimeOffset.UtcNow - v;
        if (d.TotalSeconds < 60)
        {
            return "только что";
        }

        if (d.TotalMinutes < 60)
        {
            return $"{(int)d.TotalMinutes} мин назад";
        }

        if (d.TotalHours < 24)
        {
            return $"{(int)d.TotalHours} ч назад";
        }

        return Date(v);
    }

    /// <summary>Склонение: Plural(5, "лид", "лида", "лидов").</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        var m100 = Math.Abs(n) % 100;
        var m10 = m100 % 10;
        var word = m100 is >= 11 and <= 14 ? many : m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }

    public static string Leads(int n) => Plural(n, "лид", "лида", "лидов");

    // Цвета оценки: белый текст на сплошном фоне читается и в светлой, и в тёмной теме.
    private static readonly SolidColorBrush High = new(Color.FromArgb(255, 0x10, 0x7C, 0x10));
    private static readonly SolidColorBrush Medium = new(Color.FromArgb(255, 0x9A, 0x5B, 0x00));
    private static readonly SolidColorBrush Low = new(Color.FromArgb(255, 0x5F, 0x6B, 0x7A));
    private static readonly SolidColorBrush Rejected = new(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));

    public static Brush LevelBrush(string level) => level switch {
        "high" => High,
        "medium" => Medium,
        "low" => Low,
        _ => Rejected,
    };

    public static Brush White { get; } = new SolidColorBrush(Colors.White);
}
