using System.Text.Json.Nodes;

namespace Customers.Presentation.Tests;

public sealed class LocalSessionTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private SessionModel Session(AppEnvironment? env = null) => new(env ?? _box.Env());

    [Fact]
    public async Task WithoutCloudConfigStartsInLocalMode()
    {
        var s = Session();
        Assert.False(s.CloudAvailable);
        Assert.Equal(AuthStage.Local, s.Stage);
        Assert.False(s.HasUsers);
        Assert.False(s.IsSignedIn);
        await s.RegisterAsync("maksim", "Хороший-пароль-1", "Хороший-пароль-1", remember: false);
        Assert.Null(s.Error);
        Assert.True(s.IsSignedIn);
        Assert.False(s.IsCloudSession);
        Assert.Equal("maksim", s.Current!.DisplayName);
        s.Current.Shutdown();
    }

    [Fact]
    public async Task RegisterValidatesInput()
    {
        var s = Session();
        await s.RegisterAsync("maksim", "Хороший-пароль-1", "другой пароль", remember: false);
        Assert.NotNull(s.Error);
        Assert.False(s.IsSignedIn);
        await s.RegisterAsync("maksim", "1", "1", remember: false);
        Assert.NotNull(s.Error);
        Assert.False(s.IsSignedIn);
        await s.RegisterAsync("a/b", "Хороший-пароль-1", "Хороший-пароль-1", remember: false);
        Assert.NotNull(s.Error);
        Assert.False(s.HasUsers);
    }

    [Fact]
    public async Task LoginLogoutAndRememberedUser()
    {
        var s = Session();
        await s.RegisterAsync("maksim", "Хороший-пароль-1", "Хороший-пароль-1", remember: true);
        Assert.True(s.Remembered);
        s.Logout();
        Assert.False(s.IsSignedIn);
        Assert.False(s.Remembered);

        await s.LoginAsync("maksim", "не тот пароль", remember: false);
        Assert.NotNull(s.Error);
        Assert.False(s.IsSignedIn);

        await s.LoginAsync("MAKSIM", "Хороший-пароль-1", remember: true);
        Assert.True(s.IsSignedIn);
        Assert.Null(s.Error);
        s.Current!.Shutdown();

        // Новый запуск: запомненный пользователь входит сам.
        var again = Session();
        Assert.True(again.IsSignedIn);
        Assert.Equal("maksim", again.Current!.Username, ignoreCase: true);
        again.SetRemembered(false);
        Assert.False(again.Remembered);
        again.Current.Shutdown();
        Assert.False(Session().IsSignedIn);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsLockLoginTemporarily()
    {
        var s = Session();
        await s.RegisterAsync("maksim", "Хороший-пароль-1", "Хороший-пароль-1", remember: false);
        s.Logout();
        for (var i = 0; i < 6; i++)
        {
            await s.LoginAsync("maksim", "неверный " + i, remember: false);
        }

        await s.LoginAsync("maksim", "Хороший-пароль-1", remember: false);
        Assert.False(s.IsSignedIn);
        Assert.Contains("попыт", s.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangePasswordRequiresOldAndMatchingNew()
    {
        var s = Session();
        await s.RegisterAsync("maksim", "Хороший-пароль-1", "Хороший-пароль-1", remember: false);
        Assert.NotNull(await s.ChangePasswordAsync("неверный", "новый пароль 22", "новый пароль 22"));
        Assert.NotNull(await s.ChangePasswordAsync("Хороший-пароль-1", "новый пароль 22", "иначе"));
        Assert.Null(await s.ChangePasswordAsync("Хороший-пароль-1", "новый пароль 22", "новый пароль 22"));
        s.Logout();
        await s.LoginAsync("maksim", "Хороший-пароль-1", remember: false);
        Assert.False(s.IsSignedIn);
        await s.LoginAsync("maksim", "новый пароль 22", remember: false);
        Assert.True(s.IsSignedIn);
        s.Current!.Shutdown();
    }

    [Fact]
    public async Task DeleteAccountKeepsBackupAndNeedsPassword()
    {
        var s = Session();
        await s.RegisterAsync("maksim", "Хороший-пароль-1", "Хороший-пароль-1", remember: true);
        Data.Seed(s.Current!);
        s.Current!.Prefs.BackupDirectory = Path.Combine(_box.Root, "saved");

        Assert.NotNull(await s.DeleteLocalAccountAsync("неверный"));
        Assert.True(s.IsSignedIn); // пользователь остался в своей записи
        Assert.Equal(4, s.Current!.AllLeads.Count);

        Assert.Null(await s.DeleteLocalAccountAsync("Хороший-пароль-1"));
        Assert.False(s.IsSignedIn);
        Assert.False(s.HasUsers);
        Assert.Contains("Копия базы сохранена", s.Info, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_box.Root, "saved"), "*.db"));
        Assert.Null(_box.Env().Device.RememberedUser);
    }

    [Fact]
    public async Task SameAccountReopensItsDataWithSameKey()
    {
        var s = Session();
        await s.RegisterAsync("maksim", "Хороший-пароль-1", "Хороший-пароль-1", remember: false);
        Data.Seed(s.Current!);
        s.Logout();
        await s.LoginAsync("maksim", "Хороший-пароль-1", remember: false);
        Assert.Null(s.Current!.StartupError);
        Assert.Equal(4, s.Current.AllLeads.Count);
        s.Current.Shutdown();
    }
}

public sealed class CloudSessionTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private static (int, string) Err(string code) => (400, new JsonObject { ["error_code"] = code, ["msg"] = code }.ToJsonString());

    private static (int, string) Route(HttpRequestMessage req, string body, Func<string, string, (int, string)>? extra = null)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (extra?.Invoke(path, body) is { } custom && custom.Item1 != 0)
        {
            return custom;
        }

        if (path.EndsWith("rpc/sync_push", StringComparison.Ordinal))
        {
            return (200, "{}");
        }

        if (path.StartsWith("/rest/v1/", StringComparison.Ordinal))
        {
            return (200, "[]");
        }

        return (404, "{}");
    }

    private SessionModel Session(MockCloud cloud) => new(_box.Env(Data.Config, cloud));

    [Fact]
    public void CloudConfigStartsAtSignIn()
    {
        var s = Session(new MockCloud((_, _) => (404, "{}")));
        Assert.True(s.CloudAvailable);
        Assert.Equal(AuthStage.SignIn, s.Stage);
        Assert.False(s.IsSignedIn);
    }

    [Fact]
    public async Task SignInOpensCloudAccountAndSyncsOnce()
    {
        var cloud = new MockCloud((req, body) => Route(req, body, (path, _) =>
            path.EndsWith("/auth/v1/token", StringComparison.Ordinal) ? (200, Data.Token(email: "Maksim@Example.com")) : (0, "")));
        var s = Session(cloud);
        await s.CloudSignInAsync("maksim@example.com", "Хороший-пароль-1");
        Assert.Null(s.Error);
        Assert.True(s.IsCloudSession);
        Assert.Equal("maksim@example.com", s.Current!.DisplayName, ignoreCase: true);
        Assert.NotNull(s.Sync);
        await s.Sync!.SyncNow();
        Assert.Equal(SyncState.Idle, s.Sync.State);
        Assert.Contains("/rest/v1/leads", cloud.Paths); // подтянуть изменения с сервера
        s.Logout();
        Assert.False(s.IsSignedIn);
        Assert.Equal(AuthStage.SignIn, s.Stage);
    }

    [Fact]
    public async Task SessionIsRestoredOnNextStartWithoutNetwork()
    {
        var cloud = new MockCloud((req, body) => Route(req, body, (path, _) =>
            path.EndsWith("/auth/v1/token", StringComparison.Ordinal) ? (200, Data.Token()) : (0, "")));
        var first = Session(cloud);
        await first.CloudSignInAsync("u@example.com", "Хороший-пароль-1");
        first.Sync?.Dispose();
        first.Current!.Shutdown();

        var offline = new MockCloud((_, _) => throw new HttpRequestException("нет сети"));
        var second = Session(offline);
        Assert.True(second.IsCloudSession);
        Assert.Null(second.Current!.StartupError);
        second.Sync?.Dispose();
        second.Current.Shutdown();
    }

    [Fact]
    public async Task UnconfirmedEmailLeadsToConfirmStage()
    {
        var s = Session(new MockCloud((req, body) => Route(req, body, (path, _) =>
            path.EndsWith("/auth/v1/token", StringComparison.Ordinal) ? Err("email_not_confirmed") : (0, ""))));
        await s.CloudSignInAsync("U@Example.com", "Хороший-пароль-1");
        Assert.Equal(AuthStage.Confirm, s.Stage);
        Assert.Equal("u@example.com", s.PendingEmail);
        Assert.NotNull(s.Info);
        Assert.False(s.IsSignedIn);
    }

    [Fact]
    public async Task WrongPasswordShowsReadableError()
    {
        var s = Session(new MockCloud((req, body) => Route(req, body, (path, _) =>
            path.EndsWith("/auth/v1/token", StringComparison.Ordinal) ? Err("invalid_credentials") : (0, ""))));
        await s.CloudSignInAsync("u@example.com", "не тот");
        Assert.False(s.IsSignedIn);
        Assert.False(string.IsNullOrWhiteSpace(s.Error));
        Assert.False(s.Busy);
    }

    [Fact]
    public async Task SignUpAsksGateThenWaitsForConfirmation()
    {
        var cloud = new MockCloud((req, body) => Route(req, body, (path, _) =>
        {
            if (path.EndsWith("email-gate", StringComparison.Ordinal))
            {
                return (200, "{}");
            }

            return path.EndsWith("/auth/v1/signup", StringComparison.Ordinal) ? (200, new JsonObject { ["id"] = "x", ["email"] = "n@example.com" }.ToJsonString()) : (0, "");
        }));
        var s = Session(cloud);
        await s.CloudSignUpAsync("n@example.com", "Хороший-пароль-1", "Хороший-пароль-1");
        Assert.Null(s.Error);
        Assert.Equal(AuthStage.Confirm, s.Stage);
        Assert.Equal(["/functions/v1/email-gate", "/auth/v1/signup"], cloud.Paths);
    }

    [Fact]
    public async Task SignUpWithDifferentPasswordsDoesNotTouchServer()
    {
        var cloud = new MockCloud((_, _) => (200, "{}"));
        var s = Session(cloud);
        await s.CloudSignUpAsync("n@example.com", "Хороший-пароль-1", "другой");
        Assert.NotNull(s.Error);
        Assert.Empty(cloud.Paths);
    }

    [Fact]
    public async Task GateRefusalIsShownAndNoMailIsRequested()
    {
        var cloud = new MockCloud((req, body) => Route(req, body, (path, _) =>
            path.EndsWith("email-gate", StringComparison.Ordinal) ? (429, new JsonObject { ["message"] = "Слишком часто, подождите 5 минут" }.ToJsonString()) : (0, "")));
        var s = Session(cloud);
        await s.CloudSignUpAsync("n@example.com", "Хороший-пароль-1", "Хороший-пароль-1");
        Assert.Contains("подождите 5 минут", s.Error ?? "", StringComparison.Ordinal);
        Assert.Equal(["/functions/v1/email-gate"], cloud.Paths);
    }

    [Fact]
    public async Task CodeFromEmailCompletesSignUp()
    {
        var cloud = new MockCloud((req, body) => Route(req, body, (path, _) =>
            path.EndsWith("/auth/v1/verify", StringComparison.Ordinal) ? (200, Data.Token(email: "n@example.com")) : (0, "")));
        var s = Session(cloud);
        await s.ConfirmSignUpAsync("n@example.com", "123 456");
        Assert.Null(s.Error);
        Assert.True(s.IsCloudSession);
        s.Logout();
    }

    [Fact]
    public async Task ExpiredSessionDuringSyncSignsOutGracefully()
    {
        var calls = 0;
        var cloud = new MockCloud((req, body) => Route(req, body, (path, _) =>
        {
            if (path.EndsWith("/auth/v1/token", StringComparison.Ordinal))
            {
                return calls++ == 0 ? (200, Data.Token(access: "a1")) : Err("refresh_token_already_used");
            }

            return path.EndsWith("rpc/sync_push", StringComparison.Ordinal) ? (401, """{"error_code":"bad_jwt","msg":"JWT expired"}""") : (0, "");
        }));
        var s = Session(cloud);
        await s.CloudSignInAsync("u@example.com", "Хороший-пароль-1");
        await s.Sync!.SyncNow();
        Assert.Contains(s.Sync.State, new[] { SyncState.Failed, SyncState.Idle });
        s.Sync.Dispose();
        s.Current!.Shutdown();
    }
}
