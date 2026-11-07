using System.Text.Json;
using System.Text.Json.Nodes;

namespace Customers.Presentation;

/// <summary>Настройки одной учётной записи, которые не синхронизируются через облако: папка копий, автопоиск, уведомления.</summary>
public sealed class AccountPreferences : ObservableObject
{
    private readonly string _path;
    private readonly object _gate = new();
    private int _autoSearchMinutes = 60;
    private string _signature = "";
    private string _backupDirectory = "";
    private bool _autoBackup = true;
    private int _keepBackups = 14;
    private string? _dataPath;
    private bool _notifyNewLeads = true;
    private bool _notifyErrors = true;
    private bool _notifySync;

    public AccountPreferences(string path, string defaultBackupDirectory)
    {
        _path = path;
        _backupDirectory = defaultBackupDirectory;
        Load();
    }

    public int AutoSearchMinutes { get => _autoSearchMinutes; set => Update(ref _autoSearchMinutes, Math.Clamp(value, 0, 24 * 60)); }

    public string Signature { get => _signature; set => Update(ref _signature, value); }

    public string BackupDirectory { get => _backupDirectory; set => Update(ref _backupDirectory, value); }

    public bool AutoBackup { get => _autoBackup; set => Update(ref _autoBackup, value); }

    public int KeepBackups { get => _keepBackups; set => Update(ref _keepBackups, Math.Clamp(value, 1, 365)); }

    /// <summary>Своя папка для базы (после переноса) или null — стандартная.</summary>
    public string? DataPath { get => _dataPath; set => Update(ref _dataPath, value); }

    public bool NotifyNewLeads { get => _notifyNewLeads; set => Update(ref _notifyNewLeads, value); }

    public bool NotifyErrors { get => _notifyErrors; set => Update(ref _notifyErrors, value); }

    public bool NotifySync { get => _notifySync; set => Update(ref _notifySync, value); }

    private void Update<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name))
        {
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path) || JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject o)
            {
                return;
            }

            T? Get<T>(string key) => o[key] is JsonValue v && v.TryGetValue<T>(out var x) ? x : default;
            _autoSearchMinutes = Math.Clamp(Get<int?>("autoSearchMinutes") ?? _autoSearchMinutes, 0, 24 * 60);
            _signature = Get<string>("signature") ?? _signature;
            _backupDirectory = Get<string>("backupDirectory") is { Length: > 0 } dir ? dir : _backupDirectory;
            _autoBackup = Get<bool?>("autoBackup") ?? _autoBackup;
            _keepBackups = Math.Clamp(Get<int?>("keepBackups") ?? _keepBackups, 1, 365);
            _dataPath = Get<string>("dataPath");
            _notifyNewLeads = Get<bool?>("notifyNewLeads") ?? _notifyNewLeads;
            _notifyErrors = Get<bool?>("notifyErrors") ?? _notifyErrors;
            _notifySync = Get<bool?>("notifySync") ?? _notifySync;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    public void Save()
    {
        var o = new JsonObject {
            ["autoSearchMinutes"] = AutoSearchMinutes,
            ["signature"] = Signature,
            ["backupDirectory"] = BackupDirectory,
            ["autoBackup"] = AutoBackup,
            ["keepBackups"] = KeepBackups,
            ["dataPath"] = DataPath,
            ["notifyNewLeads"] = NotifyNewLeads,
            ["notifyErrors"] = NotifyErrors,
            ["notifySync"] = NotifySync,
        };
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
