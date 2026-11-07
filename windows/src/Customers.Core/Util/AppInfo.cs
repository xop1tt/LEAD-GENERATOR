using System.Globalization;
using System.Text.Json.Nodes;

namespace Customers.Core;

/// <summary>Расположение данных приложения. Данные лежат в профиле пользователя и переживают обновление и переустановку.</summary>
public static class AppPaths
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Customers");

    public static string Logs(string root) => Path.Combine(root, "logs");

    public static string Secrets(string root) => Path.Combine(root, "secrets");

    public static string DefaultBackups(string username) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Customers Backups", username);
}

/// <summary>Номер версии вида 1.0.4 (тег v1.0.4 тоже понимается).</summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private AppVersion(int[] parts) => Parts = parts;

    public IReadOnlyList<int> Parts { get; }

    public static AppVersion? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        // «1.0.4-beta» → 1.0.4: суффиксы предварительных версий не учитываются.
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
        {
            s = s[..cut];
        }

        var nums = s.Split('.');
        if (nums.Length is 0 or > 4)
        {
            return null;
        }

        var parts = new int[nums.Length];
        for (var i = 0; i < nums.Length; i++)
        {
            if (!int.TryParse(nums[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return null;
            }
        }

        return new AppVersion(parts);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var n = Math.Max(Parts.Count, other.Parts.Count);
        for (var i = 0; i < n; i++)
        {
            var x = i < Parts.Count ? Parts[i] : 0;
            var y = i < other.Parts.Count ? other.Parts[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as AppVersion);

    public override int GetHashCode() => string.Join('.', Parts.Reverse().SkipWhile(p => p == 0).Reverse()).GetHashCode(StringComparison.Ordinal);

    public override string ToString() => string.Join('.', Parts);

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
}

/// <summary>Релиз из публичного API GitHub (без токена).</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long? Size, string? Digest);

public sealed record ReleaseInfo(string TagName, string? Name, string? Body, Uri? HtmlUrl, bool Draft, bool Prerelease, IReadOnlyList<ReleaseAsset> Assets)
{
    public static ReleaseInfo? Parse(byte[] json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["tag_name"]?.ToString() is not { } tag)
            {
                return null;
            }

            var assets = new List<ReleaseAsset>();
            foreach (var a in (o["assets"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (a["name"]?.ToString() is { } n && Uri.TryCreate(a["browser_download_url"]?.ToString(), UriKind.Absolute, out var u))
                {
                    assets.Add(new ReleaseAsset(n, u, a["size"] is JsonValue sv && sv.TryGetValue<long>(out var sz) ? sz : null, a["digest"]?.ToString()));
                }
            }

            Uri.TryCreate(o["html_url"]?.ToString(), UriKind.Absolute, out var html);
            return new ReleaseInfo(tag, o["name"]?.ToString(), o["body"]?.ToString(), html,
                o["draft"] is JsonValue d && d.TryGetValue<bool>(out var db) && db,
                o["prerelease"] is JsonValue p && p.TryGetValue<bool>(out var pb) && pb, assets);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Установщик для Windows: Customers-vX.Y.Z-Windows-x64-Setup.exe.</summary>
    public ReleaseAsset? WindowsInstaller => Assets.FirstOrDefault(a =>
        a.Name.StartsWith("Customers-", StringComparison.Ordinal) && a.Name.EndsWith("-Windows-x64-Setup.exe", StringComparison.OrdinalIgnoreCase));

    public ReleaseAsset? ChecksumFor(ReleaseAsset installer) => Assets.FirstOrDefault(a => a.Name == installer.Name + ".sha256");
}

public sealed record UpdateOffer(AppVersion Version, ReleaseInfo Release, ReleaseAsset Installer);

/// <summary>Проверка обновлений: последний релиз на GitHub, установщик Windows и проверка SHA-256 перед запуском.</summary>
public sealed class UpdateChecker(IHttpTransport transport, string repository = "xop1tt/CLIENTS")
{
    public async Task<UpdateOffer?> CheckAsync(AppVersion current, CancellationToken ct = default)
    {
        var req = new HttpRequestSpec { Url = new Uri($"https://api.github.com/repos/{repository}/releases/latest") };
        req.Headers["Accept"] = "application/vnd.github+json";
        req.Headers["User-Agent"] = SourceHttp.UserAgent;
        var resp = await transport.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.Status == 404)
        {
            return null;
        }

        if (resp.Status >= 400)
        {
            throw new HttpFailure($"GitHub ответил HTTP {resp.Status}", resp.Status);
        }

        var release = ReleaseInfo.Parse(resp.Data);
        if (release is null || release.Draft || release.Prerelease)
        {
            return null;
        }

        var version = AppVersion.Parse(release.TagName);
        if (version is null || version <= current || release.WindowsInstaller is not { } installer)
        {
            return null;
        }

        return new UpdateOffer(version, release, installer);
    }

    /// <summary>Скачивает установщик в папку и проверяет SHA-256 (из файла .sha256 или из digest GitHub).</summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var expected = await ExpectedHashAsync(offer, ct).ConfigureAwait(false)
            ?? throw new HttpFailure("у релиза нет контрольной суммы SHA-256 — обновление отменено");
        var req = new HttpRequestSpec { Url = offer.Installer.DownloadUrl, Timeout = TimeSpan.FromMinutes(10) };
        req.Headers["User-Agent"] = SourceHttp.UserAgent;
        var resp = await transport.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.Status >= 400)
        {
            throw new HttpFailure($"не удалось скачать установщик: HTTP {resp.Status}", resp.Status);
        }

        var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(resp.Data));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpFailure("контрольная сумма установщика не совпала — обновление отменено");
        }

        var path = Path.Combine(directory, offer.Installer.Name);
        await File.WriteAllBytesAsync(path, resp.Data, ct).ConfigureAwait(false);
        return path;
    }

    private async Task<string?> ExpectedHashAsync(UpdateOffer offer, CancellationToken ct)
    {
        if (offer.Installer.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return digest["sha256:".Length..].Trim();
        }

        if (offer.Release.ChecksumFor(offer.Installer) is { } sum)
        {
            var req = new HttpRequestSpec { Url = sum.DownloadUrl };
            req.Headers["User-Agent"] = SourceHttp.UserAgent;
            var resp = await transport.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.Status < 400)
            {
                var first = resp.Text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is { Length: 64 })
                {
                    return first;
                }
            }
        }

        return null;
    }
}
