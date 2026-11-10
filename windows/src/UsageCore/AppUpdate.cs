namespace AIUsageBar.Core;

/// <summary>A version like 0.7.0, compared number by number (0.10.0 is newer than 0.9.0).</summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private readonly int[] parts;

    private AppVersion(int[] parts) => this.parts = parts;

    /// <summary>Reads "0.7.0", "v0.7.0" or "0.7"; a suffix such as "-beta" or "+build" is ignored.</summary>
    public static AppVersion? Parse(string? text)
    {
        var core = (text ?? "").Trim();
        if (core.StartsWith('v') || core.StartsWith('V')) core = core[1..];
        var end = core.IndexOfAny(['-', '+']);
        if (end >= 0) core = core[..end];
        var pieces = core.Split('.');
        if (pieces.Length is < 1 or > 4) return null;
        var numbers = new int[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (pieces[i].Length == 0 || !pieces[i].All(char.IsAsciiDigit) || !int.TryParse(pieces[i], out numbers[i])) return null;
        }
        return new AppVersion(numbers);
    }

    /// <summary>The three numbers of an assembly version (0.7.0.0 → 0.7.0).</summary>
    public static AppVersion From(Version version) => new([version.Major, version.Minor, Math.Max(version.Build, 0)]);

    private int At(int index) => index < parts.Length ? parts[index] : 0;

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < Math.Max(parts.Length, other.parts.Length); i++)
        {
            var difference = At(i).CompareTo(other.At(i));
            if (difference != 0) return difference;
        }
        return 0;
    }

    public bool Equals(AppVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(At(0), At(1), At(2), At(3));

    public override string ToString() => string.Join('.', parts);

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
}

public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>A published release of this app on GitHub.</summary>
public sealed record AppRelease(AppVersion Version, Uri Page, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(asset => asset.Name == name);
}

/// <summary>
/// Looks for a newer version among the repository's GitHub releases. Only the
/// public releases API is called: nothing about the user or their usage is sent.
/// </summary>
public static class AppUpdates
{
    public const string Repository = "matheusvelloso/ai-usage-bar";
    public static readonly Uri LatestEndpoint = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases/latest");

    /// <summary>Same fixed names the release workflow uploads.</summary>
    public const string SetupAsset = "AIUsageBar-Windows-Setup.exe";
    public const string PortableAsset = "AIUsageBar-Windows-Portable.exe";

    /// <summary>How often the running app looks for a new release.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Reads the response of <c>releases/latest</c>. Drafts, pre-releases and download
    /// links outside this repository's releases are not offered.
    /// </summary>
    public static AppRelease Parse(byte[] data)
    {
        var root = Json.Parse(data);
        if (AppVersion.Parse(Json.Str(root, "tag_name")) is not { } version
            || Json.Bool(root, "draft") == true
            || Json.Bool(root, "prerelease") == true)
        {
            throw new UsageException(UsageErrorKind.Decoding);
        }
        var page = Uri.TryCreate(Json.Str(root, "html_url"), UriKind.Absolute, out var url) && IsInRepository(url, "/releases/")
            ? url
            : ReleasesPage;
        var assets = new List<ReleaseAsset>();
        foreach (var item in Json.Arr(root, "assets") ?? new System.Text.Json.Nodes.JsonArray())
        {
            if (Json.Str(item, "name") is { } name
                && Uri.TryCreate(Json.Str(item, "browser_download_url"), UriKind.Absolute, out var download)
                && IsInRepository(download, "/releases/download/"))
            {
                assets.Add(new ReleaseAsset(name, download, (long)(Json.Num(item, "size") ?? 0)));
            }
        }
        return new AppRelease(version, page, assets);
    }

    /// <summary>The release to offer: newer than the running version and carrying the file this copy installs.</summary>
    public static AppRelease? Update(AppRelease release, AppVersion current, string asset) =>
        release.Version > current && release.Asset(asset) is not null ? release : null;

    public static async Task<AppRelease> FetchLatestAsync(CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestEndpoint);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return Parse(await Http.SendAsync(request, cancellation).ConfigureAwait(false));
    }

    /// <summary>Downloads a release file, reporting progress from 0 to 1, and checks its size.</summary>
    public static async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken cancellation = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ai-usage-bar");
        using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        if (asset.Size > 0 && new FileInfo(path).Length != asset.Size)
        {
            File.Delete(path);
            throw new InvalidDataException($"{asset.Name} has the wrong size");
        }
    }

    private static bool IsInRepository(Uri url, string section) =>
        url.Scheme == Uri.UriSchemeHttps
        && url.Host == "github.com"
        && url.AbsolutePath.StartsWith($"/{Repository}{section}", StringComparison.Ordinal);
}
