using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Customers.Core;

/// <summary>
/// Адрес проекта Supabase и публичный (anon / publishable) ключ. Оба значения публичные: они встраиваются
/// в приложение при сборке и дают только то, что разрешают политики RLS. Секретные ключи отклоняются.
/// </summary>
public sealed class CloudConfig
{
    public static readonly string RedirectUrl = "leadgenerator://auth/callback";

    private CloudConfig(Uri url, string anonKey)
    {
        Url = url;
        AnonKey = anonKey;
    }

    public Uri Url { get; }
    public string AnonKey { get; }

    public static CloudConfig? Create(string? url, string? anonKey)
    {
        var trimmed = (url ?? "").Trim().TrimEnd('/');
        var key = (anonKey ?? "").Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
        {
            return null;
        }

        var local = u.Host is "127.0.0.1" or "localhost";
        if (!(u.Scheme == "https" || (u.Scheme == "http" && local)) || key.Length == 0 || !IsPublicKey(key))
        {
            return null;
        }

        return new CloudConfig(u, key);
    }

    /// <summary>
    /// Публичный ключ: publishable (sb_publishable_…) или JWT с ролью anon. Секретный ключ
    /// (sb_secret_… или JWT с другой ролью) отклоняется, чтобы его нельзя было встроить по ошибке.
    /// </summary>
    public static bool IsPublicKey(string key)
    {
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal))
        {
            return true;
        }

        if (key.StartsWith("sb_secret_", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = key.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0)
        {
            b64 += "=";
        }

        try
        {
            var obj = JsonNode.Parse(Convert.FromBase64String(b64)) as JsonObject;
            return obj?["role"]?.GetValue<string>() == "anon";
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Ошибки облака с понятными сообщениями. Текст не раскрывает, существует ли аккаунт.</summary>
public sealed class CloudException : Exception
{
    public CloudException(CloudErrorKind kind, string? detail = null)
        : base(MessageFor(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    public CloudErrorKind Kind { get; }
    public string? Detail { get; }

    private static string MessageFor(CloudErrorKind kind, string? detail) => kind switch {
        CloudErrorKind.NotConfigured => "Облако не настроено: в этой сборке нет адреса сервера.",
        CloudErrorKind.Network => "Нет соединения с сервером. Проверьте интернет — данные на этом компьютере доступны и без сети.",
        CloudErrorKind.InvalidEmail => "Проверьте адрес электронной почты.",
        CloudErrorKind.WeakPassword => detail ?? Validation.PasswordRules,
        CloudErrorKind.PasswordsDiffer => "Пароли не совпадают.",
        CloudErrorKind.InvalidCredentials => "Неверный адрес почты или пароль.",
        CloudErrorKind.EmailNotConfirmed => "Адрес почты ещё не подтверждён. Введите код из письма.",
        CloudErrorKind.EmailExists => "Этот адрес уже используется другой учётной записью.",
        CloudErrorKind.InvalidCode => "Код неверный или устарел. Запросите новое письмо.",
        CloudErrorKind.SamePassword => "Новый пароль совпадает с текущим.",
        CloudErrorKind.RateLimited => detail ?? "Слишком много попыток. Подождите немного и повторите.",
        CloudErrorKind.MailUnavailable => "Не удалось отправить письмо: почтовый сервис недоступен или исчерпан лимит. Попробуйте позже.",
        CloudErrorKind.SessionExpired => "Сессия завершена. Войдите снова.",
        CloudErrorKind.ReauthRequired => "Введите пароль ещё раз.",
        _ => $"Ошибка сервера: {detail}",
    };

    /// <summary>Разбор ответа Supabase Auth: {"code":400,"error_code":"…","msg":"…"} или {"error":"…","error_description":"…"}.</summary>
    internal static CloudException FromAuth(int status, byte[] data)
    {
        JsonObject? obj = null;
        try
        {
            obj = JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
        }

        string? S(string k) => obj?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var code = S("error_code") ?? S("error") ?? "";
        var msg = S("msg") ?? S("error_description") ?? S("message") ?? "";
        switch (code)
        {
            case "invalid_credentials":
                return new(CloudErrorKind.InvalidCredentials);
            case "invalid_grant" when msg.Contains("credentials", StringComparison.OrdinalIgnoreCase):
                return new(CloudErrorKind.InvalidCredentials);
            case "email_not_confirmed":
                return new(CloudErrorKind.EmailNotConfirmed);
            case "weak_password":
                return new(CloudErrorKind.WeakPassword);
            case "email_exists" or "user_already_exists" or "conflict":
                return new(CloudErrorKind.EmailExists);
            case "email_address_invalid" or "validation_failed":
                return new(CloudErrorKind.InvalidEmail);
            case "otp_expired" or "otp_disabled" or "bad_code_verifier":
                return new(CloudErrorKind.InvalidCode);
            case "same_password":
                return new(CloudErrorKind.SamePassword);
            case "over_email_send_rate_limit" or "over_request_rate_limit":
                return new(CloudErrorKind.RateLimited);
            case "refresh_token_not_found" or "refresh_token_already_used" or "session_not_found" or "session_expired"
                or "user_not_found" or "bad_jwt" or "no_authorization":
                return new(CloudErrorKind.SessionExpired);
            case "reauthentication_needed" or "reauthentication_not_valid":
                return new(CloudErrorKind.ReauthRequired);
        }

        if (status == 429)
        {
            return new(CloudErrorKind.RateLimited);
        }

        if (msg.Contains("hook", StringComparison.Ordinal))
        {
            return new(CloudErrorKind.MailUnavailable);
        }

        if (status is 401 or 403)
        {
            return new(CloudErrorKind.SessionExpired);
        }

        return new(CloudErrorKind.Server, msg.Length == 0 ? $"HTTP {status}" : msg[..Math.Min(200, msg.Length)]);
    }
}

public enum CloudErrorKind
{
    NotConfigured,
    Network,
    InvalidEmail,
    WeakPassword,
    PasswordsDiffer,
    InvalidCredentials,
    EmailNotConfirmed,
    EmailExists,
    InvalidCode,
    SamePassword,
    RateLimited,
    MailUnavailable,
    SessionExpired,
    ReauthRequired,
    Server,
}

/// <summary>Проверки на клиенте — для быстрых подсказок. Те же правила проверяет сервер.</summary>
public static partial class Validation
{
    public const string PasswordRules = "Пароль: не короче 8 символов, строчные и заглавные буквы и цифры.";

    [GeneratedRegex(@"^[^@\s<>()"",;:]+@[^@\s<>()"",;:]+\.[^@\s<>()"",;:.]{2,}$")]
    private static partial Regex EmailRx();

    public static string? NormalizedEmail(string raw)
    {
        var e = raw.Trim().ToLowerInvariant();
        return e.Length <= 254 && EmailRx().IsMatch(e) ? e : null;
    }

    /// <summary>null — пароль подходит, иначе — что исправить.</summary>
    public static string? PasswordProblem(string p)
    {
        var missing = new List<string>();
        if (p.Length < 8)
        {
            missing.Add("не короче 8 символов");
        }

        if (!p.Any(char.IsLower))
        {
            missing.Add("строчная буква");
        }

        if (!p.Any(char.IsUpper))
        {
            missing.Add("заглавная буква");
        }

        if (!p.Any(char.IsNumber))
        {
            missing.Add("цифра");
        }

        if (p.Length > 72)
        {
            return "Пароль не длиннее 72 символов.";
        }

        return missing.Count == 0 ? null : "В пароле нужно: " + string.Join(", ", missing) + ".";
    }

    public static string? Code(string raw)
    {
        var digits = new string(raw.Where(char.IsNumber).ToArray());
        return digits.Length is >= 6 and <= 10 ? digits : null;
    }
}

/// <summary>Пользователь облака (Supabase Auth).</summary>
public sealed record CloudUser(string Id, string Email, string? NewEmail = null, string? EmailConfirmedAt = null, string? CreatedAt = null)
{
    public bool IsConfirmed => !string.IsNullOrEmpty(EmailConfirmedAt);

    public static CloudUser FromJson(JsonObject o)
    {
        string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var newEmail = S("new_email");
        return new CloudUser(S("id") ?? "", S("email") ?? "", string.IsNullOrEmpty(newEmail) ? null : newEmail,
            S("email_confirmed_at"), S("created_at"));
    }
}

/// <summary>Сессия: токены и пользователь. Хранится только в защищённом хранилище (см. <see cref="ISessionStore"/>).</summary>
public sealed record CloudSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, CloudUser User)
{
    public bool ExpiresSoon(DateTimeOffset? now = null, TimeSpan? margin = null) =>
        ExpiresAt - (now ?? DateTimeOffset.UtcNow) < (margin ?? TimeSpan.FromSeconds(60));

    public string ToJson() => new JsonObject {
        ["access"] = AccessToken,
        ["refresh"] = RefreshToken,
        ["expires"] = ExpiresAt.ToUnixTimeSeconds(),
        ["user"] = new JsonObject {
            ["id"] = User.Id,
            ["email"] = User.Email,
            ["new_email"] = User.NewEmail,
            ["email_confirmed_at"] = User.EmailConfirmedAt,
            ["created_at"] = User.CreatedAt,
        },
    }.ToJsonString();

    public static CloudSession? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["user"] is not JsonObject u)
            {
                return null;
            }

            return new CloudSession(o["access"]!.GetValue<string>(), o["refresh"]!.GetValue<string>(),
                DateTimeOffset.FromUnixTimeSeconds(o["expires"]!.GetValue<long>()), CloudUser.FromJson(u));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Где хранится сессия между запусками. В приложении — защищённое хранилище Windows (DPAPI).</summary>
public interface ISessionStore
{
    CloudSession? Load();
    void Save(CloudSession? session);
}

/// <summary>Хранилище в памяти (тесты).</summary>
public sealed class MemorySessionStore : ISessionStore
{
    public CloudSession? Current { get; private set; }

    public CloudSession? Load() => Current;

    public void Save(CloudSession? session) => Current = session;
}

/// <summary>Чем закончилось открытие ссылки из письма.</summary>
public sealed record CallbackResult(string? SignedInType, string? Message);

/// <summary>Операции, на которые приложение запрашивает письмо (лимиты проверяет сервер).</summary>
public enum MailAction
{
    Signup,
    Recovery,
    EmailChange,
}

/// <summary>Ответ облака.</summary>
public sealed record CloudResponse(int Status, byte[] Data)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Транспорт HTTP облака. В тестах подменяется заглушкой.</summary>
public interface ICloudTransport
{
    Task<CloudResponse> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

public sealed class HttpCloudTransport : ICloudTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpCloudTransport(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All };
        _client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    public void Dispose() => _client.Dispose();

    public async Task<CloudResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var resp = await _client.SendAsync(request, ct).ConfigureAwait(false);
            return new CloudResponse((int)resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        }
        catch (HttpRequestException e)
        {
            throw new CloudException(CloudErrorKind.Network, e.Message);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new CloudException(CloudErrorKind.Network, e.Message);
        }
    }
}
