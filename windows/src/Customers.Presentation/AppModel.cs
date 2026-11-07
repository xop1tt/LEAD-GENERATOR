using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Customers.Presentation;

/// <summary>
/// Состояние открытой учётной записи: лиды, фильтры, выбор, настройки поиска и действия пользователя.
/// Все свойства и методы вызываются из потока интерфейса; фоновые операции (поиск, копии) возвращаются в него сами.
/// Логика та же, что у AppModel в macOS-версии.
/// </summary>
public sealed partial class AppModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly List<LeadItem> _all = [];
    private readonly Dictionary<string, LeadRow> _rowById = [];
    private bool _applyingRemote;
    private NavSection _section = NavSection.All;
    private string _searchText = "";
    private LeadKind? _kindFilter;
    private string? _sourceFilter;
    private string? _regionFilter;
    private double _minScore;
    private bool _hideRejected = true;
    private SortColumn _sort;
    private bool _sortDescending = true;
    private HashSet<string> _selection = [];
    private string? _lastMessage;
    private string? _lastError;
    private string? _startupError;
    private DateTimeOffset? _lastSearchAt;
    private SearchSettings _searchSettings = new();
    private string _vkToken = "";

    public AppModel(AppEnvironment env, string username, string displayName, bool isCloud, string dataDirectory)
    {
        _env = env;
        Username = username;
        DisplayName = displayName;
        IsCloud = isCloud;
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        DefaultDatabasePath = Path.Combine(dataDirectory, "leads.db");
        Prefs = new AccountPreferences(Path.Combine(dataDirectory, "prefs.json"), env.DefaultBackups(username));
        _sort = Enum.TryParse<SortColumn>(env.Device.ListSort, true, out var sc) ? sc : SortColumn.Score;
        _sortDescending = env.Device.ListSortDescending;
        _section = NavSections.FromKey(env.Device.LastSection);
        _vkToken = env.Secrets.Get(SecretName("vk.token")) ?? "";
        Rows = [];
        try
        {
            Store = new LeadStore(DatabasePath, Key);
            if (isCloud && !Store.SyncTracking)
            {
                Store.EnableSyncTracking();
            }

            _applyingRemote = true;
            _searchSettings = SearchSettings.Decode(Store.Value("search.settings"));
            _applyingRemote = false;
            ReloadSyncedSettings();
            Reload();
            Backups = new BackupManager(Store, Prefs.BackupDirectory, Prefs.KeepBackups);
            Prefs.PropertyChanged += (_, _) =>
            {
                Backups.Keep = Prefs.KeepBackups;
                ScheduleAutoSearch();
            };
            DailyBackupIfNeeded();
            _backupTimer = new Timer(_ => _env.Ui.Post(DailyBackupIfNeeded), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            ScheduleAutoSearch();
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            _env.Log.Error("База не открыта", e);
            StartupError = e.Message.StartsWith("Не удалось открыть базу", StringComparison.OrdinalIgnoreCase) ? e.Message : "Не удалось открыть базу: " + e.Message;
        }
    }

    /// <summary>Внутренний идентификатор: имя локальной записи или cloud-&lt;uuid&gt;.</summary>
    public string Username { get; }

    /// <summary>Что показывать в интерфейсе: имя пользователя или адрес почты.</summary>
    public string DisplayName { get; }

    public bool IsCloud { get; }

    public string DataDirectory { get; }

    public string DefaultDatabasePath { get; }

    public AccountPreferences Prefs { get; }

    public AppEnvironment Environment => _env;

    public LeadStore? Store { get; private set; }

    public BackupManager? Backups { get; private set; }

    /// <summary>Вызывается после каждого изменения данных: облачная синхронизация отправит их.</summary>
    public Action? OnLocalChange { get; set; }

    public string DatabasePath => Prefs.DataPath is { Length: > 0 } p ? Path.Combine(p, "leads.db") : DefaultDatabasePath;

    private string Key => new DatabaseKeyProvider(_env.Secrets).KeyFor(Username);

    internal string SecretName(string name) => $"{name}.{Username.ToLowerInvariant()}";

    public string? StartupError { get => _startupError; private set => Set(ref _startupError, value); }

    // MARK: Сообщения

    public string? LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }

    public string? LastError
    {
        get => _lastError;
        set
        {
            if (Set(ref _lastError, value) && value is not null)
            {
                _env.Log.Error(value);
            }
        }
    }

    // MARK: Данные

    public ObservableCollection<LeadRow> Rows { get; }

    public IReadOnlyList<LeadItem> AllLeads => _all;

    public IReadOnlyList<SourceState> Sources { get; private set; } = [];

    public IReadOnlyList<PipelineError> PipelineErrors { get; private set; } = [];

    public IReadOnlyList<Exclusion> Exclusions { get; private set; } = [];

    public IReadOnlyList<SyncLogEntry> RunLog { get; private set; } = [];

    public DateTimeOffset? LastSearchAt { get => _lastSearchAt; private set => Set(ref _lastSearchAt, value); }

    public event Action? DataReloaded;

    public void Reload()
    {
        if (Store is not { } store)
        {
            return;
        }

        try
        {
            _all.Clear();
            _all.AddRange(store.AllLeads());
            Sources = store.SourceStates();
            PipelineErrors = store.PipelineErrors();
            Exclusions = store.Exclusions();
            RunLog = store.SyncLog();
            LastSearchAt = ISODate.Parse(store.Value("last_search_at"));
            var existing = _all.Select(l => l.Id).ToHashSet();
            var kept = _selection.Where(existing.Contains).ToHashSet();
            if (kept.Count != _selection.Count)
            {
                _selection = kept;
            }

            RebuildRows();
            RaiseAll(nameof(Sources), nameof(PipelineErrors), nameof(Exclusions), nameof(RunLog), nameof(Counts), nameof(AllSources),
                nameof(AllRegions), nameof(TotalLeads), nameof(NewLeads), nameof(HighLeads), nameof(FavoriteLeads),
                nameof(SelectedIds), nameof(SelectedLead), nameof(SelectionCount));
            DataReloaded?.Invoke();
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastError = e.Message;
        }
    }

    // MARK: Раздел, фильтры, сортировка

    public NavSection Section
    {
        get => _section;
        set
        {
            if (Set(ref _section, value))
            {
                if (value != NavSection.Settings)
                {
                    _env.Device.LastSection = value.Key();
                }

                ClearSelection();
                RebuildRows();
                Raise(nameof(Counts));
            }
        }
    }

    public string SearchText { get => _searchText; set => FilterChanged(ref _searchText, value); }

    public LeadKind? KindFilter { get => _kindFilter; set => FilterChanged(ref _kindFilter, value); }

    public string? SourceFilter { get => _sourceFilter; set => FilterChanged(ref _sourceFilter, value); }

    public string? RegionFilter { get => _regionFilter; set => FilterChanged(ref _regionFilter, value); }

    public double MinScore { get => _minScore; set => FilterChanged(ref _minScore, Math.Clamp(value, 0, 100)); }

    public bool HideRejected { get => _hideRejected; set => FilterChanged(ref _hideRejected, value); }

    public bool FiltersActive => SearchText.Trim().Length > 0 || KindFilter is not null || SourceFilter is not null || RegionFilter is not null || MinScore > 0 || !HideRejected;

    public void ResetFilters()
    {
        _searchText = "";
        _kindFilter = null;
        _sourceFilter = null;
        _regionFilter = null;
        _minScore = 0;
        _hideRejected = true;
        ClearSelection();
        RebuildRows();
        RaiseAll(nameof(SearchText), nameof(KindFilter), nameof(SourceFilter), nameof(RegionFilter), nameof(MinScore), nameof(HideRejected), nameof(FiltersActive));
    }

    private void FilterChanged<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name))
        {
            ClearSelection(); // массовые действия не должны задеть лиды, которых пользователь больше не видит
            RebuildRows();
            Raise(nameof(FiltersActive));
            Raise(nameof(Counts));
        }
    }

    public SortColumn Sort => _sort;

    public bool SortDescending => _sortDescending;

    /// <summary>Повторный выбор того же столбца меняет направление.</summary>
    public void SortBy(SortColumn column)
    {
        if (column == _sort)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sort = column;
            _sortDescending = column is SortColumn.Score or SortColumn.Found or SortColumn.Relevance;
        }

        _env.Device.ListSort = _sort.ToString();
        _env.Device.ListSortDescending = _sortDescending;
        RebuildRows();
        RaiseAll(nameof(Sort), nameof(SortDescending));
    }

    public IEnumerable<LeadItem> Visible()
    {
        var list = _all.Where(l => LeadQuery.InSection(l, _section));
        if (_hideRejected && _section != NavSection.Excluded)
        {
            list = list.Where(l => l.Data.Relevance != Relevance.Rejected);
        }

        if (_kindFilter is { } k)
        {
            list = list.Where(l => l.Data.Kind == k);
        }

        if (_sourceFilter is { } s)
        {
            list = list.Where(l => l.Data.Source == s);
        }

        if (_regionFilter is { } r)
        {
            list = list.Where(l => l.Data.Region == r);
        }

        if (_minScore > 0)
        {
            list = list.Where(l => l.Data.Score >= _minScore);
        }

        return LeadQuery.Sort(list.Where(l => LeadQuery.Matches(l, _searchText)), _sort, _sortDescending);
    }

    private void RebuildRows()
    {
        var items = Visible().ToList();
        var sameOrder = items.Count == Rows.Count && items.Select(i => i.Id).SequenceEqual(Rows.Select(r => r.Id));
        if (sameOrder)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (!ReferenceEquals(Rows[i].Item, items[i]))
                {
                    Rows[i].Update(items[i]);
                }
            }
        }
        else
        {
            Rows.Clear();
            foreach (var item in items)
            {
                Rows.Add(new LeadRow(item));
            }
        }

        _rowById.Clear();
        foreach (var r in Rows)
        {
            _rowById[r.Id] = r;
        }

        RaiseAll(nameof(VisibleCount), nameof(IsEmpty), nameof(EmptyText));
    }

    public int VisibleCount => Rows.Count;

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => _all.Count == 0
        ? "Лидов пока нет. Нажмите «Найти лиды», чтобы запустить поиск."
        : FiltersActive ? "По выбранным фильтрам ничего не найдено." : "В этом разделе пока пусто.";

    /// <summary>Число лидов в разделах (для значков в левой панели).</summary>
    public IReadOnlyDictionary<NavSection, int> Counts => NavSections.Lists.ToDictionary(s => s, s => _all.Count(l =>
        LeadQuery.InSection(l, s) && (s == NavSection.Excluded || !(_hideRejected && l.Data.Relevance == Relevance.Rejected))));

    public int CountOf(NavSection s) => Counts.GetValueOrDefault(s);

    public IReadOnlyList<string> AllSources => [.. _all.Select(l => l.Data.Source).Distinct().Order(StringComparer.Ordinal)];

    public IReadOnlyList<string> AllRegions => [.. _all.Select(l => l.Data.Region).Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).Distinct().Order(StringComparer.CurrentCulture)];

    public int TotalLeads => _all.Count(l => !l.Archived && !l.Excluded);

    public int NewLeads => _all.Count(l => LeadQuery.InSection(l, NavSection.New) && l.Data.Relevance != Relevance.Rejected);

    public int HighLeads => _all.Count(l => LeadQuery.InSection(l, NavSection.All) && l.Data.Relevance == Relevance.High);

    public int FavoriteLeads => _all.Count(l => LeadQuery.InSection(l, NavSection.Favorites));

    // MARK: Выбор

    public IReadOnlyCollection<string> SelectedIds => _selection;

    public int SelectionCount => _selection.Count;

    public LeadRow? SelectedLead => _selection.Count == 1 && _rowById.TryGetValue(_selection.First(), out var row) ? row : null;

    public void SetSelection(IEnumerable<string> ids)
    {
        var next = ids.ToHashSet();
        if (next.SetEquals(_selection))
        {
            return;
        }

        _selection = next;
        RaiseAll(nameof(SelectedIds), nameof(SelectedLead), nameof(SelectionCount));
        SelectionChanged?.Invoke();
    }

    public event Action? SelectionChanged;

    public void ClearSelection()
    {
        if (_selection.Count > 0)
        {
            _selection = [];
            RaiseAll(nameof(SelectedIds), nameof(SelectedLead), nameof(SelectionCount));
            SelectionChanged?.Invoke();
        }
    }

    public IReadOnlyList<LeadItem> SelectedItems => [.. _all.Where(l => _selection.Contains(l.Id))];

    public LeadItem? Lead(string id) => _all.FirstOrDefault(l => l.Id == id);

    // MARK: Действия пользователя

    private void Act(Action<LeadStore> body)
    {
        if (Store is not { } store)
        {
            return;
        }

        try
        {
            body(store);
            Reload();
            OnLocalChange?.Invoke();
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastError = e.Message;
        }
    }

    public void ToggleFavorite(IReadOnlyCollection<string> ids)
    {
        var on = !ids.Select(Lead).Where(l => l is not null).All(l => l!.Favorite);
        Act(s => s.SetFavorite(ids, on));
    }

    public void SetArchived(IReadOnlyCollection<string> ids, bool on) => Act(s => s.SetArchived(ids, on));

    public void Delete(IReadOnlyCollection<string> ids)
    {
        Act(s => s.Delete(ids));
        SetSelection(_selection.Except(ids));
    }

    public void MarkSeen(IReadOnlyCollection<string> ids)
    {
        if (ids.Any(id => Lead(id) is { Seen: false }))
        {
            Act(s => s.MarkSeen(ids));
        }
    }

    public void MarkAllSeen() => Act(s => s.MarkAllSeen());

    public void SetNote(string id, string note)
    {
        if (Lead(id)?.Note != note)
        {
            Act(s => s.SetNote(id, note));
        }
    }

    public IReadOnlyList<LeadEvent> Events(string id)
    {
        try
        {
            return Store?.Events(id) ?? [];
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    /// <summary>Запись в «Историю» лида, например «Письмо отправлено на …».</summary>
    public void LogEvent(string id, string action) => Act(s => s.LogEvent(id, action));

    public void Exclude(ExclusionKind kind, string value)
    {
        Act(s => s.AddExclusion(kind, value));
        LastMessage = $"Добавлено исключение: {kind.Title().ToLowerInvariant()} {value}";
    }

    public void RemoveExclusion(long id) => Act(s => s.RemoveExclusion(id));

    public int ImportExclusions(string csvText)
    {
        var added = 0;
        Act(s => added = s.ImportExclusionsCsv(csvText));
        LastMessage = $"Из файла добавлено исключений: {added}";
        return added;
    }

    // MARK: Настройки, которые синхронизируются

    public SearchSettings SearchSettings => _searchSettings;

    /// <summary>Изменение настроек поиска: копия → правка → сохранение в базе → отправка в облако.</summary>
    public void UpdateSearchSettings(Action<SearchSettings> change)
    {
        var copy = SearchSettings.Decode(_searchSettings.Encoded());
        change(copy);
        if (copy.Encoded() == _searchSettings.Encoded())
        {
            return;
        }

        _searchSettings = copy;
        try
        {
            Store?.SetSyncedValue("search.settings", copy.Encoded());
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastError = e.Message;
        }

        Raise(nameof(SearchSettings));
        OnLocalChange?.Invoke();
    }

    public int AutoSearchMinutes
    {
        get => Prefs.AutoSearchMinutes;
        set
        {
            if (value == Prefs.AutoSearchMinutes)
            {
                return;
            }

            Prefs.AutoSearchMinutes = value;
            if (!_applyingRemote)
            {
                Store?.SetSyncedValue("search.auto", Prefs.AutoSearchMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                OnLocalChange?.Invoke();
            }

            Raise();
        }
    }

    public string Signature
    {
        get => Prefs.Signature;
        set
        {
            if (value == Prefs.Signature)
            {
                return;
            }

            Prefs.Signature = value;
            if (!_applyingRemote)
            {
                Store?.SetSyncedValue("draft.signature", JsonValue.Create(value).ToJsonString());
                OnLocalChange?.Invoke();
            }

            Raise();
        }
    }

    /// <summary>Настройки, пришедшие из облака или сохранённые в базе, — в модель, без повторной выгрузки.</summary>
    public void ReloadSyncedSettings()
    {
        if (Store is not { } store)
        {
            return;
        }

        _applyingRemote = true;
        try
        {
            var s = SearchSettings.Decode(store.Value("search.settings"));
            if (s.Encoded() != _searchSettings.Encoded())
            {
                _searchSettings = s;
                Raise(nameof(SearchSettings));
            }

            if (store.Value("draft.signature") is { } sig && TryParseString(sig) is { } text && text != Signature)
            {
                Signature = text;
            }

            if (store.Value("search.auto") is { } auto && int.TryParse(auto, out var minutes) && minutes != AutoSearchMinutes)
            {
                AutoSearchMinutes = minutes;
            }
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private static string? TryParseString(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Сервисный ключ VK хранится в защищённом хранилище, отдельно для каждого пользователя.</summary>
    public string VkToken
    {
        get => _vkToken;
        set
        {
            var v = value.Trim();
            if (v.Length == 0)
            {
                _env.Secrets.Delete(SecretName("vk.token"));
            }
            else
            {
                _env.Secrets.Set(SecretName("vk.token"), v);
            }

            _vkToken = v;
            RaiseAll(nameof(VkToken), nameof(HasVkToken));
        }
    }

    public bool HasVkToken => _vkToken.Length > 0;

    public string? Problem(string source) => SearchEngine.Problem(source, _searchSettings, new SearchSecrets(_vkToken));

    public long DatabaseSize => Store?.FileSize ?? 0;

    /// <summary>Остановить таймеры и поиск при выходе из учётной записи.</summary>
    public void Shutdown()
    {
        _searchCts?.Cancel();
        _autoTimer?.Dispose();
        _autoTimer = null;
        _backupTimer?.Dispose();
        _backupTimer = null;
        Store?.Dispose();
        Store = null;
    }

    private Timer? _backupTimer;
}
