namespace BootstrapMate.Core;

/// <summary>
/// Finds the log a CLI run writes, so the GUI can tail it.
/// </summary>
/// <remarks>
/// A run writes <c>logs\YYYY-MM-DD\HHMMSS\bootstrap.log</c>, and falls back to a flat
/// <c>logs\&lt;timestamp&gt;.log</c> only when its session directory cannot be created.
/// Looking for a new top-level <c>*.log</c> alone never sees the session log.
/// </remarks>
public static class RunLogLocator
{
    /// <summary>Name of the human log inside a session directory.</summary>
    public const string SessionLogName = "bootstrap.log";

    /// <summary>
    /// Every log a run could have written: session logs at any depth and flat files at
    /// the root. Taken before the CLI starts, so <see cref="FindNewRunLog"/> can ignore them.
    /// </summary>
    public static HashSet<string> Snapshot(string logDirectory)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(logDirectory))
            return existing;

        foreach (var path in Candidates(logDirectory))
            existing.Add(path);
        return existing;
    }

    /// <summary>
    /// The newest log not in <paramref name="existing"/> and created no earlier than
    /// <paramref name="runStartUtc"/>, preferring a session log over a flat file.
    /// Returns null while the CLI has not created one yet.
    /// </summary>
    public static string? FindNewRunLog(string logDirectory, ISet<string> existing, DateTime runStartUtc)
    {
        if (!Directory.Exists(logDirectory))
            return null;

        // File times are coarser than DateTime.UtcNow on some file systems; allow for it.
        var notBefore = runStartUtc - TimeSpan.FromSeconds(2);

        return Candidates(logDirectory)
            .Where(path => !existing.Contains(path))
            .Select(path => (Path: path, Created: CreatedUtc(path)))
            .Where(entry => entry.Created >= notBefore)
            .OrderBy(entry => IsSessionLog(logDirectory, entry.Path) ? 0 : 1)
            .ThenByDescending(entry => entry.Created)
            .Select(entry => entry.Path)
            .FirstOrDefault();
    }

    private static IEnumerable<string> Candidates(string logDirectory)
    {
        IEnumerable<string> sessions, flat;
        try
        {
            sessions = Directory.GetFiles(logDirectory, SessionLogName, SearchOption.AllDirectories)
                .Where(path => IsSessionLog(logDirectory, path));
            flat = Directory.GetFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly);
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        return sessions.Concat(flat).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSessionLog(string logDirectory, string path) =>
        !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.GetFullPath(logDirectory).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static DateTime CreatedUtc(string path)
    {
        try { return File.GetCreationTimeUtc(path); } catch { return DateTime.MinValue; }
    }
}
