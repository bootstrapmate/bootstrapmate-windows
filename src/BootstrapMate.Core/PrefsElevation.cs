using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace BootstrapMate.Core;

/// <summary>
/// Elevation for saving the GUI Prefs tab. Settings live in HKLM, so an unelevated GUI raises
/// UAC only when the user clicks Save: it relaunches its own executable elevated and headless
/// to write the pending values (see <see cref="PrefsSettingsWriter"/>).
/// </summary>
public static class PrefsElevation
{
    /// <summary>Win32 ERROR_CANCELLED: the user dismissed the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>True when the exception is the user cancelling the UAC prompt, not a real failure.</summary>
    public static bool IsElevationCancelled(Exception? ex)
        => ex is Win32Exception { NativeErrorCode: ErrorCancelled };

    /// <summary>
    /// Whether this process holds an elevated administrator token. With UAC on, an admin's
    /// filtered token is not in the Administrators role, so this is false until elevated.
    /// </summary>
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start info that runs <paramref name="exePath"/> elevated and headless to write the
    /// settings sent over the named pipe <paramref name="pipeName"/>.
    /// </summary>
    public static ProcessStartInfo BuildElevatedApply(string exePath, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"{PrefsSettingsWriter.ApplyArgument} \"{pipeName}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };
    }
}
