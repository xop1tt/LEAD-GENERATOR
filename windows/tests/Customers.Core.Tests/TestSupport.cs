using System.Text;

namespace Customers.Core.Tests;

/// <summary>Подмена сети для тестов.</summary>
internal sealed class MockTransport(Func<HttpRequestSpec, HttpResult> handler) : IHttpTransport
{
    public List<HttpRequestSpec> Requests { get; } = [];

    public Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(handler(request));
    }

    public static HttpResult Ok(string body, Uri? url = null, params (string, string)[] headers) =>
        new(200, Encoding.UTF8.GetBytes(body), headers.ToDictionary(h => h.Item1.ToLowerInvariant(), h => h.Item2), url);

    public static HttpResult Status(int code, Uri? url = null) => new(code, [], new Dictionary<string, string>(), url);
}

public sealed class TempDir : IDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "customers-test-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

internal static class Samples
{
    public static readonly string Rss = """
        <?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel><title>FL</title>
        <item><title><![CDATA[Сделать лендинг для студии]]></title><link>https://www.fl.ru/projects/1/</link>
        <guid>https://www.fl.ru/projects/1/</guid><description><![CDATA[<p>Нужен сайт &amp; форма записи. Бюджет 20 000 руб.</p>]]></description>
        <pubDate>Thu, 05 Nov 2026 10:00:00 +0300</pubDate><category>Сайты</category></item>
        <item><title>Ищу дизайнера логотипа</title><link>https://www.fl.ru/projects/2/</link><guid>https://www.fl.ru/projects/2/</guid>
        <description>Логотип для кафе</description></item>
        </channel></rss>
        """;

    public static readonly string Telegram = """
        <div class="tgme_widget_message_wrap js-widget_message_wrap"><div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="freetasks/1201" data-view="x">
        <div class="tgme_widget_message_bubble"><div class="tgme_widget_message_text js-message_text" dir="auto">Ищу разработчика <b>Telegram-бота</b> для записи клиентов.<br/>Бюджет 30 000 руб. Пишите @client_manager</div>
        <div class="tgme_widget_message_footer"><a class="tgme_widget_message_date" href="https://t.me/freetasks/1201"><time datetime="2026-11-06T10:00:00+00:00" class="time">10:00</time></a></div></div></div></div>
        <div class="tgme_widget_message_wrap"><div class="tgme_widget_message js-widget_message" data-post="freetasks/1200">
        <div class="tgme_widget_message_text js-message_text" dir="auto">Продам курс по маркетингу</div>
        <time datetime="2026-11-06T09:00:00+00:00"></time></div></div>
        """;

    public static readonly string Overpass = """
        {"elements":[
          {"type":"node","id":42,"lat":55.79,"lon":49.12,"tags":{"name":"Студия «Лаванда»","shop":"beauty","phone":"+7 843 212-48-90","addr:street":"Баумана","addr:housenumber":"44"}},
          {"type":"node","id":43,"tags":{"shop":"beauty"}}
        ]}
        """;

    public static LeadData Order(string title = "Нужен сайт для салона") => new() {
        Kind = LeadKind.Order,
        Source = "fl_ru",
        ExternalId = title,
        Title = title,
        Url = "https://fl.ru/p/1",
        Description = "Описание заказа",
        DiscoveredAt = DateTimeOffset.Parse("2026-11-05T10:00:00Z"),
        Score = 70,
        Relevance = Relevance.High,
    };

    public static LeadData Org(string name = "Лаванда") => new() {
        Kind = LeadKind.Organization,
        Source = "osm_organizations",
        ExternalId = "node/1",
        Title = name,
        CustomerName = name,
        Category = "Салоны красоты",
        Phone = "+7 843 212-48-90",
        Website = "https://www.lavanda.ru/about",
        Region = "Казань",
        Address = "Баумана, 44",
        Contacts = new Dictionary<string, string> { ["email"] = "info@lavanda.ru" },
        DiscoveredAt = DateTimeOffset.Parse("2026-11-05T10:00:00Z"),
        Score = 65,
        Relevance = Relevance.High,
    };
}
