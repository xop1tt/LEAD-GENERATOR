namespace Customers.Core.Tests;

public class SearchTextTests
{
    /// <summary>Значения посчитаны Python-версией и проверены в macOS-версии: ID и отпечатки лидов на двух платформах совпадают.</summary>
    [Fact]
    public void ParityWithPythonAndMac()
    {
        Assert.Equal("ba7816bf8f01cfea414140de", SearchText.ShortHash("abc"));
        Assert.Equal("f799338367e4bad8e2cc757d", SearchText.LeadId("fl_ru", "55"));
        var order = new LeadData { Kind = LeadKind.Order, Source = "fl_ru", ExternalId = "55", Title = "Нужен Telegram-бот!", Description = "Ищу разработчика ёлки: https://x.ru/a, бюджет 40к" };
        Assert.Equal("de84251497b9b137f0137d6e", SearchText.Fingerprint(order));
        var org = new LeadData { Kind = LeadKind.Organization, Source = "osm_organizations", ExternalId = "node/1", Title = "Лаванда", Website = "https://www.Lavanda.ru/about" };
        Assert.Equal("de37bc45ae1a1ab67798eb98", SearchText.Fingerprint(org));
        Assert.Equal("нужен telegram бот елка 40к", SearchText.Normalize("Нужен Telegram-бот! Ёлка https://x.ru/a, 40к"));
    }

    [Fact]
    public void FingerprintOfOrganizationFallsBackToPhoneThenNameAndAddress()
    {
        var byPhone = new LeadData { Kind = LeadKind.Organization, Title = "A", Phone = "+7 (843) 212-48-90" };
        var samePhoneOtherName = new LeadData { Kind = LeadKind.Organization, Title = "Другое имя", Phone = "88432124890" };
        Assert.Equal(SearchText.Fingerprint(byPhone), SearchText.Fingerprint(samePhoneOtherName));
        var a = new LeadData { Kind = LeadKind.Organization, Title = "Кафе «Ёлка»", Address = "Улица, 1" };
        var b = new LeadData { Kind = LeadKind.Organization, Title = "кафе елка", Address = "улица 1" };
        Assert.Equal(SearchText.Fingerprint(a), SearchText.Fingerprint(b));
    }

    [Fact]
    public void FingerprintOfOrderCutsAt400Characters()
    {
        var tail1 = new LeadData { Title = "t", Description = new string('а', 500) + " один" };
        var tail2 = new LeadData { Title = "t", Description = new string('а', 500) + " два" };
        Assert.Equal(SearchText.Fingerprint(tail1), SearchText.Fingerprint(tail2));
    }

    [Fact]
    public void HtmlAndContacts()
    {
        Assert.Equal("Нужен сайт & форма «записи»", SearchText.HtmlToText("<p>Нужен сайт &amp; форма &#171;записи&#187;</p>"));
        Assert.Equal("Строка 1\nСтрока 2", SearchText.HtmlToText("Строка 1<br/>Строка <b>2</b>", lines: true));
        Assert.Equal("a b", SearchText.HtmlToText("a<script>var x = '<b>';</script> b"));
        var c = SearchText.Contacts("Пишите на mail@studio.ru. или +7 (900) 123-45-67, tg @client_manager");
        Assert.Equal("mail@studio.ru", c["email"]);
        Assert.Equal("+79001234567", c["phone"]);
        Assert.Equal("@client_manager", c["telegram"]);
        Assert.True(SearchText.Prefilter("Разработка сайта", ["разработ"]));
        Assert.False(SearchText.Prefilter("Дизайн логотипа", ["разработ", "бот"]));
        Assert.True(SearchText.Prefilter("что угодно", []));
    }

    [Theory]
    [InlineData("&#x41;&#66;", "AB")]
    [InlineData("&unknown; &amp;", "&unknown; &")]
    [InlineData("&#99999999;", "&#99999999;")]
    public void Entities(string input, string expected) => Assert.Equal(expected, SearchText.DecodeEntities(input));

    [Fact]
    public void SourceInput()
    {
        Assert.Equal("freetasks", Customers.Core.SourceInput.TelegramChannel("https://t.me/s/freetasks"));
        Assert.Equal("freetasks", Customers.Core.SourceInput.TelegramChannel("@freetasks"));
        Assert.Null(Customers.Core.SourceInput.TelegramChannel("плохое имя"));
        Assert.Equal("club_orders", Customers.Core.SourceInput.VkCommunity("https://vk.com/club_orders"));
        Assert.Equal("https://www.fl.ru/rss/all.xml", Customers.Core.SourceInput.FeedUrl("www.fl.ru/rss/all.xml"));
        Assert.Null(Customers.Core.SourceInput.FeedUrl("http://localhost/rss"));
        Assert.Equal(["сайт", "бот", "парсер"], Customers.Core.SourceInput.Split("сайт, бот;\nпарсер\n\n"));
    }

    [Fact]
    public void SettingsKeepUnknownAndMissingFields()
    {
        var s = new SearchSettings();
        s.Telegram.Channels = ["mychannel"];
        var restored = SearchSettings.Decode(s.Encoded());
        Assert.Equal(s.Encoded(), restored.Encoded());

        // Старый JSON без новых полей: значения по умолчанию, а не сброс.
        var partial = SearchSettings.Decode("""{"telegram":{"channels":["a_channel"]}}""");
        Assert.Equal(["a_channel"], partial.Telegram.Channels);
        Assert.Equal(3, partial.Telegram.MaxPages);
        Assert.True(partial.Rss.Enabled);
        Assert.Equal("https://www.fl.ru/rss/all.xml", Assert.Single(SearchSettings.Decode("не json").Rss.Feeds));
    }

    /// <summary>JSON настроек читают обе платформы: имена полей — camelCase, как у macOS-приложения.</summary>
    [Fact]
    public void SettingsJsonUsesMacFieldNames()
    {
        var json = new SearchSettings().Encoded();
        Assert.Contains("\"intervalHours\":2", json);
        Assert.Contains("\"maxPages\":3", json);
        Assert.Contains("\"checkWebsites\":true", json);
        Assert.Contains("\"refreshDays\":7", json);
        Assert.Contains("\"maxNewPerRun\":40", json);
        Assert.Contains("\"endpoint\":\"https://overpass-api.de/api/interpreter\"", json);
    }
}

public class ScoringTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T11:00:00Z");

    /// <summary>Тот же пример в Python- и macOS-версиях даёт 80, «high», telegram_bot, бюджет 30 000.</summary>
    [Fact]
    public void OrderMatchesPythonAndMac()
    {
        const string text = "Ищу разработчика Telegram-бота для записи клиентов.\nБюджет 30 000 руб. Пишите @client_manager";
        var lead = new LeadData {
            Kind = LeadKind.Order,
            Source = "telegram_channels",
            ExternalId = "freetasks/1201",
            Title = "Ищу разработчика Telegram-бота для записи клиентов.",
            Description = text,
            PublishedAt = Now.AddHours(-1),
        };
        new Scorer().ScoreOrder(lead, Now);
        Assert.Equal(80, lead.Score);
        Assert.Equal(Relevance.High, lead.Relevance);
        Assert.Equal("telegram_bot", lead.ProjectType);
        Assert.Equal(30000, lead.Budget);
        Assert.Equal("тематика «telegram_bot» (+30)", lead.ScoreReasons[0]);
    }

    [Fact]
    public void Rejections()
    {
        var stop = new LeadData { Source = "x", ExternalId = "1", Title = "Разработчик сайтов в штат, оклад" };
        new Scorer().ScoreOrder(stop, Now);
        Assert.Equal(Relevance.Rejected, stop.Relevance);
        Assert.Equal("стоп-слова: в штат, оклад", stop.ScoreReasons[0]);

        var offTopic = new LeadData { Source = "x", ExternalId = "2", Title = "Ищу дизайнера логотипа" };
        new Scorer().ScoreOrder(offTopic, Now);
        Assert.Equal(["нет признаков разработки"], offTopic.ScoreReasons);

        var old = new LeadData { Source = "x", ExternalId = "3", Title = "Нужен лендинг", PublishedAt = Now.AddDays(-30) };
        new Scorer().ScoreOrder(old, Now);
        Assert.Equal("устарело", old.ScoreReasons[^1]);
        Assert.Equal(0, old.Score);
    }

    [Fact]
    public void OfferIsPenalizedAndSpamDetected()
    {
        var offer = new LeadData { Source = "x", ExternalId = "4", Title = "Сделаю сайт под ключ, мои услуги", Description = "Сделаю сайт быстро и недорого для вашей компании" };
        new Scorer().ScoreOrder(offer, Now);
        Assert.Contains("похоже на предложение услуг, а не заказ", offer.ScoreReasons);

        var spam = new LeadData { Source = "x", ExternalId = "5", Title = "Нужен сайт", Description = "пассивный доход, нужен сайт" };
        new Scorer().ScoreOrder(spam, Now);
        Assert.Contains("признаки спама", spam.ScoreReasons);
    }

    [Fact]
    public void LowBudgetAndHardTasks()
    {
        var lead = new LeadData { Source = "x", ExternalId = "6", Title = "Нужен сайт, бюджет 1500 руб", Description = "нужно интегрировать с 1с" };
        new Scorer().ScoreOrder(lead, Now);
        Assert.Contains(lead.ScoreReasons, r => r.StartsWith("бюджет ниже 3000", StringComparison.Ordinal));
        Assert.Contains(lead.ScoreReasons, r => r.Contains("1с", StringComparison.Ordinal));
    }

    [Fact]
    public void Organization()
    {
        var lead = new LeadData { Kind = LeadKind.Organization, Source = "osm_organizations", ExternalId = "node/1", Title = "Лаванда", Category = "Салоны красоты", Phone = "+7 843 000-00-00" };
        new Scorer().ScoreOrganization(lead, 20, new Dictionary<string, bool> { ["no_website"] = true });
        Assert.Equal(65, lead.Score); // 20 + 35 + 10
        Assert.Equal(Relevance.High, lead.Relevance);

        var bare = new LeadData { Kind = LeadKind.Organization, Title = "Без контактов", Category = "Кафе" };
        new Scorer().ScoreOrganization(bare, null, new Dictionary<string, bool>());
        Assert.Equal(0, bare.Score); // 10 - 20 → не ниже нуля
        Assert.Contains("нет контактов", bare.ScoreReasons);
    }

    [Fact]
    public void Budget()
    {
        Assert.Equal(50000, Customers.Core.Budget.Parse("Бюджет: 50к")?.Value);
        Assert.Equal("USD", Customers.Core.Budget.Parse("500 $")?.Currency);
        Assert.Equal(1_500_000, Customers.Core.Budget.Parse("Оплата 1.500.000 руб")?.Value);
        Assert.Null(Customers.Core.Budget.Parse("$500"));
        Assert.Null(Customers.Core.Budget.Parse("Нужно 3 страницы"));
        Assert.Equal(2_000_000, Customers.Core.Budget.Parse("до 2 млн руб")?.Value);
    }
}
