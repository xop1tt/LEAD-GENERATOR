using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Core.Tests;

internal sealed class MockCrmHttp(Func<HttpRequestMessage, string, (string Body, int Status)> handler) : ICrmHttp
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public async Task<(byte[] Data, int Status)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        var (text, status) = handler(request, body);
        return (Encoding.UTF8.GetBytes(text), status);
    }
}

public class CrmTests
{
    private static LeadItem Lead(string id = "l1", string title = "Кофейня «Зерно»") => new() {
        Id = id,
        Favorite = true,
        DiscoveredAt = ISODate.Parse("2026-11-05T09:00:00Z")!.Value,
        Data = new LeadData {
            Kind = LeadKind.Organization,
            Source = "osm",
            ExternalId = "x",
            Title = title,
            Url = "https://example.com/lead",
            Description = "Описание",
            Budget = 50000,
            Currency = "RUB",
            Region = "Казань",
            Contacts = new() { ["email"] = "info@zerno.ru", ["phone2"] = "+7 900 000-00-01" },
            CustomerName = "ООО Зерно",
            Address = "ул. Баумана, 1",
            Phone = "+7 843 212-48-90",
            Website = "https://zerno.ru",
            Score = 72.4,
            Relevance = Relevance.High,
            ScoreReasons = ["нет онлайн-записи"],
        },
    };

    [Fact]
    public void BitrixWebhookNormalization()
    {
        Assert.Equal("https://acme.bitrix24.ru/rest/1/abc123/", Bitrix24Destination.Normalize(" https://acme.bitrix24.ru/rest/1/abc123/ ").AbsoluteUri);
        Assert.Equal("https://acme.bitrix24.ru/rest/12/abc123/", Bitrix24Destination.Normalize("https://acme.bitrix24.ru/rest/12/abc123/crm.lead.add.json").AbsoluteUri);
        Assert.Equal("https://crm.example.com/rest/1/k/", Bitrix24Destination.Normalize("https://crm.example.com/rest/1/k").AbsoluteUri);
        foreach (var bad in new[] { "http://acme.bitrix24.ru/rest/1/abc/", "https://acme.bitrix24.ru/crm/lead/", "https://acme.bitrix24.ru/rest/x/abc/", "" })
        {
            Assert.Equal(CrmErrorKind.InvalidAddress, Assert.Throws<CrmException>(() => Bitrix24Destination.Normalize(bad)).Kind);
        }
    }

    [Fact]
    public void BitrixFields()
    {
        var f = Bitrix24Destination.Fields(Lead());
        Assert.Equal("Кофейня «Зерно»", (string?)f["TITLE"]);
        Assert.Equal("ООО Зерно", (string?)f["COMPANY_TITLE"]);
        Assert.Equal("l1", (string?)f["ORIGIN_ID"]);
        Assert.Equal(50000, (double?)f["OPPORTUNITY"]);
        Assert.Equal("RUB", (string?)f["CURRENCY_ID"]);
        var phones = f["PHONE"]!.AsArray();
        Assert.Equal("+7 843 212-48-90", (string?)phones[0]!["VALUE"]);
        Assert.Equal(2, phones.Count);
        Assert.Equal("info@zerno.ru", (string?)f["EMAIL"]![0]!["VALUE"]);
        Assert.Contains("https://example.com/lead", (string?)f["COMMENTS"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BitrixRequestAndResponses()
    {
        var http = new MockCrmHttp((_, _) => ("""{"result": 123, "time": {}}""", 200));
        var id = await new Bitrix24Destination("https://acme.bitrix24.ru/rest/1/abc123/").SendAsync(Lead(), http, CancellationToken.None);
        Assert.Equal("123", id);
        Assert.Equal("https://acme.bitrix24.ru/rest/1/abc123/crm.lead.add.json", http.Requests[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, http.Requests[0].Request.Method);
        Assert.Equal("l1", (string?)JsonNode.Parse(http.Requests[0].Body)!["fields"]!["ORIGIN_ID"]);

        CrmErrorKind? Failure(string json, int status)
        {
            try
            {
                Bitrix24Destination.Parse(Encoding.UTF8.GetBytes(json), status);
                return null;
            }
            catch (CrmException e)
            {
                return e.Kind;
            }
        }

        Assert.Equal(CrmErrorKind.RateLimited, Failure("""{"error":"QUERY_LIMIT_EXCEEDED","error_description":"Too many"}""", 503));
        Assert.Equal(CrmErrorKind.Unauthorized, Failure("""{"error":"INVALID_CREDENTIALS","error_description":"Invalid request credentials"}""", 401));
        Assert.Equal(CrmErrorKind.Forbidden, Failure("""{"error":"insufficient_scope"}""", 401));
        Assert.Equal(CrmErrorKind.NotFound, Failure("<html>", 404));
        Assert.Equal(CrmErrorKind.BadResponse, Failure("{}", 200));
        Assert.True(new CrmException(CrmErrorKind.Unauthorized, "x").StopsExport);
        Assert.False(new CrmException(CrmErrorKind.Server, "x", 400).StopsExport);
    }

    [Fact]
    public void NotionDatabaseId()
    {
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef",
            NotionDestination.ParseDatabaseId("https://www.notion.so/acme/Leads-0123456789abcdef0123456789ABCDEF?v=fedcba9876543210fedcba9876543210"));
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", NotionDestination.ParseDatabaseId("01234567-89ab-cdef-0123-456789abcdef"));
        Assert.Throws<CrmException>(() => NotionDestination.ParseDatabaseId("https://www.notion.so/acme/Leads"));
    }

    [Fact]
    public void NotionSchemaAndAutoMapping()
    {
        const string json = """
            {"object":"database","title":[{"plain_text":"Лиды"}],"properties":{
              "Название":{"id":"title","type":"title"},"Телефон":{"id":"a","type":"phone_number"},
              "Email":{"id":"b","type":"email"},"Сайт":{"id":"c","type":"url"},"Оценка":{"id":"d","type":"number"},
              "Статус":{"id":"e","type":"status"},"Город":{"id":"f","type":"select"},"Найден":{"id":"g","type":"date"}}}
            """;
        var obj = NotionDestination.Check(Encoding.UTF8.GetBytes(json), 200);
        var db = NotionDestination.ParseDatabase(obj, "db");
        Assert.Equal("Лиды", db.Title);
        Assert.Equal("title", db.Properties[0].Type);
        var m = NotionDestination.AutoMapping(db.Properties);
        Assert.Equal(ExportField.Title, m["Название"]);
        Assert.Equal(ExportField.Phone, m["Телефон"]);
        Assert.Equal(ExportField.Emails, m["Email"]);
        Assert.Equal(ExportField.Website, m["Сайт"]);
        Assert.Equal(ExportField.Score, m["Оценка"]);
        Assert.Equal(ExportField.Region, m["Город"]);
        Assert.Equal(ExportField.DiscoveredAt, m["Найден"]);
        Assert.False(m.ContainsKey("Статус")); // status писать не умеем
    }

    [Fact]
    public void NotionValues()
    {
        var l = Lead();
        Assert.Equal(72, (double?)NotionDestination.PropertyValue(ExportField.Score, l, "number")!["number"]);
        Assert.Equal("info@zerno.ru", (string?)NotionDestination.PropertyValue(ExportField.Emails, l, "email")!["email"]);
        Assert.Equal("+7 843 212-48-90", (string?)NotionDestination.PropertyValue(ExportField.Phone, l, "phone_number")!["phone_number"]);
        Assert.Equal("Казань", (string?)NotionDestination.PropertyValue(ExportField.Region, l, "select")!["select"]!["name"]);
        Assert.Equal("2026-11-05T09:00:00Z", (string?)NotionDestination.PropertyValue(ExportField.DiscoveredAt, l, "date")!["date"]!["start"]);
        Assert.True((bool?)NotionDestination.PropertyValue(ExportField.Favorite, l, "checkbox")!["checkbox"]);
        Assert.Null(NotionDestination.PropertyValue(ExportField.Note, l, "rich_text")); // пустое поле не отправляется
        Assert.Null(NotionDestination.PropertyValue(ExportField.Title, l, "status"));
    }

    [Fact]
    public async Task NotionPageBodyAndRequest()
    {
        var d = new NotionDestination(" secret_x ", "db-1",
            [new NotionProperty("Название", "title"), new NotionProperty("Сайт", "url")],
            new Dictionary<string, ExportField> { ["Название"] = ExportField.Title, ["Сайт"] = ExportField.Website });
        Assert.Equal("secret_x", d.Token);
        var body = d.PageBody(Lead());
        Assert.Equal("db-1", (string?)body["parent"]!["database_id"]);
        Assert.Equal("https://zerno.ru", (string?)body["properties"]!["Сайт"]!["url"]);
        Assert.NotNull(body["properties"]!["Название"]);
        Assert.DoesNotContain("secret_x", body.ToJsonString(), StringComparison.Ordinal);

        var http = new MockCrmHttp((_, _) => ("""{"object":"page","id":"page-1"}""", 200));
        Assert.Equal("page-1", await d.SendAsync(Lead(), http, CancellationToken.None));
        var req = http.Requests[0].Request;
        Assert.Equal("https://api.notion.com/v1/pages", req.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer secret_x", req.Headers.Authorization!.ToString());
        Assert.Equal("2022-06-28", req.Headers.GetValues("Notion-Version").Single());
    }

    [Theory]
    [InlineData(401, "unauthorized", CrmErrorKind.Unauthorized)]
    [InlineData(404, "object_not_found", CrmErrorKind.NotFound)]
    [InlineData(403, "restricted_resource", CrmErrorKind.Forbidden)]
    [InlineData(429, "rate_limited", CrmErrorKind.RateLimited)]
    [InlineData(400, "validation_error", CrmErrorKind.Server)]
    public void NotionErrors(int status, string code, CrmErrorKind expected)
    {
        var json = $$"""{"object":"error","status":{{status}},"code":"{{code}}","message":"msg"}""";
        var e = Assert.Throws<CrmException>(() => NotionDestination.Check(Encoding.UTF8.GetBytes(json), status));
        Assert.Equal(expected, e.Kind);
        if (status == 400)
        {
            Assert.Contains("msg", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Webhook()
    {
        Assert.Throws<CrmException>(() => new WebhookDestination("http://hook.example.com"));
        _ = new WebhookDestination("https://hook.eu1.make.com/abc");
        var p = WebhookDestination.Payload(Lead());
        Assert.Equal("l1", (string?)p["lead_id"]);
        Assert.Equal("Кофейня «Зерно»", (string?)p["lead"]!["title"]);
        Assert.Equal(72, (double?)p["lead"]!["score"]);
        Assert.Equal("lead.exported", (string?)p["event"]);

        var http = new MockCrmHttp((_, _) => ("{}", 200));
        Assert.Equal("l1", await new WebhookDestination("https://hook.example.com/x").SendAsync(Lead(), http, CancellationToken.None));
        Assert.Equal("l1", http.Requests[0].Request.Headers.GetValues("X-LeadGen-Lead-Id").Single());

        var gone = new MockCrmHttp((_, _) => ("", 410));
        Assert.Equal(CrmErrorKind.NotFound, (await Assert.ThrowsAsync<CrmException>(() => new WebhookDestination("https://hook.example.com/x").SendAsync(Lead(), gone, CancellationToken.None))).Kind);
    }

    [Fact]
    public void Ledger()
    {
        var l = new ExportLedger();
        l.Sent["a"] = "1";
        Assert.Equal(l.Sent, ExportLedger.Decode(l.Encoded()).Sent);
        Assert.Empty(ExportLedger.Decode(null).Sent);
        Assert.Empty(ExportLedger.Decode("мусор").Sent);
        var k = ExportLedger.StorageKey(CrmKind.Webhook, "https://hook.example.com/secret-token");
        Assert.DoesNotContain("secret", k, StringComparison.Ordinal);
        Assert.Equal(k, ExportLedger.StorageKey(CrmKind.Webhook, "HTTPS://hook.example.com/SECRET-token"));
        Assert.NotEqual(k, ExportLedger.StorageKey(CrmKind.Bitrix, "https://hook.example.com/secret-token"));
    }

    private sealed class Fake(ISet<string> failing, ISet<string> fatal) : ICrmDestination
    {
        public CrmKind Kind => CrmKind.Webhook;

        public string Target => "fake";

        public TimeSpan Pause => TimeSpan.Zero;

        public Task<string> SendAsync(LeadItem item, ICrmHttp http, CancellationToken ct)
        {
            if (fatal.Contains(item.Id))
            {
                throw new CrmException(CrmErrorKind.Unauthorized, "нет");
            }

            if (failing.Contains(item.Id))
            {
                throw new CrmException(CrmErrorKind.Server, "плохие данные", 400);
            }

            return Task.FromResult("r-" + item.Id);
        }
    }

    [Fact]
    public async Task RunnerSkipsDuplicatesAndReportsErrors()
    {
        var items = new[] { "a", "b", "c", "d" }.Select(i => Lead(i, "Лид " + i)).ToList();
        var http = new MockCrmHttp((_, _) => ("{}", 200));
        var seed = new ExportLedger();
        seed.Sent["a"] = "old";
        var (report, ledger) = await CrmExporter.RunAsync(items, new Fake(new HashSet<string> { "c" }, new HashSet<string>()), seed, http);
        Assert.Equal(2, report.Exported);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(["Лид c"], report.Failures.Select(f => f.Title));
        Assert.Equal(new Dictionary<string, string> { ["a"] = "old", ["b"] = "r-b", ["d"] = "r-d" }, ledger.Sent);
        Assert.False(report.StoppedEarly);

        var (again, _) = await CrmExporter.RunAsync(items, new Fake(new HashSet<string>(), new HashSet<string>()), ledger, http);
        Assert.Equal(1, again.Exported); // повторно уходит только c
        Assert.Equal(3, again.Skipped);

        var (resent, _) = await CrmExporter.RunAsync(items, new Fake(new HashSet<string>(), new HashSet<string>()), ledger, http, resend: true);
        Assert.Equal(4, resent.Exported);
    }

    [Fact]
    public async Task RunnerStopsOnAuthErrorAndSupportsCancel()
    {
        var items = new[] { "a", "b", "c" }.Select(i => Lead(i)).ToList();
        var http = new MockCrmHttp((_, _) => ("{}", 200));
        var (report, ledger) = await CrmExporter.RunAsync(items, new Fake(new HashSet<string>(), new HashSet<string> { "b" }), new ExportLedger(), http);
        Assert.Equal(1, report.Exported);
        Assert.True(report.StoppedEarly);
        Assert.Single(report.Failures);
        Assert.Equal(["a"], ledger.Sent.Keys);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (cancelled, _) = await CrmExporter.RunAsync(items, new Fake(new HashSet<string>(), new HashSet<string>()), new ExportLedger(), http, ct: cts.Token);
        Assert.True(cancelled.StoppedEarly);
        Assert.Equal(0, cancelled.Exported);
    }

    [Fact]
    public async Task RunnerRetriesOnceAfterRateLimit()
    {
        var calls = 0;
        var d = new Flaky(() => ++calls == 1 ? throw new CrmException(CrmErrorKind.RateLimited, "") : "ok");
        var waits = new List<TimeSpan>();
        var (report, ledger) = await CrmExporter.RunAsync([Lead()], d, new ExportLedger(), new MockCrmHttp((_, _) => ("{}", 200)),
            delay: (t, _) =>
            {
                waits.Add(t);
                return Task.CompletedTask;
            });
        Assert.Equal(1, report.Exported);
        Assert.Equal("ok", ledger.Sent["l1"]);
        Assert.Equal([TimeSpan.FromSeconds(2)], waits);
    }

    private sealed class Flaky(Func<string> action) : ICrmDestination
    {
        public CrmKind Kind => CrmKind.Webhook;

        public string Target => "flaky";

        public TimeSpan Pause => TimeSpan.Zero;

        public Task<string> SendAsync(LeadItem item, ICrmHttp http, CancellationToken ct) => Task.FromResult(action());
    }
}

public class VersionAndUpdateTests : IDisposable
{
    private static readonly string Digest = new('a', 64);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void VersionParsing()
    {
        Assert.Equal([1, 0, 4], AppVersion.Parse("v1.0.4")!.Parts);
        Assert.Equal([1, 2], AppVersion.Parse("1.2")!.Parts);
        Assert.Equal([1, 0, 5], AppVersion.Parse("1.0.5-beta.1")!.Parts);
        Assert.Equal([1, 0, 6, 1], AppVersion.Parse("v1.0.6.1")!.Parts);
        foreach (var bad in new[] { "", "v", "1..2", "latest", "1.2.3.4.5", null })
        {
            Assert.Null(AppVersion.Parse(bad));
        }
    }

    [Fact]
    public void VersionComparison()
    {
        Assert.True(AppVersion.Parse("1.0.4")! < AppVersion.Parse("1.0.5")!);
        Assert.True(AppVersion.Parse("1.0.9")! < AppVersion.Parse("1.0.10")!);
        Assert.True(AppVersion.Parse("1.9")! < AppVersion.Parse("2.0")!);
        Assert.Equal(AppVersion.Parse("1.0")!, AppVersion.Parse("1.0.0")!);
        Assert.Equal(AppVersion.Parse("1.0")!.GetHashCode(), AppVersion.Parse("1.0.0")!.GetHashCode());
        Assert.False(AppVersion.Parse("1.0.5")! < AppVersion.Parse("1.0.4")!);
        Assert.True(AppVersion.Parse("1.0.6")! < AppVersion.Parse("1.0.6.1")!);
        Assert.True(AppVersion.Parse("1.0.6.1")! < AppVersion.Parse("1.0.7")!);
        Assert.Equal("1.0.5", AppVersion.Parse("v1.0.5")!.ToString());
    }

    private static string ReleaseJson(string tag = "v1.0.5", bool draft = false, bool prerelease = false, string? assets = null) => $$"""
        {"tag_name":"{{tag}}","name":"Customers {{tag}}","body":"Что нового","html_url":"https://github.com/xop1tt/CLIENTS/releases/tag/{{tag}}",
         "draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":{{assets ?? $$"""
        [{"name":"Customers-{{tag}}-Windows-x64-Setup.exe","browser_download_url":"https://example.com/setup.exe","size":100,"digest":"sha256:{{Digest}}"},
         {"name":"Customers-{{tag}}-Windows-x64-Setup.exe.sha256","browser_download_url":"https://example.com/setup.sha256","size":90,"digest":null},
         {"name":"Customers-{{tag}}-macOS.dmg","browser_download_url":"https://example.com/a.dmg"}]
        """}}}
        """;

    private static async Task<UpdateOffer?> Check(string json, string current, int status = 200)
    {
        var transport = new MockTransport(r => new HttpResult(status, Encoding.UTF8.GetBytes(json), new Dictionary<string, string>(), r.Url));
        return await new UpdateChecker(transport).CheckAsync(AppVersion.Parse(current)!);
    }

    [Fact]
    public async Task NewerReleaseIsOffered()
    {
        var u = (await Check(ReleaseJson(), "1.0.4"))!;
        Assert.Equal("1.0.5", u.Version.ToString());
        Assert.Equal("Customers-v1.0.5-Windows-x64-Setup.exe", u.Installer.Name);
        Assert.Equal("Что нового", u.Release.Body);
        Assert.NotNull(u.Release.ChecksumFor(u.Installer));
    }

    [Fact]
    public async Task OlderDraftPrereleaseAndMissingInstallerAreIgnored()
    {
        Assert.Null(await Check(ReleaseJson(), "1.0.5"));
        Assert.Null(await Check(ReleaseJson(), "1.1"));
        Assert.Null(await Check(ReleaseJson(draft: true), "1.0.4"));
        Assert.Null(await Check(ReleaseJson(prerelease: true), "1.0.4"));
        Assert.Null(await Check(ReleaseJson(assets: """[{"name":"notes.txt","browser_download_url":"https://example.com/n.txt"}]"""), "1.0.4"));
        Assert.Null(await Check("{}", "1.0.4", 404));
        Assert.Null(await Check("не json", "1.0.4"));
        await Assert.ThrowsAsync<HttpFailure>(() => Check("{}", "1.0.4", 500));
    }

    [Fact]
    public async Task DownloadChecksSha256()
    {
        var payload = Encoding.UTF8.GetBytes("installer-bytes");
        var good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var assets = $$"""
            [{"name":"Customers-v1.0.5-Windows-x64-Setup.exe","browser_download_url":"https://example.com/setup.exe","digest":"sha256:{{good}}"}]
            """;
        var offer = (await Check(ReleaseJson(assets: assets), "1.0.4"))!;
        var transport = new MockTransport(r => new HttpResult(200, payload, new Dictionary<string, string>(), r.Url));
        var path = await new UpdateChecker(transport).DownloadAsync(offer, _dir.File("dl"));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));

        var tampered = new MockTransport(r => new HttpResult(200, Encoding.UTF8.GetBytes("подмена"), new Dictionary<string, string>(), r.Url));
        var e = await Assert.ThrowsAsync<HttpFailure>(() => new UpdateChecker(tampered).DownloadAsync(offer, _dir.File("dl2")));
        Assert.Contains("контрольная сумма", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_dir.File("dl2")) && Directory.EnumerateFiles(_dir.File("dl2")).Any());
    }

    [Fact]
    public async Task DownloadUsesChecksumFileAndRefusesWithoutAny()
    {
        var payload = Encoding.UTF8.GetBytes("installer-bytes");
        var good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var offer = (await Check(ReleaseJson(assets: """
            [{"name":"Customers-v1.0.5-Windows-x64-Setup.exe","browser_download_url":"https://example.com/setup.exe","digest":"md5:123"},
             {"name":"Customers-v1.0.5-Windows-x64-Setup.exe.sha256","browser_download_url":"https://example.com/setup.sha256"}]
            """), "1.0.4"))!;
        var transport = new MockTransport(r => r.Url.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? MockTransport.Ok(good.ToUpperInvariant() + "  Customers-v1.0.5-Windows-x64-Setup.exe\n", r.Url)
            : new HttpResult(200, payload, new Dictionary<string, string>(), r.Url));
        Assert.True(File.Exists(await new UpdateChecker(transport).DownloadAsync(offer, _dir.File("dl"))));

        var bare = (await Check(ReleaseJson(assets: """
            [{"name":"Customers-v1.0.5-Windows-x64-Setup.exe","browser_download_url":"https://example.com/setup.exe"}]
            """), "1.0.4"))!;
        var e = await Assert.ThrowsAsync<HttpFailure>(() => new UpdateChecker(transport).DownloadAsync(bare, _dir.File("dl3")));
        Assert.Contains("SHA-256", e.Message, StringComparison.Ordinal);
    }
}

public class SecretStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void MemoryStoreRoundTrip()
    {
        var s = new MemorySecretStore();
        Assert.Null(s.Get("a"));
        s.Set("a", "1");
        Assert.Equal("1", s.Get("a"));
        s.Delete("a");
        Assert.Null(s.Get("a"));
    }

    [Fact]
    public void DpapiStoreEncryptsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI есть только в Windows; на CI (windows-latest) проверка выполняется
        }

        var store = new DpapiSecretStore(_dir.Path);
        store.Set("token", "очень-секретный-токен");
        Assert.Equal("очень-секретный-токен", store.Get("token"));
        foreach (var file in Directory.GetFiles(_dir.Path))
        {
            Assert.DoesNotContain("очень-секретный-токен", File.ReadAllText(file), StringComparison.Ordinal);
        }

        store.Delete("token");
        Assert.Null(store.Get("token"));
    }
}
