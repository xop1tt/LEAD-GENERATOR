namespace Customers.Presentation;

public sealed partial class AppModel
{
    private CancellationTokenSource? _searchCts;
    private Timer? _autoTimer;
    private bool _searching;
    private string? _searchProgress;
    private SearchReport? _lastReport;

    public bool Searching { get => _searching; private set => Set(ref _searching, value); }

    public string? SearchProgress { get => _searchProgress; private set => Set(ref _searchProgress, value); }

    public SearchReport? LastReport { get => _lastReport; private set => Set(ref _lastReport, value); }

    /// <summary>Поиск завершён: число новых лидов и признак фонового запуска (для уведомлений).</summary>
    public event Action<SearchReport, bool>? SearchCompleted;

    /// <summary>
    /// Запуск поиска. <paramref name="force"/> — без учёта интервалов источников, <paramref name="only"/> — только эти источники.
    /// Поиск идёт на отдельном соединении с базой и не блокирует интерфейс; остановить его можно в любой момент.
    /// </summary>
    public async Task RunSearchAsync(bool force = true, ISet<string>? only = null, bool quiet = false)
    {
        if (Searching || Store is null)
        {
            return;
        }

        LeadStore repository;
        try
        {
            repository = new LeadStore(DatabasePath, Key);
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            LastError = "Поиск не запущен: " + e.Message;
            return;
        }

        Searching = true;
        SearchProgress = "Подготовка…";
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        SearchReport report;
        try
        {
            var settings = SearchSettings.Decode(_searchSettings.Encoded());
            var secrets = new SearchSecrets(_vkToken);
            var transport = _env.SearchTransport();
            try
            {
                var engine = new SearchEngine(settings, secrets, transport, repository);
                var options = new SearchOptions { Force = force, Only = only is null ? null : [.. only] };
                report = await Task.Run(() => engine.RunAsync(options, text => _env.Ui.Post(() =>
                {
                    if (Searching)
                    {
                        SearchProgress = text;
                    }
                }), token), CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                (transport as IDisposable)?.Dispose();
            }
        }
        catch (Exception e)
        {
            // Движок сам собирает ошибки источников; сюда попадает только то, чего он не ожидал.
            _env.Log.Error("Поиск завершился сбоем", e);
            LastError = "Поиск не удался: " + e.Message;
            report = new SearchReport();
        }
        finally
        {
            repository.Dispose();
            Searching = false;
            SearchProgress = null;
            _searchCts?.Dispose();
            _searchCts = null;
        }

        LastReport = report;
        Reload();
        if (report.Added > 0 || report.Updated > 0)
        {
            OnLocalChange?.Invoke();
        }

        DailyBackupIfNeeded();
        if (!quiet || report.Added > 0 || report.Failed.Count > 0)
        {
            if (report.Failed.Count == 0)
            {
                LastMessage = report.Summary;
            }
            else
            {
                LastError = report.Summary;
            }
        }

        _env.Log.Info("Поиск: " + report.Summary);
        SearchCompleted?.Invoke(report, quiet);
    }

    public void StopSearch()
    {
        if (_searchCts is { } cts && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            SearchProgress = "Останавливаю…";
        }
    }

    /// <summary>Автопоиск, пока приложение открыто: только источники, у которых подошёл интервал.</summary>
    public void ScheduleAutoSearch()
    {
        _autoTimer?.Dispose();
        _autoTimer = null;
        var minutes = Prefs.AutoSearchMinutes;
        if (minutes <= 0 || Store is null)
        {
            return;
        }

        var period = TimeSpan.FromMinutes(minutes);
        _autoTimer = new Timer(_ => _env.Ui.Post(() => _ = RunSearchAsync(force: false, quiet: true)), null, period, period);
    }

    public void ResetSourceMemory(string source)
    {
        Act(s => s.ResetSourceMemory(source));
        LastMessage = $"Память источника «{SourceCatalog.Title(source)}» сброшена";
    }
}
