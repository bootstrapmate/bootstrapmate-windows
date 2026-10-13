using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BootstrapMate.Core;

/// <summary>A Windows service or scheduled task the installer creates.</summary>
public enum FootprintKind { Service, ScheduledTask }

/// <summary>One entry of the installer's footprint: what it is called and what it is for.</summary>
public sealed record FootprintItem(string FriendlyName, string Name, FootprintKind Kind, string Description);

/// <summary>One label/value row of an expanded Services entry.</summary>
public sealed record FootprintDetail(string Label, string Value);

/// <summary>
/// The live state of one footprint entry, in plain words for display: a one-line summary for the
/// collapsed row and the detail rows (labels as ReportMate's Windows System module uses them).
/// </summary>
public sealed record FootprintStatus(
    string FriendlyName, FootprintKind Kind, string State, string Summary, IReadOnlyList<FootprintDetail> Details)
{
    public bool IsRunning => State == "Running";
    public bool IsReady => State == "Ready";
    /// <summary>Stopped, Disabled, Not installed or Unknown: the neutral dot.</summary>
    public bool IsNeutral => !IsRunning && !IsReady;
}

/// <summary>What the Task Scheduler reports for one task.</summary>
public sealed record TaskSnapshot(
    string Path, bool Enabled, int State, DateTime LastRunTime, int LastTaskResult, DateTime NextRunTime, string Xml);

/// <summary>What the service control manager reports for one service.</summary>
public sealed record ServiceSnapshot(
    string StartMode, bool DelayedStart, string Status, string? Account, string? PathName, int Win32ExitCode);

/// <summary>
/// The Windows services and scheduled tasks the MSI installs, and a read-only look at their
/// current state for the Prefs tab. The list is the installer's own (installer/Product.wxs):
/// it registers one scheduled task and no services, so only tasks are queried. Querying never
/// needs elevation; a value that exists but cannot be read shows as "Unknown".
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
        // The installer registers no services, so none are queried; a service entry added to
        // Items needs a query that fills a ServiceSnapshot for ServiceStatus.
        _ => Unreadable(item),
    };

    private static FootprintStatus QueryTask(FootprintItem item)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
                return Unreadable(item);

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic task = service.GetFolder("\\").GetTask(item.Name);

            return TaskStatus(item, new TaskSnapshot(
                (string)task.Path, (bool)task.Enabled, (int)task.State, (DateTime)task.LastRunTime,
                (int)task.LastTaskResult, (DateTime)task.NextRunTime, (string)task.Xml));
        }
        catch (Exception ex) when (ex.HResult is HResultFileNotFound or HResultPathNotFound)
        {
            return NotInstalled(item);
        }
        catch (Exception)
        {
            // Access denied for a standard user, or the scheduler is unavailable.
            return Unreadable(item);
        }
    }

    // ── Rows ─────────────────────────────────────────────────────

    /// <summary>The collapsed summary and detail rows for a task.</summary>
    public static FootprintStatus TaskStatus(FootprintItem item, TaskSnapshot task)
    {
        var state = DescribeTaskState(task.State);
        var triggers = DescribeTaskTriggers(task.Xml);
        var neverRun = task.LastTaskResult == SchedTaskHasNotRun || task.LastRunTime.Year < 2000;

        var summary = Join(DescribeKind(item.Kind), triggers,
            neverRun ? "Never run" : $"Last result {FormatResultCode(task.LastTaskResult)}");

        var details = new List<FootprintDetail>
        {
            new("Name", task.Path),
            new("Start type", task.Enabled ? "Enabled" : "Disabled"),
            new("Status", state),
        };
        AddIfPresent(details, "Account", DescribeTaskPrincipal(task.Xml));
        AddIfPresent(details, "Path and arguments", DescribeTaskActions(task.Xml));
        details.Add(new("Triggers", triggers));
        details.Add(new("Last run", neverRun ? "Never" : FormatDate(task.LastRunTime)));
        details.Add(new("Last result", DescribeTaskResult(task.LastRunTime, task.LastTaskResult)));
        details.Add(new("Next run", task.NextRunTime.Year < 2000 ? "Not scheduled" : FormatDate(task.NextRunTime)));

        return new(item.FriendlyName, item.Kind, state, summary, details);
    }

    /// <summary>The collapsed summary and detail rows for a service.</summary>
    public static FootprintStatus ServiceStatus(FootprintItem item, ServiceSnapshot service)
    {
        var startType = DescribeServiceStartType(service.StartMode, service.DelayedStart);
        var state = startType == "Disabled" ? "Disabled" : service.Status;

        var details = new List<FootprintDetail>
        {
            new("Name", item.Name),
            new("Start type", startType),
            new("Status", state),
        };
        AddIfPresent(details, "Account", service.Account);
        AddIfPresent(details, "Path and arguments", service.PathName);
        details.Add(new("Triggers", DescribeStartType(service.StartMode, service.DelayedStart)));
        if (service.Status == "Stopped")
            details.Add(new("Last result", DescribeServiceResult(running: false, service.Win32ExitCode)));

        return new(item.FriendlyName, item.Kind, state,
            Join(DescribeKind(item.Kind), startType, service.Account), details);
    }

    private static FootprintStatus NotInstalled(FootprintItem item) =>
        new(item.FriendlyName, item.Kind, "Not installed", Join(DescribeKind(item.Kind), "Not installed"),
            [new("Name", item.Name), new("Status", "Not installed")]);

    private static FootprintStatus Unreadable(FootprintItem item)
    {
        var details = new List<FootprintDetail> { new("Name", item.Name) };
        var labels = item.Kind == FootprintKind.ScheduledTask
            ? new[] { "Start type", "Status", "Account", "Path and arguments", "Triggers", "Last run", "Last result", "Next run" }
            : new[] { "Start type", "Status", "Account", "Path and arguments", "Triggers" };
        details.AddRange(labels.Select(l => new FootprintDetail(l, "Unknown")));
        return new(item.FriendlyName, item.Kind, "Unknown", DescribeKind(item.Kind), details);
    }

    private static void AddIfPresent(List<FootprintDetail> details, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            details.Add(new(label, value));
    }

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    // Times follow the user's culture: "g" (short date and time) here, "t" for trigger times.
    private static string FormatDate(DateTime value) => value.ToString("g", CultureInfo.CurrentCulture);

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

    /// <summary>A result code as the summary shows it: 0, or the HRESULT in hex.</summary>
    public static string FormatResultCode(int code) => code == 0 ? "0" : $"0x{code:X8}";

    /// <summary>
    /// The last result in words: "Succeeded", "Failed (0x…)" or "Never run". The scheduler
    /// reports a task that has never run with a zero date or 0x41303.
    /// </summary>
    public static string DescribeTaskResult(DateTime lastRun, int lastResult)
    {
        if (lastResult == SchedTaskHasNotRun || lastRun.Year < 2000)
            return "Never run";

        return lastResult switch
        {
            0 => "Succeeded",
            SchedTaskRunning => "Running",
            _ => $"Failed (0x{lastResult:X8})",
        };
    }

    /// <summary>A service start mode as a start type: Automatic, Automatic (delayed), Manual, Disabled.</summary>
    public static string DescribeServiceStartType(string startMode, bool delayed) => startMode switch
    {
        "Automatic" or "Auto" when delayed => "Automatic (delayed)",
        "Automatic" or "Auto" => "Automatic",
        "Boot" or "System" => "Automatic",
        "Manual" => "Manual",
        "Disabled" => "Disabled",
        _ => "Unknown",
    };

    /// <summary>A service start mode as a trigger in words ("At boot", …).</summary>
    public static string DescribeStartType(string startMode, bool delayed) => startMode switch
    {
        "Automatic" or "Auto" when delayed => "At boot (delayed)",
        "Automatic" or "Auto" => "At boot",
        "Boot" or "System" => "With Windows",
        "Manual" => "Manually",
        "Disabled" => "Never (disabled)",
        _ => "Unknown",
    };

    /// <summary>A stopped service's Win32 exit code in words; "—" while it runs.</summary>
    public static string DescribeServiceResult(bool running, int win32ExitCode) =>
        running ? "—" : $"Exit code {win32ExitCode}";

    /// <summary>
    /// The account a task runs as, from its principal: the user or group, with well-known SIDs
    /// named. Null when the definition names neither.
    /// </summary>
    public static string? DescribeTaskPrincipal(string taskXml)
    {
        var principal = Parse(taskXml)?.Root?.Element(TaskNs + "Principals")?.Element(TaskNs + "Principal");
        var id = (string?)principal?.Element(TaskNs + "UserId") ?? (string?)principal?.Element(TaskNs + "GroupId");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return id.Trim() switch
        {
            "S-1-5-18" => "SYSTEM",
            "S-1-5-19" => "LOCAL SERVICE",
            "S-1-5-20" => "NETWORK SERVICE",
            "S-1-5-32-544" => "Administrators",
            "S-1-5-32-545" => "Users",
            var other => other,
        };
    }

    /// <summary>Each Exec action's command and arguments, one per line. Null when there are none.</summary>
    public static string? DescribeTaskActions(string taskXml)
    {
        var lines = Parse(taskXml)?.Root?.Element(TaskNs + "Actions")?.Elements(TaskNs + "Exec")
            .Select(e => ((string?)e.Element(TaskNs + "Command") ?? "").Trim()
                         + (string.IsNullOrWhiteSpace((string?)e.Element(TaskNs + "Arguments"))
                             ? "" : " " + ((string)e.Element(TaskNs + "Arguments")!).Trim()))
            .Where(l => l.Length > 0)
            .ToList();
        return lines is { Count: > 0 } ? string.Join("\n", lines) : null;
    }

    private static XDocument? Parse(string xml)
    {
        try { return XDocument.Parse(xml); }
        catch (XmlException) { return null; }
    }

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
            ? start.ToString("t", CultureInfo.CurrentCulture)
            : "an unknown time";

    private static string FormatBoundary(XElement trigger) =>
        TryBoundary(trigger, out var start)
            ? FormatDate(start)
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
