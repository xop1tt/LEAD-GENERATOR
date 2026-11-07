namespace Customers.Core.Tests;

public class NormalizeTests
{
    [Fact]
    public void Domain()
    {
        Assert.Equal("example.ru", Normalize.Domain("example.ru/a#b"));
        Assert.Equal("example.ru", Normalize.Domain("example.ru?x=1"));
        Assert.Equal("shop.example.ru", Normalize.Domain("  HTTPS://Shop.Example.RU  "));
        Assert.Equal("example.ru", Normalize.Domain("http://www.example.ru"));
        Assert.Equal("", Normalize.Domain(""));
    }

    [Fact]
    public void Phone()
    {
        Assert.Equal("8432124890", Normalize.Phone("+7 (843) 212-48-90"));
        Assert.Equal("8432124890", Normalize.Phone("8-843-212-48-90"));
        Assert.Equal("123", Normalize.Phone("123"));
        Assert.Equal("", Normalize.Phone(""));
        Assert.Equal("", Normalize.Phone("тел. не указан"));
    }

    [Fact]
    public void EmailNameAndDispatch()
    {
        Assert.Equal("info@lavanda.ru", Normalize.Email("  Info@Lavanda.RU "));
        Assert.Equal(Normalize.Name("Ёлка"), Normalize.Name("елка"));
        Assert.Equal("ооо ромашка г казань", Normalize.Name("ООО «Ромашка», г. Казань"));
        Assert.Equal("", Normalize.Name("   "));
        Assert.Equal("a.ru", Normalize.Value("https://www.a.ru/x", ExclusionKind.Domain));
        Assert.Equal("9001112233", Normalize.Value("+7 900 111-22-33", ExclusionKind.Phone));
        Assert.Equal("a@b.ru", Normalize.Value(" A@B.RU ", ExclusionKind.Email));
        Assert.Equal("лавр", Normalize.Value("«Лавр»", ExclusionKind.Name));
    }

    [Fact]
    public void IsoDates()
    {
        var baseDate = ISODate.Parse("2026-11-05T09:42:00+00:00")!.Value;
        Assert.Equal("2026-11-05T09:42:00Z", ISODate.Format(baseDate));
        Assert.Equal(baseDate, ISODate.Parse("2026-11-05T09:42:00Z"));
        Assert.Equal(baseDate, ISODate.Parse("2026-11-05T09:42:00")); // без пояса — UTC
        Assert.Equal(baseDate, ISODate.Parse("2026-11-05T12:42:00+03:00"));
        Assert.Equal(0.123, (ISODate.Parse("2026-11-05T09:42:00.123456+00:00")!.Value - baseDate).TotalSeconds, 3);
        Assert.Null(ISODate.Parse(null));
        Assert.Null(ISODate.Parse(""));
        Assert.Null(ISODate.Parse("не дата"));
        Assert.Null(ISODate.Format(null));
        Assert.Contains('.', ISODate.FormatPrecise(baseDate));
        Assert.NotNull(ISODate.Parse(ISODate.Now()));
    }

    [Fact]
    public void SourceCatalogTitles()
    {
        Assert.Equal("OpenStreetMap", SourceCatalog.Title("osm_organizations"));
        Assert.Equal("Импорт", SourceCatalog.Title("import"));
        Assert.Equal("неизвестный", SourceCatalog.Title("неизвестный"));
        foreach (var (key, info) in SourceCatalog.Known)
        {
            Assert.False(string.IsNullOrEmpty(info.Title), key);
            Assert.False(string.IsNullOrEmpty(info.Access), key);
        }

        Assert.NotEmpty(SourceCatalog.Unavailable);
    }
}

public class LeadDataTests
{
    [Fact]
    public void JsonRoundTripKeepsEverythingAndUsesSnakeCase()
    {
        var order = Fixtures.Order();
        var json = order.ToStableJson();
        Assert.Contains("\"external_id\":\"55\"", json);
        Assert.Contains("\"project_type\":\"telegram_bot\"", json);
        Assert.Contains("\"published_at\":\"2026-11-05T08:00:00Z\"", json);
        var back = LeadData.FromJson(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject())!;
        Assert.Equal(json, back.ToStableJson());
        Assert.Equal(40000, back.Budget);
        Assert.Equal("RUB", back.Currency);
    }

    [Fact]
    public void StableJsonSortsKeys()
    {
        var json = Fixtures.Org.ToStableJson();
        var keys = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject().Select(kv => kv.Key).ToList();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
    }

    [Fact]
    public void DecodingToleratesMissingFields()
    {
        var d = LeadData.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{"title":"Только название","score":"x","kind":"странный"}""")!.AsObject())!;
        Assert.Equal("Только название", d.Title);
        Assert.Equal(LeadKind.Order, d.Kind);
        Assert.Empty(d.Facts);
    }

    [Fact]
    public void EmailsPhonesAndDomain()
    {
        var d = Fixtures.Org;
        Assert.Equal(["+7 (843) 212-48-90"], d.Phones);
        Assert.Equal(["client@example.ru"], Fixtures.Order().Emails);
        Assert.Equal("shop.example.ru", Fixtures.Order().Domain);
        Assert.Null(Fixtures.Org.Domain);
    }

    [Fact]
    public void LeadItemSortKeys()
    {
        var item = new LeadItem { Id = "1", Data = Fixtures.Order(), DiscoveredAt = DateTimeOffset.UtcNow };
        Assert.Equal("Нужен Telegram-бот", item.Title);
        Assert.Equal(88, item.Score);
        Assert.Equal("Заказ", item.KindTitle);
        Assert.Equal(3, item.RelevanceRank);
        Assert.Equal("telegram_bot", item.CategoryText);
        Assert.Equal("", item.RegionText);
        Assert.True(item.IsNew);
        Assert.False(new LeadItem { Id = "2", Data = Fixtures.Org, DiscoveredAt = DateTimeOffset.UtcNow, Seen = true }.IsNew);
        Assert.Equal("Салоны красоты", new LeadItem { Id = "3", Data = Fixtures.Org, DiscoveredAt = DateTimeOffset.UtcNow }.CategoryText);
    }
}

internal static class Fixtures
{
    public static LeadData Org => new() {
        Kind = LeadKind.Organization,
        Source = "osm_organizations",
        ExternalId = "node/1",
        Title = "Студия красоты «Лаванда»",
        Url = "https://www.openstreetmap.org/node/1",
        DiscoveredAt = ISODate.Parse("2026-11-05T09:42:00+00:00"),
        Region = "Казань",
        Contacts = new() { ["vk"] = "vk.com/lavanda" },
        Category = "Салоны красоты",
        Address = "ул. Баумана, 44",
        Phone = "+7 (843) 212-48-90",
        Facts = ["В OpenStreetMap не указан сайт"],
        Assumptions = ["Возможно, нужна онлайн-запись"],
        Score = 84,
        Relevance = Relevance.High,
        ScoreReasons = ["нет сайта (+35)"],
    };

    public static LeadData Order(string title = "Нужен Telegram-бот") => new() {
        Kind = LeadKind.Order,
        Source = "fl_ru",
        ExternalId = "55",
        Title = title,
        Url = "https://www.fl.ru/projects/55",
        Description = "Ищу разработчика бота. Пишите: client@example.ru",
        PublishedAt = ISODate.Parse("2026-11-05T08:00:00+00:00"),
        DiscoveredAt = ISODate.Parse("2026-11-05T09:15:00+00:00"),
        Budget = 40000,
        Currency = "RUB",
        Contacts = new() { ["email"] = "client@example.ru" },
        ProjectType = "telegram_bot",
        Website = "https://shop.example.ru/about",
        Score = 88,
        Relevance = Relevance.High,
    };
}

public abstract class StoreTestBase : IDisposable
{
    protected TempDir Dir { get; } = new();

    protected LeadStore Make(string name = "local.db", string? key = null)
    {
        var store = new LeadStore(Dir.File(name), key);
        Assert.Equal(SaveOutcome.Added, store.Save(Fixtures.Org, "org1", "fp1"));
        Assert.Equal(SaveOutcome.Added, store.Save(Fixtures.Order(), "ord1", "fp2"));
        return store;
    }

    protected static LeadItem Get(LeadStore s, string id) => s.AllLeads(includeDeleted: true).First(l => l.Id == id);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Dir.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class StoreTests : StoreTestBase
{
    [Fact]
    public void SaveKeepsUserState()
    {
        using var store = Make();
        var leads = store.AllLeads();
        Assert.Equal(2, leads.Count);
        Assert.Equal(["В OpenStreetMap не указан сайт"], leads.First(l => l.Id == "org1").Data.Facts);
        Assert.False(leads.First(l => l.Id == "org1").Seen);

        store.SetFavorite(["org1"], true);
        store.SetNote("org1", "позвонить");
        store.Delete(["ord1"]);

        Assert.Equal(SaveOutcome.Unchanged, store.Save(Fixtures.Org, "org1", "fp1"));
        Assert.Equal(SaveOutcome.Updated, store.Save(Fixtures.Order("Нужен Telegram-бот (обновлено)"), "ord1", "fp2"));
        Assert.Equal(SaveOutcome.Duplicate, store.Save(Fixtures.Order(), "other", "fp2"));

        leads = store.AllLeads();
        Assert.Equal(["org1"], leads.Select(l => l.Id)); // удалённый лид не возвращается
        Assert.True(leads[0].Favorite);
        Assert.Equal("позвонить", leads[0].Note);
        Assert.NotEmpty(store.Events("org1"));
    }

    [Fact]
    public void SourceStateAndMemory()
    {
        using var store = Make();
        store.MarkAttempt("fl_ru");
        store.FinishSource("fl_ru", true, false, null, "{\"a\":1}");
        Assert.Equal("{\"a\":1}", store.SourceCursor("fl_ru"));
        Assert.NotNull(store.LastSuccess("fl_ru"));
        store.FinishSource("fl_ru", false, false, "timeout", null);
        Assert.Equal("{\"a\":1}", store.SourceCursor("fl_ru")); // ошибка не стирает курсор
        Assert.Equal(1, store.SourceStates().First().ConsecutiveFailures);
        store.LogError("fl_ru", "timeout");
        Assert.Equal("timeout", store.PipelineErrors().First().Message);

        store.RecordQuery("q1", "osm_organizations", "Казань|beauty", 3);
        Assert.NotNull(store.QueryLastRun("q1"));
        store.MarkObjectChecked("osm_organizations", "node/1");
        Assert.NotNull(store.ObjectCheckedAt("osm_organizations", "node/1"));
        store.ResetSourceMemory("osm_organizations");
        Assert.Null(store.QueryLastRun("q1"));
        Assert.Null(store.ObjectCheckedAt("osm_organizations", "node/1"));
    }

    [Fact]
    public void FavoriteArchiveAndEvents()
    {
        using var store = Make();
        store.SetFavorite(["org1"], true);
        Assert.True(Get(store, "org1").Favorite);
        Assert.False(Get(store, "ord1").Favorite);
        store.SetFavorite(["org1"], false);
        Assert.False(Get(store, "org1").Favorite);

        store.SetArchived(["org1", "ord1"], true);
        Assert.All(store.AllLeads(), l => Assert.True(l.Archived));
        store.SetArchived(["ord1"], false);
        Assert.False(Get(store, "ord1").Archived);

        var actions = store.Events("org1").Select(e => e.Action).ToList();
        Assert.Contains("Добавлен в избранное", actions);
        Assert.Contains("Убран из избранного", actions);
        Assert.Contains("Перенесён в архив", actions);
        Assert.Contains(actions, a => a.StartsWith("Найден поиском", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyAndUnknownIdsAreHarmless()
    {
        using var store = Make();
        store.SetFavorite([], true);
        store.Delete([]);
        store.SetFavorite(["нет-такого"], true);
        Assert.Equal(2, store.AllLeads().Count);
    }

    [Fact]
    public void SeenAndMarkAllSeen()
    {
        using var store = Make();
        Assert.All(store.AllLeads(), l => Assert.False(l.Seen));
        store.MarkSeen(["org1"]);
        Assert.True(Get(store, "org1").Seen);
        Assert.False(Get(store, "ord1").Seen);
        var before = store.Events("org1").Count;
        store.MarkSeen(["org1"]);
        Assert.Equal(before, store.Events("org1").Count);
        store.MarkAllSeen();
        Assert.All(store.AllLeads(), l => Assert.True(l.Seen));
    }

    [Fact]
    public void NoteIsStoredAndCanBeCleared()
    {
        using var store = Make();
        store.SetNote("org1", "позвонить во вторник");
        Assert.Equal("позвонить во вторник", Get(store, "org1").Note);
        store.SetNote("org1", "");
        Assert.Equal("", Get(store, "org1").Note);
    }

    [Fact]
    public void DeletedLeadIsHiddenButKeptAndNotResurrected()
    {
        using var store = Make();
        store.Delete(["ord1"]);
        Assert.Equal(["org1"], store.AllLeads().Select(l => l.Id));
        Assert.Equal(2, store.AllLeads(includeDeleted: true).Count);
        Assert.Equal(SaveOutcome.Unchanged, store.Save(Fixtures.Order(), "ord1", "fp2"));
        Assert.Equal(["org1"], store.AllLeads().Select(l => l.Id));
    }

    [Fact]
    public void SaveOutcomes()
    {
        using var store = Make();
        var changed = Fixtures.Org;
        changed.Score = 10;
        Assert.Equal(SaveOutcome.Updated, store.Save(changed, "org1", "fp1"));
        Assert.Equal(10, Get(store, "org1").Data.Score);
        Assert.Equal(SaveOutcome.Duplicate, store.Save(Fixtures.Org, "dup", "fp1"));
        Assert.Equal(SaveOutcome.Added, store.Save(Fixtures.Order("Другой"), "a", ""));
        Assert.Equal(SaveOutcome.Added, store.Save(Fixtures.Order("Ещё другой"), "b", ""));
        Assert.Equal(4, store.AllLeads().Count);
    }

    [Fact]
    public void DiscoveryDateIsTheFirstOne()
    {
        using var store = Make();
        var first = Get(store, "org1").DiscoveredAt;
        var later = Fixtures.Org;
        later.DiscoveredAt = ISODate.Parse("2030-01-01T00:00:00Z");
        later.Score = 1;
        Assert.Equal(SaveOutcome.Updated, store.Save(later, "org1", "fp1"));
        Assert.Equal(first, Get(store, "org1").DiscoveredAt);
    }

    [Fact]
    public void KeyValueAndLogs()
    {
        using var store = Make();
        Assert.Null(store.Value("нет"));
        store.SetValue("k", "1");
        store.SetValue("k", "2");
        Assert.Equal("2", store.Value("k"));
        Assert.NotNull(store.Value("schema_version"));
        store.SearchFinished();
        Assert.NotNull(ISODate.Parse(store.Value("last_search_at")));

        store.LogRun(true, 3, 1, "готово");
        store.LogRun(false, 0, 0, "сбой");
        var log = store.SyncLog();
        Assert.Equal("сбой", log[0].Message);
        Assert.False(log[0].Ok);
        Assert.Equal(3, log[^1].Added);
        Assert.Single(store.SyncLog(1));
    }

    [Fact]
    public void PipelineErrorsLimitAndOrder()
    {
        using var store = Make();
        for (var i = 1; i <= 5; i++)
        {
            store.LogError("m", $"ошибка {i}");
        }

        Assert.Equal(["ошибка 5", "ошибка 4", "ошибка 3"], store.PipelineErrors(3).Select(e => e.Message));
    }

    [Fact]
    public void SourceFailuresCountAndRecover()
    {
        using var store = Make();
        store.MarkAttempt("rss");
        store.FinishSource("rss", false, false, "e1", null);
        store.FinishSource("rss", false, false, "e2", null);
        Assert.Equal(2, store.SourceStates().First().ConsecutiveFailures);
        Assert.Equal("e2", store.SourceStates().First().LastError);
        store.FinishSource("rss", true, false, null, "c");
        Assert.Equal(0, store.SourceStates().First().ConsecutiveFailures);
        Assert.Null(store.SourceStates().First().LastError);
    }

    [Fact]
    public void QueryMemoryForget()
    {
        using var store = Make();
        store.RecordQuery("h1", "osm", "Казань", 2);
        Assert.NotNull(store.QueryLastRun("h1"));
        store.ForgetQuery("h1");
        Assert.Null(store.QueryLastRun("h1"));
        Assert.Null(store.QueryLastRun("не было"));
    }

    [Fact]
    public void ReopenKeepsData()
    {
        var path = Dir.File("reopen.db");
        using (var store = new LeadStore(path))
        {
            store.Save(Fixtures.Org, "org1", "fp1");
            store.SetFavorite(["org1"], true);
        }

        using var again = new LeadStore(path);
        Assert.Equal(["org1"], again.AllLeads().Select(l => l.Id));
        Assert.True(again.AllLeads()[0].Favorite);
    }

    [Fact]
    public void IntegrityCheckIsClean()
    {
        using var store = Make();
        Assert.Empty(store.CheckIntegrity());
    }
}

public class ExclusionTests : StoreTestBase
{
    [Fact]
    public void ExclusionsApplyAndRemove()
    {
        using var store = Make();
        store.AddExclusion(ExclusionKind.Phone, "8 843 212 48 90");
        Assert.True(Get(store, "org1").Excluded);
        store.AddExclusion(ExclusionKind.Domain, "example.ru");
        Assert.True(Get(store, "ord1").Excluded); // поддомен shop.example.ru
        foreach (var e in store.Exclusions())
        {
            store.RemoveExclusion(e.Id);
        }

        Assert.All(store.AllLeads(), l => Assert.False(l.Excluded));

        var n = store.ImportExclusionsCsv("Компания;Email;Телефон\nЛаванда;info@lavanda.ru;+7 (843) 212-48-90\n");
        Assert.Equal(2, n);
        Assert.True(Get(store, "org1").Excluded);
    }

    [Fact]
    public void RulesAreNormalizedAndNotDuplicated()
    {
        using var store = Make();
        store.AddExclusion(ExclusionKind.Email, "CLIENT@example.ru");
        Assert.True(Get(store, "ord1").Excluded);
        Assert.False(Get(store, "org1").Excluded);
        store.AddExclusion(ExclusionKind.Name, "студия красоты лаванда");
        Assert.True(Get(store, "org1").Excluded);
        var before = store.Exclusions().Count;
        store.AddExclusion(ExclusionKind.Name, "Студия красоты «Лаванда»");
        Assert.Equal(before, store.Exclusions().Count);
        store.AddExclusion(ExclusionKind.Domain, "   ");
        Assert.Equal(before, store.Exclusions().Count);
    }

    [Fact]
    public void DomainExclusionDoesNotMatchSimilarNames()
    {
        using var store = Make();
        store.AddExclusion(ExclusionKind.Domain, "ample.ru");
        Assert.False(Get(store, "ord1").Excluded);
        store.AddExclusion(ExclusionKind.Domain, "example.ru");
        Assert.True(Get(store, "ord1").Excluded);
    }

    [Fact]
    public void ImportClassifiesCells()
    {
        using var store = Make();
        const string csv = "Компания;Сайт;Почта;Телефон\nЛавр;https://www.lavr.ru/;a@lavr.ru;+7 900 111-22-33\n;;;\nпросто текст;;;12\n";
        Assert.Equal(3, store.ImportExclusionsCsv(csv));
        Assert.Equal(new HashSet<ExclusionKind> { ExclusionKind.Domain, ExclusionKind.Email, ExclusionKind.Phone }, store.Exclusions().Select(e => e.Kind).ToHashSet());
        Assert.Equal(0, store.ImportExclusionsCsv(csv));
    }
}

public class BackupTests : StoreTestBase
{
    [Fact]
    public void BackupAndRestore()
    {
        using var store = Make();
        var backup = Dir.File("b.db");
        store.Backup(backup);
        Assert.Equal(2, store.ProbeBackup(backup));
        store.Delete(["org1", "ord1"]);
        Assert.Empty(store.AllLeads());
        store.Restore(backup);
        Assert.Equal(2, store.AllLeads().Count);
        Assert.True(File.Exists(Dir.File("before-restore.db")));

        Assert.Throws<DatabaseException>(() => store.Restore(Dir.File("local.db")));
        Assert.Equal(2, store.AllLeads().Count);
    }

    [Fact]
    public void RestoreRejectsFileThatIsNotADatabase()
    {
        using var store = Make();
        var junk = Dir.File("junk.db");
        File.WriteAllText(junk, "это не база");
        Assert.ThrowsAny<Exception>(() => store.Restore(junk));
        Assert.Equal(2, store.AllLeads().Count);
        Assert.ThrowsAny<Exception>(() => store.ProbeBackup(junk));
    }

    [Fact]
    public void MoveDatabase()
    {
        using var store = Make("move.db");
        var target = Directory.CreateDirectory(Dir.File("elsewhere")).FullName;
        var newPath = store.MoveTo(target);
        Assert.Equal(Path.Combine(target, "leads.db"), newPath);
        Assert.Equal(newPath, store.Path);
        Assert.Equal(2, store.AllLeads().Count);
        Assert.False(File.Exists(Dir.File("move.db")));
        Assert.Equal(newPath, store.MoveTo(target));
        Assert.True(store.FileSize > 0);
    }

    [Fact]
    public void BackupManagerKeepsLastCopiesAndMakesOneDailyBackup()
    {
        using var store = Make();
        var manager = new BackupManager(store, Dir.File("backups"), keep: 2);
        var day = DateTimeOffset.Parse("2026-11-05T10:00:00Z");
        manager.BackupNow(day);
        manager.BackupNow(day.AddMinutes(1));
        manager.BackupNow(day.AddMinutes(2));
        Assert.Equal(2, manager.List().Count);
        Assert.Null(manager.DailyIfNeeded(day.AddMinutes(3)));
        Assert.NotNull(manager.DailyIfNeeded(day.AddDays(1)));
        Assert.All(manager.List(), b => Assert.Equal(2, store.ProbeBackup(b.Path)));
    }
}

public class EncryptionTests : StoreTestBase
{
    private const string Secret = "СекретнаяКомпания-Ромашка";

    private static string NewKey() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool Contains(string path, string text)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(text);
        foreach (var suffix in new[] { "", "-wal" })
        {
            if (File.Exists(path + suffix) && File.ReadAllBytes(path + suffix).AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void EncryptedDatabaseHidesDataAndNeedsTheKey()
    {
        var key = NewKey();
        var path = Dir.File("enc.db");
        using (var store = new LeadStore(path, key))
        {
            var lead = Fixtures.Order(Secret);
            store.Save(lead, "x", "fp");
            Assert.True(store.IsEncrypted);
            Assert.False(Contains(path, Secret), "название лида не лежит открытым текстом");
        }

        Assert.False(Contains(path, Secret));
        Assert.ThrowsAny<Exception>(() => new LeadStore(path));
        Assert.ThrowsAny<Exception>(() => new LeadStore(path, NewKey()));
        using var again = new LeadStore(path, key);
        Assert.Equal(Secret, again.AllLeads()[0].Title);
    }

    [Fact]
    public void BackupOfEncryptedStoreIsEncryptedAndRestorable()
    {
        var key = NewKey();
        using var store = Make("enc.db", key);
        store.SetNote("org1", Secret);
        var backup = Dir.File("copy.db");
        store.Backup(backup);
        Assert.False(Contains(backup, Secret));
        Assert.Throws<DatabaseException>(() => store.ProbeBackup(Dir.File("enc.db-nothing")));
        store.Delete(["org1"]);
        store.Restore(backup);
        Assert.Equal(Secret, Get(store, "org1").Note);
        Assert.False(Contains(Dir.File("enc.db"), Secret));
    }

    [Fact]
    public void PlainBackupCanBeRestoredIntoEncryptedStore()
    {
        var plainPath = Dir.File("plain.db");
        using (var plain = new LeadStore(plainPath))
        {
            plain.Save(Fixtures.Order(Secret), "p", "fp-p");
            plain.Backup(Dir.File("plain-copy.db"));
        }

        var key = NewKey();
        using var store = new LeadStore(Dir.File("target.db"), key);
        store.Restore(Dir.File("plain-copy.db"));
        Assert.Equal(Secret, store.AllLeads()[0].Title);
        Assert.False(Contains(Dir.File("target.db"), Secret), "после восстановления база зашифрована");
    }

    [Fact]
    public void DatabaseKeysAreCreatedOncePerAccount()
    {
        var secrets = new MemorySecretStore();
        var provider = new DatabaseKeyProvider(secrets);
        Assert.False(provider.Exists("Maksim"));
        var key = provider.KeyFor("Maksim");
        Assert.Equal(32, Convert.FromBase64String(key).Length);
        Assert.Equal(key, provider.KeyFor("maksim"));
        Assert.NotEqual(key, provider.KeyFor("anna"));
        provider.Forget("maksim");
        Assert.False(provider.Exists("Maksim"));
    }
}
