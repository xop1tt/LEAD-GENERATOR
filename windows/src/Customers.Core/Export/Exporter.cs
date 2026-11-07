using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Customers.Core;

/// <summary>Поля, доступные при экспорте. Имена и порядок совпадают с macOS-версией (общий формат файлов).</summary>
public enum ExportField
{
    Id, Kind, Title, Source, Score, Relevance, Region, Category, Address, Phone, Website, Emails,
    Url, DiscoveredAt, Budget, Reasons, Facts, Assumptions, Favorite, Note, Description,
}

public static class ExportFields
{
    public static readonly IReadOnlyList<ExportField> All = Enum.GetValues<ExportField>();

    public static readonly IReadOnlyList<ExportField> Defaults =
    [
        ExportField.Title, ExportField.Kind, ExportField.Source, ExportField.Score, ExportField.Relevance, ExportField.Region,
        ExportField.Category, ExportField.Address, ExportField.Phone, ExportField.Website, ExportField.Emails, ExportField.Url,
        ExportField.DiscoveredAt, ExportField.Reasons,
    ];

    /// <summary>Ключ в JSON-экспорте (как <c>rawValue</c> в Swift): discoveredAt, emails, …</summary>
    public static string RawName(this ExportField f)
    {
        var s = f.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static string Title(this ExportField f) => f switch {
        ExportField.Id => "ID",
        ExportField.Kind => "Тип",
        ExportField.Title => "Название",
        ExportField.Source => "Источник",
        ExportField.Score => "Оценка",
        ExportField.Relevance => "Релевантность",
        ExportField.Region => "Регион",
        ExportField.Category => "Категория",
        ExportField.Address => "Адрес",
        ExportField.Phone => "Телефон",
        ExportField.Website => "Сайт",
        ExportField.Emails => "Email",
        ExportField.Url => "Ссылка",
        ExportField.DiscoveredAt => "Обнаружен",
        ExportField.Budget => "Бюджет",
        ExportField.Reasons => "Причины оценки",
        ExportField.Facts => "Факты",
        ExportField.Assumptions => "Предположения",
        ExportField.Favorite => "Избранное",
        ExportField.Note => "Заметка",
        _ => "Описание",
    };

    public static string Value(this ExportField f, LeadItem l)
    {
        var d = l.Data;
        return f switch {
            ExportField.Id => l.Id,
            ExportField.Kind => d.Kind.Title(),
            ExportField.Title => d.Title,
            ExportField.Source => SourceCatalog.Title(d.Source),
            ExportField.Score => ((long)Math.Round(d.Score, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
            ExportField.Relevance => d.Relevance.Title(),
            ExportField.Region => d.Region ?? "",
            ExportField.Category => d.Category ?? d.ProjectType ?? "",
            ExportField.Address => d.Address ?? "",
            ExportField.Phone => string.Join(", ", d.Phones),
            ExportField.Website => d.Website ?? "",
            ExportField.Emails => string.Join(", ", d.Emails),
            ExportField.Url => d.Url,
            ExportField.DiscoveredAt => ISODate.Format(l.DiscoveredAt) ?? "",
            ExportField.Budget => d.Budget is { } b ? $"{(long)b} {d.Currency ?? ""}".Trim() : "",
            ExportField.Reasons => string.Join("; ", d.ScoreReasons),
            ExportField.Facts => string.Join("; ", d.Facts),
            ExportField.Assumptions => string.Join("; ", d.Assumptions),
            ExportField.Favorite => l.Favorite ? "да" : "",
            ExportField.Note => l.Note,
            _ => d.Description,
        };
    }
}

public enum ExportFormat
{
    Csv,
    Json,
}

public static class Exporter
{
    public static string Extension(this ExportFormat f) => f == ExportFormat.Csv ? "csv" : "json";

    public static string Title(this ExportFormat f) => f == ExportFormat.Csv ? "CSV (Excel)" : "JSON";

    /// <summary>CSV с BOM, чтобы Excel правильно открыл кириллицу.</summary>
    public static byte[] Csv(IEnumerable<LeadItem> leads, IEnumerable<ExportField>? fields = null)
    {
        var fs = (fields ?? ExportFields.Defaults).ToList();
        var lines = new List<string> { string.Join(",", fs.Select(f => Customers.Core.Csv.Escape(f.Title()))) };
        foreach (var l in leads)
        {
            lines.Add(string.Join(",", fs.Select(f => Customers.Core.Csv.Escape(f.Value(l)))));
        }

        return [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n")];
    }

    public static byte[] Json(IEnumerable<LeadItem> leads, IEnumerable<ExportField>? fields = null)
    {
        var fs = (fields ?? ExportFields.All).ToList();
        var rows = new JsonArray();
        foreach (var l in leads)
        {
            var o = new JsonObject();
            foreach (var f in fs.OrderBy(f => f.RawName(), StringComparer.Ordinal))
            {
                o[f.RawName()] = f.Value(l);
            }

            rows.Add(o);
        }

        return Encoding.UTF8.GetBytes(rows.ToJsonString(new System.Text.Json.JsonSerializerOptions {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    public static byte[] Export(IEnumerable<LeadItem> leads, ExportFormat format, IEnumerable<ExportField> fields) =>
        format == ExportFormat.Csv ? Csv(leads, fields) : Json(leads, fields);
}

/// <summary>Результат импорта файла, ранее экспортированного из Customers (macOS или Windows).</summary>
public sealed record ImportResult(int Added, int Duplicates, int Skipped, List<string> Problems);

/// <summary>
/// Импорт CSV и JSON, выгруженных из Customers. Идентификатор лида берётся из колонки ID, поэтому один и тот же
/// лид на разных компьютерах остаётся одним лидом, а повторный импорт не создаёт дублей.
/// </summary>
public static class Importer
{
    public static ImportResult Import(LeadStore store, byte[] file)
    {
        var text = Encoding.UTF8.GetString(file).TrimStart('﻿');
        List<Dictionary<string, string>> rows;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('['))
        {
            rows = ReadJson(trimmed);
        }
        else
        {
            rows = ReadCsv(text);
        }

        var problems = new List<string>();
        int added = 0, dup = 0, skipped = 0;
        foreach (var row in rows)
        {
            var data = ToLead(row);
            if (data is null)
            {
                skipped++;
                continue;
            }

            var id = row.GetValueOrDefault("id");
            if (string.IsNullOrEmpty(id))
            {
                id = SearchText.LeadId(data.Source, data.ExternalId);
            }

            data.ExternalId = string.IsNullOrEmpty(data.ExternalId) ? id : data.ExternalId;
            data.DiscoveredAt ??= DateTimeOffset.UtcNow;
            var outcome = store.Save(data, id, SearchText.Fingerprint(data));
            switch (outcome)
            {
                case SaveOutcome.Added:
                    added++;
                    if (row.GetValueOrDefault("favorite") is "да" or "true")
                    {
                        store.SetFavorite([id], true);
                    }

                    if (!string.IsNullOrEmpty(row.GetValueOrDefault("note")))
                    {
                        store.SetNote(id, row["note"]);
                    }

                    break;
                default:
                    dup++;
                    break;
            }
        }

        store.ApplyExclusions();
        if (rows.Count == 0)
        {
            problems.Add("В файле нет строк с данными.");
        }

        return new ImportResult(added, dup, skipped, problems);
    }

    private static List<Dictionary<string, string>> ReadJson(string text)
    {
        var rows = new List<Dictionary<string, string>>();
        if (JsonNode.Parse(text) is not JsonArray arr)
        {
            return rows;
        }

        foreach (var o in arr.OfType<JsonObject>())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in o)
            {
                if (v is not null)
                {
                    row[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToString();
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<Dictionary<string, string>> ReadCsv(string text)
    {
        var records = Csv.ParseDocument(text);
        if (records.Count < 2)
        {
            return [];
        }

        // Заголовки — русские названия колонок (как в экспорте) или английские ключи.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ExportFields.All)
        {
            map[f.Title()] = f.RawName();
            map[f.RawName()] = f.RawName();
        }

        var header = records[0].Select(h => map.GetValueOrDefault(h.Trim(), h.Trim())).ToList();
        var rows = new List<Dictionary<string, string>>();
        foreach (var rec in records.Skip(1))
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < rec.Count; i++)
            {
                // Апостроф, которым экспорт обезвреживает формулы, снимается.
                var v = rec[i];
                if (v.Length > 1 && v[0] == '\'' && "=+-@\t\r".Contains(v[1]))
                {
                    v = v[1..];
                }

                row[header[i]] = v;
            }

            rows.Add(row);
        }

        return rows;
    }

    internal static LeadData? ToLead(Dictionary<string, string> row)
    {
        string S(string k) => row.GetValueOrDefault(k) ?? "";
        var title = S("title").Trim();
        if (title.Length == 0)
        {
            return null;
        }

        var kind = S("kind") switch {
            "Организация" or "organization" => LeadKind.Organization,
            _ => LeadKind.Order,
        };
        var relevance = S("relevance") switch {
            "Высокая" or "high" => Relevance.High,
            "Средняя" or "medium" => Relevance.Medium,
            "Низкая" or "low" => Relevance.Low,
            _ => Relevance.Rejected,
        };
        var d = new LeadData {
            Kind = kind,
            Source = SourceCatalog.KeyForTitle(S("source")),
            ExternalId = S("id"),
            Title = title,
            Url = S("url"),
            Description = S("description"),
            DiscoveredAt = ISODate.Parse(S("discoveredAt")),
            Region = NullIfEmpty(S("region")),
            Category = NullIfEmpty(S("category")),
            Address = NullIfEmpty(S("address")),
            Website = NullIfEmpty(S("website")),
            Score = double.TryParse(S("score"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 0,
            Relevance = relevance,
            ScoreReasons = SplitList(S("reasons")),
            Facts = SplitList(S("facts")),
            Assumptions = SplitList(S("assumptions")),
        };
        var phones = SplitComma(S("phone"));
        if (phones.Count > 0)
        {
            d.Phone = phones[0];
            for (var i = 1; i < phones.Count; i++)
            {
                d.Contacts[$"phone{i + 1}"] = phones[i];
            }
        }

        var emails = SplitComma(S("emails"));
        for (var i = 0; i < emails.Count; i++)
        {
            d.Contacts[i == 0 ? "email" : $"email{i + 1}"] = emails[i];
        }

        var budget = S("budget").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (budget.Length > 0 && double.TryParse(budget[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            d.Budget = b;
            d.Currency = budget.Length > 1 ? budget[1] : null;
        }

        return d;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static List<string> SplitList(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private static List<string> SplitComma(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}

/// <summary>Черновик первого обращения по шаблону, без AI. Отправка только вручную.</summary>
public static class DraftWriter
{
    public enum Channel
    {
        Message,
        Email,
        Call,
    }

    public static string Title(this Channel c) => c switch {
        Channel.Message => "Мессенджер",
        Channel.Email => "Email",
        _ => "Звонок",
    };

    private static readonly Dictionary<string, string> Services = new() {
        ["site"] = "сайт",
        ["telegram_bot"] = "Telegram-бот",
        ["mobile_app"] = "мобильное приложение",
        ["web_app"] = "веб-приложение",
        ["automation"] = "автоматизацию",
        ["crm"] = "CRM-систему",
        ["support"] = "доработку существующего проекта",
    };

    public static string Draft(LeadData lead, Channel channel, string signature)
    {
        var sign = signature.Trim();
        var greeting = channel == Channel.Call
            ? $"Добрый день! Меня зовут {(sign.Length == 0 ? "…" : sign)}, я разработчик."
            : "Здравствуйте!";
        string body;
        if (lead.Kind == LeadKind.Order)
        {
            var what = lead.ProjectType is { } pt && Services.TryGetValue(pt, out var svc) ? svc : "этот проект";
            body = $"Увидел вашу заявку «{lead.Title}». Делаю {what} под ключ: могу коротко рассказать, как бы я это реализовал, и назвать срок и стоимость после пары уточняющих вопросов.";
            if (lead.Budget is not null)
            {
                body += " Бюджет вижу, предложение уложу в него.";
            }
        }
        else
        {
            var name = lead.CustomerName ?? lead.Title;
            var facts = lead.Facts.Select(f => f.ToLowerInvariant()).ToList();
            string problem;
            if (string.IsNullOrEmpty(lead.Website) || facts.Any(f => f.Contains("не указан сайт", StringComparison.Ordinal)))
            {
                problem = $"не нашёл у «{name}» сайта, где можно посмотреть услуги и записаться онлайн";
            }
            else if (facts.Any(f => f.Contains("не открылся", StringComparison.Ordinal) || (f.Contains("http", StringComparison.Ordinal) && f.Contains('5'))))
            {
                problem = $"сайт «{name}» сейчас не открывается";
            }
            else if (facts.Any(f => f.Contains("viewport", StringComparison.Ordinal) || f.Contains("мобильн", StringComparison.Ordinal)))
            {
                problem = $"сайт «{name}» неудобно открывается с телефона";
            }
            else if (facts.Any(f => f.Contains("запис", StringComparison.Ordinal)))
            {
                problem = $"на сайте «{name}» нет онлайн-записи";
            }
            else
            {
                problem = $"у «{name}» можно упростить работу с клиентами";
            }

            body = $"Обратил внимание, что {problem}. Я делаю небольшие сайты, онлайн-запись и Telegram-ботов для бизнеса. Могу бесплатно показать, как это выглядело бы у вас, — займёт пять минут.";
        }

        var text = channel == Channel.Call ? $"{greeting} {body}" : $"{greeting}\n\n{body}";
        if (channel != Channel.Call && sign.Length > 0)
        {
            text += $"\n\n{sign}";
        }

        if (channel == Channel.Email)
        {
            var subject = lead.Kind == LeadKind.Order ? "Ваша заявка: " + lead.Title : "Предложение для " + (lead.CustomerName ?? lead.Title);
            text = $"Тема: {subject}\n\n{text}";
        }

        return text;
    }
}
