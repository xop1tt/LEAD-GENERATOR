using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Presentation.Tests;

internal sealed class MockSearchTransport(Func<HttpRequestSpec, HttpResult> handler) : IHttpTransport
{
    public Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken ct) => Task.FromResult(handler(request));
}

internal sealed class MockCloud(Func<HttpRequestMessage, string, (int Status, string Body)> handler) : ICloudTransport
{
    public List<string> Paths { get; } = [];

    public async Task<CloudResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Paths)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
        }

        var (status, text) = handler(request, body);
        return new CloudResponse(status, Encoding.UTF8.GetBytes(text));
    }
}

public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "customers-ui-" + Guid.NewGuid().ToString("N"));

    // Одно хранилище секретов на всю «установку»: после повторного открытия база получает тот же ключ.
    private readonly MemorySecretStore _secrets = new();

    internal AppEnvironment Env(CloudConfig? cloud = null, ICloudTransport? transport = null, Func<IHttpTransport>? search = null) =>
        AppEnvironment.ForTests(Root, cloud, transport, search, _secrets);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

internal static class Data
{
    public static LeadData Order(string title, double score = 70, Relevance rel = Relevance.Medium, string ext = "1") => new() {
        Kind = LeadKind.Order,
        Source = "fl_ru",
        ExternalId = ext,
        Title = title,
        Url = "https://fl.ru/p/" + ext,
        Description = "Нужен " + title,
        DiscoveredAt = DateTimeOffset.Parse("2026-11-05T10:00:00Z"),
        Score = score,
        Relevance = rel,
    };

    public static LeadData Org(string name, string region = "Казань") => new() {
        Kind = LeadKind.Organization,
        Source = "osm_organizations",
        ExternalId = "n/" + name,
        Title = name,
        Region = region,
        Category = "Салоны красоты",
        Phone = "+7 843 000-00-00",
        DiscoveredAt = DateTimeOffset.Parse("2026-11-04T10:00:00Z"),
        Score = 50,
        Relevance = Relevance.Medium,
    };

    public static string Token(string access = "a1", string refresh = "r1", string email = "u@example.com", string id = "11111111-1111-1111-1111-111111111111") =>
        new JsonObject {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["expires_in"] = 3600,
            ["user"] = new JsonObject { ["id"] = id, ["email"] = email, ["email_confirmed_at"] = "2026-11-06T10:00:00Z" },
        }.ToJsonString();

    public static readonly CloudConfig Config = CloudConfig.Create("https://example.supabase.co", "sb_publishable_test")!;

    public static void Seed(AppModel m)
    {
        var s = m.Store!;
        s.Save(Order("Сайт для салона", 80, Relevance.High, "1"), "a", "fa");
        s.Save(Order("Telegram-бот для кофейни", 60, Relevance.Medium, "2"), "b", "fb");
        s.Save(Order("Мусорный заказ", 10, Relevance.Rejected, "3"), "c", "fc");
        s.Save(Org("Лаванда"), "d", "fd");
        m.Reload();
    }
}
