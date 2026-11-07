using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Customers.Core;

/// <summary>Учётная запись. Пароль не хранится — только соль и хэш PBKDF2.</summary>
public sealed record UserAccount(string Username, DateTimeOffset? CreatedAt = null, DateTimeOffset? LastLoginAt = null);

public enum AuthFailure
{
    InvalidUsername,
    WeakPassword,
    PasswordsDiffer,
    Exists,
    WrongCredentials,
    TooManyAttempts,
}

public sealed class AuthException : Exception
{
    public AuthException(AuthFailure kind, int retryAfterSeconds = 0)
        : base(MessageFor(kind, retryAfterSeconds))
    {
        Kind = kind;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public AuthFailure Kind { get; }
    public int RetryAfterSeconds { get; }

    private static string MessageFor(AuthFailure kind, int seconds) => kind switch {
        AuthFailure.InvalidUsername => "Имя пользователя: от 3 до 32 символов — буквы, цифры, точка, дефис или подчёркивание, начинается с буквы или цифры.",
        AuthFailure.WeakPassword => "Пароль должен быть не короче 8 символов.",
        AuthFailure.PasswordsDiffer => "Пароли не совпадают.",
        AuthFailure.Exists => "Такое имя пользователя уже занято.",
        AuthFailure.WrongCredentials => "Неверное имя пользователя или пароль.",
        _ => $"Слишком много попыток. Повторите через {seconds} с.",
    };
}

/// <summary>
/// Локальные учётные записи в accounts.db рядом с базами пользователей. Формат таблицы и параметры
/// PBKDF2-HMAC-SHA256 (310 000 итераций) те же, что у macOS-версии. После пяти неудачных попыток подряд вход блокируется
/// на растущее время.
/// </summary>
public sealed partial class LocalAccounts : IDisposable
{
    internal const int Iterations = 310_000;
    private readonly Db _db;
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> _failures = [];
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public LocalAccounts(string root, Func<DateTimeOffset>? now = null)
    {
        Root = root;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(root);
        _db = Db.Open(Path.Combine(root, "accounts.db"));
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS users (
                username_key TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                salt TEXT NOT NULL,
                hash TEXT NOT NULL,
                iterations INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                last_login_at TEXT
            );
            """);
    }

    public string Root { get; }

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}_.-]{2,31}$")]
    private static partial Regex NameRx();

    public static string? Normalized(string username)
    {
        var u = username.Trim();
        return NameRx().IsMatch(u) ? u : null;
    }

    internal static string KeyOf(string username) => username.ToLowerInvariant();

    public bool HasUsers => Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM users") ?? 0L, System.Globalization.CultureInfo.InvariantCulture) > 0;

    public UserAccount? Account(string username)
    {
        var r = _db.First("SELECT username, created_at, last_login_at FROM users WHERE username_key=?", KeyOf(username));
        return r is null ? null : new UserAccount(r.Str("username") ?? username, ISODate.Parse(r.Str("created_at")), ISODate.Parse(r.Str("last_login_at")));
    }

    public List<UserAccount> AllAccounts() => _db.Query("SELECT username, created_at, last_login_at FROM users ORDER BY username_key")
        .Select(r => new UserAccount(r.Str("username") ?? "", ISODate.Parse(r.Str("created_at")), ISODate.Parse(r.Str("last_login_at")))).ToList();

    public UserAccount Register(string username, string password)
    {
        var name = Normalized(username) ?? throw new AuthException(AuthFailure.InvalidUsername);
        if (password.Length < 8)
        {
            throw new AuthException(AuthFailure.WeakPassword);
        }

        if (Account(name) is not null)
        {
            throw new AuthException(AuthFailure.Exists);
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Pbkdf2(password, salt, Iterations);
        var now = ISODate.Now();
        _db.Run("INSERT INTO users(username_key, username, salt, hash, iterations, created_at, last_login_at) VALUES(?,?,?,?,?,?,?)",
            KeyOf(name), name, Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations, now, now);
        Directory.CreateDirectory(DataDirectory(name));
        return Account(name) ?? new UserAccount(name);
    }

    public UserAccount Login(string username, string password)
    {
        var key = KeyOf(username.Trim());
        lock (_gate)
        {
            if (_failures.TryGetValue(key, out var f) && f.Until > _now())
            {
                throw new AuthException(AuthFailure.TooManyAttempts, (int)Math.Ceiling((f.Until - _now()).TotalSeconds));
            }
        }

        if (!Verify(key, password))
        {
            lock (_gate)
            {
                var f = _failures.TryGetValue(key, out var existing) ? existing : (Count: 0, Until: DateTimeOffset.MinValue);

                // После 5 неудачных попыток подряд — пауза, растущая с каждой следующей.
                f.Count++;
                if (f.Count >= 5)
                {
                    f.Until = _now().AddSeconds(Math.Min(300, 15 * (f.Count - 4)));
                }

                _failures[key] = f;
            }

            throw new AuthException(AuthFailure.WrongCredentials);
        }

        lock (_gate)
        {
            _failures.Remove(key);
        }

        _db.Run("UPDATE users SET last_login_at=? WHERE username_key=?", ISODate.Now(), key);
        return Account(key) ?? new UserAccount(username);
    }

    private bool Verify(string key, string password)
    {
        var r = _db.First("SELECT salt, hash, iterations FROM users WHERE username_key=?", key);
        if (r is null || r.Str("salt") is not { } s || r.Str("hash") is not { } h || r.Int("iterations") is not { } rounds)
        {
            // Тратим столько же времени, сколько на настоящую проверку: имя пользователя не угадать по задержке.
            _ = Pbkdf2(password, new byte[16], Iterations);
            return false;
        }

        try
        {
            var computed = Pbkdf2(password, Convert.FromBase64String(s), (int)rounds);
            return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(h));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void ChangePassword(string username, string oldPassword, string newPassword)
    {
        var key = KeyOf(username);
        if (!Verify(key, oldPassword))
        {
            throw new AuthException(AuthFailure.WrongCredentials);
        }

        if (newPassword.Length < 8)
        {
            throw new AuthException(AuthFailure.WeakPassword);
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Pbkdf2(newPassword, salt, Iterations);
        _db.Run("UPDATE users SET salt=?, hash=?, iterations=? WHERE username_key=?",
            Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations, key);
    }

    /// <summary>Удаляет учётную запись и её данные. Нужен пароль.</summary>
    public void Delete(string username, string password)
    {
        var key = KeyOf(username);
        if (!Verify(key, password))
        {
            throw new AuthException(AuthFailure.WrongCredentials);
        }

        _db.Run("DELETE FROM users WHERE username_key=?", key);
        var dir = DataDirectory(username);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public string DataDirectory(string username) => Path.Combine(Root, "users", KeyOf(username));

    internal static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    public void Dispose() => _db.Close();
}
