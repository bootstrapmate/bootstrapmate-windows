using System.Text.Json;
using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class PreflightDecisionTests
{
    [Fact]
    public void ExitZeroSkips() => Assert.Equal(PreflightDecision.Skip, Preflight.Decide(0));

    [Fact]
    public void ExitTwoIsBaseline()
    {
        Assert.Equal(2, Preflight.BaselineExitCode);
        Assert.Equal(PreflightDecision.Baseline, Preflight.Decide(2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(1603)]
    public void OtherPositiveExitsProvision(int exitCode) =>
        Assert.Equal(PreflightDecision.Provision, Preflight.Decide(exitCode));

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeExitsFail(int exitCode) =>
        Assert.Equal(PreflightDecision.Failed, Preflight.Decide(exitCode));

    [Fact]
    public void EnvironmentVariableNameMatchesMacOS() =>
        Assert.Equal("BOOTSTRAPMATE_BASELINE_EXIT_CODE", Preflight.BaselineExitCodeVariable);

    [Fact]
    public void ItemsRunInBaselineUnlessTheyOptOut()
    {
        using var doc = JsonDocument.Parse("""
            [
              {"name": "a", "file": "a.msi", "url": "https://example.com/a.msi", "type": "msi"},
              {"name": "b", "file": "b.ps1", "url": "https://example.com/b.ps1", "type": "ps1", "baseline": false},
              {"name": "c", "file": "c.exe", "url": "https://example.com/c.exe", "type": "exe", "baseline": true}
            ]
            """);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.True(Preflight.RunsInBaseline(items[0]));
        Assert.False(Preflight.RunsInBaseline(items[1]));
        Assert.True(Preflight.RunsInBaseline(items[2]));
    }
}

public sealed class InstallLedgerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}");
    private string LedgerPath => Path.Combine(_directory, "installed.json");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void ARecordedHashIsFoundAgainCaseInsensitively()
    {
        var ledger = new InstallLedger(LedgerPath);
        Assert.False(ledger.Contains("abc123"));

        ledger.Record("ABC123", "Example");

        Assert.True(ledger.Contains("abc123"));
        Assert.True(new InstallLedger(LedgerPath).Contains("ABC123"));
        Assert.False(new InstallLedger(LedgerPath).Contains("other"));
    }

    [Fact]
    public void AnEmptyHashIsNeverRecordedOrMatched()
    {
        var ledger = new InstallLedger(LedgerPath);
        ledger.Record("", "Example");
        Assert.False(ledger.Contains(""));
        Assert.False(File.Exists(LedgerPath));
    }

    [Fact]
    public void ACorruptLedgerReadsAsEmptyAndIsReplacedOnWrite()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(LedgerPath, "not json");
        var ledger = new InstallLedger(LedgerPath);

        Assert.False(ledger.Contains("abc"));
        ledger.Record("abc", "Example");
        Assert.True(ledger.Contains("abc"));
    }

    [Fact]
    public void ComputesLowercaseSha256OfTheFile()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "payload.txt");
        File.WriteAllText(file, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            InstallLedger.ComputeSha256(file));
    }
}

public class MsiProductTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("26.5.6.1248", "26.5.6.1207", 1)]
    [InlineData("2026.05.06.1207", "2026.05.06.1248", -1)]
    [InlineData("10.0", "9.9.9", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    public void ComparesDottedVersionsNumerically(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(MsiProduct.CompareVersions(left, right)));

    [Fact]
    public void AFileThatIsNotAnMsiReadsAsNull()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "not an msi");
            Assert.Null(MsiProduct.Read(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AProductThatIsNotInstalledHasNoInstalledVersion()
    {
        var package = new MsiProduct.Info("{00000000-0000-0000-0000-000000000001}", "1.0.0",
            "{00000000-0000-0000-0000-000000000002}", "Not Installed");
        Assert.Null(MsiProduct.InstalledVersion(package));
        Assert.False(MsiProduct.IsInstalledAtOrAbove(package, out _));
    }
}
