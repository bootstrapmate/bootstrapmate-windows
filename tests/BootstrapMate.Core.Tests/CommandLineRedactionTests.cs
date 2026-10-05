using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class CommandLineRedactionTests
{
    [Fact]
    public void TheValueAfterHeadersIsHidden() =>
        Assert.Equal(["--url", "https://x/m.json", "--headers", CommandLineRedaction.Mask, "--silent"],
            CommandLineRedaction.Redact(["--url", "https://x/m.json", "--headers", "Bearer abc", "--silent"]));

    [Theory]
    [InlineData("--headers=Bearer abc", "--headers=<redacted>")]
    [InlineData("--HEADERS:Basic xyz", "--HEADERS:<redacted>")]
    public void AnAttachedValueIsHidden(string arg, string expected) =>
        Assert.Equal([expected], CommandLineRedaction.Redact([arg]));

    [Fact]
    public void AWholeCommandLineIsRedacted()
    {
        var line = CommandLineRedaction.Redact("\"C:/Program Files/BootstrapMate/managedbootstrapinstall.exe\" --headers \"Bearer secret token\" --silent");
        Assert.DoesNotContain("secret", line);
        Assert.Contains("--headers <redacted> --silent", line);
        Assert.StartsWith("\"C:/Program Files/BootstrapMate/managedbootstrapinstall.exe\"", line);
    }

    [Fact]
    public void ACommandLineWithoutSecretsIsUnchangedInSubstance() =>
        Assert.Equal("app.exe --silent --no-dialog", CommandLineRedaction.Redact("app.exe --silent --no-dialog"));

    [Theory]
    [InlineData("AuthorizationHeader")]
    [InlineData("reportingheader")]
    public void CredentialSettingsAreSecrets(string name) => Assert.True(SecretStore.IsSecret(name));

    [Fact]
    public void OtherSettingsAreNot() => Assert.False(SecretStore.IsSecret("ManifestUrl"));
}

public class LegacyRegistryTests
{
    // SOFTWARE\Policies is shared between the 32- and 64-bit views: removing its "32-bit
    // copy" deletes the live policy. Only the redirected settings key may be cleaned up.
    [Fact]
    public void ThePolicyKeyIsNeverTreatedAsAStale32BitCopy()
    {
        Assert.DoesNotContain(BootstrapMateConstants.PolicyRegistryPath, LegacyRegistry.StaleKeys);
        Assert.Equal([BootstrapMateConstants.MachineSettingsRegistryPath], LegacyRegistry.StaleKeys);
    }
}
