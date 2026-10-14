using System.Reflection;

namespace Composa.App;

/// <summary>
/// What this build calls itself. The version comes from MinVer by way of the informational version
/// attribute, so it is derived from the git tag and never hand-edited; the About dialog and the
/// update check both read it here so there is one answer rather than two.
/// </summary>
public static class AppInfo
{
    public const string Name = "Composa";

    /// <summary>
    /// Where a user is sent when they ask about a newer version. Withdrawn along with the rest of the
    /// update check for the Chinese trial edition: it used to point at the original project, and from
    /// there a visitor is one download away from replacing this build with the English one. Put this
    /// edition's own releases page here before switching updates back on; see
    /// <see cref="UpdateCheck.Offered"/>.
    /// </summary>
    public const string ReleasesUrl = "http://localhost";

    /// <summary>
    /// The version without build metadata: MinVer writes <c>0.2.0+&lt;sha&gt;</c>, and the commit hash is
    /// noise in a dialog and would break a comparison against a release tag.
    /// </summary>
    public static string Version { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
