using System.Globalization;

namespace Customers.Core;

public sealed record BackupFile(string Path, DateTimeOffset CreatedAt, long Size);

/// <summary>Резервные копии базы: ежедневные автоматические и по кнопке, хранится заданное число последних.</summary>
public sealed class BackupManager(LeadStore store, string directory, int keep = 14)
{
    public string Directory { get; } = directory;

    public int Keep { get; set; } = keep;

    public List<BackupFile> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(Directory, "Customers-*.db")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFile(f.FullName, f.LastWriteTimeUtc, f.Length))
            .ToList();
    }

    /// <summary>Создаёт копию и удаляет старые сверх лимита. Возвращает путь к новой копии.</summary>
    public string BackupNow(DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var t = stamp.ToLocalTime();
        var path = System.IO.Path.Combine(Directory, $"Customers-{t.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}.db");
        store.Backup(path);
        foreach (var old in List().Skip(Math.Max(Keep, 1)))
        {
            LeadStore.TryDelete(old.Path);
        }

        store.SetValue("last_backup_at", ISODate.Format(stamp)!);
        return path;
    }

    /// <summary>Копия не чаще раза в сутки (если с прошлой прошло больше 20 часов).</summary>
    public string? DailyIfNeeded(DateTimeOffset? now = null)
    {
        var last = ISODate.Parse(store.Value("last_backup_at")) ?? DateTimeOffset.MinValue;
        return ((now ?? DateTimeOffset.UtcNow) - last).TotalHours > 20 ? BackupNow(now) : null;
    }
}
