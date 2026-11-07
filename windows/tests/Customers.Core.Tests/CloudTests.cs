using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Core.Tests;

/// <summary>Заглушка сервера: отвечает по сценарию и записывает запросы.</summary>
internal sealed class MockCloud(Func<HttpRequestMessage, string, (int Status, string Body)> handler) : ICloudTransport
{
    private readonly object _gate = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public List<string> Paths
    {
        get
        {
            lock (_gate)
            {
                return Requests.Select(r => r.Request.RequestUri!.AbsolutePath).ToList();
            }
        }
    }

    public async Task<CloudResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_gate)
        {
            Requests.Add((request, body));
        }

        var (status, text) = handler(request, body);
        return new CloudResponse(status, Encoding.UTF8.GetBytes(text));
    }
}

internal static class CloudFixtures
{
    public static readonly CloudConfig Config = CloudConfig.Create("https://example.supabase.co", "sb_publishable_test")!;

    public static string Token(string access = "a1", string refresh = "r1", int expiresIn = 3600, string email = "u@example.com") =>
        new JsonObject {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["expires_in"] = expiresIn,
            ["user"] = new JsonObject { ["id"] = "11111111-1111-1111-1111-111111111111", ["email"] = email, ["email_confirmed_at"] = "2026-11-06T10:00:00Z" },
        }.ToJsonString();
}

public class CloudConfigTests
{
    private static string Jwt(string role) =>
        "eyJhbGciOiJIUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"role\":\"{role}\"}}")) + ".sig";

    [Fact]
    public void RejectsSecretKeysAndPlainHttp()
    {
        Assert.NotNull(CloudConfig.Create("https://abc.supabase.co/", "sb_publishable_x"));
        Assert.Null(CloudConfig.Create("https://abc.supabase.co", "sb_secret_x"));
        Assert.Null(CloudConfig.Create("http://abc.supabase.co", "sb_publishable_x"));
        Assert.Null(CloudConfig.Create("https://abc.supabase.co", Jwt("service_role")));
        Assert.NotNull(CloudConfig.Create("https://abc.supabase.co", Jwt("anon")));
        Assert.Null(CloudConfig.Create("", "sb_publishable_x"));
        Assert.Null(CloudConfig.Create("https://abc.supabase.co", ""));
        Assert.Null(CloudConfig.Create(null, null));
        Assert.NotNull(CloudConfig.Create("http://127.0.0.1:54321", "sb_publishable_x")); // локальный стенд
    }
}

public class ValidationTests
{
    [Fact]
    public void EmailAndPassword()
    {
        Assert.Equal("user@example.com", Validation.NormalizedEmail("  User@Example.COM "));
        foreach (var bad in new[] { "", "a@b", "no-at", "a b@c.ru", "a@b.ru\r\nBcc: x@y.ru" })
        {
            Assert.Null(Validation.NormalizedEmail(bad));
        }

        Assert.Null(Validation.PasswordProblem("Str0ngPass"));
        Assert.NotNull(Validation.PasswordProblem("short1A"));
        Assert.NotNull(Validation.PasswordProblem("alllowercase1"));
        Assert.NotNull(Validation.PasswordProblem("NoDigitsHere"));
        Assert.NotNull(Validation.PasswordProblem(new string('A', 40) + new string('a', 40) + "1"));
        Assert.Equal("123456", Validation.Code(" 123 456 "));
        Assert.Null(Validation.Code("12"));
    }

    private static CloudErrorKind Map(int status, string body) => CloudException.FromAuth(status, Encoding.UTF8.GetBytes(body)).Kind;

    [Fact]
    public void AuthErrorMapping()
    {
        Assert.Equal(CloudErrorKind.InvalidCredentials, Map(400, """{"error_code":"invalid_credentials","msg":"x"}"""));
        Assert.Equal(CloudErrorKind.EmailNotConfirmed, Map(400, """{"error_code":"email_not_confirmed"}"""));
        Assert.Equal(CloudErrorKind.WeakPassword, Map(422, """{"error_code":"weak_password"}"""));
        Assert.Equal(CloudErrorKind.InvalidCode, Map(403, """{"error_code":"otp_expired"}"""));
        Assert.Equal(CloudErrorKind.SessionExpired, Map(400, """{"error_code":"refresh_token_already_used"}"""));
        Assert.Equal(CloudErrorKind.MailUnavailable, Map(500, """{"error_code":"unexpected_failure","msg":"Service currently unavailable due to hook"}"""));
        Assert.Equal(CloudErrorKind.RateLimited, Map(429, """{"error_code":"over_request_rate_limit"}"""));
        Assert.Equal(CloudErrorKind.Server, Map(500, "<html>"));
        Assert.All(Enum.GetValues<CloudErrorKind>(), k => Assert.False(string.IsNullOrEmpty(new CloudException(k, "x").Message)));
    }

    [Fact]
    public void SessionRoundTrip()
    {
        var s = new CloudSession("a", "r", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), new CloudUser("1", "u@example.com", null, "2026-01-01T00:00:00Z"));
        var back = CloudSession.FromJson(s.ToJson())!;
        Assert.Equal(s, back);
        Assert.True(back.User.IsConfirmed);
        Assert.Null(CloudSession.FromJson("мусор"));
        Assert.Null(CloudSession.FromJson(null));
        Assert.True(new CloudSession("a", "r", DateTimeOffset.UtcNow.AddSeconds(10), s.User).ExpiresSoon());
        Assert.False(new CloudSession("a", "r", DateTimeOffset.UtcNow.AddHours(1), s.User).ExpiresSoon());
    }
}

public class CloudAuthTests
{
    [Fact]
    public async Task SignUpAsksGateFirstAndStopsOnLimit()
    {
        var t = new MockCloud((req, _) => req.RequestUri!.AbsolutePath.EndsWith("email-gate", StringComparison.Ordinal)
            ? (429, """{"allowed":false,"reason":"cooldown","retry_after":600,"message":"Письмо уже отправлено, проверьте почту. Повторите через 10 мин."}""")
            : (200, "{}"));
        var auth = new CloudAuth(CloudFixtures.Config, t, new MemorySessionStore(), "dev");
        var e = await Assert.ThrowsAsync<CloudException>(() => auth.SignUpAsync("u@example.com", "Str0ngPass"));
        Assert.Equal(CloudErrorKind.RateLimited, e.Kind);
        Assert.Contains("10 мин", e.Message, StringComparison.Ordinal);
        Assert.Equal(["/functions/v1/email-gate"], t.Paths); // до Auth дело не дошло
        var gate = JsonNode.Parse(t.Requests[0].Body)!;
        Assert.Equal("dev", (string?)gate["device_id"]);
        Assert.Equal("signup", (string?)gate["action"]);
        Assert.Equal("sb_publishable_test", t.Requests[0].Request.Headers.GetValues("apikey").Single());
    }

    [Fact]
    public async Task SuccessfulSignUpCallsAuthAfterGate()
    {
        var t = new MockCloud((req, _) => (200, "{}"));
        var auth = new CloudAuth(CloudFixtures.Config, t, new MemorySessionStore(), "dev");
        await auth.SignUpAsync(" U@Example.com ", "Str0ngPass");
        Assert.Equal(["/functions/v1/email-gate", "/auth/v1/signup"], t.Paths);
        var body = JsonNode.Parse(t.Requests[1].Body)!;
        Assert.Equal("u@example.com", (string?)body["email"]);
        Assert.Equal("leadgenerator://auth/callback", (string?)body["options"]!["email_redirect_to"]);
    }

    [Fact]
    public async Task WeakPasswordAndBadEmailNeverHitNetwork()
    {
        var t = new MockCloud((_, _) => (200, "{}"));
        var auth = new CloudAuth(CloudFixtures.Config, t, new MemorySessionStore(), "d");
        Assert.Equal(CloudErrorKind.InvalidEmail, (await Assert.ThrowsAsync<CloudException>(() => auth.SignUpAsync("bad", "Str0ngPass"))).Kind);
        Assert.Equal(CloudErrorKind.WeakPassword, (await Assert.ThrowsAsync<CloudException>(() => auth.SignUpAsync("a@b.ru", "weak"))).Kind);
        Assert.Empty(t.Paths);
    }

    [Fact]
    public async Task SignInStoresSessionAndRefreshesOnceWhenExpiring()
    {
        var store = new MemorySessionStore();
        var refreshes = 0;
        var t = new MockCloud((req, _) =>
        {
            var q = req.RequestUri!.Query;
            if (q.Contains("grant_type=password", StringComparison.Ordinal))
            {
                return (200, CloudFixtures.Token("old", "r1", 10));
            }

            if (q.Contains("grant_type=refresh_token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref refreshes);
                return (200, CloudFixtures.Token("new", "r2"));
            }

            return (200, "[]");
        });
        var auth = new CloudAuth(CloudFixtures.Config, t, store, "d");
        await auth.SignInAsync("u@example.com", "Str0ngPass");
        Assert.Equal("old", store.Load()?.AccessToken);

        // Токен истекает через 10 с — перед запросом он обновляется один раз даже при параллельных вызовах.
        var results = await Task.WhenAll(auth.ValidSessionAsync(), auth.ValidSessionAsync());
        Assert.All(results, s => Assert.Equal("new", s.AccessToken));
        Assert.Equal(1, refreshes);
        Assert.Equal("r2", store.Load()?.RefreshToken);
    }

    [Fact]
    public async Task RevokedSessionSignsOut()
    {
        var expired = new CloudSession("a", "r", DateTimeOffset.UtcNow.AddSeconds(-5), new CloudUser("1", "u@example.com"));
        var store = new MemorySessionStore();
        store.Save(expired);
        var t = new MockCloud((_, _) => (400, """{"error_code":"refresh_token_not_found","msg":"Invalid Refresh Token"}"""));
        var auth = new CloudAuth(CloudFixtures.Config, t, store, "d");
        Assert.Equal(CloudErrorKind.SessionExpired, (await Assert.ThrowsAsync<CloudException>(() => auth.ValidSessionAsync())).Kind);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task RetryOnceOn401()
    {
        var store = new MemorySessionStore();
        store.Save(new CloudSession("stale", "r", DateTimeOffset.UtcNow.AddSeconds(3000), new CloudUser("1", "u@example.com")));
        var t = new MockCloud((req, _) =>
        {
            if (req.RequestUri!.Query.Contains("refresh_token", StringComparison.Ordinal))
            {
                return (200, CloudFixtures.Token("fresh"));
            }

            return req.Headers.Authorization?.Parameter == "fresh" ? (200, "[]") : (401, """{"message":"JWT expired"}""");
        });
        var auth = new CloudAuth(CloudFixtures.Config, t, store, "d");
        var res = await auth.AuthorizedAsync("GET", "/rest/v1/leads");
        Assert.Equal(200, res.Status);
    }

    [Fact]
    public async Task SecondUnauthorizedSignsOut()
    {
        var store = new MemorySessionStore();
        store.Save(new CloudSession("stale", "r", DateTimeOffset.UtcNow.AddSeconds(3000), new CloudUser("1", "u@example.com")));
        var t = new MockCloud((req, _) => req.RequestUri!.Query.Contains("refresh_token", StringComparison.Ordinal)
            ? (200, CloudFixtures.Token("fresh"))
            : (401, "{}"));
        var auth = new CloudAuth(CloudFixtures.Config, t, store, "d");
        await Assert.ThrowsAsync<CloudException>(() => auth.AuthorizedAsync("GET", "/rest/v1/leads"));
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task SignOutAlwaysClearsSession()
    {
        var store = new MemorySessionStore();
        store.Save(new CloudSession("a", "r", DateTimeOffset.UtcNow.AddHours(1), new CloudUser("1", "u@example.com")));
        var t = new MockCloud((_, _) => (500, "{}"));
        var auth = new CloudAuth(CloudFixtures.Config, t, store, "d");
        await auth.SignOutAsync();
        Assert.Null(store.Load());
        Assert.Null(auth.Session);
    }

    [Fact]
    public async Task CallbackParsing()
    {
        var t = new MockCloud((req, _) => req.RequestUri!.AbsolutePath == "/auth/v1/user"
            ? (200, """{"id":"u1","email":"u@example.com"}""")
            : (404, "{}"));
        var auth = new CloudAuth(CloudFixtures.Config, t, new MemorySessionStore(), "d");
        var ok = await auth.HandleCallbackAsync("leadgenerator://auth/callback#access_token=A&refresh_token=R&expires_in=3600&type=recovery");
        Assert.Equal("recovery", ok.SignedInType);
        Assert.Equal("u@example.com", auth.User?.Email);

        var msg = await auth.HandleCallbackAsync("leadgenerator://auth/callback#message=Confirmation+link+accepted");
        Assert.Equal("Confirmation link accepted", msg.Message);

        var e = await Assert.ThrowsAsync<CloudException>(() =>
            auth.HandleCallbackAsync("leadgenerator://auth/callback#error=access_denied&error_code=otp_expired&error_description=expired"));
        Assert.Equal(CloudErrorKind.InvalidCode, e.Kind);
        await Assert.ThrowsAsync<CloudException>(() => auth.HandleCallbackAsync("просто текст"));
    }

    [Fact]
    public async Task VerifyWithCodeSignsIn()
    {
        var t = new MockCloud((req, _) => req.RequestUri!.AbsolutePath == "/auth/v1/verify" ? (200, CloudFixtures.Token()) : (404, "{}"));
        var store = new MemorySessionStore();
        var auth = new CloudAuth(CloudFixtures.Config, t, store, "d");
        var session = await auth.VerifyAsync("u@example.com", "123 456", "signup");
        Assert.NotNull(session);
        Assert.Equal("123456", (string?)JsonNode.Parse(t.Requests[0].Body)!["token"]);
        Assert.NotNull(store.Load());
        Assert.Equal(CloudErrorKind.InvalidCode, (await Assert.ThrowsAsync<CloudException>(() => auth.VerifyAsync("u@example.com", "12", "signup"))).Kind);
    }
}

public class SyncJournalTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private LeadStore Store(string name)
    {
        var s = new LeadStore(_dir.File(name + ".db"));
        s.EnableSyncTracking();
        return s;
    }

    private static LeadData Lead(string title = "Нужен сайт") => new() {
        Kind = LeadKind.Order,
        Source = "fl_ru",
        ExternalId = "1",
        Title = title,
        DiscoveredAt = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000),
        Score = 70,
        Relevance = Relevance.Medium,
    };

    [Fact]
    public void JournalSurvivesReopenAndClearsAfterPush()
    {
        string path;
        using (var s = Store("a"))
        {
            path = s.Path;
            s.Save(Lead(), "L1", "fp");
            s.SetFavorite(["L1"], true);
            s.SetNote("L1", "позвонить");
            s.AddExclusion(ExclusionKind.Domain, "example.ru");
            s.SetSyncedValue("draft.signature", "\"Максим\"");
        }

        // «Аварийное завершение»: открываем базу заново — журнал на месте.
        using var reopened = new LeadStore(path);
        Assert.True(reopened.SyncTracking);
        var p = reopened.PendingChanges(100);
        var lead = Assert.Single(p.Leads);
        Assert.Equal(new HashSet<string> { "data", "favorite", "note" }, lead.Ts.Keys.ToHashSet());
        Assert.True(lead.Favorite);
        var ex = Assert.Single(p.Exclusions);
        Assert.Equal(("domain", "example.ru", false), (ex.Kind, ex.Value, ex.Deleted));
        Assert.Equal("Максим", (string?)p.Settings.Single().Value);
        reopened.MarkPushed(p);
        Assert.True(reopened.PendingChanges(100).IsEmpty);
    }

    [Fact]
    public void ChangeDuringPushStaysInJournal()
    {
        using var s = Store("a");
        s.Save(Lead(), "L1", "fp");
        var p = s.PendingChanges(100);
        Thread.Sleep(15);
        s.SetNote("L1", "изменено во время выгрузки");
        s.MarkPushed(p);
        var left = s.PendingChanges(100);
        Assert.Equal(new HashSet<string> { "note" }, Assert.Single(left.Leads).Ts.Keys.ToHashSet());
    }

    private static RemoteLead RemoteOf(string id, LeadData data, bool favorite, bool deleted, string note, Dictionary<string, string> ts, long seq) => new() {
        LeadId = id,
        Fingerprint = "fp",
        Kind = "order",
        Source = "fl_ru",
        Title = data.Title,
        Score = 70,
        Relevance = "medium",
        DiscoveredAt = "2026-09-21T13:46:40+00:00",
        Data = data.ToJson(),
        Favorite = favorite,
        Deleted = deleted,
        Seen = true,
        Note = note,
        FieldTs = ts,
        ServerSeq = seq,
    };

    [Fact]
    public void ApplyRemoteDoesNotEchoAndRespectsNewerLocal()
    {
        using var s = Store("a");
        s.Save(Lead(), "L1", "fp");
        s.MarkPushed(s.PendingChanges(100));
        s.SetNote("L1", "локально новее");
        var remote = RemoteOf("L1", Lead(), true, false, "старое с сервера",
            new() { ["favorite"] = "2099-01-01T00:00:00Z", ["note"] = "2001-01-01T00:00:00Z", ["seen"] = "2099-01-01T00:00:00Z" }, 5);
        s.ApplyRemote([remote], [], []);
        var item = s.AllLeads().Single();
        Assert.True(item.Favorite); // с сервера
        Assert.Equal("локально новее", item.Note); // локальное новее — не затёрто
        // Применённое с сервера не попало в журнал (нет эха), осталась только заметка.
        Assert.Equal(new HashSet<string> { "note" }, Assert.Single(s.PendingChanges(10).Leads).Ts.Keys.ToHashSet());
    }

    [Fact]
    public void RemoteDeletionAndNewLeadFromOtherDevice()
    {
        using var s = Store("a");
        s.AddExclusion(ExclusionKind.Domain, "old.ru");
        s.MarkPushed(s.PendingChanges(100));
        s.ApplyRemote(
            [RemoteOf("L9", Lead("С другого компьютера"), false, true, "", [], 3)],
            [new RemoteExclusion { Kind = "domain", Value = "old.ru", Deleted = true, ChangedAt = "2099-01-01T00:00:00Z", ServerSeq = 4 }],
            [
                new RemoteSetting { Key = "search.auto", Value = JsonValue.Create(30), ChangedAt = "2026-11-01T10:00:00Z", ServerSeq = 5 },
                new RemoteSetting { Key = "evil.key", Value = JsonValue.Create("x"), ChangedAt = "2026-11-01T10:00:00Z", ServerSeq = 6 },
            ]);
        Assert.Empty(s.Exclusions());
        Assert.Equal(["L9"], s.AllLeads(includeDeleted: true).Select(l => l.Id));
        Assert.Empty(s.AllLeads()); // удалённый на другом устройстве — скрыт, поиск его не вернёт
        Assert.Equal("30", s.Value("search.auto"));
        Assert.Null(s.Value("evil.key")); // чужие ключи настроек не принимаются
        Assert.True(s.PendingChanges(10).IsEmpty);
    }

    [Fact]
    public void ImportLocalAccountCopiesWithoutDuplicatesAndEarlyStamp()
    {
        using var local = new LeadStore(_dir.File("local-src.db"));
        local.Save(Lead("Первый"), "A", "fpA");
        local.Save(Lead("Второй"), "B", "fpB");
        local.SetFavorite(["A"], true);
        local.AddExclusion(ExclusionKind.Domain, "spam.ru");
        local.SetValue("draft.signature", "\"Подпись\"");
        using var cloud = Store("cloud");
        cloud.Save(Lead("Уже в облаке"), "B2", "fpB"); // тот же отпечаток
        cloud.MarkPushed(cloud.PendingChanges(100));

        Assert.Equal(1, cloud.ImportFrom(local));
        Assert.Equal(["A", "B2"], cloud.AllLeads().Select(l => l.Id).Order(StringComparer.Ordinal));
        Assert.True(cloud.AllLeads().First(l => l.Id == "A").Favorite);
        Assert.Equal("\"Подпись\"", cloud.Value("draft.signature"));
        var p = cloud.PendingChanges(100);
        var change = Assert.Single(p.Leads);
        Assert.Equal("A", change.LeadId);
        Assert.Equal(LeadStore.Stamp(LeadStore.ImportStamp), change.Ts["favorite"]);
        Assert.Equal(["spam.ru"], p.Exclusions.Select(e => e.Value));
        Assert.Equal(["draft.signature"], p.Settings.Select(x => x.Key));
        Assert.Equal(2, local.AllLeads().Count); // источник не изменился и журнала не ведёт
        Assert.False(local.SyncTracking);
        Assert.Equal(0, cloud.ImportFrom(local));
    }

    [Fact]
    public void LocalOnlyAccountsDoNotJournal()
    {
        using var s = new LeadStore(_dir.File("local.db"));
        s.Save(Lead(), "L1", "fp");
        s.SetFavorite(["L1"], true);
        Assert.True(s.PendingChanges(10).IsEmpty);
    }

    [Fact]
    public void RestoreInCloudAccountSendsEverythingAgain()
    {
        using var s = Store("a");
        s.Save(Lead(), "L1", "fp");
        s.MarkPushed(s.PendingChanges(100));
        s.SetSyncCursor("leads", 42);
        var backup = _dir.File("copy.db");
        s.Backup(backup);
        s.Restore(backup);
        Assert.True(s.SyncTracking);
        Assert.Equal(0, s.SyncCursor("leads"));
        Assert.Single(s.PendingChanges(100).Leads);
    }
}

public class SyncEngineTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CloudAuth SignedIn(MockCloud t)
    {
        var store = new MemorySessionStore();
        store.Save(new CloudSession("tok", "r", DateTimeOffset.UtcNow.AddHours(1), new CloudUser("1", "u@example.com")));
        return new CloudAuth(CloudFixtures.Config, t, store, "dev");
    }

    [Fact]
    public async Task PushesJournalThenPullsPagesByCursor()
    {
        using var store = new LeadStore(_dir.File("a.db"));
        store.EnableSyncTracking();
        store.Save(new LeadData { Kind = LeadKind.Order, Source = "fl_ru", ExternalId = "1", Title = "Нужен сайт", Score = 70, Relevance = Relevance.Medium }, "L1", "fp");

        var remoteRow = new JsonObject {
            ["lead_id"] = "R1",
            ["fingerprint"] = "fr",
            ["kind"] = "order",
            ["source"] = "fl_ru",
            ["title"] = "С сервера",
            ["score"] = 50,
            ["relevance"] = "low",
            ["discovered_at"] = "2026-11-01T10:00:00Z",
            ["data"] = new LeadData { Kind = LeadKind.Order, Source = "fl_ru", ExternalId = "r", Title = "С сервера" }.ToJson(),
            ["favorite"] = false,
            ["archived"] = false,
            ["deleted"] = false,
            ["seen"] = false,
            ["note"] = "",
            ["field_ts"] = new JsonObject(),
            ["server_seq"] = 7,
        };
        var t = new MockCloud((req, body) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("rpc/sync_push", StringComparison.Ordinal))
            {
                return (200, "{}");
            }

            if (path.EndsWith("/leads", StringComparison.Ordinal))
            {
                return (200, req.RequestUri.Query.Contains("server_seq=gt.0", StringComparison.Ordinal) ? new JsonArray(remoteRow.DeepClone()).ToJsonString() : "[]");
            }

            return (200, "[]");
        });
        var engine = new SyncEngine(SignedIn(t), store, "dev");
        var report = await engine.SyncAsync();
        Assert.Equal(1, report.Pushed);
        Assert.Equal(1, report.Pulled);
        Assert.Equal(7, store.SyncCursor("leads"));
        Assert.Equal(["L1", "R1"], store.AllLeads().Select(l => l.Id).Order(StringComparer.Ordinal));
        Assert.True(store.PendingChanges(10).IsEmpty);

        var push = t.Requests.First(r => r.Request.RequestUri!.AbsolutePath.EndsWith("sync_push", StringComparison.Ordinal));
        var payload = JsonNode.Parse(push.Body)!;
        Assert.Equal("dev", (string?)payload["p_device"]);
        Assert.Equal("L1", (string?)payload["p_changes"]!["leads"]![0]!["lead_id"]);
        Assert.Equal("Bearer tok", push.Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ServerErrorKeepsJournal()
    {
        using var store = new LeadStore(_dir.File("a.db"));
        store.EnableSyncTracking();
        store.Save(new LeadData { Source = "fl_ru", ExternalId = "1", Title = "Нужен сайт" }, "L1", "fp");
        var t = new MockCloud((_, _) => (500, """{"message":"database is down"}"""));
        var e = await Assert.ThrowsAsync<CloudException>(() => new SyncEngine(SignedIn(t), store, "dev").SyncAsync());
        Assert.Equal(CloudErrorKind.Server, e.Kind);
        Assert.Contains("database is down", e.Message, StringComparison.Ordinal);
        Assert.False(store.PendingChanges(10).IsEmpty); // ничего не потеряно, уйдёт при следующей попытке
    }

    [Fact]
    public async Task RemoteStats()
    {
        using var store = new LeadStore(_dir.File("a.db"));
        var t = new MockCloud((_, _) => (200, """{"leads":12,"exclusions":3}"""));
        var stats = await new SyncEngine(SignedIn(t), store, "dev").RemoteStatsAsync();
        Assert.Equal(12, stats["leads"]);
        Assert.Equal(3, stats["exclusions"]);
    }
}
