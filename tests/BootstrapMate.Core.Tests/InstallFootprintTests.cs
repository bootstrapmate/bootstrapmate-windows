using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class InstallFootprintTests
{
    private static string TaskXml(string triggers) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers>{triggers}</Triggers>
        </Task>
        """.Trim();

    [Fact]
    public void TheFootprintIsTheSelfHealTaskTheInstallerRegisters()
    {
        var item = Assert.Single(InstallFootprint.Items);
        Assert.Equal("BootstrapMate Self-Heal", item.Name);
        Assert.Equal(FootprintKind.ScheduledTask, item.Kind);
    }

    [Theory]
    [InlineData("Automatic", false, "Starts automatically at boot")]
    [InlineData("Automatic", true, "Starts automatically (delayed) at boot")]
    [InlineData("Manual", false, "Starts manually")]
    [InlineData("Disabled", false, "Disabled")]
    [InlineData("Something", false, "Unknown")]
    public void StartTypesReadAsWords(string startType, bool delayed, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeStartType(startType, delayed));

    [Theory]
    [InlineData(1, "Disabled")]
    [InlineData(3, "Ready")]
    [InlineData(4, "Running")]
    [InlineData(0, "Unknown")]
    public void TaskStatesReadAsWords(int state, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeTaskState(state));

    [Fact]
    public void ASuccessfulRunShowsSucceededWithItsTime() =>
        Assert.Equal("Succeeded — Oct 12, 2026 3:00 AM",
            InstallFootprint.DescribeTaskResult(new DateTime(2026, 10, 12, 3, 0, 0), 0));

    [Fact]
    public void AFailedRunShowsItsHexCode() =>
        Assert.StartsWith("Failed (0x00000002)",
            InstallFootprint.DescribeTaskResult(new DateTime(2026, 10, 12, 3, 0, 0), 2));

    [Fact]
    public void ANegativeResultShowsAsAnHResult() =>
        Assert.StartsWith("Failed (0x80070005)",
            InstallFootprint.DescribeTaskResult(new DateTime(2026, 10, 12, 3, 0, 0), unchecked((int)0x80070005)));

    [Theory]
    [InlineData(0x41303)]
    [InlineData(0)]
    public void ATaskThatHasNotRunSaysSo(int result) =>
        Assert.Equal("Never run", InstallFootprint.DescribeTaskResult(new DateTime(1899, 12, 30), result));

    [Theory]
    [InlineData(false, 1067, "Exit code 1067")]
    [InlineData(true, 0, "—")]
    public void ServiceResultsReadAsWords(bool running, int code, string expected) =>
        Assert.Equal(expected, InstallFootprint.DescribeServiceResult(running, code));

    [Fact]
    public void TheSelfHealScheduleReadsAsDailyAtThree() =>
        Assert.Equal("Daily at 3:00 AM", InstallFootprint.DescribeTaskTriggers(TaskXml("""
            <CalendarTrigger>
              <StartBoundary>2026-10-12T03:00:00</StartBoundary>
              <Enabled>true</Enabled>
              <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
            </CalendarTrigger>
            """)));

    [Fact]
    public void EveryFewDaysNamesTheInterval() =>
        Assert.Equal("Every 3 days at 9:30 PM", InstallFootprint.DescribeTaskTriggers(TaskXml("""
            <CalendarTrigger>
              <StartBoundary>2026-10-12T21:30:00-07:00</StartBoundary>
              <ScheduleByDay><DaysInterval>3</DaysInterval></ScheduleByDay>
            </CalendarTrigger>
            """)));

    [Fact]
    public void WeeklyListsItsDays() =>
        Assert.Equal("Weekly on Monday, Friday at 8:00 AM", InstallFootprint.DescribeTaskTriggers(TaskXml("""
            <CalendarTrigger>
              <StartBoundary>2026-10-12T08:00:00</StartBoundary>
              <ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><Monday /><Friday /></DaysOfWeek></ScheduleByWeek>
            </CalendarTrigger>
            """)));

    [Fact]
    public void StartupLogonAndRepetitionReadAsWords() =>
        Assert.Equal("At startup; At logon of any user; Once at Oct 12, 2026 12:00 AM, then every 4 hours",
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
}
