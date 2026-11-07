using System.Globalization;
using System.Text;

namespace Customers.Presentation;

/// <summary>
/// Журнал приложения в папке logs: файл на день, хранится 14 дней. Секреты в журнал не пишутся:
/// вызывающий код передаёт только сообщения, без токенов и паролей, а адреса очищаются от параметров запроса.
/// </summary>
public sealed class FileLog
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;

    public FileLog(string directory, Func<DateTimeOffset>? now = null)
    {
        _dir = directory;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string Directory => _dir;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                var t = _now();
                var file = Path.Combine(_dir, $"app-{t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
                File.AppendAllText(file, $"{t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {Scrub(message)}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Журнал не должен ронять приложение.
        }
    }

    /// <summary>Удаляет параметры запроса из адресов (в них бывают ключи доступа).</summary>
    public static string Scrub(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"(https?://[^\s?#]+)[?#]\S*", "$1?…", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    public void Prune(int keepDays = 14)
    {
        try
        {
            if (!System.IO.Directory.Exists(_dir))
            {
                return;
            }

            var limit = _now().AddDays(-keepDays).UtcDateTime;
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(f) < limit)
                {
                    File.Delete(f);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // MARK: Отчёты об аварийном завершении

    private string CrashDir => Path.Combine(_dir, "crashes");

    /// <summary>Сохраняет отчёт о необработанной ошибке. При следующем запуске приложение предложит его посмотреть.</summary>
    public string? WriteCrash(Exception e, string context)
    {
        try
        {
            System.IO.Directory.CreateDirectory(CrashDir);
            var t = _now();
            var path = Path.Combine(CrashDir, $"crash-{t.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");
            var text = new StringBuilder()
                .AppendLine($"Customers {AppBuild.Version}, {Environment.OSVersion}, .NET {Environment.Version}")
                .AppendLine(t.ToString("O", CultureInfo.InvariantCulture))
                .AppendLine($"Где: {context}")
                .AppendLine(Scrub(e.ToString()))
                .ToString();
            File.WriteAllText(path, text, Encoding.UTF8);
            Error("Необработанная ошибка (" + context + ")", e);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> PendingCrashReports() =>
        System.IO.Directory.Exists(CrashDir) ? [.. System.IO.Directory.EnumerateFiles(CrashDir, "crash-*.txt").Order(StringComparer.Ordinal)] : [];

    public void DismissCrashReports()
    {
        foreach (var f in PendingCrashReports())
        {
            try
            {
                var archived = Path.ChangeExtension(f, ".seen");
                File.Move(f, archived, overwrite: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

public static class AppBuild
{
    public static string Version { get; } =
        typeof(AppBuild).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}
