namespace Customers.Core.Tests;

public class SourceParsingTests
{
    [Fact]
    public void Rss()
    {
        var entries = FeedParser.Parse(System.Text.Encoding.UTF8.GetBytes(Samples.Rss));
        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Сделать лендинг для студии", entries[0].Title);
        Assert.Equal("https://www.fl.ru/projects/1/", entries[0].Id);
        Assert.Equal("Сайты", entries[0].Category);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T07:00:00Z"), entries[0].Published);
        Assert.Equal("Нужен сайт & форма записи. Бюджет 20 000 руб.", SearchText.HtmlToText(entries[0].Summary));
        Assert.Null(FeedParser.Parse(System.Text.Encoding.UTF8.GetBytes("не xml")));
    }

    [Fact]
    public void Atom()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>Нужен бот</title>
            <link rel="self" href="https://x/self"/><link rel="alternate" href="https://x/1"/>
            <id>tag:x,1</id><updated>2026-11-05T10:00:00Z</updated><summary>Текст</summary><category term="боты"/></entry></feed>
            """;
        var e = Assert.Single(FeedParser.Parse(System.Text.Encoding.UTF8.GetBytes(atom))!);
        Assert.Equal("https://x/1", e.Link);
        Assert.Equal("tag:x,1", e.Id);
        Assert.Equal("боты", e.Category);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T10:00:00Z"), e.Published);
    }

    [Theory]
    [InlineData("Thu, 05 Nov 2026 10:00:00 GMT", "2026-11-05T10:00:00Z")]
    [InlineData("Thu, 05 Nov 2026 10:00:00 +0300", "2026-11-05T07:00:00Z")]
    [InlineData("05 Nov 2026 10:00 -0500", "2026-11-05T15:00:00Z")]
    [InlineData("Thu, 05 Nov 2026 10:00:00 MSK", "2026-11-05T07:00:00Z")]
    [InlineData("2026-11-05T10:00:00+03:00", "2026-11-05T07:00:00Z")]
    public void FeedDates(string raw, string expected) => Assert.Equal(DateTimeOffset.Parse(expected), FeedParser.ParseDate(raw));

    [Fact]
    public void FeedDateGarbage()
    {
        Assert.Null(FeedParser.ParseDate("вчера"));
        Assert.Null(FeedParser.ParseDate(null));
        Assert.Null(FeedParser.ParseDate("32 Nov 2026 10:00:00 GMT"));
    }

    [Fact]
    public void Telegram()
    {
        var posts = TelegramSource.Parse(Samples.Telegram);
        Assert.Equal([1201, 1200], posts.Select(p => p.Id));
        Assert.Equal("Ищу разработчика Telegram-бота для записи клиентов.\nБюджет 30 000 руб. Пишите @client_manager", posts[0].Text);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T10:00:00Z"), posts[0].Published);
        Assert.Equal("https://t.me/freetasks/1201", posts[0].Url);
    }

    [Fact]
    public void OverpassQuery()
    {
        var q = OsmSource.BuildQuery("Ереван", ["[\"shop\"=\"beauty\"]"]);
        Assert.Equal("[out:json][timeout:90];area[\"name\"=\"Ереван\"][\"boundary\"=\"administrative\"]->.a0;area[\"name:ru\"=\"Ереван\"][\"boundary\"=\"administrative\"]->.a1;area[\"name:en\"=\"Ереван\"][\"boundary\"=\"administrative\"]->.a2;(.a0;.a1;.a2;)->.a;(nwr[\"shop\"=\"beauty\"](area.a););out center tags;", q);
        Assert.DoesNotContain("\"\"", OsmSource.BuildQuery("Ка\"зань", ["[a]"]).Replace("[\"", "").Replace("\"]", ""));
    }

    [Fact]
    public void RetryAfter()
    {
        var now = DateTimeOffset.Parse("2026-11-06T11:00:00Z");
        Assert.Equal(30, SourceHttp.RetryAfter("30", now));
        Assert.Equal(60, SourceHttp.RetryAfter("Fri, 06 Nov 2026 11:01:00 GMT", now));
        Assert.Null(SourceHttp.RetryAfter("скоро", now));
    }

    [Fact]
    public void PublicUrl()
    {
        Assert.True(SiteChecker.IsPublicHttpUrl("https://example.ru"));
        Assert.False(SiteChecker.IsPublicHttpUrl("http://192.168.1.1"));
        Assert.False(SiteChecker.IsPublicHttpUrl("ftp://example.ru"));
        Assert.False(SiteChecker.IsPublicHttpUrl("http://172.20.0.1"));
        Assert.False(SiteChecker.IsPublicHttpUrl("http://127.0.0.1:8080"));
        Assert.False(SiteChecker.IsPublicHttpUrl("http://localhost"));
        Assert.False(SiteChecker.IsPublicHttpUrl("http://printer.local"));
        Assert.True(SiteChecker.IsPublicHttpUrl("http://172.32.0.1"));
    }

    [Fact]
    public async Task SiteCheckerReadsFacts()
    {
        var html = "<html><head><meta name=\"viewport\" content=\"width=device-width\"></head><body>© 2019 Студия. Записаться онлайн</body></html>";
        var http = new SourceHttp("t", new MockTransport(r => MockTransport.Ok(html, r.Url)), 0, sleep: (_, _) => Task.CompletedTask);
        var chk = await SiteChecker.CheckAsync("lavanda.ru", http, DateTimeOffset.Parse("2026-11-06T11:00:00Z"), CancellationToken.None);
        Assert.True(chk.Reachable);
        Assert.False(chk.Https);
        Assert.True(chk.Viewport);
        Assert.Equal(2019, chk.CopyrightYear);
        Assert.True(chk.HasBooking);
        Assert.Contains("Сайт работает без HTTPS", chk.Facts);

        var broken = new SourceHttp("t", new MockTransport(r => MockTransport.Status(404, r.Url)), 0, sleep: (_, _) => Task.CompletedTask);
        var bad = await SiteChecker.CheckAsync("https://gone.example", broken, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(bad.Reachable);
        Assert.Equal(404, bad.Status);
        var local = await SiteChecker.CheckAsync("http://192.168.0.1", http, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(local.Reachable);
    }

    [Fact]
    public async Task HttpRetriesTransientErrorsAndStopsOnClientErrors()
    {
        var calls = 0;
        var sleeps = new List<double>();
        var flaky = new MockTransport(r => ++calls < 3 ? MockTransport.Status(503) : MockTransport.Ok("ok"));
        var http = new SourceHttp("t", flaky, 0, maxAttempts: 4, sleep: (s, _) =>
        {
            sleeps.Add(s);
            return Task.CompletedTask;
        });
        var resp = await http.GetAsync(new Uri("https://x.example"), CancellationToken.None);
        Assert.Equal("ok", resp.Text);
        Assert.Equal(3, calls);
        Assert.Equal(2, sleeps.Count);
        Assert.True(sleeps[1] > sleeps[0]);

        var notFound = new SourceHttp("t", new MockTransport(_ => MockTransport.Status(404)), 0, sleep: (_, _) => Task.CompletedTask);
        var e = await Assert.ThrowsAsync<HttpFailure>(() => notFound.GetAsync(new Uri("https://x.example"), CancellationToken.None));
        Assert.Equal(404, e.Status);
        Assert.False(e.Transient);
    }

    [Fact]
    public async Task HttpBudgetIsEnforcedAndHeadersAreSent()
    {
        var transport = new MockTransport(_ => MockTransport.Ok("ok"));
        var http = new SourceHttp("Тест", transport, 0, maxRequests: 2, sleep: (_, _) => Task.CompletedTask);
        await http.GetAsync(new Uri("https://x.example/a?key=secret"), CancellationToken.None);
        await http.GetAsync(new Uri("https://x.example/b"), CancellationToken.None);
        await Assert.ThrowsAsync<BudgetExceededException>(() => http.GetAsync(new Uri("https://x.example/c"), CancellationToken.None));
        Assert.StartsWith("Customers/", transport.Requests[0].Headers["User-Agent"], StringComparison.Ordinal);
        Assert.Equal("https://x.example/a", SourceHttp.Safe(transport.Requests[0].Url));
    }

    [Fact]
    public void Windows1251IsDecoded()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var bytes = System.Text.Encoding.GetEncoding(1251).GetBytes("Привет, мир");
        Assert.Equal("Привет, мир", new HttpResult(200, bytes, new Dictionary<string, string>(), null).Text);
    }
}

public sealed class EngineTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static MockTransport Transport() => new(req =>
    {
        var url = req.Url.AbsoluteUri;
        if (url.StartsWith("https://example.com/rss", StringComparison.Ordinal))
        {
            return MockTransport.Ok(Samples.Rss, req.Url, ("ETag", "v1"));
        }

        if (url.StartsWith("https://t.me/s/freetasks", StringComparison.Ordinal))
        {
            return MockTransport.Ok(Samples.Telegram, req.Url);
        }

        return url.StartsWith("https://overpass.example", StringComparison.Ordinal)
            ? MockTransport.Ok(Samples.Overpass, req.Url)
            : MockTransport.Status(404, req.Url);
    });

    private static SearchSettings Settings()
    {
        var s = new SearchSettings();
        s.Rss.Feeds = ["https://example.com/rss"];
        s.Telegram.Channels = ["freetasks"];
        s.Vk.Enabled = true; // без ключа: должен быть пропущен с понятной причиной
        s.Osm.Regions = ["Казань"];
        s.Osm.Endpoint = "https://overpass.example/api/interpreter";
        s.Osm.CheckWebsites = false;
        foreach (var c in s.Osm.Categories)
        {
            c.Enabled = c.Key == "beauty";
        }

        return s;
    }

    [Fact]
    public async Task FullRunAndRepeat()
    {
        using var store = new LeadStore(_dir.File("leads.db"));
        var now = DateTimeOffset.Parse("2026-11-06T11:00:00Z");
        var engine = new SearchEngine(Settings(), new SearchSecrets(), Transport(), store, sleep: (_, _) => Task.CompletedTask, now: () => now);
        var first = await engine.RunAsync();
        Assert.Equal(4, first.Added); // 2 из RSS, 1 из Telegram (второй пост не прошёл фильтр), 1 организация
        Assert.Empty(first.Failed);
        Assert.Equal("нет сервисного ключа VK", first.Sources.First(s => s.Source == SourceKey.Vk).Skipped);

        var leads = store.AllLeads();
        var tg = leads.First(l => l.Id == SearchText.LeadId(SourceKey.Telegram, "freetasks/1201"));
        Assert.Equal(Relevance.High, tg.Data.Relevance);
        Assert.Equal("@client_manager", tg.Data.Contacts["telegram"]);
        var org = leads.First(l => l.Data.Kind == LeadKind.Organization);
        Assert.Equal("Салоны красоты", org.Data.Category);
        Assert.Equal("Баумана, 44", org.Data.Address);
        Assert.Contains(org.Data.Facts, f => f.Contains("не указан сайт", StringComparison.Ordinal));
        Assert.NotNull(store.Value("last_search_at"));
        Assert.Single(store.SyncLog());

        // Повторный запуск: всё уже обработано, курсоры и история запросов не дают дублей.
        var second = await engine.RunAsync();
        Assert.Equal(0, second.Added);
        Assert.Equal(4, store.AllLeads().Count);

        // Без force источники не проверяются раньше своего интервала.
        var scheduled = await engine.RunAsync(new SearchOptions { Force = false });
        Assert.All(scheduled.Sources, s => Assert.NotNull(s.Skipped));
    }

    [Fact]
    public async Task FailingSourceIsReported()
    {
        using var store = new LeadStore(_dir.File("leads.db"));
        var s = Settings();
        s.Rss.Feeds = ["https://broken.example/rss"];
        var engine = new SearchEngine(s, new SearchSecrets(), Transport(), store, sleep: (_, _) => Task.CompletedTask);
        var report = await engine.RunAsync(new SearchOptions { Force = true, Only = [SourceKey.Rss] });
        var source = Assert.Single(report.Sources);
        Assert.True(source.Failed);
        Assert.Equal(1, store.SourceStates().First(x => x.Source == SourceKey.Rss).ConsecutiveFailures);
        Assert.NotEmpty(store.PipelineErrors());
    }

    [Fact]
    public async Task CancellationStopsSearchAndKeepsFoundLeads()
    {
        using var store = new LeadStore(_dir.File("leads.db"));
        using var cts = new CancellationTokenSource();
        var engine = new SearchEngine(Settings(), new SearchSecrets(), Transport(), store, sleep: (_, _) => Task.CompletedTask);
        var report = await engine.RunAsync(new SearchOptions(), progress: msg =>
        {
            if (msg.StartsWith("Telegram", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        }, ct: cts.Token);
        Assert.True(report.Cancelled);
        Assert.Contains("Поиск остановлен", report.Summary, StringComparison.Ordinal);
        Assert.Equal(2, store.AllLeads().Count); // то, что успели найти в RSS, сохранено
    }

    [Fact]
    public async Task VkSourceReadsWallAndRespectsCursor()
    {
        const string wall = """{"response":{"count":2,"items":[{"id":11,"owner_id":-5,"date":1793000000,"text":"Нужен бот для магазина\nбюджет 20 тыс руб"},{"id":10,"owner_id":-5,"date":1792990000,"text":"Продам гараж"}]}}""";
        var transport = new MockTransport(r => MockTransport.Ok(wall, r.Url));
        var settings = new SearchSettings();
        settings.Vk.Enabled = true;
        settings.Vk.Communities = ["club_orders"];
        settings.Rss.Enabled = false;
        settings.Telegram.Enabled = false;
        settings.Osm.Enabled = false;
        using var store = new LeadStore(_dir.File("leads.db"));
        var engine = new SearchEngine(settings, new SearchSecrets("token"), transport, store, sleep: (_, _) => Task.CompletedTask,
            now: () => DateTimeOffset.FromUnixTimeSeconds(1793000100));
        var report = await engine.RunAsync();
        Assert.Equal(1, report.Added);
        var lead = Assert.Single(store.AllLeads());
        Assert.Equal("-5_11", lead.Data.ExternalId);
        Assert.Equal("https://vk.com/wall-5_11", lead.Data.Url);
        Assert.Contains("access_token=token", transport.Requests[0].Url.Query, StringComparison.Ordinal);

        await engine.RunAsync();
        Assert.Single(store.AllLeads()); // курсор: пост 11 уже виден
    }

    [Fact]
    public async Task OsmSkipsRecentQueriesAndChecksWebsites()
    {
        const string withSite = """{"elements":[{"type":"node","id":7,"tags":{"name":"Студия","shop":"beauty","website":"http://old.example.ru"}}]}""";
        var transport = new MockTransport(r => r.Url.Host == "overpass.example"
            ? MockTransport.Ok(withSite, r.Url)
            : MockTransport.Ok("<html>© 2015</html>", r.Url));
        var s = Settings();
        s.Osm.CheckWebsites = true;
        s.Rss.Enabled = false;
        s.Telegram.Enabled = false;
        s.Vk.Enabled = false;
        using var store = new LeadStore(_dir.File("leads.db"));
        var now = DateTimeOffset.Parse("2026-11-06T11:00:00Z");
        var engine = new SearchEngine(s, new SearchSecrets(), transport, store, sleep: (_, _) => Task.CompletedTask, now: () => now);
        await engine.RunAsync();
        var lead = Assert.Single(store.AllLeads());
        Assert.Contains(lead.Data.ScoreReasons, r => r.Contains("нет HTTPS", StringComparison.Ordinal));
        Assert.Contains(lead.Data.ScoreReasons, r => r.Contains("мобильной вёрстки", StringComparison.Ordinal));
        Assert.Contains(lead.Data.ScoreReasons, r => r.Contains("устаревшим", StringComparison.Ordinal));
        var overpassCalls = transport.Requests.Count(r => r.Url.Host == "overpass.example");
        await engine.RunAsync();
        Assert.Equal(overpassCalls, transport.Requests.Count(r => r.Url.Host == "overpass.example")); // запрос не повторяется раньше срока
    }
}
