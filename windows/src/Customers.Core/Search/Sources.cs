using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Customers.Core;

/// <summary>Найденный, но ещё не оценённый лид.</summary>
public sealed class Candidate
{
    public required LeadData Data { get; init; }

    /// <summary>Для организаций: проверенные сигналы и вес категории.</summary>
    public Dictionary<string, bool> Signals { get; init; } = [];

    public double? CategoryWeight { get; init; }
}

/// <summary>Итог опроса одного источника.</summary>
public sealed class FetchResult
{
    public List<Candidate> Candidates { get; } = [];
    public string? Cursor { get; set; }
    public List<string> Warnings { get; } = [];

    /// <summary>Ошибки отдельных лент, каналов и сообществ.</summary>
    public List<string> PartialErrors { get; } = [];

    /// <summary>Сколько независимых подзапросов было (чтобы отличить частичный сбой от полного).</summary>
    public int Items { get; set; } = 1;
}

/// <summary>Хранилище служебного состояния поиска (история запросов OSM, проверенные объекты).</summary>
public interface ISearchMemory
{
    DateTimeOffset? QueryLastRun(string hash);
    void RecordQuery(string hash, string source, string label, int results);
    void ForgetQuery(string hash);
    DateTimeOffset? ObjectCheckedAt(string source, string objectId);
    void MarkObjectChecked(string source, string objectId);
}

internal static class Cursors
{
    public static string? Encode<T>(T value) => JsonStable.Serialize(JsonSerializer.SerializeToNode(value));

    public static T? Decode<T>(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

// MARK: - RSS

public sealed class FeedCursor
{
    [System.Text.Json.Serialization.JsonPropertyName("etag")]
    public string? Etag { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lastModified")]
    public string? LastModified { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("seen")]
    public List<string> Seen { get; set; } = [];
}

public static class RssSource
{
    internal const int MaxSeen = 500;

    public static async Task<FetchResult> FetchAsync(FeedSourceSettings cfg, string? cursor, SourceHttp http, CancellationToken ct)
    {
        var res = new FetchResult { Items = Math.Max(1, cfg.Feeds.Count) };
        var cursors = Cursors.Decode<Dictionary<string, FeedCursor>>(cursor) ?? [];
        foreach (var feed in cfg.Feeds)
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(feed, UriKind.Absolute, out var url))
            {
                res.PartialErrors.Add($"{feed}: некорректный адрес");
                continue;
            }

            var fc = cursors.GetValueOrDefault(feed) ?? new FeedCursor();
            var headers = new Dictionary<string, string>();
            if (fc.Etag is { } etag)
            {
                headers["If-None-Match"] = etag;
            }

            if (fc.LastModified is { } lm)
            {
                headers["If-Modified-Since"] = lm;
            }

            HttpResult resp;
            try
            {
                resp = await http.GetAsync(url, ct, headers).ConfigureAwait(false);
            }
            catch (HttpFailure e)
            {
                res.PartialErrors.Add($"{feed}: {e.Message}");
                continue;
            }

            if (resp.Status == 304)
            {
                continue;
            }

            var entries = FeedParser.Parse(resp.Data);
            if (entries is null)
            {
                res.PartialErrors.Add($"{feed}: не удалось разобрать ленту");
                continue;
            }

            var seen = new HashSet<string>(fc.Seen);
            var newIds = new List<string>();
            foreach (var e in entries)
            {
                if (e.Id.Length == 0 || seen.Contains(e.Id))
                {
                    continue;
                }

                newIds.Add(e.Id);
                var title = SearchText.Prefix(SearchText.HtmlToText(e.Title), 300);
                var desc = SearchText.HtmlToText(e.Summary);
                if (!SearchText.Prefilter($"{title} {desc}", cfg.Keywords))
                {
                    continue;
                }

                // GUID-ссылка уникальна сама по себе (так было и в Python-версии); короткий GUID вроде «1»
                // может совпасть у разных лент, поэтому к нему добавляется адрес ленты.
                var low = e.Id.ToLowerInvariant();
                var externalId = low.StartsWith("http://", StringComparison.Ordinal) || low.StartsWith("https://", StringComparison.Ordinal)
                    ? e.Id : $"{feed}#{e.Id}";
                res.Candidates.Add(new Candidate {
                    Data = new LeadData {
                        Kind = LeadKind.Order,
                        Source = SourceKey.Rss,
                        ExternalId = externalId,
                        Title = title,
                        Url = e.Link,
                        Description = SearchText.Prefix(desc, 4000),
                        PublishedAt = e.Published,
                        Contacts = SearchText.Contacts(desc),
                        Category = e.Category,
                    },
                });
            }

            // Ни одной уже виденной записи: лента короче пропущенного периода.
            if (seen.Count > 0 && entries.Count > 0 && !entries.Any(e => seen.Contains(e.Id)))
            {
                res.Warnings.Add($"{feed}: лента короче пропущенного периода, часть заявок могла не попасть");
            }

            fc.Seen = newIds.Concat(fc.Seen.Where(x => !newIds.Contains(x))).Take(MaxSeen).ToList();
            fc.Etag = resp.Headers.GetValueOrDefault("etag");
            fc.LastModified = resp.Headers.GetValueOrDefault("last-modified");
            cursors[feed] = fc;
        }

        res.Cursor = Cursors.Encode(cursors);
        return res;
    }
}

// MARK: - Telegram

public sealed record TelegramPost(string Channel, int Id, string Text, DateTimeOffset? Published)
{
    public string Url => $"https://t.me/{Channel}/{Id}";
}

public static partial class TelegramSource
{
    [GeneratedRegex("data-post=\"([^\"/]+)/(\\d+)\"")]
    private static partial Regex PostRx();

    // Сначала основной текст (js-message_text): у ответов выше идёт цитата с похожим классом.
    [GeneratedRegex("<div class=\"tgme_widget_message_text js-message_text[^\"]*\"[^>]*>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex MainTextRx();

    [GeneratedRegex("<div class=\"tgme_widget_message_text[^\"]*\"[^>]*>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex TextRx();

    [GeneratedRegex("<time[^>]*datetime=\"([^\"]+)\"")]
    private static partial Regex TimeRx();

    /// <summary>Разбор страницы https://t.me/s/&lt;канал&gt;.</summary>
    public static List<TelegramPost> Parse(string html)
    {
        var marks = PostRx().Matches(html);
        var posts = new List<TelegramPost>();
        for (var i = 0; i < marks.Count; i++)
        {
            var m = marks[i];
            var end = i + 1 < marks.Count ? marks[i + 1].Index : html.Length;
            var chunk = html[m.Index..end];
            var channel = m.Groups[1].Value;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var tm = MainTextRx().Match(chunk);
            if (!tm.Success)
            {
                tm = TextRx().Match(chunk);
            }

            var text = tm.Success ? SearchText.HtmlToText(tm.Groups[1].Value, lines: true) : "";
            var time = TimeRx().Match(chunk);
            var published = time.Success ? ISODate.Parse(time.Groups[1].Value) : null;
            posts.Add(new TelegramPost(channel, id, text, published));
        }

        return posts;
    }

    public static async Task<FetchResult> FetchAsync(TelegramSourceSettings cfg, string? cursor, SourceHttp http, CancellationToken ct)
    {
        var res = new FetchResult { Items = Math.Max(1, cfg.Channels.Count) };
        var cursors = Cursors.Decode<Dictionary<string, int>>(cursor) ?? [];
        foreach (var channel in cfg.Channels)
        {
            ct.ThrowIfCancellationRequested();
            var lastId = cursors.GetValueOrDefault(channel);
            var newest = lastId;
            int? before = null;
            var reached = lastId == 0; // первый запуск: только первая страница
            for (var page = 0; page < Math.Max(1, cfg.MaxPages); page++)
            {
                var s = $"https://t.me/s/{channel}" + (before is { } b ? $"?before={b}" : "");
                if (!Uri.TryCreate(s, UriKind.Absolute, out var url))
                {
                    break;
                }

                HttpResult resp;
                try
                {
                    resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                }
                catch (HttpFailure e)
                {
                    res.PartialErrors.Add($"{channel}: {e.Message}");
                    break;
                }

                var posts = Parse(resp.Text);
                if (posts.Count == 0)
                {
                    if (page == 0)
                    {
                        res.PartialErrors.Add($"{channel}: нет публичного превью или канал не найден");
                    }

                    break;
                }

                foreach (var p in posts)
                {
                    if (p.Id <= lastId)
                    {
                        reached = true;
                        continue;
                    }

                    newest = Math.Max(newest, p.Id);
                    if (p.Text.Length == 0 || !SearchText.Prefilter(p.Text, cfg.Keywords))
                    {
                        continue;
                    }

                    var firstLine = SearchText.Prefix(p.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? p.Text, 200);
                    res.Candidates.Add(new Candidate {
                        Data = new LeadData {
                            Kind = LeadKind.Order,
                            Source = SourceKey.Telegram,
                            ExternalId = $"{channel}/{p.Id}",
                            Title = firstLine,
                            Url = p.Url,
                            Description = SearchText.Prefix(p.Text, 4000),
                            PublishedAt = p.Published,
                            Contacts = SearchText.Contacts(p.Text),
                        },
                    });
                }

                var minId = posts.Min(p => p.Id);
                if (reached || minId <= lastId + 1)
                {
                    reached = true;
                    break;
                }

                before = minId;
            }

            if (!reached)
            {
                res.Warnings.Add($"{channel}: за {cfg.MaxPages} стр. не дошли до последнего обработанного поста — возможен пропуск");
            }

            cursors[channel] = newest;
        }

        res.Cursor = Cursors.Encode(cursors);
        return res;
    }
}

// MARK: - ВКонтакте

public static class VkSource
{
    private const string Api = "https://api.vk.com/method/wall.get";
    private const string Version = "5.199";

    public static async Task<FetchResult> FetchAsync(VKSourceSettings cfg, string token, string? cursor, SourceHttp http, CancellationToken ct)
    {
        var res = new FetchResult { Items = Math.Max(1, cfg.Communities.Count) };
        var cursors = Cursors.Decode<Dictionary<string, long>>(cursor) ?? [];
        foreach (var domain in cfg.Communities)
        {
            ct.ThrowIfCancellationRequested();
            var lastId = cursors.GetValueOrDefault(domain);
            var newest = lastId;
            var reached = lastId == 0;
            for (var page = 0; page < Math.Max(1, cfg.MaxPages); page++)
            {
                var url = new Uri($"{Api}?domain={Uri.EscapeDataString(domain)}&count={cfg.PageSize}&offset={page * cfg.PageSize}" +
                                  $"&access_token={Uri.EscapeDataString(token)}&v={Version}");
                JsonObject json;
                try
                {
                    var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                    json = JsonNode.Parse(resp.Data) as JsonObject ?? throw new HttpFailure("неожиданный ответ VK");
                }
                catch (HttpFailure e)
                {
                    res.PartialErrors.Add($"{domain}: {e.Message}");
                    break;
                }
                catch (JsonException)
                {
                    res.PartialErrors.Add($"{domain}: ответ VK не разобран");
                    break;
                }

                if (json["error"] is JsonObject err)
                {
                    res.PartialErrors.Add($"{domain}: VK API {err["error_code"]?.ToString() ?? "?"}: {err["error_msg"]?.ToString() ?? ""}");
                    break;
                }

                var response = json["response"] as JsonObject ?? [];
                var items = response["items"] as JsonArray ?? [];
                if (items.Count == 0)
                {
                    break;
                }

                foreach (var it in items.OfType<JsonObject>())
                {
                    var pid = LongOf(it["id"]);
                    if (pid is null)
                    {
                        continue;
                    }

                    if (pid <= lastId)
                    {
                        if (!(it["is_pinned"] is JsonValue pv && pv.TryGetValue<long>(out var pinned) && pinned != 0)
                            && !(it["is_pinned"] is JsonValue pb && pb.TryGetValue<bool>(out var pinnedB) && pinnedB))
                        {
                            reached = true;
                        }

                        continue;
                    }

                    newest = Math.Max(newest, pid.Value);
                    var text = it["text"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "";
                    if (text.Length == 0 || !SearchText.Prefilter(text, cfg.Keywords))
                    {
                        continue;
                    }

                    var owner = LongOf(it["owner_id"]) ?? 0;
                    DateTimeOffset? date = LongOf(it["date"]) is { } secs ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
                    res.Candidates.Add(new Candidate {
                        Data = new LeadData {
                            Kind = LeadKind.Order,
                            Source = SourceKey.Vk,
                            ExternalId = $"{owner}_{pid}",
                            Title = SearchText.Prefix(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text, 200),
                            Url = $"https://vk.com/wall{owner}_{pid}",
                            Description = SearchText.Prefix(text, 4000),
                            PublishedAt = date,
                            Contacts = SearchText.Contacts(text),
                        },
                    });
                }

                var count = LongOf(response["count"]) ?? 0;
                if (reached || (long)cfg.PageSize * (page + 1) >= count)
                {
                    reached = true;
                    break;
                }
            }

            if (!reached)
            {
                res.Warnings.Add($"{domain}: возможен пропуск постов (увеличьте число страниц)");
            }

            cursors[domain] = newest;
        }

        res.Cursor = Cursors.Encode(cursors);
        return res;
    }

    internal static long? LongOf(JsonNode? n)
    {
        if (n is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }

        return v.TryGetValue<double>(out var d) ? (long)d : null;
    }
}

// MARK: - OpenStreetMap

public static class OsmSource
{
    private static readonly HashSet<string> NeedsBooking = ["beauty", "dance", "fitness", "medical", "education", "restaurant"];

    /// <summary>Запрос Overpass: город ищется по name, name:ru и name:en (Ереван в OSM — «Երևան»).</summary>
    public static string BuildQuery(string region, IEnumerable<string> filters, int timeout = 90)
    {
        var name = region.Replace("\"", "");
        var body = string.Concat(filters.Select(f => $"nwr{f}(area.a);"));
        var alt = string.Concat(new[] { "name", "name:ru", "name:en" }.Select((k, i) =>
            $"area[\"{k}\"=\"{name}\"][\"boundary\"=\"administrative\"]->.a{i};"));
        return $"[out:json][timeout:{timeout}];{alt}(.a0;.a1;.a2;)->.a;({body});out center tags;";
    }

    public static async Task<FetchResult> FetchAsync(OSMSourceSettings cfg, ISearchMemory memory, SourceHttp http, SourceHttp? siteHttp,
        DateTimeOffset now, CancellationToken ct)
    {
        var res = new FetchResult();
        var cats = cfg.Categories.Where(c => c.Enabled && c.Filters.Count > 0).ToList();
        res.Items = Math.Max(1, cfg.Regions.Count * cats.Count);
        if (!Uri.TryCreate(cfg.Endpoint, UriKind.Absolute, out var endpoint))
        {
            res.PartialErrors.Add("некорректный адрес Overpass API");
            return res;
        }

        var added = 0;
        var siteCache = new Dictionary<string, SiteCheck>();
        foreach (var region in cfg.Regions)
        {
            foreach (var cat in cats)
            {
                // Остановка: возвращаем уже найденное, чтобы движок успел его сохранить.
                if (ct.IsCancellationRequested)
                {
                    return res;
                }

                var query = BuildQuery(region, cat.Filters);
                var qhash = SearchText.ShortHash(query);
                if (memory.QueryLastRun(qhash) is { } last && (now - last).TotalSeconds < cfg.RefreshDays * 86400)
                {
                    continue; // этот запрос недавно выполнялся — не тратим квоту
                }

                JsonArray elements;
                try
                {
                    var resp = await http.PostFormAsync(endpoint, new Dictionary<string, string> { ["data"] = query }, ct).ConfigureAwait(false);
                    var obj = JsonNode.Parse(resp.Data) as JsonObject ?? throw new HttpFailure("неожиданный ответ Overpass");
                    elements = obj["elements"] as JsonArray ?? [];
                }
                catch (BudgetExceededException e)
                {
                    res.Warnings.Add(e.Message);
                    return res;
                }
                catch (HttpFailure e)
                {
                    res.PartialErrors.Add($"{region}/{cat.Label}: {e.Message}");
                    continue;
                }
                catch (JsonException)
                {
                    res.PartialErrors.Add($"{region}/{cat.Label}: ответ не разобран");
                    continue;
                }

                memory.RecordQuery(qhash, SourceKey.Osm, $"{region}|{cat.Key}", elements.Count);
                foreach (var el in elements.OfType<JsonObject>())
                {
                    if (added >= cfg.MaxNewPerRun)
                    {
                        res.Warnings.Add($"достигнут лимит {cfg.MaxNewPerRun} новых организаций за запуск, остальные — в следующий раз");

                        // Запрос обработан частично: забываем его, чтобы дочитать в следующий раз.
                        memory.ForgetQuery(qhash);
                        return res;
                    }

                    try
                    {
                        var c = await CandidateAsync(el, cat, region, cfg, memory, siteHttp, siteCache, now, ct).ConfigureAwait(false);
                        if (c is not null)
                        {
                            res.Candidates.Add(c);
                            added++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Запрос обработан не до конца: забываем его, а найденное возвращаем на сохранение.
                        memory.ForgetQuery(qhash);
                        return res;
                    }
                }
            }
        }

        return res;
    }

    internal static async Task<Candidate?> CandidateAsync(JsonObject el, OSMCategory cat, string region, OSMSourceSettings cfg,
        ISearchMemory memory, SourceHttp? siteHttp, Dictionary<string, SiteCheck> cache, DateTimeOffset now, CancellationToken ct)
    {
        var tags = new Dictionary<string, string>();
        if (el["tags"] is JsonObject to)
        {
            foreach (var (k, v) in to)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var sv))
                {
                    tags[k] = sv;
                }
            }
        }

        if (!tags.TryGetValue("name", out var name) || name.Length == 0)
        {
            return null;
        }

        var type = el["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "node";
        var oid = $"{type}/{VkSource.LongOf(el["id"]) ?? 0}";
        if (memory.ObjectCheckedAt(SourceKey.Osm, oid) is { } checkedAt && (now - checkedAt).TotalSeconds < cfg.RecheckDays * 86400)
        {
            return null; // уже обработан, перепроверять рано
        }

        string? Tag(params string[] keys) => keys.Select(k => tags.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);
        var website = Tag("website", "contact:website", "url");
        var phone = Tag("phone", "contact:phone");
        var contacts = new Dictionary<string, string>();
        foreach (var (k, v) in new (string, string?)[]
                 {
                     ("phone", phone), ("email", Tag("email", "contact:email")),
                     ("vk", tags.GetValueOrDefault("contact:vk")), ("telegram", tags.GetValueOrDefault("contact:telegram")),
                 })
        {
            if (!string.IsNullOrEmpty(v))
            {
                contacts[k] = v;
            }
        }

        var signals = new Dictionary<string, bool>();
        var facts = new List<string> { "Данные © участники OpenStreetMap (ODbL)" };
        var assumptions = new List<string>();
        if (string.IsNullOrEmpty(website))
        {
            signals["no_website"] = true;
            facts.Add("В OpenStreetMap у организации не указан сайт (сам сайт при этом может существовать)");
            if (NeedsBooking.Contains(cat.Group))
            {
                assumptions.Add("Возможно, пригодится сайт с онлайн-записью");
            }
        }
        else if (cfg.CheckWebsites && siteHttp is not null)
        {
            var key = Normalize.Domain(website);
            if (!cache.TryGetValue(key, out var chk))
            {
                try
                {
                    chk = await SiteChecker.CheckAsync(website, siteHttp, now, ct).ConfigureAwait(false);
                    cache[key] = chk;
                }
                catch (BudgetExceededException)
                {
                    chk = null!;
                    assumptions.Add("Сайт не проверен: исчерпан лимит проверок на этот запуск");
                }
            }

            if (chk is not null)
            {
                facts.AddRange(chk.Facts);
                signals["site_unreachable"] = !chk.Reachable;
                if (chk.Reachable)
                {
                    signals["no_https"] = !chk.Https;
                    signals["no_viewport"] = !chk.Viewport;
                    if (chk.CopyrightYear is { } y && y <= now.Year - 4)
                    {
                        signals["outdated"] = true;
                    }

                    if (!chk.HasBooking && NeedsBooking.Contains(cat.Group))
                    {
                        signals["no_booking"] = true;
                        assumptions.Add("Возможно, нужна онлайн-запись (на сайте её признаков не найдено)");
                    }
                }
            }
        }

        memory.MarkObjectChecked(SourceKey.Osm, oid);
        var address = string.Join(", ", new[] { tags.GetValueOrDefault("addr:city"), tags.GetValueOrDefault("addr:street"), tags.GetValueOrDefault("addr:housenumber") }
            .Where(x => !string.IsNullOrEmpty(x)));
        var data = new LeadData {
            Kind = LeadKind.Organization,
            Source = SourceKey.Osm,
            ExternalId = oid,
            Title = name,
            Url = $"https://www.openstreetmap.org/{oid}",
            Description = $"{cat.Label} · {region}",
            Region = region,
            Contacts = contacts,
            Category = cat.Label,
            CustomerName = name,
            Address = address.Length == 0 ? null : address,
            Phone = phone,
            Website = website,
            Facts = facts,
            Assumptions = assumptions,
        };
        return new Candidate { Data = data, Signals = signals, CategoryWeight = cat.Weight };
    }
}

// MARK: - Проверка сайта

public sealed class SiteCheck
{
    public string Url { get; set; } = "";
    public bool Reachable { get; set; }
    public int? Status { get; set; }
    public bool Https { get; set; }
    public bool Viewport { get; set; }
    public int? CopyrightYear { get; set; }
    public bool HasBooking { get; set; }
    public List<string> Facts { get; set; } = [];
}

/// <summary>Один запрос к главной странице. Возвращает только проверенные факты.</summary>
public static partial class SiteChecker
{
    private static readonly string[] BookingMarkers =
    [
        "онлайн-запис", "онлайн запис", "записаться", "запись онлайн", "yclients", "dikidi", "sonline", "booking",
        "забронировать", "бронирование", "arnica", "altegio",
    ];

    [GeneratedRegex(@"(?:©|&copy;|copyright)\s*(?:\d{4}\s*[-–—]\s*)?(20\d{2}|19\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex YearRx();

    [GeneratedRegex(@"<meta\b[^>]*\bname\s*=\s*[""']?viewport\b", RegexOptions.IgnoreCase)]
    private static partial Regex ViewportRx();

    [GeneratedRegex(@"^(127|10|192\.168|169\.254|0)\.")]
    private static partial Regex PrivateRx();

    [GeneratedRegex(@"^172\.(1[6-9]|2\d|3[01])\.")]
    private static partial Regex Private172Rx();

    public static string NormalizeUrl(string raw)
    {
        var s = raw.Trim();
        var low = s.ToLowerInvariant();
        return low.StartsWith("http://", StringComparison.Ordinal) || low.StartsWith("https://", StringComparison.Ordinal) ? s : "http://" + s;
    }

    /// <summary>Только http(s) и не локальные адреса.</summary>
    public static bool IsPublicHttpUrl(string s)
    {
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = u.IdnHost.ToLowerInvariant();
        if (host.Length == 0 || !host.Contains('.'))
        {
            return false;
        }

        if (host == "localhost" || host.EndsWith(".local", StringComparison.Ordinal) || PrivateRx().IsMatch(host) || Private172Rx().IsMatch(host))
        {
            return false;
        }

        return true;
    }

    public static async Task<SiteCheck> CheckAsync(string raw, SourceHttp http, DateTimeOffset now, CancellationToken ct)
    {
        var urlString = NormalizeUrl(raw);
        var day = now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        if (!IsPublicHttpUrl(urlString))
        {
            return new SiteCheck { Url = urlString, Reachable = false, Facts = ["В источнике указан некорректный адрес сайта"] };
        }

        HttpResult resp;
        try
        {
            resp = await http.GetAsync(new Uri(urlString), ct).ConfigureAwait(false);
        }
        catch (HttpFailure e)
        {
            return new SiteCheck {
                Url = urlString,
                Reachable = false,
                Status = e.Status,
                Facts = [$"Сайт не открылся при проверке {day}: {e.Message}"],
            };
        }

        var final = resp.Url?.AbsoluteUri ?? urlString;
        var html = resp.Text;
        if (html.Length > 500_000)
        {
            html = html[..500_000];
        }

        var chk = new SiteCheck {
            Url = final,
            Reachable = true,
            Status = resp.Status,
            Https = final.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            Viewport = ViewportRx().IsMatch(html),
        };
        var years = YearRx().Matches(html).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        chk.CopyrightYear = years.Count > 0 ? years.Max() : null;
        var low = html.ToLowerInvariant();
        chk.HasBooking = BookingMarkers.Any(m => low.Contains(m, StringComparison.Ordinal));
        chk.Facts.Add($"Сайт открылся ({resp.Status}) при проверке {day}");
        if (!chk.Https)
        {
            chk.Facts.Add("Сайт работает без HTTPS");
        }

        if (!chk.Viewport)
        {
            chk.Facts.Add("На главной нет meta viewport — мобильная вёрстка вероятно не адаптивна");
        }

        if (chk.CopyrightYear is { } y)
        {
            chk.Facts.Add($"Последний год в копирайте: {y}");
        }

        if (!chk.HasBooking)
        {
            chk.Facts.Add("На главной не найдено признаков онлайн-записи/бронирования");
        }

        return chk;
    }
}
