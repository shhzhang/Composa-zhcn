using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Composa.App;

/// <summary>Where this build came from, which decides whether it should talk about updates at all.</summary>
public enum UpdateChannel
{
    /// <summary>Downloaded from the releases page: nothing else will tell the user a new version exists.</summary>
    GitHub,
    /// <summary>Installed from a repository by apt, dnf or similar. The package manager owns updates and this must stay quiet.</summary>
    Managed
}

/// <summary>One file attached to a release: what it is called, where it downloads from and how large it is.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size)
{
    /// <summary>
    /// Only the releases of this repository, over HTTPS, are ever downloaded. GitHub then redirects to
    /// its own storage, which HttpClient follows only while it stays on HTTPS.
    /// </summary>
    public static bool IsOwnAsset(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/dvdstelt/Composa/releases/download/", StringComparison.Ordinal);
}

/// <param name="Assets">The release's own files, the packages and <see cref="ChecksumsName"/>, and nothing from anywhere else.</param>
public sealed record ReleaseInfo(string Tag, string Url, bool PreRelease, IReadOnlyList<ReleaseAsset>? Assets = null)
{
    /// <summary>What the release workflow calls the file that lists every package's SHA-256.</summary>
    public const string ChecksumsName = "sha256sums.txt";

    public IReadOnlyList<ReleaseAsset> Assets { get; } = Assets ?? [];

    /// <summary>The release's list of checksums, without which nothing from it is offered.</summary>
    public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => a.Name == ChecksumsName);
}

/// <summary>The network half, kept behind an interface so the rules below can be tested without it.</summary>
public interface IReleaseSource
{
    Task<ReleaseInfo?> Latest(CancellationToken cancel);
}

/// <param name="handler">Stands in for the network in tests; null uses a real connection.</param>
public sealed class GitHubReleaseSource(HttpMessageHandler? handler = null) : IReleaseSource
{
    /// <summary>
    /// Where the releases are asked for. Left on the loopback address for the Chinese trial edition:
    /// nothing listens there, so a check that runs anyway fails the way an offline one fails and can
    /// never offer the original English build. Restoring updates means pointing this, and
    /// <see cref="AppInfo.ReleasesUrl"/> beside it, at this edition's own repository; see
    /// <see cref="UpdateCheck.Offered"/>.
    /// </summary>
    private const string Endpoint = "http://localhost";

    /// <summary>
    /// GitHub refuses a request with no User-Agent. Nothing identifying is sent: no version, no
    /// machine details, no identifier of any kind. The download sends exactly the same.
    /// </summary>
    public const string UserAgent = "Composa";

    private sealed record Payload(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("prerelease")] bool PreRelease,
        [property: JsonPropertyName("assets")] List<AssetPayload>? Assets);

    private sealed record AssetPayload(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? Url,
        [property: JsonPropertyName("size")] long Size);

    public async Task<ReleaseInfo?> Latest(CancellationToken cancel)
    {
        using var client = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        var payload = await client.GetFromJsonAsync<Payload>(Endpoint, cancel);
        if (payload?.TagName is not { Length: > 0 } tag || payload.HtmlUrl is not { Length: > 0 } url) return null;
        var assets = (payload.Assets ?? [])
            .Where(a => a.Name is { Length: > 0 } && a.Url is { } link && ReleaseAsset.IsOwnAsset(link))
            .Select(a => new ReleaseAsset(a.Name!, a.Url!, a.Size))
            .ToList();
        return new ReleaseInfo(tag, url, payload.PreRelease, assets);
    }
}

public enum UpdateOutcome { Available, UpToDate, Skipped, Disabled, TooSoon, Failed }

/// <param name="Release">The release that was found, with its files, when there is one to offer.</param>
public sealed record UpdateResult(UpdateOutcome Outcome, ReleaseVersion Version = default, string Url = "", ReleaseInfo? Release = null)
{
    public bool ShouldNotify => Outcome == UpdateOutcome.Available;
}

/// <summary>
/// Reports that a newer version exists. It reads one release from the GitHub API and nothing more:
/// fetching a file from that release is <see cref="UpdateDownload"/>'s job, and only when the
/// person presses Download.
/// </summary>
/// <param name="runningVersion">
/// What to compare against, defaulting to this build's own version. Tests pass it explicitly:
/// otherwise every comparison would depend on whether the current commit happens to be tagged, and
/// the same test would exercise a stable version the day of a release and a pre-release the day
/// after.
/// </param>
public sealed class UpdateCheck(IReleaseSource source, Settings settings, Func<DateTime>? now = null, string? runningVersion = null)
{
    private readonly Func<DateTime> now = now ?? (() => DateTime.UtcNow);
    private readonly string runningVersion = runningVersion ?? AppInfo.Version;

    /// <summary>How long an automatic check waits before asking again. A manual check ignores it.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// Set at build time by the packaging scripts. Every download on the releases page is "github",
    /// the .deb and .rpm included, since a file installed by hand has no repository to update it. A
    /// package that comes from a repository is built as "managed", because telling someone to
    /// sidestep their package manager is worse than saying nothing.
    /// </summary>
    public static UpdateChannel Channel { get; } =
        typeof(UpdateCheck).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateChannel")?.Value?.Equals("managed", StringComparison.OrdinalIgnoreCase) == true
            ? UpdateChannel.Managed
            : UpdateChannel.GitHub;

    /// <summary>A packager can switch the check off without patching code.</summary>
    public static bool DisabledByEnvironment =>
        Environment.GetEnvironmentVariable("COMPOSA_DISABLE_UPDATE_CHECK") is { Length: > 0 } value &&
        value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this build offers update checking at all. Withdrawn for the Chinese trial edition, and
    /// switched back on once that edition has a release feed of its own.
    /// <para>
    /// Why it had to go: this edition calls itself <c>1.4.0-CN</c>, which <see cref="ReleaseVersion"/>
    /// reads as a pre-release of the upstream <c>1.4.0</c>, so an offered update would announce the
    /// original English build as newer, and taking it would replace this one.
    /// </para>
    /// <para>
    /// Three things keep it out of reach, because a feature that is merely switched off can be
    /// switched on again by a settings file, a stray caller or a later release: this flag, the
    /// loopback feed address in <see cref="GitHubReleaseSource"/>, and
    /// <see cref="AppInfo.ReleasesUrl"/>. Restoring updates means putting all three back and aiming
    /// the first two at this edition's own releases.
    /// </para>
    /// <para>
    /// A field of the build rather than a setting, so no configuration file can bring the check back.
    /// <c>static readonly</c> rather than <c>const</c>: a constant would have the compiler report the
    /// code it guards as unreachable, and that code is staying — the tests drive it, and a later
    /// release switches the flag back on. The menu items stay too, greyed out, because a feature that
    /// silently vanishes reads as a bug.
    /// </para>
    /// </summary>
    public static readonly bool Offered = false;

    /// <summary>Whether the check runs by itself at launch. A manual check is always allowed.</summary>
    public bool RunsAutomatically =>
        Channel == UpdateChannel.GitHub && settings.CheckForUpdates && !DisabledByEnvironment;

    public async Task<UpdateResult> Run(bool manual, CancellationToken cancel = default)
    {
        if (!manual)
        {
            if (!RunsAutomatically) return new UpdateResult(UpdateOutcome.Disabled);
            if (settings.LastUpdateCheck is { } last && now() - last < Interval)
                return new UpdateResult(UpdateOutcome.TooSoon);
        }

        // The attempt is recorded before anything can go wrong with it, so that a failure waits its
        // turn like a success does. Otherwise someone offline, or rate limited by GitHub, would
        // send another request on every single launch.
        settings.LastUpdateCheck = now();
        settings.Save();

        ReleaseInfo? latest;
        try { latest = await source.Latest(cancel); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            // An automatic check says nothing at all; a manual one owes the person an answer.
            return new UpdateResult(UpdateOutcome.Failed);
        }

        if (latest is null || !ReleaseVersion.TryParse(latest.Tag, out var available))
            return new UpdateResult(UpdateOutcome.Failed);
        if (!ReleaseVersion.TryParse(this.runningVersion, out var running))
            return new UpdateResult(UpdateOutcome.Failed);

        // Someone on a stable build is not offered a pre-release: running 0.3.0 should never be
        // told that 0.4.0-beta.1 is "available". Someone already on a pre-release does get them.
        if (available.IsPreRelease && !running.IsPreRelease) return new UpdateResult(UpdateOutcome.UpToDate);
        if (available.CompareTo(running) <= 0) return new UpdateResult(UpdateOutcome.UpToDate);

        // A skip applies to that version only, so the next one is still announced.
        if (!manual && settings.SkippedVersion == available.ToString())
            return new UpdateResult(UpdateOutcome.Skipped, available, latest.Url, latest);

        return new UpdateResult(UpdateOutcome.Available, available, latest.Url, latest);
    }
}
