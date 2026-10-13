using System.ComponentModel;
using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class PrefsElevationTests
{
    private static HashSet<string> Managed(params string[] keys) =>
        new(keys, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AnUnmanagedSettingIsEditableWithoutElevation() =>
        Assert.True(PrefsPolicy.CanEdit("DialogTitle", Managed()));

    [Fact]
    public void AManagedSettingIsNotEditable() =>
        Assert.False(PrefsPolicy.CanEdit("DialogTitle", Managed("DialogTitle")));

    [Fact]
    public void NoDialogPolicyLocksTheShowDialogSwitch() =>
        Assert.False(PrefsPolicy.CanEdit("EnableDialog", Managed("NoDialog")));

    [Fact]
    public void DismissingUacIsACancelNotAFailure()
    {
        Assert.True(PrefsElevation.IsElevationCancelled(new Win32Exception(1223)));
        Assert.False(PrefsElevation.IsElevationCancelled(new Win32Exception(2)));
        Assert.False(PrefsElevation.IsElevationCancelled(new InvalidOperationException()));
        Assert.False(PrefsElevation.IsElevationCancelled(null));
    }

    [Fact]
    public void SaveRunsTheSameExeElevatedAndHeadless()
    {
        var exe = @"C:\Program Files\BootstrapMate\Managed Bootstrap Install.exe";
        var pipe = "BootstrapMate-settings-1";
        var info = PrefsElevation.BuildElevatedApply(exe, pipe);

        Assert.Equal(exe, info.FileName);
        Assert.Equal("--apply-settings \"BootstrapMate-settings-1\"", info.Arguments);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\Program Files\BootstrapMate", info.WorkingDirectory);
    }

    [Fact]
    public void SaveNeedsAnExeAndAPipe()
    {
        Assert.ThrowsAny<ArgumentException>(() => PrefsElevation.BuildElevatedApply(" ", "pipe"));
        Assert.ThrowsAny<ArgumentException>(() => PrefsElevation.BuildElevatedApply("app.exe", ""));
    }
}
