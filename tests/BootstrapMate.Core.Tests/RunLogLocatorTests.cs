using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public sealed class RunLogLocatorTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "bm-logs-" + Guid.NewGuid().ToString("N"));

    public RunLogLocatorTests() => Directory.CreateDirectory(_logs);

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); } catch { }
    }

    private string Write(string relative, DateTime? createdUtc = null)
    {
        var path = Path.Combine(_logs, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "line\n");
        if (createdUtc is { } created)
            File.SetCreationTimeUtc(path, created);
        return path;
    }

    [Fact]
    public void FindsTheSessionLogInADateFolder()
    {
        Write(@"2026-10-02\101500\bootstrap.log");
        var existing = RunLogLocator.Snapshot(_logs);
        var start = DateTime.UtcNow;

        var created = Write(@"2026-10-03\142233\bootstrap.log");

        Assert.Equal(created, RunLogLocator.FindNewRunLog(_logs, existing, start));
    }

    [Fact]
    public void FallsBackToAFlatTimestampLog()
    {
        var existing = RunLogLocator.Snapshot(_logs);
        var start = DateTime.UtcNow;

        var created = Write("2026-10-03-142233.log");

        Assert.Equal(created, RunLogLocator.FindNewRunLog(_logs, existing, start));
    }

    [Fact]
    public void PrefersTheSessionLogOverAFlatFileWrittenAlongsideIt()
    {
        var existing = RunLogLocator.Snapshot(_logs);
        var start = DateTime.UtcNow;

        var session = Write(@"2026-10-03\142233\bootstrap.log");
        Write("esp-install-wrapper.log");

        Assert.Equal(session, RunLogLocator.FindNewRunLog(_logs, existing, start));
    }

    [Fact]
    public void IgnoresLogsThatExistedBeforeTheRun()
    {
        Write(@"2026-10-03\090000\bootstrap.log");
        Write("2026-10-03-090000.log");
        var existing = RunLogLocator.Snapshot(_logs);

        Assert.Null(RunLogLocator.FindNewRunLog(_logs, existing, DateTime.UtcNow));
    }

    [Fact]
    public void IgnoresALogCreatedBeforeTheRunStarted()
    {
        var start = DateTime.UtcNow;
        Write(@"2026-10-03\090000\bootstrap.log", createdUtc: start.AddMinutes(-5));

        Assert.Null(RunLogLocator.FindNewRunLog(_logs, new HashSet<string>(), start));
    }

    [Fact]
    public void IgnoresOtherFilesInTheSessionDirectory()
    {
        var existing = RunLogLocator.Snapshot(_logs);
        var start = DateTime.UtcNow;

        Write(@"2026-10-03\142233\events.jsonl");
        Write(@"2026-10-03\142233\session.json");

        Assert.Null(RunLogLocator.FindNewRunLog(_logs, existing, start));
    }

    [Fact]
    public void ReturnsNullWhenTheLogDirectoryIsMissing() =>
        Assert.Null(RunLogLocator.FindNewRunLog(Path.Combine(_logs, "missing"), new HashSet<string>(), DateTime.UtcNow));
}
