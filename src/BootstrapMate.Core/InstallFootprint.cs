using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BootstrapMate.Core;

/// <summary>A Windows service or scheduled task the installer creates.</summary>
public enum FootprintKind { Service, ScheduledTask }

/// <summary>One entry of the installer's footprint: what it is called and what it is for.</summary>
public sealed record FootprintItem(string FriendlyName, string Name, FootprintKind Kind, string Description);

/// <summary>The live state of one footprint entry, already in plain words for display.</summary>
public sealed record FootprintStatus(
    string FriendlyName, string Name, string KindText, string Description,
    string State, string LastResult, string Trigger);

/// <summary>
/// The Windows services and scheduled tasks the MSI installs, and a read-only look at their
/// current state for the Prefs tab. The list is the installer's own (installer/Product.wxs):
/// it registers one scheduled task and no services. Querying never needs elevation; anything
/// the caller may not read shows as "Unknown".
/// </summary>
public static class InstallFootprint
{
    public const string SelfHealTaskName = "BootstrapMate Self-Heal";

    public static readonly IReadOnlyList<FootprintItem> Items =
    [
        new("Daily self-heal", SelfHealTaskName, FootprintKind.ScheduledTask,
            "Re-runs the bootstrap as SYSTEM so missing packages are reinstalled."),
    ];

    // Task Scheduler result codes that are not failures.
    private const int SchedTaskHasNotRun = 0x00041303;
    private const int SchedTaskRunning = 0x00041301;

    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);

    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Queries every footprint entry. Blocking; call it off the UI thread.</summary>
    public static IReadOnlyList<FootprintStatus> QueryAll() => Items.Select(Query).ToList();

    private static FootprintStatus Query(FootprintItem item) => item.Kind switch
    {
        FootprintKind.ScheduledTask => QueryTask(item),
        // The installer registers no services, so there is nothing to query here yet.
        _ => Row(item, "Unknown", "—", "—"),
    };

    private static FootprintStatus QueryTask(FootprintItem item)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
                return Row(item, "Unknown", "—", "—");

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic task = service.GetFolder("\\").GetTask(item.Name);

            int state = task.State;
            DateTime lastRun = task.LastRunTime;
            int lastResult = task.LastTaskResult;
            string xml = task.Xml;

            return Row(item, DescribeTaskState(state), DescribeTaskResult(lastRun, lastResult), DescribeTaskTriggers(xml));
        }
        catch (Exception ex) when (ex.HResult is HResultFileNotFound or HResultPathNotFound)
        {
            return Row(item, "Not installed", "—", "—");
        }
        catch (Exception)
        {
            // Access denied for a standard user, or the scheduler is unavailable.
            return Row(item, "Unknown", "Unknown", "Unknown");
        }
    }

    private static FootprintStatus Row(FootprintItem item, string state, string lastResult, string trigger) =>
        new(item.FriendlyName, item.Name, DescribeKind(item.Kind), item.Description, state, lastResult, trigger);

    // ── Plain-words formatting ───────────────────────────────────

    public static string DescribeKind(FootprintKind kind) =>
        kind == FootprintKind.Service ? "Service" : "Scheduled task";

    /// <summary>Task Scheduler TASK_STATE value in words.</summary>
    public static string DescribeTaskState(int state) => state switch
    {
        1 => "Disabled",
        2 => "Ready",   // queued to run
        3 => "Ready",
        4 => "Running",
        _ => "Unknown",
    };

    /// <summary>
    /// Last run time and result in words: "Never run", or "Succeeded" / "Failed (0x…)" with the
    /// run time. The scheduler reports a task that has never run with a zero date or 0x41303.
    /// </summary>
    public static string DescribeTaskResult(DateTime lastRun, int lastResult)
    {
        if (lastResult == SchedTaskHasNotRun || lastRun.Year < 2000)
            return "Never run";

        var outcome = lastResult switch
        {
            0 => "Succeeded",
            SchedTaskRunning => "Running",
            _ => $"Failed (0x{lastResult:X8})",
        };
        return $"{outcome} — {lastRun.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)}";
    }

    /// <summary>A service start type in words ("Starts automatically at boot", …).</summary>
    public static string DescribeStartType(string startType, bool delayed) => startType switch
    {
        "Automatic" when delayed => "Starts automatically (delayed) at boot",
        "Automatic" => "Starts automatically at boot",
        "Boot" or "System" => "Starts with Windows",
        "Manual" => "Starts manually",
        "Disabled" => "Disabled",
        _ => "Unknown",
    };

    /// <summary>A stopped service's Win32 exit code in words; "—" while it runs.</summary>
    public static string DescribeServiceResult(bool running, int win32ExitCode) =>
        running ? "—" : $"Exit code {win32ExitCode}";

    /// <summary>
    /// The enabled triggers of a task definition (Task Scheduler XML) in words, joined with "; ".
    /// </summary>
    public static string DescribeTaskTriggers(string taskXml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(taskXml); }
        catch (XmlException) { return "Unknown"; }

        var triggers = doc.Root?.Element(TaskNs + "Triggers")?.Elements() ?? [];
        var words = triggers
            .Where(t => !string.Equals((string?)t.Element(TaskNs + "Enabled"), "false", StringComparison.OrdinalIgnoreCase))
            .Select(DescribeTrigger)
            .ToList();

        return words.Count == 0 ? "On demand only" : string.Join("; ", words);
    }

    private static string DescribeTrigger(XElement trigger)
    {
        var text = trigger.Name.LocalName switch
        {
            "BootTrigger" => "At startup",
            "LogonTrigger" => trigger.Element(TaskNs + "UserId") is { } user
                ? $"At logon of {user.Value}"
                : "At logon of any user",
            "TimeTrigger" => $"Once at {FormatBoundary(trigger)}",
            "CalendarTrigger" => DescribeCalendar(trigger),
            "IdleTrigger" => "When the computer is idle",
            "EventTrigger" => "On an event log entry",
            "RegistrationTrigger" => "When the task is created or changed",
            "SessionStateChangeTrigger" => "On a session change",
            _ => "On a custom trigger",
        };

        var interval = (string?)trigger.Element(TaskNs + "Repetition")?.Element(TaskNs + "Interval");
        if (!string.IsNullOrEmpty(interval) && TryDuration(interval, out var every))
            text += $", then every {every}";
        return text;
    }

    private static string DescribeCalendar(XElement trigger)
    {
        var at = $"at {FormatTime(trigger)}";

        if (trigger.Element(TaskNs + "ScheduleByDay") is { } byDay)
        {
            var days = (int?)byDay.Element(TaskNs + "DaysInterval") ?? 1;
            return days == 1 ? $"Daily {at}" : $"Every {days} days {at}";
        }
        if (trigger.Element(TaskNs + "ScheduleByWeek") is { } byWeek)
        {
            var weeks = (int?)byWeek.Element(TaskNs + "WeeksInterval") ?? 1;
            var names = byWeek.Element(TaskNs + "DaysOfWeek")?.Elements().Select(d => d.Name.LocalName).ToList() ?? [];
            var prefix = weeks == 1 ? "Weekly" : $"Every {weeks} weeks";
            return names.Count == 0 ? $"{prefix} {at}" : $"{prefix} on {string.Join(", ", names)} {at}";
        }
        if (trigger.Element(TaskNs + "ScheduleByMonth") is not null
            || trigger.Element(TaskNs + "ScheduleByMonthDayOfWeek") is not null)
            return $"Monthly {at}";

        return $"On a schedule {at}";
    }

    private static string FormatTime(XElement trigger) =>
        TryBoundary(trigger, out var start)
            ? start.ToString("h:mm tt", CultureInfo.InvariantCulture)
            : "an unknown time";

    private static string FormatBoundary(XElement trigger) =>
        TryBoundary(trigger, out var start)
            ? start.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)
            : "an unknown time";

    // StartBoundary is local wall-clock time, sometimes with an offset; the wall-clock part is
    // what the scheduler shows, so the offset is ignored rather than converted.
    private static bool TryBoundary(XElement trigger, out DateTime start)
    {
        start = default;
        var raw = (string?)trigger.Element(TaskNs + "StartBoundary");
        if (string.IsNullOrEmpty(raw) || raw.Length < 19)
            return false;
        return DateTime.TryParseExact(raw[..19], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out start);
    }

    private static bool TryDuration(string iso, out string words)
    {
        words = "";
        TimeSpan span;
        try { span = XmlConvert.ToTimeSpan(iso); }
        catch (FormatException) { return false; }

        words = span switch
        {
            { TotalDays: >= 1 } when span.TotalDays % 1 == 0 => Plural((int)span.TotalDays, "day"),
            { TotalHours: >= 1 } when span.TotalHours % 1 == 0 => Plural((int)span.TotalHours, "hour"),
            _ => Plural((int)span.TotalMinutes, "minute"),
        };
        return true;
    }

    private static string Plural(int n, string unit) => n == 1 ? unit : $"{n} {unit}s";
}
