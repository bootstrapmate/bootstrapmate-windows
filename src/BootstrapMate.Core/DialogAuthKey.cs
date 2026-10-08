namespace BootstrapMate.Core;

/// <summary>
/// The csharpDialog authorisation key. When policy sets an AuthorisationKey for csharpDialog,
/// the dialog refuses to open (exit 30) unless its caller passes the matching plain key in the
/// DIALOG_AUTH_KEY environment variable. The key reaches this process either already in that
/// variable or in a file only SYSTEM and Administrators can read.
/// </summary>
public static class DialogAuthKey
{
    /// <summary>The environment variable csharpDialog reads the key from.</summary>
    public const string EnvironmentVariable = "DIALOG_AUTH_KEY";

    /// <summary>The exit code csharpDialog returns when the key is missing or wrong.</summary>
    public const int KeyRequiredExitCode = 30;

    /// <summary>
    /// The key to hand csharpDialog, or null to launch it without one: the caller's own
    /// DIALOG_AUTH_KEY if set, otherwise the trimmed contents of <paramref name="path"/>
    /// (the default path when null or empty). A missing or unreadable file is not an error.
    /// </summary>
    public static string? Resolve(string? path, Func<string, string?>? readEnvironment = null)
    {
        var fromEnvironment = (readEnvironment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        var file = string.IsNullOrWhiteSpace(path) ? BootstrapMateConstants.DefaultDialogAuthKeyPath : path;
        try
        {
            if (!File.Exists(file))
                return null;
            var key = File.ReadAllText(file).Trim();
            return key.Length > 0 ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
