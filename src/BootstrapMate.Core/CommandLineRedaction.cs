namespace BootstrapMate.Core;

/// <summary>
/// Hides the value of every switch that carries a credential before a command line is
/// written to a log or session record.
/// </summary>
public static class CommandLineRedaction
{
    public const string Mask = "<redacted>";

    /// <summary>Switches whose value is a credential.</summary>
    public static readonly string[] SecretSwitches = ["--headers", "--reporting-header"];

    public static IEnumerable<string> Redact(IEnumerable<string> args)
    {
        bool hideNext = false;
        foreach (var arg in args)
        {
            if (hideNext)
            {
                hideNext = false;
                yield return Mask;
                continue;
            }
            var match = SecretSwitches.FirstOrDefault(s =>
                arg.StartsWith(s + "=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith(s + ":", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                yield return $"{arg[..(match.Length + 1)]}{Mask}";
                continue;
            }
            hideNext = SecretSwitches.Contains(arg, StringComparer.OrdinalIgnoreCase);
            yield return arg;
        }
    }

    /// <summary>
    /// The same for a whole command line as Windows quotes it. Quoting is honoured well
    /// enough to find a value; the result is for display only.
    /// </summary>
    public static string Redact(string commandLine) => string.Join(" ", Redact(Split(commandLine)).Select(Quote));

    private static IEnumerable<string> Split(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { yield return current.ToString(); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) yield return current.ToString();
    }

    private static string Quote(string arg) => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
}
