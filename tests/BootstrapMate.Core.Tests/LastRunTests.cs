using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class LastRunFormatTests
{
    private static LastRunRecord Record(string status = "completed", params LastRunItem[] items) => new()
    {
        SessionId = "2026-10-04-030001",
        RunType = RunTypes.Baseline,
        Status = status,
        ToolVersion = "2026.10.04.1200",
        StartTime = "2026-10-04T03:00:01.120-07:00",
        EndTime = "2026-10-04T03:04:12.480-07:00",
        Items = items.ToList()
    };

    private static LastRunItem Item(string name, string result, string? error = null) =>
        new() { Name = name, Stage = "setupassistant", Result = result, Error = error };

    [Fact]
    public void NoRecordSaysSo() => Assert.Equal("no run recorded", LastRunFile.FormatLine(null));

    [Fact]
    public void CleanRunCountsItems()
    {
        var line = LastRunFile.FormatLine(Record("completed",
            Item("A", ItemResults.Installed), Item("B", ItemResults.Skipped), Item("C", ItemResults.Skipped)));
        Assert.Equal("2026-10-04T03:04-07:00 baseline completed v2026.10.04.1200 installed=1 skipped=2 failed=0", line);
    }

    [Fact]
    public void FailuresAreListedWithTheirErrors()
    {
        var line = LastRunFile.FormatLine(Record("partial_failure",
            Item("A", ItemResults.Failed, "Download stalled"), Item("B", ItemResults.Failed, "exit code 1603")));
        Assert.EndsWith("installed=0 skipped=0 failed=2: A: Download stalled; B: exit code 1603", line);
    }

    [Fact]
    public void ARunningRecordUsesItsStartTime()
    {
        var record = Record("running");
        record.EndTime = null;
        Assert.StartsWith("2026-10-04T03:00-07:00 baseline running ", LastRunFile.FormatLine(record));
    }

    [Fact]
    public void TheLineNeverExceedsTheLimitOrBreaks()
    {
        var items = Enumerable.Range(0, 50)
            .Select(i => Item($"Package {i}", ItemResults.Failed, new string('x', 150) + "\nsecond line"))
            .ToArray();
        var line = LastRunFile.FormatLine(Record("partial_failure", items));
        Assert.Equal(LastRunFile.MaxLineLength, line.Length);
        Assert.EndsWith("...", line);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData(PreflightDecision.Skip, "skip")]
    [InlineData(PreflightDecision.Baseline, "baseline")]
    [InlineData(PreflightDecision.Provision, "provisioning")]
    [InlineData(PreflightDecision.Failed, "provisioning")]
    public void RunTypeFollowsThePreflight(PreflightDecision decision, string expected) =>
        Assert.Equal(expected, RunTypes.For(decision));

    [Fact]
    public void ShortErrorKeepsTheFirstLineAndCapsIt()
    {
        Assert.Equal("first", LastRunFile.ShortError("first\r\nsecond"));
        var capped = LastRunFile.ShortError(new string('e', 500));
        Assert.Equal(LastRunFile.MaxErrorLength, capped.Length);
        Assert.Equal("", LastRunFile.ShortError(null));
    }
}

public sealed class LastRunFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lastrun-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(_directory, "last-run.json");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void WritesAndReadsBackWithTheContractKeys()
    {
        var record = new LastRunRecord
        {
            SessionId = "s", RunType = "baseline", Status = "completed", ToolVersion = "1",
            StartTime = "2026-10-04T03:00:01.000-07:00",
            Items = { new LastRunItem { Name = "A", Stage = "setupassistant", Result = "installed" } }
        };
        LastRunFile.Write(record, FilePath);

        var json = File.ReadAllText(FilePath);
        foreach (var key in new[] { "session_id", "run_type", "status", "tool_version", "start_time",
                     "errors", "warnings", "items", "name", "stage", "result" })
        {
            Assert.Contains($"\"{key}\"", json);
        }
        Assert.DoesNotContain("\"error\"", json);
        Assert.False(File.Exists(FilePath + ".tmp"));

        var back = LastRunFile.Read(FilePath);
        Assert.NotNull(back);
        Assert.Equal("baseline", back!.RunType);
        Assert.Single(back.Items);
    }

    [Fact]
    public void AMissingOrCorruptFileReadsAsNull()
    {
        Assert.Null(LastRunFile.Read(FilePath));
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Null(LastRunFile.Read(FilePath));
    }
}
