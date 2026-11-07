namespace Customers.Presentation;

public enum SyncState
{
    Idle,
    Syncing,
    Offline,
    Failed,
}

/// <summary>
/// Сессия облака в защищённом хранилище Windows (DPAPI), а не в файлах настроек.
/// </summary>
public sealed class SecretSessionStore(ISecretStore secrets) : ISessionStore
{
    private const string Name = "cloud.session";

    public CloudSession? Load() => CloudSession.FromJson(secrets.Get(Name));

    public void Save(CloudSession? session)
    {
        if (session is null)
        {
            secrets.Delete(Name);
        }
        else
        {
            secrets.Set(Name, session.ToJson());
        }
    }
}

/// <summary>
/// Фоновая синхронизация открытой облачной учётной записи. Запускается после входа, после каждого локального
/// изменения (с задержкой 3 с, чтобы собрать пачку), раз в минуту и при возвращении сети. Одновременно идёт не больше
/// одной синхронизации; изменения во время неё запускают ещё одну.
/// </summary>
public sealed class CloudSyncController : ObservableObject, IDisposable
{
    private readonly CloudAuth _auth;
    private readonly AppModel _model;
    private readonly AppEnvironment _env;
    private readonly TimeSpan _debounceDelay;
    private Task? _running;
    private bool _again;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource _stop = new();
    private Timer? _timer;
    private SyncState _state;
    private string? _error;
    private DateTimeOffset? _lastSync;
    private int _pending;
    private bool _online = true;
    private bool _started;

    public CloudSyncController(CloudAuth auth, AppModel model, AppEnvironment env, TimeSpan? debounce = null)
    {
        _auth = auth;
        _model = model;
        _env = env;
        _debounceDelay = debounce ?? TimeSpan.FromSeconds(3);
        _lastSync = ISODate.Parse(model.Store?.Value("sync.last_at"));
        _online = env.IsOnline();
    }

    public SyncState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(Summary)); } } }

    public string? ErrorText { get => _error; private set => Set(ref _error, value); }

    public DateTimeOffset? LastSync { get => _lastSync; private set { if (Set(ref _lastSync, value)) { Raise(nameof(Summary)); } } }

    public int Pending { get => _pending; private set { if (Set(ref _pending, value)) { Raise(nameof(Summary)); } } }

    /// <summary>Сессия отозвана на сервере: нужно войти заново.</summary>
    public Action? OnSessionExpired { get; set; }

    public event Action<SyncReport>? Synced;

    public string Summary => State switch {
        SyncState.Syncing => "Синхронизация…",
        SyncState.Offline => Pending > 0 ? $"Нет сети · ждут отправки: {Pending}" : "Нет сети · работа без облака",
        SyncState.Failed => "Ошибка синхронизации",
        _ => Pending > 0 ? $"Ждут отправки: {Pending}"
            : LastSync is { } t ? "Синхронизировано " + Relative(t) : "Облако подключено",
    };

    public string Glyph => State switch {
        SyncState.Syncing => "",
        SyncState.Offline => "",
        SyncState.Failed => "",
        _ => Pending > 0 ? "" : "",
    };

    internal static string Relative(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        return d.TotalSeconds < 60 ? "только что"
            : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} мин назад"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours} ч назад"
            : t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _model.OnLocalChange = SyncSoon;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _timer = new Timer(_ => _env.Ui.Post(() => _ = SyncNow()), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _ = SyncNow();
    }

    private void OnNetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) => _env.Ui.Post(() =>
    {
        var was = _online;
        _online = e.IsAvailable;
        if (_online && !was)
        {
            _ = SyncNow();
        }

        if (!_online)
        {
            State = SyncState.Offline;
        }
    });

    public void Dispose()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _timer?.Dispose();
        _timer = null;
        _debounce?.Cancel();
        _stop.Cancel();
        if (_model.OnLocalChange == SyncSoon)
        {
            _model.OnLocalChange = null;
        }
    }

    public void SyncSoon()
    {
        RefreshPending();
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(_debounceDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _env.Ui.Post(() => _ = SyncNow());
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Запускает синхронизацию. Возвращаемая задача завершается, когда она окончена (нужна тестам и кнопке «Синхронизировать»).</summary>
    public Task SyncNow()
    {
        if (_running is { IsCompleted: false })
        {
            _again = true;
            return _running;
        }

        if (!_env.IsOnline())
        {
            State = SyncState.Offline;
            return Task.CompletedTask;
        }

        State = SyncState.Syncing;
        return _running = RunOnceAsync();
    }

    private async Task RunOnceAsync()
    {
        var path = _model.DatabasePath;
        var key = new DatabaseKeyProvider(_env.Secrets).KeyFor(_model.Username);
        try
        {
            var report = await Task.Run(async () =>
            {
                // Своё соединение с базой: синхронизация не мешает интерфейсу и поиску.
                using var store = new LeadStore(path, key);
                return await new SyncEngine(_auth, store, _env.DeviceId).SyncAsync(_stop.Token).ConfigureAwait(false);
            }).ConfigureAwait(true);
            State = SyncState.Idle;
            ErrorText = null;
            LastSync = report.FinishedAt;
            _model.Store?.SetValue("sync.last_at", ISODate.Now());
            if (report.Pulled > 0)
            {
                _model.Reload();
                _model.ReloadSyncedSettings();
            }

            Synced?.Invoke(report);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.SessionExpired)
        {
            State = SyncState.Failed;
            ErrorText = e.Message;
            OnSessionExpired?.Invoke();
            return;
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.Network)
        {
            State = SyncState.Offline;
        }
        catch (Exception e) when (e is CloudException or DatabaseException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _env.Log.Error("Синхронизация не удалась", e);
            State = SyncState.Failed;
            ErrorText = e.Message;
        }

        RefreshPending();
        if (_again)
        {
            _again = false;
            _ = SyncNow();
        }
    }

    private void RefreshPending() => Pending = _model.Store?.PendingSyncCount ?? 0;

    /// <summary>Сводка в облаке для сверки после переноса данных.</summary>
    public async Task<Dictionary<string, int>> RemoteStatsAsync(CancellationToken ct = default)
    {
        var store = _model.Store ?? throw new CloudException(CloudErrorKind.NotConfigured);
        return await new SyncEngine(_auth, store, _env.DeviceId).RemoteStatsAsync(ct).ConfigureAwait(true);
    }
}
