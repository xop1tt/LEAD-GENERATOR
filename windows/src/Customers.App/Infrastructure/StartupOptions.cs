namespace Customers.Ui;

/// <summary>Параметры командной строки. Для обычного запуска их не нужно: они нужны проверкам и сопровождению.</summary>
public sealed class StartupOptions
{
    /// <summary>--smoke &lt;папка&gt;: открыть окно, пройти по всем разделам, сохранить снимки и результат в папку и выйти (проверка в CI).</summary>
    public string? SmokeDirectory { get; private init; }

    /// <summary>--data &lt;папка&gt;: хранить данные в другой папке (по умолчанию %LOCALAPPDATA%\Customers).</summary>
    public string? DataRoot { get; private init; }

    /// <summary>Ссылка leadgenerator://… из письма, если приложение запущено по ней.</summary>
    public string? Link { get; private init; }

    public bool Smoke => SmokeDirectory is not null;

    public static StartupOptions Parse(string[] args)
    {
        string? smoke = null;
        string? data = null;
        string? link = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--smoke" when i + 1 < args.Length:
                    smoke = args[++i];
                    break;
                case "--data" when i + 1 < args.Length:
                    data = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("leadgenerator:", StringComparison.OrdinalIgnoreCase))
                    {
                        link = args[i];
                    }

                    break;
            }
        }

        return new StartupOptions { SmokeDirectory = smoke, DataRoot = data, Link = link };
    }
}
