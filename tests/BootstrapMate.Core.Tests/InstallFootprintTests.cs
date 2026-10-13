using System.Globalization;
using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

/// <summary>
/// Times follow the current culture, so every test runs pinned to en-GB (24-hour) unless it pins
/// another culture itself.
/// </summary>
public sealed class InstallFootprintTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public InstallFootprintTests() => CultureInfo.CurrentCulture = new CultureInfo("en-GB");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static T In<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ICU puts a narrow no-break space before AM/PM; compare it as a plain space.
    private static string Spaces(string value) => value.Replace(' ', ' ').Replace(' ', ' ');

    private static string TaskXml(string triggers, string principal = "", string actions = "") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers>{triggers}</Triggers>
          <Principals>{principal}</Principals>
          <Actions Context="Author">{actions}</Actions>
        </Task>
        """.Trim();

    // What schtasks /Create … /SC DAILY /ST 03:00 /RU SYSTEM registers for the self-heal task.
    private static readonly string SelfHealXml = TaskXml(
        """
        <CalendarTrigger>
          <StartBoundary>2026-10-12T03:00:00</StartBoundary>
          <Enabled>true</Enabled>
          <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
        </CalendarTrigger>
        """,
        """<Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>""",
        """<Exec><Command>"C:\Program Files\BootstrapMate\managedbootstrapinstall.exe"</Command><Arguments>--silent --no-dialog</Arguments></Exec>""");

    private static FootprintItem SelfHeal => InstallFootprint.Items[0];

    private static TaskSnapshot Snapshot(int state = 3, int result = 0, int lastYear = 2026, int nextYear = 2026, bool enabled = true) =>
        new(@"\BootstrapMate Self-Heal", enabled, state,
            new DateTime(lastYear, 10, 12, 3, 0, 0), result, new DateTime(nextYear, 10, 13, 3, 0, 0), SelfHealXml);

    private static string Detail(FootprintStatus status, string label) =>
        Assert.Single(status.Details, d => d.Label == label).Value;

    [Fact]
    public void TheFootprintIsTheSelfHealTaskTheInstallerRegisters()
    {
        var item = Assert.Single(InstallFootprint.Items);
        Assert.Equal("BootstrapMate Self-Heal", item.Name);
        Assert.Equal(FootprintKind.ScheduledTask, item.Kind);
    }

    // ── Task rows ────────────────────────────────────────────────

    [Fact]
    public void TheCollapsedTaskSummaryNamesKindScheduleAndResult() =>
        Assert.Equal("Scheduled task · Daily at 03:00 · Last result 0",
            InstallFootprint.TaskStatus(SelfHeal, Snapshot()).Summary);

    [Fact]
    public void AFailedResultShowsItsHexCodeInTheSummary() =>
        Assert.EndsWith("Last result 0x80070005",
            InstallFootprint.TaskStatus(SelfHeal, Snapshot(result: unchecked((int)0x80070005))).Summary);

    [Fact]
    public void ATaskThatHasNeverRunSaysSoInTheSummary() =>
        Assert.Equal("Scheduled task · Daily at 03:00 · Never run",
            InstallFootprint.TaskStatus(SelfHeal, Snapshot(result: 0x41303, lastYear: 1899)).Summary);

    [Fact]
    public void TaskDetailsFollowTheReportMateOrderAndLabels()
    {
        var status = InstallFootprint.TaskStatus(SelfHeal, Snapshot());

        Assert.Equal(
            ["Name", "Start type", "Status", "Account", "Path and arguments", "Triggers", "Last run", "Last result", "Next run"],
            status.Details.Select(d => d.Label));
        Assert.Equal(@"\BootstrapMate Self-Heal", Detail(status, "Name"));
        Assert.Equal("Enabled", Detail(status, "Start type"));
        Assert.Equal("Ready", Detail(status, "Status"));
        Assert.Equal("SYSTEM", Detail(status, "Account"));
        Assert.Equal("\"C:\\Program Files\\BootstrapMate\\managedbootstrapinstall.exe\" --silent --no-dialog",
            Detail(status, "Path and arguments"));
        Assert.Equal("Daily at 03:00", Detail(status, "Triggers"));
        Assert.Equal("12/10/2026 03:00", Detail(status, "Last run"));
        Assert.Equal("Succeeded", Detail(status, "Last result"));
        Assert.Equal("13/10/2026 03:00", Detail(status, "Next run"));
    }

    [Fact]
    public void NeverRunAndNothingScheduledReadAsWords()
    {
        var status = InstallFootprint.TaskStatus(SelfHeal, Snapshot(result: 0x41303, lastYear: 1899, nextYear: 1899));
        Assert.Equal("Never", Detail(status, "Last run"));
        Assert.Equal("Never run", Detail(status, "Last result"));
        Assert.Equal("Not scheduled", Detail(status, "Next run"));
    }

    [Fact]
    public void ADisabledTaskShowsDisabledAndTheNeutralDot()
    {
        var status = InstallFootprint.TaskStatus(SelfHeal, Snapshot(state: 1, enabled: false));
        Assert.Equal("Disabled", status.State);
        Assert.Equal("Disabled", Detail(status, "Start type"));
        Assert.True(status.IsNeutral);
    }

    [Theory]
    [InlineData(4, "Running")]
    [InlineData(3, "Ready")]
    public void RunningAndReadyGetTheirOwnDots(int state, string word)
    {
        var status = InstallFootprint.TaskStatus(SelfHeal, Snapshot(state: state));
        Assert.Equal(word, status.State);
        Assert.Equal(word == "Running", status.IsRunning);
        Assert.Equal(word == "Ready", status.IsReady);
        Assert.False(status.IsNeutral);
    }

    // ── Service rows ─────────────────────────────────────────────

    [Fact]
    public void ARunningServiceOmitsTheRowsItDoesNotHave()
    {
        var item = new FootprintItem("Agent", "ExampleSvc", FootprintKind.Service, "");
        var status = InstallFootprint.ServiceStatus(item,
            new ServiceSnapshot("Auto", false, "Running", "LocalSystem", @"C:\x\svc.exe --run", 0));

        Assert.Equal("Service · Automatic · LocalSystem", status.Summary);
        Assert.Equal(["Name", "Start type", "Status", "Account", "Path and arguments", "Triggers"],
            status.Details.Select(d => d.Label));
        Assert.Equal("At boot", Detail(status, "Triggers"));
    }

    [Fact]
    public void AStoppedServiceShowsItsExitCode() =>
        Assert.Equal("Exit code 1067", Detail(InstallFootprint.ServiceStatus(
            new FootprintItem("Agent", "ExampleSvc", FootprintKind.Service, ""),
            new ServiceSnapshot("Manual", false, "Stopped", "LocalSystem", null, 1067)), "Last result"));

    // ── Formatting ───────────────────────────────────────────────

    [Theory]
    [InlineData("Auto", false, "Automatic")]
    [InlineData("Automatic", true, "Automatic (delayed)")]
    [InlineData("Manual", false, "Manual")]
    [InlineData("Disabled", false, "Disabled")]
    [InlineData("Something", false, "Unknown")]
    public void ServiceStartTypesReadAsWords(string mode, bool delayed, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeServiceStartType(mode, delayed));

    [Theory]
    [InlineData("Auto", false, "At boot")]
    [InlineData("Auto", true, "At boot (delayed)")]
    [InlineData("Manual", false, "Manually")]
    public void ServiceStartTypesReadAsTriggers(string mode, bool delayed, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeStartType(mode, delayed));

    [Theory]
    [InlineData(1, "Disabled")]
    [InlineData(3, "Ready")]
    [InlineData(4, "Running")]
    [InlineData(0, "Unknown")]
    public void TaskStatesReadAsWords(int state, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeTaskState(state));

    [Theory]
    [InlineData(0, "Succeeded")]
    [InlineData(2, "Failed (0x00000002)")]
    [InlineData(unchecked((int)0x80070005), "Failed (0x80070005)")]
    public void ResultCodesReadAsWords(int code, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeTaskResult(new DateTime(2026, 10, 12), code));

    [Theory]
    [InlineData("<UserId>S-1-5-18</UserId>", "SYSTEM")]
    [InlineData("<UserId>S-1-5-20</UserId>", "NETWORK SERVICE")]
    [InlineData("<GroupId>S-1-5-32-545</GroupId>", "Users")]
    [InlineData(@"<UserId>CONTOSO\svc-bootstrap</UserId>", @"CONTOSO\svc-bootstrap")]
    public void PrincipalsReadAsAccounts(string principal, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeTaskPrincipal(TaskXml("", $"<Principal>{principal}</Principal>")));

    [Fact]
    public void ATaskWithoutAPrincipalHasNoAccount() =>
        Assert.Null(InstallFootprint.DescribeTaskPrincipal(TaskXml("")));

    [Fact]
    public void EachExecActionIsOneLine() =>
        Assert.Equal("a.exe --one\nb.exe", InstallFootprint.DescribeTaskActions(TaskXml("", "",
            "<Exec><Command>a.exe</Command><Arguments>--one</Arguments></Exec><Exec><Command>b.exe</Command></Exec>")));

    [Fact]
    public void ATaskWithoutExecActionsHasNoPath() =>
        Assert.Null(InstallFootprint.DescribeTaskActions(TaskXml("")));

    [Fact]
    public void TheSelfHealScheduleReadsAsDailyAtThree() =>
        Assert.Equal("Daily at 03:00", InstallFootprint.DescribeTaskTriggers(SelfHealXml));

    [Fact]
    public void EveryFewDaysNamesTheInterval() =>
        Assert.Equal("Every 3 days at 21:30", InstallFootprint.DescribeTaskTriggers(TaskXml("""
            <CalendarTrigger>
              <StartBoundary>2026-10-12T21:30:00-07:00</StartBoundary>
              <ScheduleByDay><DaysInterval>3</DaysInterval></ScheduleByDay>
            </CalendarTrigger>
            """)));

    [Fact]
    public void WeeklyListsItsDays() =>
        Assert.Equal("Weekly on Monday, Friday at 08:00", InstallFootprint.DescribeTaskTriggers(TaskXml("""
            <CalendarTrigger>
              <StartBoundary>2026-10-12T08:00:00</StartBoundary>
              <ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><Monday /><Friday /></DaysOfWeek></ScheduleByWeek>
            </CalendarTrigger>
            """)));

    [Fact]
    public void StartupLogonAndRepetitionReadAsWords() =>
        Assert.Equal("At startup; At logon of any user; Once at 12/10/2026 00:00, then every 4 hours",
            InstallFootprint.DescribeTaskTriggers(TaskXml("""
                <BootTrigger />
                <LogonTrigger />
                <TimeTrigger>
                  <StartBoundary>2026-10-12T00:00:00</StartBoundary>
                  <Repetition><Interval>PT4H</Interval></Repetition>
                </TimeTrigger>
                """)));

    [Fact]
    public void DisabledTriggersAreLeftOut() =>
        Assert.Equal("At startup", InstallFootprint.DescribeTaskTriggers(TaskXml("""
            <BootTrigger />
            <LogonTrigger><Enabled>false</Enabled></LogonTrigger>
            """)));

    [Fact]
    public void ATaskWithNoTriggersRunsOnDemand() =>
        Assert.Equal("On demand only", InstallFootprint.DescribeTaskTriggers(TaskXml("")));

    [Fact]
    public void UnreadableXmlIsUnknown() =>
        Assert.Equal("Unknown", InstallFootprint.DescribeTaskTriggers("<not xml"));

    // ── Culture ──────────────────────────────────────────────────

    [Theory]
    [InlineData("en-GB", "Daily at 03:00")]
    [InlineData("en-US", "Daily at 3:00 AM")]
    public void TriggerTimesUseTheCulturesShortTime(string culture, string expected) =>
        Assert.Equal(expected, Spaces(In(culture, () => InstallFootprint.DescribeTaskTriggers(SelfHealXml))));

    [Theory]
    [InlineData("en-GB", "12/10/2026 03:00", "13/10/2026 03:00")]
    [InlineData("en-US", "10/12/2026 3:00 AM", "10/13/2026 3:00 AM")]
    public void RunTimesUseTheCulturesShortDateAndTime(string culture, string lastRun, string nextRun)
    {
        var status = In(culture, () => InstallFootprint.TaskStatus(SelfHeal, Snapshot()));
        Assert.Equal(lastRun, Spaces(Detail(status, "Last run")));
        Assert.Equal(nextRun, Spaces(Detail(status, "Next run")));
    }
}
