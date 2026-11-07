namespace Customers.Core;

/// <summary>Куда поиск складывает результаты. Сейчас это локальная база (<see cref="LeadStore"/>).</summary>
public interface ISearchRepository : ISearchMemory
{
    string? SourceCursor(string source);
    DateTimeOffset? LastSuccess(string source);
    void MarkAttempt(string source);
    void FinishSource(string source, bool ok, bool partial, string? error, string? cursor);
    SaveOutcome Save(LeadData lead, string id, string fingerprint);
    void LogError(string module, string message);
    void LogRun(bool ok, int added, int updated, string message);
    void SearchFinished();
}

public sealed class SearchOptions
{
    /// <summary>Не смотреть на интервалы источников (кнопка «Найти лиды»).</summary>
    public bool Force { get; init; } = true;

    /// <summary>Только эти источники (кнопка «Проверить сейчас» у источника).</summary>
    public HashSet<string>? Only { get; init; }
}

public sealed class SourceReport(string source)
{
    public string Source { get; } = source;
    public int Found { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Duplicates { get; set; }
    public string? Skipped { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool Failed { get; set; }
}

public sealed class SearchReport
{
    public List<SourceReport> Sources { get; } = [];
    public bool Cancelled { get; set; }
    public int Added => Sources.Sum(s => s.Added);
    public int Updated => Sources.Sum(s => s.Updated);
    public List<SourceReport> Failed => Sources.Where(s => s.Failed).ToList();

    public string Summary
    {
        get
        {
            if (Cancelled)
            {
                return $"Поиск остановлен. Новых лидов: {Added}";
            }

            var ran = Sources.Where(s => s.Skipped is null).ToList();
            if (ran.Count == 0)
            {
                return Sources.FirstOrDefault()?.Skipped is { } skipped ? $"Поиск не выполнен: {skipped}" : "Нет включённых источников";
            }

            var s = $"Поиск завершён: новых {Added}, обновлено {Updated}";
            if (Failed.Count > 0)
            {
                s += ". С ошибками: " + string.Join(", ", Failed.Select(f => SourceCatalog.Title(f.Source)));
            }

            return s;
        }
    }
}

/// <summary>Поиск лидов по всем источникам. Не зависит от интерфейса; отменяется через <see cref="CancellationToken"/>.</summary>
public sealed class SearchEngine
{
    private readonly SearchSettings _settings;
    private readonly SearchSecrets _secrets;
    private readonly IHttpTransport _transport;
    private readonly ISearchRepository _repo;
    private readonly Scorer _scorer;
    private readonly Func<double, CancellationToken, Task> _sleep;
    private readonly Func<DateTimeOffset> _now;

    public SearchEngine(SearchSettings settings, SearchSecrets secrets, IHttpTransport transport, ISearchRepository repository,
        Scorer? scorer = null, Func<double, CancellationToken, Task>? sleep = null, Func<DateTimeOffset>? now = null)
    {
        _settings = settings;
        _secrets = secrets;
        _transport = transport;
        _repo = repository;
        _scorer = scorer ?? new Scorer();
        _sleep = sleep ?? SourceHttp.RealSleep;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Проверка настроек источника: null — готов, иначе причина.</summary>
    public static string? Problem(string key, SearchSettings s, SearchSecrets secrets) => key switch {
        SourceKey.Rss => s.Rss.Feeds.Count == 0 ? "не добавлено ни одной ленты" : null,
        SourceKey.Telegram => s.Telegram.Channels.Count == 0 ? "не добавлено ни одного канала" : null,
        SourceKey.Vk => string.IsNullOrEmpty(secrets.VkToken) ? "нет сервисного ключа VK"
            : s.Vk.Communities.Count == 0 ? "не добавлено ни одного сообщества" : null,
        SourceKey.Osm => s.Osm.Regions.Count == 0 ? "не добавлено ни одного региона"
            : s.Osm.Categories.Any(c => c.Enabled) ? null : "не выбрано ни одной категории",
        _ => "неизвестный источник",
    };

    public async Task<SearchReport> RunAsync(SearchOptions? options = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new SearchOptions();
        var report = new SearchReport();
        foreach (var key in SourceKey.All)
        {
            if (options.Only is { } only && !only.Contains(key))
            {
                continue;
            }

            var r = new SourceReport(key);
            if (!_settings.IsEnabled(key))
            {
                if (options.Only is not null)
                {
                    r.Skipped = "источник выключен";
                    report.Sources.Add(r);
                }

                continue;
            }

            if (Problem(key, _settings, _secrets) is { } problem)
            {
                r.Skipped = problem;
                report.Sources.Add(r);
                continue;
            }

            if (!options.Force && _repo.LastSuccess(key) is { } last && (_now() - last).TotalSeconds < _settings.IntervalHours(key) * 3600)
            {
                continue; // ещё рано
            }

            if (ct.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }

            progress?.Invoke($"{SourceCatalog.Title(key)}: поиск…");
            _repo.MarkAttempt(key);
            try
            {
                var fetched = await FetchAsync(key, ct).ConfigureAwait(false);
                r.Warnings = fetched.Warnings;
                r.Errors = fetched.PartialErrors;
                r.Found = fetched.Candidates.Count;
                progress?.Invoke($"{SourceCatalog.Title(key)}: оценка {fetched.Candidates.Count}");
                foreach (var c in fetched.Candidates)
                {
                    Store(c, r);
                }

                var allFailed = fetched.PartialErrors.Count > 0 && fetched.PartialErrors.Count >= fetched.Items;
                r.Failed = allFailed;
                foreach (var e in fetched.PartialErrors)
                {
                    _repo.LogError(key, e);
                }

                foreach (var w in fetched.Warnings)
                {
                    _repo.LogError(key, $"Предупреждение: {w}");
                }

                _repo.FinishSource(key, !allFailed, fetched.PartialErrors.Count > 0, fetched.PartialErrors.FirstOrDefault(), fetched.Cursor);
            }
            catch (OperationCanceledException)
            {
                report.Cancelled = true;
                report.Sources.Add(r);
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                r.Failed = true;
                r.Errors.Add(e.Message);
                _repo.LogError(key, e.Message);
                _repo.FinishSource(key, false, false, e.Message, null);
            }

            report.Sources.Add(r);
        }

        _repo.SearchFinished();
        var ok = report.Failed.Count == 0 && !report.Cancelled;
        if (report.Sources.Any(s => s.Skipped is null))
        {
            _repo.LogRun(ok, report.Added, report.Updated, report.Summary);
        }

        return report;
    }

    private Task<FetchResult> FetchAsync(string key, CancellationToken ct)
    {
        var cursor = _repo.SourceCursor(key);
        switch (key)
        {
            case SourceKey.Rss:
                return RssSource.FetchAsync(_settings.Rss, cursor,
                    new SourceHttp("RSS-ленты", _transport, 2, Math.Max(5, _settings.Rss.Feeds.Count * 2), sleep: _sleep), ct);
            case SourceKey.Telegram:
                return TelegramSource.FetchAsync(_settings.Telegram, cursor,
                    new SourceHttp("Telegram", _transport, 3, Math.Max(40, (_settings.Telegram.Channels.Count * _settings.Telegram.MaxPages) + 5), sleep: _sleep), ct);
            case SourceKey.Vk:
                return VkSource.FetchAsync(_settings.Vk, _secrets.VkToken ?? "", cursor,
                    new SourceHttp("VK", _transport, 0.4, Math.Max(40, (_settings.Vk.Communities.Count * _settings.Vk.MaxPages) + 5), sleep: _sleep), ct);
            case SourceKey.Osm:
                var http = new SourceHttp("OpenStreetMap", _transport, 5, 10, timeout: 120, sleep: _sleep);
                var site = new SourceHttp("Проверка сайтов", _transport, 1, 40, maxAttempts: 2, timeout: 12, sleep: _sleep);
                return OsmSource.FetchAsync(_settings.Osm, _repo, http, _settings.Osm.CheckWebsites ? site : null, _now(), ct);
            default:
                return Task.FromResult(new FetchResult());
        }
    }

    private void Store(Candidate c, SourceReport r)
    {
        var data = c.Data;
        data.DiscoveredAt = _now();
        if (data.Kind == LeadKind.Organization)
        {
            _scorer.ScoreOrganization(data, c.CategoryWeight, c.Signals);
        }
        else
        {
            _scorer.ScoreOrder(data, _now());
        }

        var id = SearchText.LeadId(data.Source, data.ExternalId);
        switch (_repo.Save(data, id, SearchText.Fingerprint(data)))
        {
            case SaveOutcome.Added:
                r.Added++;
                break;
            case SaveOutcome.Updated:
                r.Updated++;
                break;
            case SaveOutcome.Duplicate:
                r.Duplicates++;
                break;
        }
    }
}
