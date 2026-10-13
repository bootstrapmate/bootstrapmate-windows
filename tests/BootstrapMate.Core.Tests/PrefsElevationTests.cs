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
        var file = @"C:\Users\someone\AppData\Local\Temp\BootstrapMate-settings-1.json";
        var info = PrefsElevation.BuildElevatedApply(exe, file);

        Assert.Equal(exe, info.FileName);
        Assert.Equal($"--apply-settings \"{file}\"", info.Arguments);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\Program Files\BootstrapMate", info.WorkingDirectory);
    }

    [Fact]
    public void SaveNeedsAnExeAndAFile()
    {
        Assert.ThrowsAny<ArgumentException>(() => PrefsElevation.BuildElevatedApply(" ", "x.json"));
        Assert.ThrowsAny<ArgumentException>(() => PrefsElevation.BuildElevatedApply("app.exe", ""));
    }
}
