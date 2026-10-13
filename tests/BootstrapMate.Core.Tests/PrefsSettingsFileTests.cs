using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class PrefsSettingsFileTests
{
    private static HashSet<string> Managed(params string[] keys) =>
        new(keys, StringComparer.OrdinalIgnoreCase);

    private static PrefsSettingsPayload Sample() => new()
    {
        ManifestUrl = "https://example.com/bootstrap.json",
        AuthorizationHeader = "Bearer abc",
        SilentMode = true,
        VerboseMode = true,
        DryRun = false,
        EnableDialog = false,
        DialogTitle = "Title",
        DialogMessage = "Message",
        DialogIcon = @"C:\icon.png",
        BlurScreen = true,
        CustomInstallPath = @"D:\BootstrapMate",
        NetworkTimeout = 120,
    };

    [Fact]
    public void ThePayloadRoundTripsThroughJson()
    {
        var original = Sample();
        var copy = PrefsSettingsFile.Deserialize(PrefsSettingsFile.Serialize(original));

        Assert.NotNull(copy);
        Assert.Equivalent(original, copy, strict: true);
    }

    [Fact]
    public void ThePayloadRoundTripsThroughATempFile()
    {
        var path = PrefsSettingsFile.WriteTempFile(Sample());
        try
        {
            Assert.StartsWith(Path.GetTempPath(), path);
            Assert.Equivalent(Sample(), PrefsSettingsFile.Deserialize(File.ReadAllText(path)), strict: true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithNoPolicyEveryPageKeyIsWritten() =>
        Assert.Equal(
            ["ManifestUrl", "AuthorizationHeader", "SilentMode", "VerboseMode", "DryRun", "EnableDialog", "NoDialog",
             "DialogTitle", "DialogMessage", "DialogIcon", "BlurScreen", "CustomInstallPath", "NetworkTimeout"],
            PrefsSettingsFile.WritableKeys(Sample(), Managed()));

    [Fact]
    public void TheWriterSkipsPolicyManagedKeys()
    {
        var keys = PrefsSettingsFile.WritableKeys(Sample(), Managed("ManifestUrl", "networktimeout"));
        Assert.DoesNotContain("ManifestUrl", keys);
        Assert.DoesNotContain("NetworkTimeout", keys);
        Assert.Contains("DialogTitle", keys);
    }

    [Fact]
    public void ANoDialogPolicySkipsBothDialogSwitchKeys()
    {
        var keys = PrefsSettingsFile.WritableKeys(Sample(), Managed("NoDialog"));
        Assert.DoesNotContain("EnableDialog", keys);
        Assert.DoesNotContain("NoDialog", keys);
    }

    [Fact]
    public void ABlankHeaderKeepsTheSavedOne()
    {
        var payload = Sample();
        payload.AuthorizationHeader = "";
        Assert.DoesNotContain("AuthorizationHeader", PrefsSettingsFile.WritableKeys(payload, Managed()));
    }

    [Fact]
    public void TheDialogSwitchIsStoredAsBothKeys()
    {
        var config = PrefsSettingsFile.ToConfig(Sample());
        Assert.False(config.EnableDialog);
        Assert.True(config.NoDialog);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(600, true)]
    [InlineData(9, false)]
    [InlineData(601, false)]
    public void TimeoutFollowsTheGuiRange(int timeout, bool valid)
    {
        var payload = Sample();
        payload.NetworkTimeout = timeout;
        Assert.Equal(valid, PrefsSettingsFile.Validate(payload) is null);
    }

    [Fact]
    public void TheApplySwitchCarriesItsPath()
    {
        string[] args = ["--apply-settings", @"C:\t\s.json"];
        Assert.True(PrefsSettingsFile.IsApplyRequest(args));
        Assert.Equal(@"C:\t\s.json", PrefsSettingsFile.ApplyPath(args));
    }

    [Fact]
    public void TheApplySwitchWithoutAPathHasNoPath()
    {
        string[] args = ["--apply-settings"];
        Assert.True(PrefsSettingsFile.IsApplyRequest(args));
        Assert.Null(PrefsSettingsFile.ApplyPath(args));
    }

    [Fact]
    public void ANormalLaunchIsNotAnApplyRequest() =>
        Assert.False(PrefsSettingsFile.IsApplyRequest(["--prefs"]));

    [Fact]
    public void AMissingFileIsAUsageError() =>
        Assert.Equal(PrefsSettingsFile.ExitUsage,
            PrefsSettingsFile.Apply(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));

    [Fact]
    public void AnInvalidPayloadIsRejectedBeforeWriting()
    {
        var payload = Sample();
        payload.NetworkTimeout = 5;
        var path = PrefsSettingsFile.WriteTempFile(payload);
        try
        {
            Assert.Equal(PrefsSettingsFile.ExitInvalid, PrefsSettingsFile.Apply(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
