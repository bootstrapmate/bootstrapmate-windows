using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class BaselineThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 3, 0, 0, TimeSpan.FromHours(-7));

    private static BaselineRecord Record(string status, double hoursAgo, int failures = 0) => new()
    {
        Status = status,
        EndTime = Now.AddHours(-hoursAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        ConsecutiveFailures = failures
    };

    private static BaselineThrottle.Decision Eval(BaselineRecord? last, bool forced = false, int interval = 144) =>
        BaselineThrottle.Evaluate(last, Now, interval, forced);

    [Fact]
    public void AYoungCompletedBaselineSkips()
    {
        var d = Eval(Record(RunStatuses.Completed, 24));
        Assert.True(d.Skip);
        Assert.Contains("next baseline allowed after", d.Reason);
    }

    [Fact]
    public void AnOldCompletedBaselineRuns() =>
        Assert.False(Eval(Record(RunStatuses.Completed, 145)).Skip);

    [Theory]
    [InlineData(RunStatuses.Failed)]
    [InlineData(RunStatuses.PartialFailure)]
    [InlineData(RunStatuses.Interrupted)]
    public void AFirstFailureRetriesOnceAfter24Hours(string status)
    {
        Assert.True(Eval(Record(status, 23, failures: 1)).Skip);
        Assert.False(Eval(Record(status, 25, failures: 1)).Skip);
    }

    [Fact]
    public void OnceTheRetryIsUsedTheFullIntervalApplies()
    {
        Assert.True(Eval(Record(RunStatuses.Failed, 25, failures: 2)).Skip);
        Assert.True(Eval(Record(RunStatuses.Interrupted, 143, failures: 5)).Skip);
        Assert.False(Eval(Record(RunStatuses.Failed, 145, failures: 2)).Skip);
    }

    [Fact]
    public void AFailedBaselineNeverRetriesSoonerThan24HoursEvenWithAShortInterval() =>
        Assert.True(Eval(Record(RunStatuses.Failed, 2, failures: 1), interval: 1).Skip);

    [Fact]
    public void TheForceFileAlwaysRuns()
    {
        var d = Eval(Record(RunStatuses.Completed, 1), forced: true);
        Assert.False(d.Skip);
        Assert.Contains(".bootstrap_force", d.Reason);
    }

    [Fact]
    public void NoRecordRuns() => Assert.False(Eval(null).Skip);

    [Fact]
    public void AnUnreadableTimeDoesNotBlockTheRun()
    {
        var last = Record(RunStatuses.Completed, 1);
        last.EndTime = "garbage";
        Assert.False(Eval(last).Skip);
    }

    [Fact]
    public void ZeroIntervalTurnsTheThrottleOffForCompletedRuns() =>
        Assert.False(Eval(Record(RunStatuses.Completed, 0.1), interval: 0).Skip);

    [Fact]
    public void TheDefaultIntervalIsSixDays() =>
        Assert.Equal(144, new BootstrapMateConfig().BaselineMinIntervalHours);

    [Fact]
    public void FailuresCountUntilABaselineCompletes()
    {
        var first = BaselineThrottle.After(null, RunStatuses.Failed, Now);
        Assert.Equal(1, first.ConsecutiveFailures);
        var second = BaselineThrottle.After(first, RunStatuses.Interrupted, Now);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal(0, BaselineThrottle.After(second, RunStatuses.Completed, Now).ConsecutiveFailures);
    }

    [Fact]
    public void TheRecordSurvivesAWriteAndRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baseline-{Guid.NewGuid():N}.json");
        try
        {
            BaselineThrottle.Write(Record(RunStatuses.PartialFailure, 3, failures: 1), path);
            var read = BaselineThrottle.Read(path);
            Assert.NotNull(read);
            Assert.Equal(RunStatuses.PartialFailure, read!.Status);
            Assert.Equal(1, read.ConsecutiveFailures);
            Assert.Contains("\"consecutive_failures\"", File.ReadAllText(path));
            BaselineThrottle.Clear(path);
            Assert.Null(BaselineThrottle.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class InterruptedRunTests
{
    [Fact]
    public void ARunStillMarkedRunningIsRelabelledInterrupted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"logs-{Guid.NewGuid():N}");
        var sessionDir = Path.Combine(root, "2026-10-05", "030002");
        Directory.CreateDirectory(sessionDir);
        var sessionFile = Path.Combine(sessionDir, "session.json");
        File.WriteAllText(sessionFile, "{\"session_id\":\"2026-10-05-030002\",\"status\":\"running\"}");
        try
        {
            var record = new LastRunRecord { SessionId = "2026-10-05-030002", RunType = RunTypes.Baseline, Status = RunStatuses.Running };
            var marked = LastRunFile.MarkInterrupted(record, root);
            Assert.NotNull(marked);
            Assert.Equal(RunStatuses.Interrupted, marked!.Status);
            Assert.Contains("\"interrupted\"", File.ReadAllText(sessionFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(RunStatuses.Completed)]
    [InlineData(RunStatuses.Failed)]
    [InlineData(RunStatuses.PartialFailure)]
    public void AFinishedRunIsLeftAlone(string status) =>
        Assert.Null(LastRunFile.MarkInterrupted(new LastRunRecord { Status = status }));

    [Fact]
    public void NoRecordIsNotAnInterruption() => Assert.Null(LastRunFile.MarkInterrupted(null));

    [Theory]
    [InlineData("2026-10-05-030002", "2026-10-05", "030002")]
    [InlineData("2026-10-05-030002_2", "2026-10-05", "030002_2")]
    public void ASessionIdNamesItsDirectory(string id, string day, string time) =>
        Assert.Equal(Path.Combine("logs", day, time), LastRunFile.SessionDirectory("logs", id));

    [Fact]
    public void AnUnrecognisedSessionIdHasNoDirectory() =>
        Assert.Null(LastRunFile.SessionDirectory("logs", ""));
}
