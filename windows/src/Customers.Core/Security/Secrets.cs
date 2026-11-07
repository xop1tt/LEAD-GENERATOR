using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Customers.Core;

/// <summary>Хранилище секретов (токены, ключи, пароли приложений). Секреты никогда не попадают в базу и логи.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

/// <summary>Хранилище в памяти (тесты).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public void Set(string name, string value) => _values[name] = value;

    public void Delete(string name) => _values.Remove(name);
}

/// <summary>
/// Секреты в файлах, зашифрованных Windows DPAPI для текущего пользователя: расшифровать их может только он
/// на этом компьютере. Имя секрета в имени файла — хэш, содержимое — шифртекст.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Customers.Secrets.v1");
    private readonly string _dir;

    public DpapiSecretStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    private string FileFor(string name) =>
        Path.Combine(_dir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..32] + ".sec");

    public string? Get(string name)
    {
        var file = FileFor(name);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Set(string name, string value)
    {
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var file = FileFor(name);
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, file, overwrite: true);
    }

    public void Delete(string name) => LeadStore.TryDelete(FileFor(name));
}

/// <summary>Ключ шифрования базы пользователя: случайные 32 байта в защищённом хранилище, создаются при первом обращении.</summary>
public sealed class DatabaseKeyProvider(ISecretStore store)
{
    public string KeyFor(string account)
    {
        var name = $"db.key.{account.ToLowerInvariant()}";
        if (store.Get(name) is { Length: > 0 } existing)
        {
            return existing;
        }

        var fresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        store.Set(name, fresh);
        return fresh;
    }

    public bool Exists(string account) => store.Get($"db.key.{account.ToLowerInvariant()}") is { Length: > 0 };

    public void Forget(string account) => store.Delete($"db.key.{account.ToLowerInvariant()}");
}
