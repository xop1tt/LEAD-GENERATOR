namespace Customers.Presentation;

/// <summary>Этап экрана входа.</summary>
public enum AuthStage
{
    SignIn,
    SignUp,
    Confirm,
    Forgot,
    Reset,
    NewPassword,
    Local,
}

/// <summary>
/// Кто вошёл в приложение: облачная учётная запись (Supabase) или локальная (только этот компьютер, работает без сети).
/// Порядок входа, перехода между этапами и сообщения те же, что у SessionModel в macOS-версии.
/// </summary>
public sealed class SessionModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private AuthStage _stage = AuthStage.SignIn;
    private string? _error;
    private string? _info;
    private bool _busy;
    private AppModel? _current;
    private CloudUser? _cloudUser;
    private CloudSyncController? _sync;
    private string _pendingEmail = "";

    public SessionModel(AppEnvironment env)
    {
        _env = env;
        try
        {
            Accounts = new LocalAccounts(env.Root);
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            env.Log.Error("Учётные записи не открыты", e);
            _error = "Не удалось открыть учётные записи: " + e.Message;
        }

        if (env.Cloud is { } config)
        {
            Cloud = new CloudAuth(config, env.CloudTransport(), new SecretSessionStore(env.Secrets), env.DeviceId);

            // Сессия из защищённого хранилища: данные открываются сразу, даже без сети; токен обновится в фоне.
            if (Cloud.Session is { } saved)
            {
                OpenCloud(saved.User);
                _ = RevalidateAsync();
            }
        }
        else
        {
            _stage = AuthStage.Local;
        }

        if (_current is null && Accounts is not null && env.Device.RememberedUser is { Length: > 0 } name && Accounts.Account(name) is { } account)
        {
            Open(account);
        }
    }

    public LocalAccounts? Accounts { get; }

    public CloudAuth? Cloud { get; }

    public AppEnvironment Environment => _env;

    public AppModel? Current { get => _current; private set => Set(ref _current, value); }

    public CloudSyncController? Sync { get => _sync; private set => Set(ref _sync, value); }

    public CloudUser? CloudUser { get => _cloudUser; private set => Set(ref _cloudUser, value); }

    public AuthStage Stage { get => _stage; set { if (Set(ref _stage, value)) { Error = null; } } }

    public string? Error { get => _error; set => Set(ref _error, value); }

    public string? Info { get => _info; set => Set(ref _info, value); }

    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    /// <summary>Адрес, для которого ждём код из письма.</summary>
    public string PendingEmail { get => _pendingEmail; private set => Set(ref _pendingEmail, value); }

    public bool CloudAvailable => Cloud is not null;

    public bool HasUsers => Accounts?.HasUsers ?? false;

    public bool IsCloudSession => Current?.IsCloud == true;

    public bool IsSignedIn => Current is not null;

    // MARK: Облако: вход и регистрация

    /// <summary>Выполняет облачную операцию с индикатором занятости и понятной ошибкой.</summary>
    private async Task RunAsync(Func<Task> body)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Error = null;
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (CloudException e)
        {
            Error = e.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            _env.Log.Error("Операция учётной записи не удалась", e);
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public Task CloudSignInAsync(string email, string password) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        try
        {
            var s = await cloud.SignInAsync(email, password).ConfigureAwait(true);
            OpenCloud(s.User);
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.EmailNotConfirmed)
        {
            PendingEmail = Validation.NormalizedEmail(email) ?? email.Trim().ToLowerInvariant();
            Stage = AuthStage.Confirm;
            Info = "Адрес ещё не подтверждён. Введите код из письма или запросите новое письмо.";
        }
    });

    public Task CloudSignUpAsync(string email, string password, string confirm) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        if (password != confirm)
        {
            throw new CloudException(CloudErrorKind.PasswordsDiffer);
        }

        await cloud.SignUpAsync(email, password).ConfigureAwait(true);
        if (cloud.User is { } user)
        {
            OpenCloud(user); // на сервере выключено подтверждение почты
            return;
        }

        var e = Validation.NormalizedEmail(email) ?? email;
        PendingEmail = e;
        Stage = AuthStage.Confirm;
        Info = $"Мы отправили письмо с кодом на {e}. Если аккаунт с этим адресом уже есть, просто войдите.";
    });

    public Task ConfirmSignUpAsync(string email, string code) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        var s = await cloud.VerifyAsync(email, code, "signup").ConfigureAwait(true) ?? throw new CloudException(CloudErrorKind.InvalidCode);
        Info = null;
        OpenCloud(s.User);
    });

    public Task ResendConfirmationAsync(string email) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        await cloud.ResendConfirmationAsync(email).ConfigureAwait(true);
        Info = "Письмо отправлено ещё раз. Проверьте и папку «Спам».";
    });

    public Task RequestResetAsync(string email) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        await cloud.RequestPasswordResetAsync(email).ConfigureAwait(true);
        var e = Validation.NormalizedEmail(email) ?? email;
        PendingEmail = e;
        Stage = AuthStage.Reset;
        Info = $"Если аккаунт с адресом {e} существует, на него придёт письмо с кодом.";
    });

    public Task FinishResetAsync(string email, string code, string password, string confirm) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        if (password != confirm)
        {
            throw new CloudException(CloudErrorKind.PasswordsDiffer);
        }

        if (Validation.PasswordProblem(password) is { } problem)
        {
            throw new CloudException(CloudErrorKind.WeakPassword, problem);
        }

        _ = await cloud.VerifyAsync(email, code, "recovery").ConfigureAwait(true) ?? throw new CloudException(CloudErrorKind.InvalidCode);
        await cloud.SetNewPasswordAsync(password).ConfigureAwait(true);
        Info = null;
        if (cloud.User is { } u)
        {
            OpenCloud(u);
        }
    });

    /// <summary>Новый пароль после входа по ссылке восстановления.</summary>
    public Task SetPasswordAfterRecoveryAsync(string password, string confirm) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        if (password != confirm)
        {
            throw new CloudException(CloudErrorKind.PasswordsDiffer);
        }

        await cloud.SetNewPasswordAsync(password).ConfigureAwait(true);
        Info = null;
        if (cloud.User is { } u)
        {
            OpenCloud(u);
        }
    });

    /// <summary>Ссылка из письма (leadgenerator://auth/callback#…), вставленная пользователем.</summary>
    public Task HandleLinkAsync(string link) => RunAsync(async () =>
    {
        var cloud = Cloud ?? throw new CloudException(CloudErrorKind.NotConfigured);
        var result = await cloud.HandleCallbackAsync(link.Trim()).ConfigureAwait(true);
        if (result.SignedInType is { } type)
        {
            if (type == "recovery")
            {
                CloseCurrent();
                Stage = AuthStage.NewPassword;
                Info = "Задайте новый пароль.";
            }
            else if (cloud.User is { } u)
            {
                if (IsCloudSession && CloudUser?.Id == u.Id)
                {
                    CloudUser = u;
                    Current!.LastMessage = type == "email_change" ? "Адрес почты изменён" : "Адрес подтверждён";
                }
                else
                {
                    OpenCloud(u);
                }
            }
        }
        else
        {
            Info = "Первая ссылка принята. Подтвердите смену адреса и по ссылке из второго письма.";
            if (Current is { } c)
            {
                c.LastMessage = Info;
            }
        }
    });

    /// <summary>Проверить сохранённую сессию на сервере. Отозванная — выход с понятным сообщением.</summary>
    private async Task RevalidateAsync()
    {
        if (Cloud is not { } cloud)
        {
            return;
        }

        try
        {
            CloudUser = await cloud.ReloadUserAsync().ConfigureAwait(true);
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.SessionExpired)
        {
            SessionExpired();
        }
        catch (CloudException)
        {
            // Нет сети — продолжаем работать с локальными данными.
        }
    }

    private void SessionExpired()
    {
        if (!IsCloudSession)
        {
            return;
        }

        CloseCurrent();
        Stage = AuthStage.SignIn;
        Error = new CloudException(CloudErrorKind.SessionExpired).Message;
    }

    private void OpenCloud(CloudUser user)
    {
        CloseCurrent();
        CloudUser = user;
        Error = null;
        var id = "cloud-" + user.Id.ToLowerInvariant();
        var model = new AppModel(_env, id, user.Email, true, Path.Combine(_env.UsersDirectory, id));
        Current = model;
        if (Cloud is { } cloud && model.StartupError is null)
        {
            var controller = new CloudSyncController(cloud, model, _env) { OnSessionExpired = SessionExpired };
            Sync = controller;
            controller.Start();
        }

        _stage = AuthStage.SignIn;
        RaiseAll(nameof(Stage), nameof(IsSignedIn), nameof(IsCloudSession));
    }

    // MARK: Облако: управление учётной записью

    private async Task<string?> Guard(Func<CloudAuth, Task> body)
    {
        if (Cloud is not { } cloud)
        {
            return new CloudException(CloudErrorKind.NotConfigured).Message;
        }

        try
        {
            await body(cloud).ConfigureAwait(true);
            return null;
        }
        catch (CloudException e)
        {
            return e.Message;
        }
    }

    public void ReloadCloudUser() => _ = RevalidateAsync();

    public Task<string?> ChangeCloudPasswordAsync(string current, string next, string confirm) => Guard(async c =>
    {
        if (next != confirm)
        {
            throw new CloudException(CloudErrorKind.PasswordsDiffer);
        }

        await c.ChangePasswordAsync(current, next).ConfigureAwait(true);
    });

    public Task<string?> ChangeCloudEmailAsync(string email, string password) => Guard(async c =>
    {
        await c.ChangeEmailAsync(email, password).ConfigureAwait(true);
        CloudUser = c.User;
    });

    public Task<string?> ConfirmCloudEmailAsync(string email, string code) => Guard(async c =>
    {
        await c.ConfirmEmailChangeAsync(email, code).ConfigureAwait(true);
        CloudUser = c.User;
    });

    public Task<string?> ResendCloudEmailChangeAsync() => Guard(c => c.ResendEmailChangeAsync());

    /// <summary>Удаление облачной учётной записи. Сначала сохраняется копия базы, без неё удалять нельзя.</summary>
    public async Task<string?> DeleteCloudAccountAsync(string password)
    {
        if (Cloud is not { } cloud || Current is not { IsCloud: true } model)
        {
            return null;
        }

        var backup = model.BackupNow();
        if (backup is null)
        {
            return "Не удалось сохранить резервную копию базы, учётная запись не удалена. " + model.LastError;
        }

        try
        {
            await cloud.DeleteAccountAsync(password).ConfigureAwait(true);
        }
        catch (CloudException e)
        {
            return e.Message;
        }

        var dir = model.DataDirectory;
        var username = model.Username;
        CloseCurrent();
        new DatabaseKeyProvider(_env.Secrets).Forget(username);
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _env.Log.Error("Данные удалённой учётной записи не очищены", e);
        }

        Stage = AuthStage.SignIn;
        Info = "Учётная запись удалена вместе с данными в облаке. Копия базы сохранена: " + backup;
        return null;
    }

    // MARK: Перенос локальной учётной записи в облако

    public IReadOnlyList<UserAccount> LocalAccounts => Accounts?.AllAccounts() ?? [];

    /// <summary>
    /// Копирует лиды, исключения и настройки локальной учётной записи в открытую облачную. Исходная база не меняется;
    /// записи, которые уже есть в облаке и менялись там, не затираются: перенесённым данным даётся раннее время изменения.
    /// </summary>
    public async Task<string?> ImportLocalAsync(string username, string password)
    {
        if (Accounts is not { } auth || Current is not { IsCloud: true, Store: { } target } model)
        {
            return null;
        }

        try
        {
            await Task.Run(() => auth.Login(username, password)).ConfigureAwait(true);
        }
        catch (AuthException e)
        {
            return e.Message;
        }

        var prefs = new AccountPreferences(Path.Combine(auth.DataDirectory(username), "prefs.json"), "");
        var path = prefs.DataPath is { Length: > 0 } p ? Path.Combine(p, "leads.db") : Path.Combine(auth.DataDirectory(username), "leads.db");
        if (!File.Exists(path))
        {
            return "У этой учётной записи нет базы лидов.";
        }

        try
        {
            model.BackupNow();
            var key = new DatabaseKeyProvider(_env.Secrets).KeyFor(username);
            using var source = new LeadStore(path, key);
            var total = source.AllLeads(includeDeleted: true).Count;
            var count = target.ImportFrom(source);
            model.Reload();
            model.ReloadSyncedSettings();
            _ = Sync?.SyncNow();
            model.LastMessage = $"Перенесено лидов: {count} из {total} (остальные уже были). Исходная база не изменена.";
            return null;
        }
        catch (Exception e) when (e is DatabaseException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return "Перенос не удался: " + e.Message;
        }
    }

    // MARK: Локальные учётные записи

    public async Task RegisterAsync(string username, string password, string confirm, bool remember)
    {
        if (Accounts is not { } auth || Busy)
        {
            return;
        }

        Busy = true;
        try
        {
            if (password != confirm)
            {
                throw new AuthException(AuthFailure.PasswordsDiffer);
            }

            var account = await Task.Run(() => auth.Register(username, password)).ConfigureAwait(true);
            Finish(account, remember);
        }
        catch (AuthException e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task LoginAsync(string username, string password, bool remember)
    {
        if (Accounts is not { } auth || Busy)
        {
            return;
        }

        Busy = true;
        try
        {
            var account = await Task.Run(() => auth.Login(username, password)).ConfigureAwait(true);
            Finish(account, remember);
        }
        catch (AuthException e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public void Logout()
    {
        if (Current?.IsCloud == false)
        {
            _env.Device.RememberedUser = null;
        }
        else if (Cloud is { } cloud)
        {
            // Облачная сессия (в том числе после входа по ссылке восстановления) отзывается.
            _ = cloud.SignOutAsync();
        }

        CloseCurrent();
        Stage = CloudAvailable ? AuthStage.SignIn : AuthStage.Local;
    }

    private void CloseCurrent()
    {
        Sync?.Dispose();
        Sync = null;
        Current?.Shutdown();
        Current = null;
        CloudUser = null;
        RaiseAll(nameof(IsSignedIn), nameof(IsCloudSession));
    }

    public async Task<string?> ChangePasswordAsync(string old, string next, string confirm)
    {
        if (Accounts is not { } auth || Current is not { IsCloud: false } model)
        {
            return null;
        }

        if (next != confirm)
        {
            return new AuthException(AuthFailure.PasswordsDiffer).Message;
        }

        try
        {
            await Task.Run(() => auth.ChangePassword(model.Username, old, next)).ConfigureAwait(true);
            return null;
        }
        catch (AuthException e)
        {
            return e.Message;
        }
    }

    /// <summary>Удаление локальной учётной записи вместе с данными. Нужен пароль; копия базы сохраняется заранее.</summary>
    public async Task<string?> DeleteLocalAccountAsync(string password)
    {
        if (Accounts is not { } auth || Current is not { IsCloud: false } model)
        {
            return null;
        }

        var backup = model.BackupNow();
        if (backup is null)
        {
            return "Не удалось сохранить резервную копию, учётная запись не удалена. " + model.LastError;
        }

        var username = model.Username;
        try
        {
            CloseCurrent();
            await Task.Run(() => auth.Delete(username, password)).ConfigureAwait(true);
        }
        catch (AuthException e)
        {
            // Пароль неверный: возвращаем пользователя в его учётную запись.
            Open(auth.Account(username) ?? new UserAccount(username));
            return e.Message;
        }

        new DatabaseKeyProvider(_env.Secrets).Forget(username);
        if (string.Equals(_env.Device.RememberedUser, username, StringComparison.OrdinalIgnoreCase))
        {
            _env.Device.RememberedUser = null;
        }

        Stage = CloudAvailable ? AuthStage.SignIn : AuthStage.Local;
        Info = "Учётная запись удалена. Копия базы сохранена: " + backup;
        return null;
    }

    public bool Remembered => Current is { IsCloud: false } m && string.Equals(_env.Device.RememberedUser, m.Username, StringComparison.OrdinalIgnoreCase);

    public void SetRemembered(bool on)
    {
        _env.Device.RememberedUser = on && Current is { IsCloud: false } m ? m.Username : null;
        Raise(nameof(Remembered));
    }

    private void Finish(UserAccount account, bool remember)
    {
        Error = null;
        _env.Device.RememberedUser = remember ? account.Username : null;
        Open(account);
    }

    /// <summary>Открывает текущую учётную запись заново (например, после восстановления базы из копии).</summary>
    public void Reopen()
    {
        if (Current is not { } model)
        {
            return;
        }

        if (model.IsCloud && CloudUser is { } user)
        {
            OpenCloud(user);
        }
        else if (Accounts?.Account(model.Username) is { } account)
        {
            Open(account);
        }
    }

    private void Open(UserAccount account)
    {
        if (Accounts is not { } auth)
        {
            return;
        }

        CloseCurrent();
        Current = new AppModel(_env, account.Username, account.Username, false, auth.DataDirectory(account.Username));
        RaiseAll(nameof(IsSignedIn), nameof(IsCloudSession));
    }
}
