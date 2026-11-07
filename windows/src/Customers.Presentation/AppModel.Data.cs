using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Presentation;

public sealed partial class AppModel
{
    // MARK: Экспорт и импорт

    public void Export(IReadOnlyCollection<LeadItem> items, ExportFormat format, IReadOnlyCollection<ExportField> fields, string path)
    {
        try
        {
            AtomicFile.WriteAllBytes(path, Exporter.Export(items, format, fields));
            LastMessage = $"Экспортировано лидов: {items.Count}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = "Экспорт не удался: " + e.Message;
        }
    }

    /// <summary>Импорт лидов из CSV или JSON, выгруженного этим приложением (на любой платформе).</summary>
    public ImportResult? ImportLeads(string path)
    {
        if (Store is not { } store)
        {
            return null;
        }

        try
        {
            var result = Importer.Import(store, File.ReadAllBytes(path));
            Reload();
            if (result.Added > 0)
            {
                OnLocalChange?.Invoke();
            }

            LastMessage = $"Импорт: добавлено {result.Added}, дубликатов {result.Duplicates}, пропущено {result.Skipped}";
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DatabaseException)
        {
            LastError = "Не удалось прочитать файл: " + e.Message;
            return null;
        }
    }

    public void ImportExclusionsFile(string path)
    {
        try
        {
            ImportExclusions(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = "Не удалось прочитать файл: " + e.Message;
        }
    }

    // MARK: Резервные копии

    public IReadOnlyList<BackupFile> BackupFiles => Backups?.List() ?? [];

    public string? BackupNow()
    {
        if (Backups is null)
        {
            return null;
        }

        try
        {
            var path = Backups.BackupNow();
            LastMessage = "Резервная копия сохранена";
            Raise(nameof(BackupFiles));
            return path;
        }
        catch (Exception e) when (e is DatabaseException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastError = "Резервная копия не создана: " + e.Message;
            return null;
        }
    }

    /// <summary>Копия не чаще раза в сутки и только если включена в настройках.</summary>
    public void DailyBackupIfNeeded()
    {
        if (!Prefs.AutoBackup || Backups is null)
        {
            return;
        }

        try
        {
            if (Backups.DailyIfNeeded() is not null)
            {
                _env.Log.Info("Создана ежедневная резервная копия");
            }
        }
        catch (Exception e) when (e is DatabaseException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _env.Log.Error("Ежедневная копия не создана", e);
        }
    }

    /// <summary>Проверяет файл и возвращает число лидов в нём (для подтверждения восстановления).</summary>
    public int ProbeBackup(string path) => Store?.ProbeBackup(path) ?? 0;

    public bool Restore(string path)
    {
        if (Store is not { } store)
        {
            return false;
        }

        if (Searching)
        {
            LastError = "Дождитесь окончания поиска или остановите его, затем восстановите базу.";
            return false;
        }

        try
        {
            store.Restore(path);
            Reload();
            ReloadSyncedSettings();
            OnLocalChange?.Invoke();
            LastMessage = "База восстановлена из копии. Предыдущая версия сохранена как before-restore.db";
            return true;
        }
        catch (Exception e) when (e is DatabaseException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastError = "Восстановление не удалось: " + e.Message;
            return false;
        }
    }

    public bool MoveDatabase(string directory)
    {
        if (Store is not { } store)
        {
            return false;
        }

        if (Searching)
        {
            LastError = "Дождитесь окончания поиска или остановите его, затем перенесите базу.";
            return false;
        }

        try
        {
            var path = store.MoveTo(directory);
            Prefs.DataPath = Path.GetDirectoryName(path);
            LastMessage = "База перенесена: " + path;
            Raise(nameof(DatabasePath));
            return true;
        }
        catch (Exception e) when (e is DatabaseException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastError = "Перенос не удался: " + e.Message;
            return false;
        }
    }

    /// <summary>Проверка целостности базы; проблемы пишутся в журнал.</summary>
    public IReadOnlyList<string> CheckIntegrity()
    {
        var problems = Store?.CheckIntegrity() ?? [];
        foreach (var p in problems)
        {
            _env.Log.Error("Целостность базы: " + p);
        }

        return problems;
    }

    /// <summary>
    /// Аварийное восстановление, когда база не открывается: испорченный файл откладывается рядом, на его место
    /// кладётся выбранная копия. После этого учётную запись нужно открыть заново.
    /// </summary>
    public static string RecoverDatabase(string databasePath, string backupFile)
    {
        var broken = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, $"leads.broken-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        if (File.Exists(databasePath))
        {
            File.Move(databasePath, broken);
        }

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            LeadStore.TryDelete(databasePath + suffix);
        }

        File.Copy(backupFile, databasePath, overwrite: true);
        return broken;
    }

    // MARK: Экспорт в CRM

    public string? CrmSecret(CrmKind kind) => _env.Secrets.Get(SecretName(kind.SecretName()));

    public void SetCrmSecret(CrmKind kind, string value)
    {
        if (value.Trim().Length == 0)
        {
            _env.Secrets.Delete(SecretName(kind.SecretName()));
        }
        else
        {
            _env.Secrets.Set(SecretName(kind.SecretName()), value.Trim());
        }

        Raise(nameof(CrmSecret));
    }

    /// <summary>Настройка Notion (ссылка на базу и сопоставление полей) хранится в базе пользователя.</summary>
    public NotionSetup? LoadNotionSetup()
    {
        if (Store?.Value("export.notion") is not { } json)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject o)
            {
                return null;
            }

            var mapping = new Dictionary<string, ExportField>();
            foreach (var (k, v) in o["mapping"] as JsonObject ?? [])
            {
                if (Enum.TryParse<ExportField>(v?.ToString(), out var f))
                {
                    mapping[k] = f;
                }
            }

            return new NotionSetup(o["database"]?.ToString() ?? "", mapping);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void SaveNotionSetup(NotionSetup setup)
    {
        var o = new JsonObject {
            ["database"] = setup.Database,
            ["mapping"] = new JsonObject(setup.Mapping.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value.ToString())))),
        };
        Store?.SetValue("export.notion", o.ToJsonString());
    }

    public ExportLedger Ledger(ICrmDestination d) => ExportLedger.Decode(Store?.Value(ExportLedger.StorageKey(d.Kind, d.Target)));

    /// <summary>Отправка лидов в CRM. Журнал отправленного сохраняется после каждого запуска, даже при ошибках.</summary>
    public async Task<CrmReport> ExportToCrmAsync(ICrmDestination destination, IReadOnlyCollection<LeadItem> items, bool resend,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var http = _env.CrmHttp();
        try
        {
            var (report, ledger) = await CrmExporter.RunAsync(items, destination, Ledger(destination), http, resend,
                progress is null ? null : (a, b) => _env.Ui.Post(() => progress(a, b)), ct).ConfigureAwait(true);
            Store?.SetValue(ExportLedger.StorageKey(destination.Kind, destination.Target), ledger.Encoded());
            return report;
        }
        finally
        {
            (http as IDisposable)?.Dispose();
        }
    }
}

/// <summary>Куда и как писать в Notion.</summary>
public sealed record NotionSetup(string Database, IReadOnlyDictionary<string, ExportField> Mapping);

/// <summary>Запись файла целиком или не запись вовсе: сначала во временный файл, затем замена.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            LeadStore.TryDelete(tmp);
        }
    }
}
