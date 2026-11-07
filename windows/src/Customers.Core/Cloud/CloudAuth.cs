using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Core;

/// <summary>
/// Вход, регистрация, восстановление и смена пароля, смена адреса — через Supabase Auth (тот же проект, что у macOS-версии).
/// Каждое письмо сначала разрешает функция email-gate (лимиты устройства, IP, адреса и общий суточный),
/// и только потом приложение просит Auth отправить письмо. Обычный вход писем не тратит.
/// </summary>
public sealed class CloudAuth
{
    private readonly ICloudTransport _transport;
    private readonly ISessionStore _store;
    private readonly string _deviceId;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public CloudAuth(CloudConfig config, ICloudTransport transport, ISessionStore store, string deviceId)
    {
        Config = config;
        _transport = transport;
        _store = store;
        _deviceId = deviceId;
        Session = store.Load();
    }

    public CloudConfig Config { get; }
    public CloudSession? Session { get; private set; }
    public CloudUser? User => Session?.User;

    // MARK: Запросы

    internal async Task<CloudResponse> RequestAsync(string method, string path, JsonNode? body, string? token, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), Config.Url.AbsoluteUri.TrimEnd('/') + path);
        req.Headers.TryAddWithoutValidation("apikey", Config.AnonKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (token is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return await _transport.SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> AuthAsync(string method, string path, JsonNode? body, CancellationToken ct, string? token = null)
    {
        var res = await RequestAsync(method, "/auth/v1" + path, body, token, ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            throw CloudException.FromAuth(res.Status, res.Data);
        }

        return res.Data;
    }

    /// <summary>Разрешение на письмо от email-gate. Отказ — понятное сообщение с временем ожидания.</summary>
    private async Task GateAsync(MailAction action, string email, CancellationToken ct)
    {
        var name = action switch { MailAction.Signup => "signup", MailAction.Recovery => "recovery", _ => "email_change" };
        var res = await RequestAsync("POST", "/functions/v1/email-gate",
            new JsonObject { ["action"] = name, ["email"] = email, ["device_id"] = _deviceId }, null, ct).ConfigureAwait(false);
        if (res.Ok)
        {
            return;
        }

        JsonObject? obj = null;
        try
        {
            obj = JsonNode.Parse(res.Data) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (obj?["reason"]?.ToString() == "invalid_email")
        {
            throw new CloudException(CloudErrorKind.InvalidEmail);
        }

        if (obj?["message"] is JsonValue m && m.TryGetValue<string>(out var text))
        {
            throw new CloudException(CloudErrorKind.RateLimited, text);
        }

        throw new CloudException(CloudErrorKind.Server, $"HTTP {res.Status}");
    }

    private CloudSession Take(byte[] data)
    {
        var o = JsonNode.Parse(data) as JsonObject ?? throw new CloudException(CloudErrorKind.Server, "пустой ответ");
        var access = o["access_token"]?.GetValue<string>() ?? throw new CloudException(CloudErrorKind.Server, "нет токена");
        var refresh = o["refresh_token"]?.GetValue<string>() ?? "";
        DateTimeOffset exp;
        if (o["expires_at"] is JsonValue ea && ea.TryGetValue<double>(out var eaSecs))
        {
            exp = DateTimeOffset.FromUnixTimeSeconds((long)eaSecs);
        }
        else
        {
            var secs = o["expires_in"] is JsonValue ei && ei.TryGetValue<double>(out var eiSecs) ? eiSecs : 3600;
            exp = DateTimeOffset.UtcNow.AddSeconds(secs);
        }

        var user = o["user"] is JsonObject uo ? CloudUser.FromJson(uo) : new CloudUser("", "");
        var s = new CloudSession(access, refresh, exp, user);
        SetSession(s);
        return s;
    }

    private void SetSession(CloudSession? s)
    {
        Session = s;
        _store.Save(s);
    }

    private static string ValidEmail(string raw) =>
        Validation.NormalizedEmail(raw) ?? throw new CloudException(CloudErrorKind.InvalidEmail);

    private static bool HasToken(byte[] data)
    {
        try
        {
            return (JsonNode.Parse(data) as JsonObject)?["access_token"] is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string RedirectParam => Uri.EscapeDataString(CloudConfig.RedirectUrl);

    // MARK: Регистрация

    /// <summary>Регистрация. Если адрес уже занят, ответ такой же, как для нового: письмо просто не придёт.</summary>
    public async Task SignUpAsync(string rawEmail, string password, CancellationToken ct = default)
    {
        var email = ValidEmail(rawEmail);
        if (Validation.PasswordProblem(password) is { } problem)
        {
            throw new CloudException(CloudErrorKind.WeakPassword, problem);
        }

        await GateAsync(MailAction.Signup, email, ct).ConfigureAwait(false);
        var data = await AuthAsync("POST", $"/signup?redirect_to={RedirectParam}", new JsonObject {
            ["email"] = email,
            ["password"] = password,
            ["options"] = new JsonObject { ["email_redirect_to"] = CloudConfig.RedirectUrl },
        }, ct).ConfigureAwait(false);

        // Если на сервере выключено подтверждение почты, Auth сразу возвращает сессию.
        if (HasToken(data))
        {
            Take(data);
        }
    }

    public async Task ResendConfirmationAsync(string rawEmail, CancellationToken ct = default)
    {
        var email = ValidEmail(rawEmail);
        await GateAsync(MailAction.Signup, email, ct).ConfigureAwait(false);
        await AuthAsync("POST", "/resend", new JsonObject {
            ["type"] = "signup",
            ["email"] = email,
            ["options"] = new JsonObject { ["email_redirect_to"] = CloudConfig.RedirectUrl },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Подтверждение кодом из письма (регистрация, восстановление, смена адреса).</summary>
    public async Task<CloudSession?> VerifyAsync(string rawEmail, string rawCode, string type, CancellationToken ct = default)
    {
        var email = ValidEmail(rawEmail);
        var code = Validation.Code(rawCode) ?? throw new CloudException(CloudErrorKind.InvalidCode);
        var data = await AuthAsync("POST", "/verify", new JsonObject { ["type"] = type, ["email"] = email, ["token"] = code }, ct).ConfigureAwait(false);
        return HasToken(data) ? Take(data) : null;
    }

    // MARK: Вход и выход

    public async Task<CloudSession> SignInAsync(string rawEmail, string password, CancellationToken ct = default)
    {
        var email = ValidEmail(rawEmail);
        if (password.Length == 0)
        {
            throw new CloudException(CloudErrorKind.InvalidCredentials);
        }

        var data = await AuthAsync("POST", "/token?grant_type=password", new JsonObject { ["email"] = email, ["password"] = password }, ct).ConfigureAwait(false);
        return Take(data);
    }

    /// <summary>Выход: сессия отзывается на сервере (если есть сеть) и всегда удаляется с этого компьютера.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (Session is { } s)
        {
            try
            {
                await AuthAsync("POST", "/logout?scope=local", null, ct, s.AccessToken).ConfigureAwait(false);
            }
            catch (CloudException)
            {
            }
        }

        SetSession(null);
    }

    /// <summary>Действующая сессия: при скором истечении токен обновляется.</summary>
    public async Task<CloudSession> ValidSessionAsync(CancellationToken ct = default)
    {
        var s = Session ?? throw new CloudException(CloudErrorKind.SessionExpired);
        return s.ExpiresSoon() ? await RefreshAsync(ct).ConfigureAwait(false) : s;
    }

    /// <summary>Обновление токена. Параллельные вызовы ждут одно обновление (refresh-токен одноразовый).</summary>
    public async Task<CloudSession> RefreshAsync(CancellationToken ct = default)
    {
        var before = Session ?? throw new CloudException(CloudErrorKind.SessionExpired);
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Пока ждали, токен мог уже обновить другой вызов.
            if (Session is { } now && now.RefreshToken != before.RefreshToken)
            {
                return now;
            }

            var current = Session ?? throw new CloudException(CloudErrorKind.SessionExpired);
            try
            {
                var data = await AuthAsync("POST", "/token?grant_type=refresh_token",
                    new JsonObject { ["refresh_token"] = current.RefreshToken }, ct).ConfigureAwait(false);
                return Take(data);
            }
            catch (CloudException e) when (e.Kind is CloudErrorKind.SessionExpired or CloudErrorKind.InvalidCredentials
                                           || (e.Kind == CloudErrorKind.Server && (e.Detail ?? "").Contains("refresh token", StringComparison.OrdinalIgnoreCase)))
            {
                SetSession(null);
                throw new CloudException(CloudErrorKind.SessionExpired);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Запрос с токеном пользователя; при 401 один раз обновляет токен и повторяет.</summary>
    public async Task<CloudResponse> AuthorizedAsync(string method, string path, JsonNode? body = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var s = await ValidSessionAsync(ct).ConfigureAwait(false);
        var res = await RequestAsync(method, path, body, s.AccessToken, ct, headers).ConfigureAwait(false);
        if (res.Status == 401)
        {
            s = await RefreshAsync(ct).ConfigureAwait(false);
            res = await RequestAsync(method, path, body, s.AccessToken, ct, headers).ConfigureAwait(false);
            if (res.Status == 401)
            {
                SetSession(null);
                throw new CloudException(CloudErrorKind.SessionExpired);
            }
        }

        return res;
    }

    /// <summary>Свежие данные пользователя с сервера (подтверждение почты, новый адрес).</summary>
    public async Task<CloudUser> ReloadUserAsync(CancellationToken ct = default)
    {
        var res = await AuthorizedAsync("GET", "/auth/v1/user", null, ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            var e = CloudException.FromAuth(res.Status, res.Data);
            if (e.Kind == CloudErrorKind.SessionExpired)
            {
                SetSession(null);
            }

            throw e;
        }

        var u = CloudUser.FromJson(JsonNode.Parse(res.Data) as JsonObject ?? throw new CloudException(CloudErrorKind.Server, "пустой ответ"));
        if (Session is { } s)
        {
            SetSession(s with { User = u });
        }

        return u;
    }

    // MARK: Пароль

    public async Task RequestPasswordResetAsync(string rawEmail, CancellationToken ct = default)
    {
        var email = ValidEmail(rawEmail);
        await GateAsync(MailAction.Recovery, email, ct).ConfigureAwait(false);
        await AuthAsync("POST", $"/recover?redirect_to={RedirectParam}", new JsonObject { ["email"] = email }, ct).ConfigureAwait(false);
    }

    /// <summary>Новый пароль после входа по коду восстановления. Остальные сессии завершаются.</summary>
    public async Task SetNewPasswordAsync(string password, CancellationToken ct = default)
    {
        if (Validation.PasswordProblem(password) is { } problem)
        {
            throw new CloudException(CloudErrorKind.WeakPassword, problem);
        }

        var res = await AuthorizedAsync("PUT", "/auth/v1/user", new JsonObject { ["password"] = password }, ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            throw CloudException.FromAuth(res.Status, res.Data);
        }

        try
        {
            await SignOutOthersAsync(ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (CloudException)
        {
        }
    }

    /// <summary>Смена пароля из настроек: сначала текущий пароль проверяется входом на сервере.</summary>
    public async Task ChangePasswordAsync(string current, string next, CancellationToken ct = default)
    {
        var email = Session?.User.Email ?? throw new CloudException(CloudErrorKind.SessionExpired);
        if (Validation.PasswordProblem(next) is { } problem)
        {
            throw new CloudException(CloudErrorKind.WeakPassword, problem);
        }

        try
        {
            await SignInAsync(email, current, ct).ConfigureAwait(false);
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.InvalidCredentials)
        {
            throw new CloudException(CloudErrorKind.Server, "текущий пароль неверный");
        }

        await SetNewPasswordAsync(next, ct).ConfigureAwait(false);
    }

    private async Task SignOutOthersAsync(CancellationToken ct)
    {
        var res = await AuthorizedAsync("POST", "/auth/v1/logout?scope=others", null, ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            throw CloudException.FromAuth(res.Status, res.Data);
        }
    }

    // MARK: Адрес почты

    /// <summary>Смена адреса: нужен текущий пароль. Адрес меняется только после подтверждения обоих писем.</summary>
    public async Task ChangeEmailAsync(string rawNew, string password, CancellationToken ct = default)
    {
        var current = Session?.User.Email ?? throw new CloudException(CloudErrorKind.SessionExpired);
        var next = ValidEmail(rawNew);
        if (next == current.ToLowerInvariant())
        {
            throw new CloudException(CloudErrorKind.Server, "это текущий адрес");
        }

        try
        {
            await SignInAsync(current, password, ct).ConfigureAwait(false);
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.InvalidCredentials)
        {
            throw new CloudException(CloudErrorKind.Server, "пароль неверный");
        }

        await GateAsync(MailAction.EmailChange, current, ct).ConfigureAwait(false);
        var res = await AuthorizedAsync("PUT", $"/auth/v1/user?redirect_to={RedirectParam}", new JsonObject { ["email"] = next }, ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            throw CloudException.FromAuth(res.Status, res.Data);
        }

        try
        {
            await ReloadUserAsync(ct).ConfigureAwait(false);
        }
        catch (CloudException)
        {
        }
    }

    public async Task ResendEmailChangeAsync(CancellationToken ct = default)
    {
        var current = Session?.User.Email;
        var next = Session?.User.NewEmail;
        if (current is null || next is null)
        {
            throw new CloudException(CloudErrorKind.SessionExpired);
        }

        await GateAsync(MailAction.EmailChange, current, ct).ConfigureAwait(false);
        await AuthAsync("POST", "/resend", new JsonObject { ["type"] = "email_change", ["email"] = next }, ct).ConfigureAwait(false);
    }

    public async Task ConfirmEmailChangeAsync(string email, string code, CancellationToken ct = default)
    {
        await VerifyAsync(email, code, "email_change", ct).ConfigureAwait(false);
        try
        {
            await ReloadUserAsync(ct).ConfigureAwait(false);
        }
        catch (CloudException)
        {
        }
    }

    // MARK: Удаление учётной записи

    /// <summary>Удаляет учётную запись и все данные в облаке. Пароль проверяется заново.</summary>
    public async Task DeleteAccountAsync(string password, CancellationToken ct = default)
    {
        var email = Session?.User.Email ?? throw new CloudException(CloudErrorKind.SessionExpired);
        try
        {
            await SignInAsync(email, password, ct).ConfigureAwait(false);
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.InvalidCredentials)
        {
            throw new CloudException(CloudErrorKind.Server, "пароль неверный");
        }

        var res = await AuthorizedAsync("POST", "/functions/v1/delete-account", null, ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            string? message = null;
            try
            {
                message = (JsonNode.Parse(res.Data) as JsonObject)?["message"]?.ToString();
            }
            catch (System.Text.Json.JsonException)
            {
            }

            throw new CloudException(CloudErrorKind.Server, message ?? $"HTTP {res.Status}");
        }

        SetSession(null);
    }

    // MARK: Ссылки из писем

    /// <summary>Обработка ссылки вида leadgenerator://auth/callback#access_token=…&amp;refresh_token=…&amp;type=…, вставленной из письма.</summary>
    public async Task<CallbackResult> HandleCallbackAsync(string link, CancellationToken ct = default)
    {
        var p = FragmentParams(link);
        if ((p.GetValueOrDefault("error_description") ?? p.GetValueOrDefault("error")) is { } err)
        {
            if (p.GetValueOrDefault("error_code") == "otp_expired")
            {
                throw new CloudException(CloudErrorKind.InvalidCode);
            }

            throw new CloudException(CloudErrorKind.Server, err);
        }

        if (p.TryGetValue("access_token", out var access) && p.TryGetValue("refresh_token", out var refresh))
        {
            DateTimeOffset exp = p.TryGetValue("expires_at", out var ea) && double.TryParse(ea, System.Globalization.CultureInfo.InvariantCulture, out var eaSecs)
                ? DateTimeOffset.FromUnixTimeSeconds((long)eaSecs)
                : DateTimeOffset.UtcNow.AddSeconds(p.TryGetValue("expires_in", out var ei) && double.TryParse(ei, System.Globalization.CultureInfo.InvariantCulture, out var eiSecs) ? eiSecs : 3600);
            SetSession(new CloudSession(access, refresh, exp, new CloudUser("", "")));
            try
            {
                await ReloadUserAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                SetSession(null);
                throw;
            }

            return new CallbackResult(p.GetValueOrDefault("type") ?? "", null);
        }

        if (p.TryGetValue("message", out var m))
        {
            return new CallbackResult(null, m);
        }

        throw new CloudException(CloudErrorKind.Server, "ссылка не распознана");
    }

    internal static Dictionary<string, string> FragmentParams(string link)
    {
        var output = new Dictionary<string, string>();
        var hash = link.IndexOf('#');
        var q = link.IndexOf('?');
        var sources = new List<string>();
        if (hash >= 0)
        {
            sources.Add(link[(hash + 1)..]);
        }

        if (q >= 0)
        {
            sources.Add(hash >= 0 && hash > q ? link[(q + 1)..hash] : link[(q + 1)..]);
        }

        foreach (var source in sources)
        {
            foreach (var pair in source.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                var k = Uri.UnescapeDataString(kv[0]);
                var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                output.TryAdd(k, v);
            }
        }

        return output;
    }
}
