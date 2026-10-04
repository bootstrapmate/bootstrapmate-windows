using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class InstallerRoutingTests
{
    [Fact]
    public void ACimianMsiWithNoArgumentsGoesToSbinInstaller() =>
        Assert.Equal(MsiInstaller.SbinInstaller,
            InstallerRouting.Choose(true, true, "CimianAuth-2026.10.04.1953.msi", "Cimian Auth", 0));

    [Fact]
    public void ArgumentsAreMsiexecArgumentsSoTheyGoToMsiexec() =>
        Assert.Equal(MsiInstaller.Msiexec,
            InstallerRouting.Choose(true, true, "CimianAuth-2026.10.04.1953.msi", "Cimian Auth", 1));

    [Theory]
    [InlineData("SbinInstaller-x64-2026.09.04.1556.msi", "SbinInstaller (x64)")]
    [InlineData("sbin-installer-x64-2026.04.27.1630.msi", "Bootstrap tool")]
    [InlineData("tool.msi", "System Binary Installer")]
    [InlineData(@"C:\cache\SBIN_INSTALLER-arm64.msi", null)]
    public void SbinInstallerNeverInstallsItself(string file, string? name)
    {
        Assert.True(InstallerRouting.IsSbinInstallerPackage(file, name));
        Assert.Equal(MsiInstaller.Msiexec, InstallerRouting.Choose(true, true, file, name, 0));
    }

    [Fact]
    public void OtherPackagesAreNotMistakenForSbinInstaller() =>
        Assert.False(InstallerRouting.IsSbinInstallerPackage("CimianTools-x64-2026.09.18.1356.msi", "Cimian Tools (x64)"));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ThirdPartyOrNoSbinInstallerMeansMsiexec(bool cimian, bool sbinAvailable) =>
        Assert.Equal(MsiInstaller.Msiexec,
            InstallerRouting.Choose(cimian, sbinAvailable, "Any-1.0.msi", "Any", 0));
}

public class PayloadIntegrityTests
{
    private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoHashMeansNothingToCheck(string? hash) => Assert.Null(PayloadIntegrity.Check(hash, Sha));

    [Theory]
    [InlineData(Sha)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("sha256:" + Sha)]
    [InlineData("  " + Sha + "  ")]
    public void AMatchingHashPasses(string hash) => Assert.Null(PayloadIntegrity.Check(hash, Sha));

    [Fact]
    public void AMismatchFails()
    {
        var problem = PayloadIntegrity.Check(new string('0', 64), Sha);
        Assert.NotNull(problem);
        Assert.Contains("mismatch", problem);
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void AHashThatIsNotSha256IsRefused(string hash)
    {
        var problem = PayloadIntegrity.Check(hash, Sha);
        Assert.NotNull(problem);
        Assert.Contains("not a SHA-256", problem);
    }
}

public class ConfigManagerTests
{
    [Fact]
    public void TheSingletonInitializesAndListsTheRetiredSettings()
    {
        // Regression: Instance was constructed before RetiredSettingNames was assigned,
        // so the type initializer threw on every run.
        var config = ConfigManager.Instance;
        Assert.NotNull(config.Config);
        Assert.Equal(new[] { "FollowRedirects", "Reboot" }, ConfigManager.RetiredSettingNames);
    }
}
