using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Customers.Ui;

/// <summary>Тема, акцентный цвет и материал окна (Mica / Mica Alt / Acrylic) с запасными вариантами для Windows 10.</summary>
internal static class Appearance
{
    public static bool MicaSupported => TrySupported(MicaController.IsSupported);

    public static bool AcrylicSupported => TrySupported(DesktopAcrylicController.IsSupported);

    private static bool TrySupported(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or TypeLoadException)
        {
            return false;
        }
    }

    public static ElementTheme ToElementTheme(ThemeChoice theme) => theme switch {
        ThemeChoice.Light => ElementTheme.Light,
        ThemeChoice.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Материал окна по выбору пользователя. Mica есть только в Windows 11: на Windows 10 берётся акрил, а если и его нет — обычный фон.</summary>
    public static SystemBackdrop? CreateBackdrop(BackdropChoice choice)
    {
        switch (choice)
        {
            case BackdropChoice.None:
                return null;
            case BackdropChoice.Acrylic:
                return AcrylicSupported ? new DesktopAcrylicBackdrop() : MicaFallback();
            case BackdropChoice.MicaAlt when MicaSupported:
                return new MicaBackdrop { Kind = MicaKind.BaseAlt };
            case BackdropChoice.Mica when MicaSupported:
                return new MicaBackdrop();
            default:
                return AcrylicSupported ? new DesktopAcrylicBackdrop() : null;
        }
    }

    private static SystemBackdrop? MicaFallback() => MicaSupported ? new MicaBackdrop() : null;

    /// <summary>Что именно получится при выбранном материале (для подсказки в настройках).</summary>
    public static string BackdropNote(BackdropChoice choice) => choice switch {
        BackdropChoice.None => "Сплошной фон без прозрачности.",
        BackdropChoice.Acrylic when !AcrylicSupported => "Акрил недоступен в этой системе, используется обычный фон.",
        BackdropChoice.Acrylic => "Полупрозрачный акрил.",
        _ when MicaSupported => "Mica — материал Windows 11, подстраивается под обои.",
        _ when AcrylicSupported => "Mica есть только в Windows 11. В этой системе вместо него используется акрил.",
        _ => "Прозрачные материалы недоступны в этой системе, используется обычный фон.",
    };

    public static Color? ParseColor(string? hex)
    {
        var normalized = DevicePreferences.NormalizeColor(hex);
        if (normalized is null)
        {
            return null;
        }

        return Color.FromArgb(255, Convert.ToByte(normalized.Substring(1, 2), 16), Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    private static Color Mix(Color c, Color other, double amount) => Color.FromArgb(255,
        (byte)Math.Round(c.R + ((other.R - c.R) * amount)),
        (byte)Math.Round(c.G + ((other.G - c.G) * amount)),
        (byte)Math.Round(c.B + ((other.B - c.B) * amount)));

    /// <summary>
    /// Подставляет свой акцентный цвет в ресурсы приложения. Делается до создания окна: уже показанные стандартные
    /// элементы подхватывают новый цвет только после перезапуска (так устроен WinUI), поэтому в настройках есть кнопка перезапуска.
    /// </summary>
    public static void ApplyAccent(string? hex)
    {
        if (ParseColor(hex) is not { } accent)
        {
            return;
        }

        var r = Application.Current.Resources;
        r["SystemAccentColor"] = accent;
        for (var i = 1; i <= 3; i++)
        {
            r["SystemAccentColorLight" + i] = Mix(accent, Colors.White, 0.18 * i);
            r["SystemAccentColorDark" + i] = Mix(accent, Colors.Black, 0.18 * i);
        }
    }

    /// <summary>Цвета кнопок свернуть/развернуть/закрыть под текущую тему (окно рисует их само, а тему задаём мы).</summary>
    public static void StyleTitleBar(AppWindow window, bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = window.TitleBar;
        var fore = dark ? Colors.White : Colors.Black;
        var hover = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = fore;
        bar.ButtonInactiveForegroundColor = Color.FromArgb(160, fore.R, fore.G, fore.B);
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = fore;
        bar.ButtonPressedBackgroundColor = Color.FromArgb(60, fore.R, fore.G, fore.B);
        bar.ButtonPressedForegroundColor = fore;
    }
}
