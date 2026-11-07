namespace Customers.Presentation.Tests;

public class LeadQueryTests
{
    private static LeadItem Item(string id, LeadData d, bool fav = false, bool arch = false, bool excl = false, bool seen = false, string note = "") => new() {
        Id = id,
        Data = d,
        DiscoveredAt = d.DiscoveredAt ?? DateTimeOffset.UtcNow,
        Favorite = fav,
        Archived = arch,
        Excluded = excl,
        Seen = seen,
        Note = note,
    };

    [Fact]
    public void Sections()
    {
        var plain = Item("1", Data.Order("a"));
        var fav = Item("2", Data.Order("b"), fav: true, seen: true);
        var arch = Item("3", Data.Order("c"), arch: true);
        var excl = Item("4", Data.Order("d"), excl: true, fav: true);
        Assert.True(LeadQuery.InSection(plain, NavSection.All));
        Assert.True(LeadQuery.InSection(plain, NavSection.New));
        Assert.False(LeadQuery.InSection(fav, NavSection.New));
        Assert.True(LeadQuery.InSection(fav, NavSection.Favorites));
        Assert.False(LeadQuery.InSection(arch, NavSection.All));
        Assert.True(LeadQuery.InSection(arch, NavSection.Archive));
        Assert.True(LeadQuery.InSection(excl, NavSection.Excluded));
        Assert.False(LeadQuery.InSection(excl, NavSection.Favorites));
        Assert.False(LeadQuery.InSection(plain, NavSection.Sources));
    }

    [Fact]
    public void SearchLooksInAllTextFieldsAndNotes()
    {
        var l = Item("1", Data.Org("Лаванда"), note: "позвонить во вторник");
        Assert.True(LeadQuery.Matches(l, "ЛАВАНД"));
        Assert.True(LeadQuery.Matches(l, "вторник"));
        Assert.True(LeadQuery.Matches(l, "843"));
        Assert.True(LeadQuery.Matches(l, "  "));
        Assert.False(LeadQuery.Matches(l, "роза"));
    }

    [Fact]
    public void SortIsStableAndRespectsDirection()
    {
        var items = new[]
        {
            Item("a", Data.Order("Б", 50)), Item("b", Data.Order("А", 90)), Item("c", Data.Order("В", 50)),
        };
        Assert.Equal(["b", "a", "c"], LeadQuery.Sort(items, SortColumn.Score, true).Select(i => i.Id).Take(1).Concat(["a", "c"]));
        Assert.Equal(["А", "Б", "В"], LeadQuery.Sort(items, SortColumn.Title, false).Select(i => i.Data.Title));
        Assert.Equal(["В", "Б", "А"], LeadQuery.Sort(items, SortColumn.Title, true).Select(i => i.Data.Title));
        Assert.Equal(LeadQuery.Sort(items, SortColumn.Region, false).Select(i => i.Id), LeadQuery.Sort(items, SortColumn.Region, false).Select(i => i.Id));
    }

    [Fact]
    public void RowShowsReadableValues()
    {
        var row = new LeadRow(Item("1", Data.Org("Лаванда")));
        Assert.Equal("50", row.ScoreText);
        Assert.Equal("Организация", row.KindTitle);
        Assert.Equal("OpenStreetMap", row.SourceTitle);
        Assert.Contains("Салоны красоты", row.Subtitle, StringComparison.Ordinal);
        Assert.True(row.IsNew);
        Assert.Equal("(без названия)", new LeadRow(Item("2", new LeadData())).Title);
    }
}

public class PreferencesTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void DevicePreferencesPersistAndClamp()
    {
        var path = Path.Combine(_box.Root, "device.json");
        var p = new DevicePreferences(path) {
            Theme = ThemeChoice.Dark,
            Backdrop = BackdropChoice.Acrylic,
            Accent = "#12ab34",
            NavWidth = 9999,
            InspectorWidth = 10,
            CompactRows = true,
            RememberedUser = "maksim",
        };
        Assert.Equal("#12AB34", p.Accent);
        Assert.Equal(420, p.NavWidth);
        Assert.Equal(280, p.InspectorWidth);

        var again = new DevicePreferences(path);
        Assert.Equal(ThemeChoice.Dark, again.Theme);
        Assert.Equal(BackdropChoice.Acrylic, again.Backdrop);
        Assert.Equal("#12AB34", again.Accent);
        Assert.True(again.CompactRows);
        Assert.Equal("maksim", again.RememberedUser);
        Assert.Null(DevicePreferences.NormalizeColor("красный"));
    }

    [Fact]
    public void CorruptedFileFallsBackToDefaults()
    {
        var path = Path.Combine(_box.Root, "device.json");
        Directory.CreateDirectory(_box.Root);
        File.WriteAllText(path, "{ это не json");
        var p = new DevicePreferences(path);
        Assert.Equal(ThemeChoice.System, p.Theme);
        Assert.Equal(BackdropChoice.Mica, p.Backdrop);
        p.Theme = ThemeChoice.Light; // и снова сохраняется
        Assert.Equal(ThemeChoice.Light, new DevicePreferences(path).Theme);
    }

    [Fact]
    public void AccountPreferencesPersist()
    {
        var path = Path.Combine(_box.Root, "u", "prefs.json");
        var p = new AccountPreferences(path, "C:\\Backups") { AutoSearchMinutes = 30, KeepBackups = 5, Signature = "Максим", NotifySync = true };
        var again = new AccountPreferences(path, "C:\\Backups");
        Assert.Equal(30, again.AutoSearchMinutes);
        Assert.Equal(5, again.KeepBackups);
        Assert.Equal("Максим", again.Signature);
        Assert.True(again.NotifySync);
        Assert.Equal("C:\\Backups", again.BackupDirectory);
        Assert.Equal(p.AutoBackup, again.AutoBackup);
    }

    [Fact]
    public void LogScrubsQueryStringsAndWritesCrashReports()
    {
        Assert.Equal("GET https://api.vk.com/method/wall.get?… ok", FileLog.Scrub("GET https://api.vk.com/method/wall.get?access_token=SECRET&v=5 ok"));
        var log = new FileLog(Path.Combine(_box.Root, "logs"));
        log.Info("запрос https://x.example/a?key=SECRET");
        var text = File.ReadAllText(Directory.GetFiles(log.Directory, "app-*.log").Single());
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);

        Assert.Empty(log.PendingCrashReports());
        var report = log.WriteCrash(new InvalidOperationException("сбой"), "тест");
        Assert.NotNull(report);
        Assert.Contains("сбой", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.Single(log.PendingCrashReports());
        log.DismissCrashReports();
        Assert.Empty(log.PendingCrashReports());
    }
}

public class AppModelTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private AppModel Model(string name = "maksim", bool cloud = false, AppEnvironment? env = null)
    {
        env ??= _box.Env();
        var m = new AppModel(env, name, name, cloud, Path.Combine(_box.Root, "users", name));
        Assert.Null(m.StartupError);
        return m;
    }

    [Fact]
    public void SectionsFiltersAndCounts()
    {
        using var scope = new Scope(Model());
        var m = scope.Model;
        Data.Seed(m);
        Assert.Equal(3, m.CountOf(NavSection.All)); // отклонённый скрыт
        Assert.Equal(3, m.Rows.Count);
        Assert.Equal("a", m.Rows[0].Id); // по оценке

        m.HideRejected = false;
        Assert.Equal(4, m.Rows.Count);
        m.KindFilter = LeadKind.Organization;
        Assert.Equal(["d"], m.Rows.Select(r => r.Id));
        Assert.True(m.FiltersActive);
        m.ResetFilters();
        Assert.False(m.FiltersActive);
        Assert.Equal(3, m.Rows.Count);

        m.SearchText = "кофейн";
        Assert.Equal(["b"], m.Rows.Select(r => r.Id));
        m.SearchText = "";
        m.MinScore = 70;
        Assert.Equal(["a"], m.Rows.Select(r => r.Id));
        m.ResetFilters();
        m.RegionFilter = "Казань";
        Assert.Equal(["d"], m.Rows.Select(r => r.Id));
        Assert.Contains("Казань", m.AllRegions);
        m.SortBy(SortColumn.Title);
        Assert.False(m.SortDescending);
    }

    [Fact]
    public void ChangingFiltersOrSectionClearsSelection()
    {
        using var scope = new Scope(Model());
        var m = scope.Model;
        Data.Seed(m);
        m.SetSelection(["a", "b"]);
        Assert.Equal(2, m.SelectionCount);
        Assert.Null(m.SelectedLead);
        m.SearchText = "салон";
        Assert.Equal(0, m.SelectionCount);
        m.SetSelection(["a"]);
        Assert.Equal("a", m.SelectedLead?.Id);
        m.Section = NavSection.Favorites;
        Assert.Equal(0, m.SelectionCount);
    }

    [Fact]
    public void UserActionsUpdateListsAndNotifyCloud()
    {
        using var scope = new Scope(Model());
        var m = scope.Model;
        var changes = 0;
        m.OnLocalChange = () => changes++;
        Data.Seed(m);
        m.ToggleFavorite(["a", "b"]);
        Assert.Equal(2, m.CountOf(NavSection.Favorites));
        m.ToggleFavorite(["a", "b"]);
        Assert.Equal(0, m.CountOf(NavSection.Favorites));
        m.SetArchived(["a"], true);
        Assert.Equal(1, m.CountOf(NavSection.Archive));
        Assert.Equal(2, m.CountOf(NavSection.All));
        m.SetNote("b", "позвонить");
        Assert.Equal("позвонить", m.Lead("b")!.Note);
        m.MarkSeen(["b"]);
        Assert.True(m.Lead("b")!.Seen);
        m.MarkAllSeen();
        Assert.Equal(0, m.CountOf(NavSection.New));
        m.Exclude(ExclusionKind.Name, "Лаванда");
        Assert.Equal(1, m.CountOf(NavSection.Excluded));
        Assert.Single(m.Exclusions);
        m.RemoveExclusion(m.Exclusions[0].Id);
        Assert.Equal(0, m.CountOf(NavSection.Excluded));
        m.SetSelection(["b"]);
        m.Delete(["b"]);
        Assert.Null(m.Lead("b"));
        Assert.Equal(0, m.SelectionCount);
        Assert.True(changes >= 8);
        Assert.Contains(m.Events("a").Select(e => e.Action), a => a == "Перенесён в архив");
    }

    [Fact]
    public async Task SearchRunsInBackgroundAndKeepsReport()
    {
        const string rss = """<rss><channel><item><title>Нужен сайт для магазина</title><link>https://fl.ru/1</link><guid>1</guid><description>Разработать сайт. Бюджет 30000 руб.</description></item></channel></rss>""";
        var env = _box.Env(search: () => new MockSearchTransport(r => new HttpResult(200, System.Text.Encoding.UTF8.GetBytes(rss), new Dictionary<string, string>(), r.Url)));
        using var scope = new Scope(Model(env: env));
        var m = scope.Model;
        m.UpdateSearchSettings(s =>
        {
            s.Telegram.Enabled = false;
            s.Osm.Enabled = false;
            s.Vk.Enabled = false;
        });
        var completed = 0;
        m.SearchCompleted += (_, _) => completed++;
        var states = new List<bool>();
        m.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.Searching))
            {
                states.Add(m.Searching);
            }
        };
        await m.RunSearchAsync();
        Assert.Equal([true, false], states);
        Assert.Equal(1, completed);
        Assert.NotNull(m.LastReport);
        Assert.Equal(1, m.LastReport!.Added);
        Assert.Single(m.AllLeads);
        Assert.NotNull(m.LastSearchAt);
        Assert.Contains("новых 1", m.LastMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWithBrokenSourceReportsErrorAndKeepsApplicationAlive()
    {
        var env = _box.Env(search: () => new MockSearchTransport(r => new HttpResult(500, [], new Dictionary<string, string>(), r.Url)));
        using var scope = new Scope(Model(env: env));
        var m = scope.Model;
        m.UpdateSearchSettings(s =>
        {
            s.Telegram.Enabled = false;
            s.Osm.Enabled = false;
        });
        await m.RunSearchAsync(only: new HashSet<string> { SourceKey.Rss });
        Assert.NotNull(m.LastError);
        Assert.Single(m.LastReport!.Failed);
        Assert.False(m.Searching);
        Assert.NotEmpty(m.PipelineErrors);
    }

    [Fact]
    public async Task SearchCanBeStopped()
    {
        var gate = new TaskCompletionSource();
        var env = _box.Env(search: () => new BlockingTransport(gate.Task));
        using var scope = new Scope(Model(env: env));
        var m = scope.Model;
        m.UpdateSearchSettings(s =>
        {
            s.Telegram.Enabled = false;
            s.Osm.Enabled = false;
        });
        var run = m.RunSearchAsync();
        await Task.Delay(100);
        Assert.True(m.Searching);
        m.StopSearch();
        gate.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(m.Searching);
        Assert.True(m.LastReport!.Cancelled);
    }

    private sealed class BlockingTransport(Task gate) : IHttpTransport
    {
        public async Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new HttpResult(200, "<rss/>"u8.ToArray(), new Dictionary<string, string>(), request.Url);
        }
    }

    [Fact]
    public void SearchSettingsAreStoredAndRestoredAfterReopen()
    {
        string name = "maksim";
        using (var scope = new Scope(Model(name)))
        {
            scope.Model.UpdateSearchSettings(s => s.Rss.Feeds = ["https://example.com/feed.xml"]);
            scope.Model.VkToken = "  token  ";
            scope.Model.Signature = "Максим";
            scope.Model.AutoSearchMinutes = 15;
        }

        using var again = new Scope(Model(name));
        Assert.Equal(["https://example.com/feed.xml"], again.Model.SearchSettings.Rss.Feeds);
        Assert.Equal("token", again.Model.VkToken);
        Assert.True(again.Model.HasVkToken);
        Assert.Equal("Максим", again.Model.Signature);
        Assert.Equal(15, again.Model.AutoSearchMinutes);
        Assert.Null(again.Model.Problem(SourceKey.Rss));
        Assert.NotNull(again.Model.Problem(SourceKey.Vk)); // нет сообществ
    }

    [Fact]
    public void BackupRestoreAndMove()
    {
        using var scope = new Scope(Model());
        var m = scope.Model;
        m.Prefs.BackupDirectory = Path.Combine(_box.Root, "backups");
        m.Backups!.GetType(); // менеджер создан при запуске; папка берётся из настроек при создании
        var manager = new BackupManager(m.Store!, m.Prefs.BackupDirectory, 3);
        Data.Seed(m);
        var file = manager.BackupNow();
        Assert.Equal(4, m.ProbeBackup(file));
        m.Delete(["a", "b", "c", "d"]);
        Assert.Empty(m.AllLeads);
        Assert.True(m.Restore(file));
        Assert.Equal(4, m.AllLeads.Count + 1 - 1);

        var target = Path.Combine(_box.Root, "moved");
        Directory.CreateDirectory(target);
        Assert.True(m.MoveDatabase(target));
        Assert.Equal(Path.Combine(target, "leads.db"), m.DatabasePath);
        Assert.Empty(m.CheckIntegrity());
        m.SetNote("a", "после переноса");
        Assert.Equal("после переноса", m.Lead("a")!.Note);
    }

    [Fact]
    public void AutomaticDailyBackupHonorsSettings()
    {
        // Первый запуск за день сам делает копию; второй вызов в тот же день ничего не добавляет.
        using (var scope = new Scope(Model()))
        {
            var m = scope.Model;
            Assert.Single(m.BackupFiles);
            m.DailyBackupIfNeeded();
            Assert.Single(m.BackupFiles);
        }

        // Автокопия выключена в настройках учётной записи: копий при запуске нет.
        var other = Path.Combine(_box.Root, "users", "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "prefs.json"), "{\"autoBackup\": false}");
        using var off = new Scope(Model("other"));
        Assert.False(off.Model.Prefs.AutoBackup);
        Assert.Empty(off.Model.BackupFiles);
        off.Model.DailyBackupIfNeeded();
        Assert.Empty(off.Model.BackupFiles);
        Assert.NotNull(off.Model.BackupNow());
        Assert.Single(off.Model.BackupFiles);
    }

    [Fact]
    public void ExportAndImportThroughFiles()
    {
        using var scope = new Scope(Model("a"));
        var m = scope.Model;
        Data.Seed(m);
        var csv = Path.Combine(_box.Root, "out", "leads.csv");
        m.Export(m.AllLeads.ToList(), ExportFormat.Csv, ExportFields.All, csv);
        Assert.True(File.Exists(csv));
        Assert.Contains("Лаванда", File.ReadAllText(csv), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(csv)!, "*.tmp"));

        using var other = new Scope(Model("b"));
        var result = other.Model.ImportLeads(csv);
        Assert.Equal(4, result!.Added);
        Assert.Equal(4, other.Model.AllLeads.Count);
        Assert.Null(other.Model.ImportLeads(Path.Combine(_box.Root, "нет-такого.csv")));
        Assert.NotNull(other.Model.LastError);
    }

    [Fact]
    public void BrokenDatabaseIsReportedAndCanBeRecoveredFromBackup()
    {
        string dbPath;
        string backup;
        using (var scope = new Scope(Model()))
        {
            Data.Seed(scope.Model);
            dbPath = scope.Model.DatabasePath;
            backup = scope.Model.BackupNow()!;
        }

        File.WriteAllText(dbPath, "испорченный файл, не база данных" + new string('x', 5000));
        var broken = new AppModel(_box.Env(), "maksim", "maksim", false, Path.Combine(_box.Root, "users", "maksim"));
        Assert.NotNull(broken.StartupError);
        broken.Shutdown();

        var moved = AppModel.RecoverDatabase(dbPath, backup);
        Assert.True(File.Exists(moved));
        using var healed = new Scope(Model());
        Assert.Equal(4, healed.Model.AllLeads.Count);
    }

    private sealed class FakeDestination(ISet<string> failing) : ICrmDestination
    {
        public CrmKind Kind => CrmKind.Webhook;

        public string Target => "https://hook.example/secret";

        public TimeSpan Pause => TimeSpan.Zero;

        public Task<string> SendAsync(LeadItem item, ICrmHttp http, CancellationToken ct) =>
            failing.Contains(item.Id) ? throw new CrmException(CrmErrorKind.Server, "плохие данные", 400) : Task.FromResult("r-" + item.Id);
    }

    [Fact]
    public async Task CrmExportKeepsLedgerBetweenRuns()
    {
        using var scope = new Scope(Model());
        var m = scope.Model;
        Data.Seed(m);
        var dest = new FakeDestination(new HashSet<string> { "b" });
        var first = await m.ExportToCrmAsync(dest, m.AllLeads.ToList(), resend: false);
        Assert.Equal(3, first.Exported);
        Assert.Single(first.Failures);
        var second = await m.ExportToCrmAsync(new FakeDestination(new HashSet<string>()), m.AllLeads.ToList(), resend: false);
        Assert.Equal(1, second.Exported);
        Assert.Equal(3, second.Skipped);

        m.SetCrmSecret(CrmKind.Bitrix, " https://x.bitrix24.ru/rest/1/abc/ ");
        Assert.Equal("https://x.bitrix24.ru/rest/1/abc/", m.CrmSecret(CrmKind.Bitrix));
        m.SaveNotionSetup(new NotionSetup("db-1", new Dictionary<string, ExportField> { ["Название"] = ExportField.Title }));
        Assert.Equal(ExportField.Title, m.LoadNotionSetup()!.Mapping["Название"]);
    }

    private sealed class Scope(AppModel model) : IDisposable
    {
        public AppModel Model { get; } = model;

        public void Dispose() => Model.Shutdown();
    }
}
