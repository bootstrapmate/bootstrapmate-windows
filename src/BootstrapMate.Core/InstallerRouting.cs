namespace BootstrapMate.Core;

public enum MsiInstaller
{
    Msiexec,
    SbinInstaller
}

/// <summary>
/// Which tool installs an MSI item.
/// </summary>
/// <remarks>
/// sbin-installer installs Cimian-built MSIs well because it runs their embedded
/// scripts, but its command line is <c>--pkg &lt;path&gt; --target &lt;target&gt;</c> and nothing
/// else. A manifest item's <c>arguments</c> for an MSI are msiexec arguments
/// (<c>/qn</c>, <c>PROPERTY=value</c>); handed to sbin-installer they were read as the
/// package path ("Installing package: qn") and the install failed. And sbin-installer
/// must never install itself: the published file name for it is not always the
/// upstream <c>sbin-installer-*</c> name, so a name-prefix check alone let it through.
/// </remarks>
public static class InstallerRouting
{
    public static MsiInstaller Choose(bool isCimianBuilt, bool sbinAvailable, string fileName, string? itemName, int argumentCount)
    {
        if (!isCimianBuilt || !sbinAvailable) return MsiInstaller.Msiexec;
        if (IsSbinInstallerPackage(fileName, itemName)) return MsiInstaller.Msiexec;
        if (argumentCount > 0) return MsiInstaller.Msiexec;
        return MsiInstaller.SbinInstaller;
    }

    /// <summary>
    /// True for the sbin-installer package itself, under any of the names it is
    /// published as (<c>sbin-installer-x64-…</c>, <c>SbinInstaller-x64-…</c>) or by its
    /// display names.
    /// </summary>
    public static bool IsSbinInstallerPackage(string fileName, string? itemName)
    {
        static string Squash(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var file = Squash(Path.GetFileName(fileName));
        var name = Squash(itemName);
        return file.StartsWith("sbininstaller")
            || name.StartsWith("sbininstaller")
            || name.Contains("systembinaryinstaller");
    }
}

/// <summary>
/// Checks a downloaded payload against the manifest's <c>hash</c> before it runs.
/// </summary>
public static class PayloadIntegrity
{
    /// <summary>
    /// Null when the payload may run: no hash was given, or it matches. Otherwise the
    /// reason the item must fail. A hash that is not 64 hex digits is refused rather
    /// than skipped, because a manifest author who wrote one meant to pin the payload.
    /// </summary>
    public static string? Check(string? expected, string actualSha256)
    {
        if (string.IsNullOrWhiteSpace(expected)) return null;

        var want = expected.Trim();
        if (want.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) want = want[7..];
        if (want.Length != 64 || !want.All(Uri.IsHexDigit))
            return $"manifest hash is not a SHA-256 hex digest: '{expected}'";

        return string.Equals(want, actualSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"SHA-256 mismatch: manifest {want.ToLowerInvariant()}, downloaded {actualSha256.ToLowerInvariant()}";
    }
}
