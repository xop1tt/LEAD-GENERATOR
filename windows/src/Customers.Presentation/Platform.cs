namespace Customers.Presentation;

/// <summary>Переход в поток интерфейса. В приложении — DispatcherQueue окна, в тестах — немедленное выполнение.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Всё общее для приложения: папки, хранилище секретов, сеть, журнал, настройки этого компьютера.</summary>
public sealed class AppEnvironment
{
    public required string Root { get; init; }
    public required ISecretStore Secrets { get; init; }
    public required IUiDispatcher Ui { get; init; }
    public required FileLog Log { get; init; }
    public required DevicePreferences Device { get; init; }
    public CloudConfig? Cloud { get; init; }
    public Func<IHttpTransport> SearchTransport { get; init; } = () => new HttpClientTransport();
    public Func<ICloudTransport> CloudTransport { get; init; } = () => new HttpCloudTransport();
    public Func<ICrmHttp> CrmHttp { get; init; } = () => new CrmHttp();
    /// <summary>Папка копий по умолчанию для учётной записи (в тестах — внутри временной папки).</summary>
    public Func<string, string> DefaultBackups { get; init; } = AppPaths.DefaultBackups;

    public Func<bool> IsOnline { get; init; } = () => System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

    /// <summary>Случайный идентификатор этого компьютера для лимитов писем. Хранится в защищённом хранилище.</summary>
    public string DeviceId
    {
        get
        {
            if (Secrets.Get("cloud.device") is { Length: > 0 } existing)
            {
                return existing;
            }

            var fresh = Guid.NewGuid().ToString();
            Secrets.Set("cloud.device", fresh);
            return fresh;
        }
    }

    public string UsersDirectory => Path.Combine(Root, "users");

    /// <summary>Окружение для тестов: временная папка, секреты в памяти, без сети.</summary>
    public static AppEnvironment ForTests(string root, CloudConfig? cloud = null, ICloudTransport? cloudTransport = null,
        Func<IHttpTransport>? search = null, ISecretStore? secrets = null) => new() {
            Root = root,
            Secrets = secrets ?? new MemorySecretStore(),
            Ui = new ImmediateDispatcher(),
            Log = new FileLog(Path.Combine(root, "logs")),
            Device = new DevicePreferences(Path.Combine(root, "device.json")),
            Cloud = cloud,
            SearchTransport = search ?? (() => new HttpClientTransport()),
            CloudTransport = cloudTransport is null ? () => new HttpCloudTransport() : () => cloudTransport,
            IsOnline = () => true,
            DefaultBackups = username => Path.Combine(root, "default-backups", username),
        };
}
