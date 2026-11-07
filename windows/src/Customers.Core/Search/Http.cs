using System.Net;
using System.Text;

namespace Customers.Core;

public sealed record HttpResult(int Status, byte[] Data, IReadOnlyDictionary<string, string> Headers, Uri? Url)
{
    /// <summary>Текст ответа: UTF-8, иначе Windows-1251 (старые русские сайты).</summary>
    public string Text
    {
        get
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(Data);
            }
            catch (ArgumentException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1251).GetString(Data);
            }
        }
    }
}

public sealed class HttpRequestSpec
{
    public required Uri Url { get; init; }
    public string Method { get; init; } = "GET";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>Транспорт. В приложении — HttpClient, в тестах — подмена.</summary>
public interface IHttpTransport
{
    Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken ct);
}

public sealed class HttpFailure(string message, int? status = null, bool transient = false) : Exception(message)
{
    public int? Status { get; } = status;
    public bool Transient { get; } = transient;
}

/// <summary>Исчерпан лимит запросов источника на этот запуск.</summary>
public sealed class BudgetExceededException(string message) : Exception(message);

/// <summary>
/// HttpClient-транспорт. Перенаправления проверяются вручную: они не могут увести запрос
/// на локальные адреса (127.0.0.1, 192.168.…, localhost).
/// </summary>
public sealed class HttpClientTransport : IHttpTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpClientTransport()
    {
        var handler = new SocketsHttpHandler {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _client.Dispose();

    public async Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken ct)
    {
        var url = request.Url;
        for (var hop = 0; hop < 8; hop++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(request.Timeout);
            using var msg = new HttpRequestMessage(new HttpMethod(request.Method), url);
            foreach (var (k, v) in request.Headers)
            {
                msg.Headers.TryAddWithoutValidation(k, v);
            }

            if (request.Body is not null)
            {
                msg.Content = new ByteArrayContent(request.Body);
                if (request.ContentType is not null)
                {
                    msg.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
                }
            }

            HttpResponseMessage resp;
            try
            {
                resp = await _client.SendAsync(msg, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpFailure("таймаут запроса", null, transient: true);
            }
            catch (HttpRequestException e)
            {
                throw new HttpFailure(e.Message, null, transient: true);
            }

            using (resp)
            {
                var code = (int)resp.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                    if (!SiteChecker.IsPublicHttpUrl(next.AbsoluteUri))
                    {
                        throw new HttpFailure("перенаправление на нелокальный адрес запрещено", code);
                    }

                    url = next;
                    continue;
                }

                var data = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in resp.Headers)
                {
                    headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
                }

                foreach (var h in resp.Content.Headers)
                {
                    headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
                }

                return new HttpResult(code, data, headers, url);
            }
        }

        throw new HttpFailure("слишком много перенаправлений");
    }
}

/// <summary>HTTP для одного источника: пауза между запросами, лимит на запуск, повторы с растущей задержкой.</summary>
public sealed class SourceHttp
{
    public const string UserAgent = "Customers/1.0 (Windows; personal non-commercial research)";
    private static readonly HashSet<int> RetryStatuses = [429, 500, 502, 503, 504];

    private readonly string _name;
    private readonly IHttpTransport _transport;
    private readonly double _minInterval;
    private readonly int _maxRequests;
    private readonly int _maxAttempts;
    private readonly double _baseDelay;
    private readonly TimeSpan _timeout;
    private readonly Func<double, CancellationToken, Task> _sleep;
    private DateTimeOffset? _last;

    public SourceHttp(string name, IHttpTransport transport, double minInterval = 1, int maxRequests = 50,
        int maxAttempts = 4, double baseDelay = 2, double timeout = 20, Func<double, CancellationToken, Task>? sleep = null)
    {
        _name = name;
        _transport = transport;
        _minInterval = minInterval;
        _maxRequests = maxRequests;
        _maxAttempts = maxAttempts;
        _baseDelay = baseDelay;
        _timeout = TimeSpan.FromSeconds(timeout);
        _sleep = sleep ?? RealSleep;
    }

    public int RequestsMade { get; private set; }

    public static async Task RealSleep(double seconds, CancellationToken ct)
    {
        if (seconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
    }

    public Task<HttpResult> GetAsync(Uri url, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
    {
        var req = new HttpRequestSpec { Url = url };
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                req.Headers[k] = v;
            }
        }

        return SendAsync(req, ct);
    }

    public Task<HttpResult> PostFormAsync(Uri url, IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        var body = string.Join("&", fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var req = new HttpRequestSpec {
            Url = url,
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(body),
            ContentType = "application/x-www-form-urlencoded; charset=utf-8",
        };
        return SendAsync(req, ct);
    }

    internal async Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken ct)
    {
        request.Timeout = _timeout;
        request.Headers["User-Agent"] = UserAgent;
        var shown = Safe(request.Url);
        Exception lastError = new HttpFailure($"{_name}: запрос не выполнен", null, true);
        for (var attempt = 1; attempt <= Math.Max(1, _maxAttempts); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (RequestsMade >= _maxRequests)
            {
                throw new BudgetExceededException($"{_name}: лимит {_maxRequests} запросов за запуск");
            }

            if (_last is { } last)
            {
                var wait = _minInterval - (DateTimeOffset.UtcNow - last).TotalSeconds;
                if (wait > 0)
                {
                    await _sleep(wait, ct).ConfigureAwait(false);
                }
            }

            _last = DateTimeOffset.UtcNow;
            RequestsMade++;
            try
            {
                var resp = await _transport.SendAsync(request, ct).ConfigureAwait(false);
                if (resp.Status < 400)
                {
                    return resp;
                }

                var err = new HttpFailure($"HTTP {resp.Status} для {shown}", resp.Status, RetryStatuses.Contains(resp.Status));
                if (!err.Transient)
                {
                    throw err;
                }

                lastError = err;
                if (resp.Headers.TryGetValue("retry-after", out var ra) && RetryAfter(ra) is { } secs && attempt < _maxAttempts)
                {
                    await _sleep(Math.Min(secs, 120), ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (HttpFailure e)
            {
                if (!e.Transient)
                {
                    throw;
                }

                lastError = e;
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (attempt < _maxAttempts)
            {
                await _sleep((_baseDelay * Math.Pow(2, attempt - 1)) + (Random.Shared.NextDouble() * 0.5), ct).ConfigureAwait(false);
            }
        }

        throw lastError;
    }

    /// <summary>Retry-After: число секунд или HTTP-дата.</summary>
    internal static double? RetryAfter(string value, DateTimeOffset? now = null)
    {
        var v = value.Trim();
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs))
        {
            return Math.Max(0, secs);
        }

        if (DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
        {
            return Math.Max(0, (date - (now ?? DateTimeOffset.UtcNow)).TotalSeconds);
        }

        return null;
    }

    /// <summary>Адрес без параметров (там бывают ключи доступа).</summary>
    internal static string Safe(Uri? url) => url is null ? "?" : url.AbsoluteUri.Split('?')[0];
}
