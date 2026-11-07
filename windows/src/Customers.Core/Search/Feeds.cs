using System.Globalization;
using System.Xml;

namespace Customers.Core;

/// <summary>Запись RSS или Atom.</summary>
public sealed record FeedEntry(string Id, string Title, string Link, string Summary, DateTimeOffset? Published, string? Category);

/// <summary>Разбор RSS 2.0 и Atom без сторонних библиотек.</summary>
public static class FeedParser
{
    private static string Local(string name)
    {
        var i = name.LastIndexOf(':');
        return (i >= 0 ? name[(i + 1)..] : name).ToLowerInvariant();
    }

    /// <summary>Записи ленты или <c>null</c>, если это не лента (и ничего разобрать не удалось).</summary>
    public static List<FeedEntry>? Parse(byte[] data)
    {
        var entries = new List<FeedEntry>();
        var settings = new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = false,
        };
        Dictionary<string, string>? current = null;
        var text = new System.Text.StringBuilder();
        try
        {
            using var stream = new MemoryStream(data);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var name = Local(reader.Name);
                        if (name is "item" or "entry")
                        {
                            current = [];
                        }
                        else if (current is not null && name == "link" && reader.GetAttribute("href") is { } href)
                        {
                            // Atom: <link rel="alternate" href="…"/>
                            var rel = reader.GetAttribute("rel") ?? "alternate";
                            if (rel == "alternate" && !current.ContainsKey("link"))
                            {
                                current["link"] = href;
                            }
                        }
                        else if (current is not null && name == "category" && reader.GetAttribute("term") is { } term
                                 && !current.ContainsKey("category"))
                        {
                            current["category"] = term;
                        }

                        text.Clear();
                        if (reader.IsEmptyElement)
                        {
                            EndElement(name, current, text, entries, out current);
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement:
                        EndElement(Local(reader.Name), current, text, entries, out current);
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return entries.Count == 0 ? null : entries;
        }

        return entries;
    }

    private static void EndElement(string name, Dictionary<string, string>? cur, System.Text.StringBuilder text,
        List<FeedEntry> entries, out Dictionary<string, string>? next)
    {
        next = cur;
        if (cur is null)
        {
            return;
        }

        var value = text.ToString().Trim();
        switch (name)
        {
            case "item" or "entry":
                var id = First(cur, "guid", "id", "link", "title") ?? "";
                var summary = First(cur, "description", "summary", "encoded", "content") ?? "";
                var date = ParseDate(First(cur, "pubdate", "published", "updated", "date"));
                entries.Add(new FeedEntry(id, cur.GetValueOrDefault("title") ?? "", cur.GetValueOrDefault("link") ?? "",
                    summary, date, cur.GetValueOrDefault("category")));
                next = null;
                return;
            case "title" or "guid" or "id" or "description" or "summary" or "encoded" or "content" or "pubdate"
                or "published" or "updated" or "date" or "link" or "category":
                if (!cur.ContainsKey(name) && value.Length > 0)
                {
                    cur[name] = value;
                }

                break;
        }

        text.Clear();
    }

    private static string? First(Dictionary<string, string> d, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (d.TryGetValue(k, out var v))
            {
                return v;
            }
        }

        return null;
    }

    private static readonly Dictionary<string, TimeSpan> Zones = new(StringComparer.OrdinalIgnoreCase) {
        ["GMT"] = TimeSpan.Zero,
        ["UT"] = TimeSpan.Zero,
        ["UTC"] = TimeSpan.Zero,
        ["Z"] = TimeSpan.Zero,
        ["EST"] = TimeSpan.FromHours(-5),
        ["EDT"] = TimeSpan.FromHours(-4),
        ["CST"] = TimeSpan.FromHours(-6),
        ["CDT"] = TimeSpan.FromHours(-5),
        ["MST"] = TimeSpan.FromHours(-7),
        ["MDT"] = TimeSpan.FromHours(-6),
        ["PST"] = TimeSpan.FromHours(-8),
        ["PDT"] = TimeSpan.FromHours(-7),
        ["MSK"] = TimeSpan.FromHours(3),
    };

    /// <summary>ISO 8601 или RFC 822 («Wed, 05 Nov 2026 09:42:00 +0300», «GMT», «MSK»).</summary>
    public static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();
        if (char.IsDigit(s[0]) && s.Length >= 10 && s[4] == '-' && ISODate.Parse(s) is { } iso)
        {
            return iso;
        }

        // RFC 822: [день недели,] DD Mon YYYY HH:MM[:SS] ZONE
        var comma = s.IndexOf(',');
        if (comma >= 0)
        {
            s = s[(comma + 1)..].Trim();
        }

        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return null;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return null;
        }

        var month = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
            m => m.Length > 0 && parts[1].StartsWith(m, StringComparison.OrdinalIgnoreCase)) + 1;
        if (month < 1 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        if (year < 100)
        {
            year += year < 70 ? 2000 : 1900;
        }

        var hms = parts[3].Split(':');
        if (hms.Length < 2 || !int.TryParse(hms[0], out var h) || !int.TryParse(hms[1], out var mi))
        {
            return null;
        }

        var sec = hms.Length > 2 && int.TryParse(hms[2], out var sv) ? sv : 0;
        var offset = TimeSpan.Zero;
        if (parts.Length > 4)
        {
            var z = parts[4];
            if ((z[0] == '+' || z[0] == '-') && z.Length >= 5 && int.TryParse(z.AsSpan(1, 2), out var oh) && int.TryParse(z.AsSpan(3, 2), out var om))
            {
                offset = new TimeSpan(oh, om, 0);
                if (z[0] == '-')
                {
                    offset = -offset;
                }
            }
            else if (Zones.TryGetValue(z, out var known))
            {
                offset = known;
            }
        }

        try
        {
            return new DateTimeOffset(year, month, day, h, mi, sec, offset).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
