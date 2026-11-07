using System.Reflection;

namespace Customers.Ui;

/// <summary>Адрес и публичный ключ Supabase. Вшиваются при сборке (как у macOS-версии); без них доступны только локальные учётные записи.</summary>
internal static class CloudSettings
{
    public static CloudConfig? Load()
    {
        var assembly = typeof(CloudSettings).Assembly;
        string? Metadata(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

        // Для разработки значения можно переопределить переменными окружения.
        var url = Environment.GetEnvironmentVariable("CUSTOMERS_SUPABASE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            url = Metadata("SupabaseUrl");
        }

        var key = Environment.GetEnvironmentVariable("CUSTOMERS_SUPABASE_ANON_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Metadata("SupabaseAnonKey");
        }

        return CloudConfig.Create(url, key);
    }
}
