using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class PrefsSettingsWriterTests
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
        var copy = PrefsSettingsWriter.Deserialize(PrefsSettingsWriter.Serialize(original));

        Assert.NotNull(copy);
        Assert.Equivalent(original, copy, strict: true);
    }

    [Fact]
    public async Task ThePayloadRoundTripsThroughTheNamedPipe()
    {
        var name = PrefsSettingsWriter.NewPipeName();
        using var pipe = PrefsSettingsWriter.CreatePipe(name);
        var send = PrefsSettingsWriter.SendAsync(pipe, Sample(), CancellationToken.None);

        var json = await Task.Run(() => PrefsSettingsWriter.Receive(name, TimeSpan.FromSeconds(10)));
        await send;

        Assert.Equivalent(Sample(), PrefsSettingsWriter.Deserialize(json), strict: true);
    }

    [Fact]
    public void EachSaveGetsItsOwnPipeName() =>
        Assert.NotEqual(PrefsSettingsWriter.NewPipeName(), PrefsSettingsWriter.NewPipeName());

    [Fact]
    public void WithNoPolicyEveryPageKeyIsWritten() =>
        Assert.Equal(
            ["ManifestUrl", "AuthorizationHeader", "SilentMode", "VerboseMode", "DryRun", "EnableDialog", "NoDialog",
             "DialogTitle", "DialogMessage", "DialogIcon", "BlurScreen", "CustomInstallPath", "NetworkTimeout"],
            PrefsSettingsWriter.WritableKeys(Sample(), Managed()));

    [Fact]
    public void TheWriterSkipsPolicyManagedKeys()
    {
        var keys = PrefsSettingsWriter.WritableKeys(Sample(), Managed("ManifestUrl", "networktimeout"));
        Assert.DoesNotContain("ManifestUrl", keys);
        Assert.DoesNotContain("NetworkTimeout", keys);
        Assert.Contains("DialogTitle", keys);
    }

    [Fact]
    public void ANoDialogPolicySkipsBothDialogSwitchKeys()
    {
        var keys = PrefsSettingsWriter.WritableKeys(Sample(), Managed("NoDialog"));
        Assert.DoesNotContain("EnableDialog", keys);
        Assert.DoesNotContain("NoDialog", keys);
    }

    [Fact]
    public void ABlankHeaderKeepsTheSavedOne()
    {
        var payload = Sample();
        payload.AuthorizationHeader = "";
        Assert.DoesNotContain("AuthorizationHeader", PrefsSettingsWriter.WritableKeys(payload, Managed()));
    }

    [Fact]
    public void TheDialogSwitchIsStoredAsBothKeys()
    {
        var config = PrefsSettingsWriter.ToConfig(Sample());
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
        Assert.Equal(valid, PrefsSettingsWriter.Validate(payload) is null);
    }

    [Fact]
    public void TheApplySwitchCarriesItsPath()
    {
        string[] args = ["--apply-settings", "BootstrapMate-settings-abc"];
        Assert.True(PrefsSettingsWriter.IsApplyRequest(args));
        Assert.Equal("BootstrapMate-settings-abc", PrefsSettingsWriter.ApplyPipeName(args));
    }

    [Fact]
    public void TheApplySwitchWithoutAPipeHasNoPipe()
    {
        string[] args = ["--apply-settings"];
        Assert.True(PrefsSettingsWriter.IsApplyRequest(args));
        Assert.Null(PrefsSettingsWriter.ApplyPipeName(args));
    }

    [Fact]
    public void ANormalLaunchIsNotAnApplyRequest() =>
        Assert.False(PrefsSettingsWriter.IsApplyRequest(["--prefs"]));

    [Fact]
    public void NoPipeIsAUsageError() =>
        Assert.Equal(PrefsSettingsWriter.ExitUsage,
            PrefsSettingsWriter.Apply(PrefsSettingsWriter.NewPipeName(), TimeSpan.FromMilliseconds(200)));

    [Fact]
    public async Task AnInvalidPayloadIsRejectedBeforeWriting()
    {
        var payload = Sample();
        payload.NetworkTimeout = 5;
        var name = PrefsSettingsWriter.NewPipeName();
        using var pipe = PrefsSettingsWriter.CreatePipe(name);
        var send = PrefsSettingsWriter.SendAsync(pipe, payload, CancellationToken.None);

        var code = await Task.Run(() => PrefsSettingsWriter.Apply(name, TimeSpan.FromSeconds(10)));
        await send;

        Assert.Equal(PrefsSettingsWriter.ExitInvalid, code);
    }
}
