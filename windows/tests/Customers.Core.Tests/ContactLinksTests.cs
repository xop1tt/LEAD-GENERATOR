namespace Customers.Core.Tests;

public class ContactLinksTests
{
    [Theory]
    [InlineData("8 (843) 000-00-00", "+78430000000")]
    [InlineData("+7 843 000-00-00", "+78430000000")]
    [InlineData("7 843 000 00 00", "+78430000000")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    [InlineData("8 800 555-35-35 доб. 123", "+78005553535")]
    [InlineData("+7 843 000-00-00, +7 900 000-00-00", "+78430000000")]
    [InlineData("12-34", null)]
    [InlineData("нет телефона", null)]
    [InlineData("1234567890123456", null)]
    [InlineData("٨٠٠١٢٣٤٥", null)]
    public void PhonesAreNormalizedLikeOnMac(string raw, string? expected) =>
        Assert.Equal(expected, ContactLinks.NormalizedPhone(raw));

    [Fact]
    public void TelUri()
    {
        Assert.Equal("tel:+78430000000", ContactLinks.TelUri("8 (843) 000-00-00")!.AbsoluteUri);
        Assert.Null(ContactLinks.TelUri("abc"));
    }

    [Theory]
    [InlineData("a@b.ru", true)]
    [InlineData("first.last+tag@sub.example.com", true)]
    [InlineData("Иван@почта.рф", true)]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("a@b", false)]
    [InlineData("@b.ru", false)]
    [InlineData("a@.ru", false)]
    [InlineData("a@b.", false)]
    [InlineData("a@@b.ru", false)]
    [InlineData("a b@c.ru", false)]
    [InlineData("a@b..ru", false)]
    [InlineData("a@b.ru\r\nBcc: x@y.ru", false)]
    [InlineData("<a@b.ru>", false)]
    [InlineData("a@b.ru,c@d.ru", false)]
    public void EmailValidation(string raw, bool ok) => Assert.Equal(ok, ContactLinks.IsEmail(raw));

    [Fact]
    public void LongLocalPartIsRejected() => Assert.False(ContactLinks.IsEmail(new string('a', 65) + "@b.ru"));

    [Fact]
    public void MailtoEncodesEverythingOutsideUnreserved()
    {
        var uri = ContactLinks.MailtoUri("info@example.ru", "Привет & пока", "Строка 1\nСтрока 2 + 3=4#")!;
        var s = uri.AbsoluteUri;
        Assert.StartsWith("mailto:info@example.ru?subject=", s, StringComparison.Ordinal);
        Assert.Contains("%20%26%20", s, StringComparison.Ordinal);
        Assert.Contains("%0D%0A", s, StringComparison.Ordinal);
        Assert.Contains("%2B", s, StringComparison.Ordinal);
        Assert.Contains("%3D", s, StringComparison.Ordinal);
        Assert.Contains("%23", s, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', s);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("Привет & пока", query["subject"]);
        Assert.Equal("Строка 1\r\nСтрока 2 + 3=4#", query["body"]);
    }

    [Fact]
    public void MailtoWithoutSubjectAndInvalidAddress()
    {
        Assert.Equal("mailto:a@b.ru", ContactLinks.MailtoUri(" a@b.ru ")!.AbsoluteUri);
        Assert.Null(ContactLinks.MailtoUri("a@b.ru?cc=x@y.ru"));
        Assert.Null(ContactLinks.MailtoUri("не адрес"));
    }

    [Fact]
    public void EmailDraftSplitsSubject()
    {
        var org = new LeadData { Kind = LeadKind.Organization, Source = "osm", ExternalId = "1", Title = "Студия «Лаванда»", Phone = "+7 843 000-00-00" };
        var d = ContactLinks.EmailFor(org, "Максим");
        Assert.Equal("Предложение для Студия «Лаванда»", d.Subject);
        Assert.StartsWith("Здравствуйте!", d.Body, StringComparison.Ordinal);
        Assert.EndsWith("Максим", d.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Тема:", d.Body, StringComparison.Ordinal);
        Assert.Equal($"Тема: {d.Subject}\n\n{d.Body}", DraftWriter.Draft(org, DraftWriter.Channel.Email, "Максим"));
    }

    [Fact]
    public void SplitWithoutSubject()
    {
        Assert.Equal(new EmailDraft("", "Просто текст"), ContactLinks.Split("Просто текст"));
        Assert.Equal(new EmailDraft("Только тема", ""), ContactLinks.Split("Тема: Только тема"));
    }
}
