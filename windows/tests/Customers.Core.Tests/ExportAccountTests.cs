using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Core.Tests;

public class ExportTests
{
    private static readonly LeadItem[] Items =
    [
        new() { Id = "o1", Data = Fixtures.Order(), DiscoveredAt = ISODate.Parse("2026-11-05T09:15:00Z")!.Value, Favorite = true, Note = "важно" },
        new() { Id = "g1", Data = Fixtures.Org, DiscoveredAt = ISODate.Parse("2026-11-05T09:42:00Z")!.Value },
    ];

    [Fact]
    public void FieldsHaveUniqueTitles()
    {
        Assert.Equal(ExportFields.All.Count, ExportFields.All.Select(f => f.Title()).Distinct().Count());
        Assert.Equal(ExportFields.Defaults.Count, ExportFields.Defaults.Distinct().Count());
        Assert.All(ExportFields.Defaults, f => Assert.Contains(f, ExportFields.All));
        Assert.Equal("discoveredAt", ExportField.DiscoveredAt.RawName());
    }

    [Fact]
    public void FieldValues()
    {
        var o = Items[0];
        var g = Items[1];
        Assert.Equal("o1", ExportField.Id.Value(o));
        Assert.Equal("Заказ", ExportField.Kind.Value(o));
        Assert.Equal("88", ExportField.Score.Value(o));
        Assert.Equal("Высокая", ExportField.Relevance.Value(o));
        Assert.Equal("40000 RUB", ExportField.Budget.Value(o));
        Assert.Equal("", ExportField.Budget.Value(g));
        Assert.Equal("client@example.ru", ExportField.Emails.Value(o));
        Assert.Equal("+7 (843) 212-48-90", ExportField.Phone.Value(g));
        Assert.Equal("да", ExportField.Favorite.Value(o));
        Assert.Equal("", ExportField.Favorite.Value(g));
        Assert.Equal("важно", ExportField.Note.Value(o));
        Assert.Equal("telegram_bot", ExportField.Category.Value(o));
        Assert.Equal("Салоны красоты", ExportField.Category.Value(g));
        Assert.Equal("нет сайта (+35)", ExportField.Reasons.Value(g));
        Assert.Equal("2026-11-05T09:15:00Z", ExportField.DiscoveredAt.Value(o));
        Assert.Equal("OpenStreetMap", ExportField.Source.Value(g));
    }

    [Fact]
    public void CsvStructure()
    {
        var data = Exporter.Csv(Items, [ExportField.Title, ExportField.Score]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, data.Take(3));
        var lines = Encoding.UTF8.GetString(data, 3, data.Length - 3).Split("\r\n");
        Assert.Equal(4, lines.Length);
        Assert.Equal("Название,Оценка", lines[0]);
        Assert.Equal("Нужен Telegram-бот,88", lines[1]);
        Assert.Equal("", lines[3]);
        Assert.Equal("﻿Название,Телефон\r\n", Encoding.UTF8.GetString(Exporter.Csv([], [ExportField.Title, ExportField.Phone])));
    }

    [Fact]
    public void CsvEscapesSpecialCharacters()
    {
        var d = Fixtures.Order("Бот, с \"кавычками\"\nи переносом");
        var text = Encoding.UTF8.GetString(Exporter.Csv([new LeadItem { Id = "x", Data = d, DiscoveredAt = DateTimeOffset.UtcNow }], [ExportField.Title]));
        Assert.Contains("\"Бот, с \"\"кавычками\"\"\nи переносом\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvFormulasAreNeutralized()
    {
        Assert.Equal("\"'=HYPERLINK(\"\"http://x\"\")\"", Csv.Escape("=HYPERLINK(\"http://x\")"));
        Assert.Equal("'@SUM(A1)", Csv.Escape("@SUM(A1)"));
        Assert.Equal("-1+2", Csv.Escape("-1+2"));
        Assert.Equal("'-cmd|x", Csv.Escape("-cmd|x"));
        Assert.Equal("+7 (843) 212-48-90", Csv.Escape("+7 (843) 212-48-90"));
        Assert.Equal("Студия", Csv.Escape("Студия"));
    }

    [Fact]
    public void CsvParsing()
    {
        Assert.Equal(["a", "b, c", "d \"e\""], Csv.ParseLine("a,\"b, c\",\"d \"\"e\"\"\""));
        Assert.Equal(["x", "y", "z"], Csv.ParseLine("x;y;z"));
        Assert.Equal(["ООО Ромашка, Казань, РФ", "info@r.ru", "+7 900 1112233"], Csv.ParseLine("\"ООО Ромашка, Казань, РФ\";info@r.ru;+7 900 1112233"));
        Assert.Equal([""], Csv.ParseLine(""));
        Assert.Equal(["a", "", "c"], Csv.ParseLine("a,,c"));
        Assert.Equal(["a", "b", "c"], Csv.ParseLine("a\tb\tc"));
        Assert.Equal(';', Csv.Separator("a;b;c"));
        Assert.Equal(',', Csv.Separator("a,b,c"));
        Assert.Equal(',', Csv.Separator("одно значение"));
    }

    [Fact]
    public void JsonContainsRequestedFieldsOnly()
    {
        var data = Exporter.Json(Items, [ExportField.Id, ExportField.Title]);
        var rows = JsonNode.Parse(data)!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal(["id", "title"], rows[0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("o1", (string?)rows[0]!["id"]);
        Assert.DoesNotContain("\\/", Encoding.UTF8.GetString(Exporter.Json(Items, [ExportField.Url])), StringComparison.Ordinal);
        Assert.Equal(Exporter.Csv(Items, [ExportField.Title]), Exporter.Export(Items, ExportFormat.Csv, [ExportField.Title]));
        Assert.Equal("json", ExportFormat.Json.Extension());
    }

    [Fact]
    public void ImportRoundTripSkipsDuplicates()
    {
        using var dir = new TempDir();
        using var source = new LeadStore(dir.File("a.db"));
        source.Save(Fixtures.Org, "org1", "fp1");
        source.Save(Fixtures.Order(), "ord1", "fp2");
        var csv = Exporter.Csv(source.AllLeads(), ExportFields.All);

        using var target = new LeadStore(dir.File("b.db"));
        var first = Importer.Import(target, csv);
        Assert.Equal(2, first.Added);
        Assert.Empty(first.Problems);
        Assert.Equal(2, target.AllLeads().Count);
        var again = Importer.Import(target, csv);
        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.Duplicates);
        Assert.Equal(2, target.AllLeads().Count);
    }

    [Fact]
    public void ImportOfGarbageReportsProblems()
    {
        using var dir = new TempDir();
        using var store = new LeadStore(dir.File("a.db"));
        var result = Importer.Import(store, Encoding.UTF8.GetBytes("не таблица"));
        Assert.Equal(0, result.Added);
        Assert.NotEmpty(result.Problems);
    }
}

public class DraftTests
{
    [Fact]
    public void OrderDraft()
    {
        var text = DraftWriter.Draft(Fixtures.Order(), DraftWriter.Channel.Message, "Максим");
        Assert.StartsWith("Здравствуйте!", text, StringComparison.Ordinal);
        Assert.Contains("«Нужен Telegram-бот»", text, StringComparison.Ordinal);
        Assert.Contains("Делаю Telegram-бот под ключ", text, StringComparison.Ordinal);
        Assert.Contains("Бюджет вижу", text, StringComparison.Ordinal);
        Assert.EndsWith("Максим", text, StringComparison.Ordinal);

        var noBudget = Fixtures.Order();
        noBudget.Budget = null;
        Assert.DoesNotContain("Бюджет вижу", DraftWriter.Draft(noBudget, DraftWriter.Channel.Message, ""), StringComparison.Ordinal);
        var blank = DraftWriter.Draft(Fixtures.Order(), DraftWriter.Channel.Message, "  ");
        Assert.False(blank.EndsWith("\n\n", StringComparison.Ordinal) || blank.EndsWith(' '));
    }

    [Fact]
    public void ChannelsDiffer()
    {
        var call = DraftWriter.Draft(Fixtures.Org, DraftWriter.Channel.Call, "Максим");
        Assert.StartsWith("Добрый день! Меня зовут Максим", call, StringComparison.Ordinal);
        Assert.False(call.EndsWith("Максим", StringComparison.Ordinal));
        Assert.StartsWith("Тема: Ваша заявка: Нужен Telegram-бот", DraftWriter.Draft(Fixtures.Order(), DraftWriter.Channel.Email, "М"), StringComparison.Ordinal);
        Assert.StartsWith("Тема: Предложение для", DraftWriter.Draft(Fixtures.Org, DraftWriter.Channel.Email, "М"), StringComparison.Ordinal);
        Assert.Equal(3, Enum.GetValues<DraftWriter.Channel>().Select(c => c.Title()).Distinct().Count());
        var org = DraftWriter.Draft(Fixtures.Org, DraftWriter.Channel.Email, "Максим");
        Assert.Contains("сайта", org, StringComparison.Ordinal);
        Assert.EndsWith("Максим", org, StringComparison.Ordinal);
    }

    [Fact]
    public void OrganizationProblemFollowsFacts()
    {
        var d = Fixtures.Org;
        d.Website = "https://lavanda.ru";
        string Text() => DraftWriter.Draft(d, DraftWriter.Channel.Message, "");
        d.Facts = ["Сайт не открылся"];
        Assert.Contains("не открывается", Text(), StringComparison.Ordinal);
        d.Facts = ["Нет viewport: сайт неудобен с телефона"];
        Assert.Contains("с телефона", Text(), StringComparison.Ordinal);
        d.Facts = ["Нет онлайн-записи (запись только по звонку)"];
        Assert.Contains("онлайн-записи", Text(), StringComparison.Ordinal);
        d.Facts = [];
        Assert.Contains("упростить работу с клиентами", Text(), StringComparison.Ordinal);
        d.Website = null;
        Assert.Contains("сайта", Text(), StringComparison.Ordinal);
    }
}

public class LocalAccountTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void UsernameRules()
    {
        Assert.Equal("maksim", LocalAccounts.Normalized("  maksim  "));
        Assert.Equal("Анна.Иванова-1", LocalAccounts.Normalized("Анна.Иванова-1"));
        Assert.Null(LocalAccounts.Normalized("ab"));
        Assert.Null(LocalAccounts.Normalized(new string('a', 33)));
        Assert.NotNull(LocalAccounts.Normalized(new string('a', 32)));
        Assert.Null(LocalAccounts.Normalized("-abc"));
        Assert.Null(LocalAccounts.Normalized("a b c"));
        Assert.Null(LocalAccounts.Normalized("../etc"));
        Assert.Null(LocalAccounts.Normalized(""));
    }

    [Fact]
    public void UsernameIsCaseInsensitiveButKeepsDisplayName()
    {
        using var auth = new LocalAccounts(_dir.Path);
        Assert.Equal("Maksim", auth.Register("Maksim", "correct horse").Username);
        Assert.Equal("Maksim", auth.Account("MAKSIM")?.Username);
        Assert.Equal(["Maksim"], auth.AllAccounts().Select(a => a.Username));
        Assert.Equal(auth.DataDirectory("Maksim"), auth.DataDirectory("maksim"));
        Assert.Equal(AuthFailure.Exists, Assert.Throws<AuthException>(() => auth.Register("MAKSIM", "correct horse")).Kind);
    }

    [Fact]
    public void PasswordRules()
    {
        using var auth = new LocalAccounts(_dir.Path);
        Assert.Equal(AuthFailure.WeakPassword, Assert.Throws<AuthException>(() => auth.Register("maksim", "1234567")).Kind);
        auth.Register("maksim", "12345678");
        Assert.Equal(AuthFailure.WeakPassword, Assert.Throws<AuthException>(() => auth.ChangePassword("maksim", "12345678", "short")).Kind);
        Assert.Equal(AuthFailure.WrongCredentials, Assert.Throws<AuthException>(() => auth.ChangePassword("maksim", "не тот пароль", "new password")).Kind);
        auth.Login("maksim", "12345678");
        auth.ChangePassword("maksim", "12345678", "new password");
        Assert.Throws<AuthException>(() => auth.Login("maksim", "12345678"));
        auth.Login("maksim", "new password");
    }

    [Fact]
    public void PasswordsAreNotStoredInPlainText()
    {
        using (var auth = new LocalAccounts(_dir.Path))
        {
            auth.Register("maksim", "very secret phrase");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_dir.Path, "accounts.db*"))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("very secret phrase")) < 0);
        }
    }

    [Fact]
    public void AccountsSurviveReopen()
    {
        using (var first = new LocalAccounts(_dir.Path))
        {
            first.Register("maksim", "correct horse");
        }

        using var again = new LocalAccounts(_dir.Path);
        Assert.True(again.HasUsers);
        again.Login("maksim", "correct horse");
        Assert.NotNull(again.Account("maksim")?.LastLoginAt);
    }

    [Fact]
    public void UnknownUserLooksLikeWrongPassword()
    {
        using var auth = new LocalAccounts(_dir.Path);
        auth.Register("maksim", "correct horse");
        foreach (var name in new[] { "maksim", "nobody" })
        {
            Assert.Equal(AuthFailure.WrongCredentials, Assert.Throws<AuthException>(() => auth.Login(name, "wrong wrong")).Kind);
        }
    }

    [Fact]
    public void LockoutAfterFiveFailuresIsTemporaryAndPerName()
    {
        var now = DateTimeOffset.Parse("2026-11-06T10:00:00Z");
        using var auth = new LocalAccounts(_dir.Path, () => now);
        auth.Register("maksim", "correct horse");
        auth.Register("anna", "correct horse");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(AuthFailure.WrongCredentials, Assert.Throws<AuthException>(() => auth.Login("maksim", "wrong wrong")).Kind);
        }

        var locked = Assert.Throws<AuthException>(() => auth.Login("maksim", "correct horse"));
        Assert.Equal(AuthFailure.TooManyAttempts, locked.Kind);
        Assert.InRange(locked.RetryAfterSeconds, 1, 300);
        Assert.Contains(locked.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), locked.Message, StringComparison.Ordinal);
        auth.Login("anna", "correct horse");

        now = now.AddMinutes(10);
        auth.Login("maksim", "correct horse");
    }

    [Fact]
    public void SuccessfulLoginResetsFailureCounter()
    {
        using var auth = new LocalAccounts(_dir.Path);
        auth.Register("maksim", "correct horse");
        for (var i = 0; i < 4; i++)
        {
            Assert.Throws<AuthException>(() => auth.Login("maksim", "wrong wrong"));
        }

        auth.Login("maksim", "correct horse");
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(AuthFailure.WrongCredentials, Assert.Throws<AuthException>(() => auth.Login("maksim", "wrong wrong")).Kind);
        }

        auth.Login("maksim", "correct horse");
    }

    [Fact]
    public void DeleteNeedsPasswordAndRemovesData()
    {
        using var auth = new LocalAccounts(_dir.Path);
        auth.Register("maksim", "correct horse");
        File.WriteAllText(Path.Combine(auth.DataDirectory("maksim"), "leads.db"), "x");
        Assert.Throws<AuthException>(() => auth.Delete("maksim", "wrong wrong"));
        Assert.True(Directory.Exists(auth.DataDirectory("maksim")));
        auth.Delete("maksim", "correct horse");
        Assert.False(Directory.Exists(auth.DataDirectory("maksim")));
        Assert.Null(auth.Account("maksim"));
    }

    [Fact]
    public void ErrorsHaveReadableText()
    {
        foreach (var kind in Enum.GetValues<AuthFailure>())
        {
            Assert.False(string.IsNullOrEmpty(new AuthException(kind, 7).Message));
        }
    }
}
