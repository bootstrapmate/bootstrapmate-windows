using System.Security.Principal;
using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class DataDirectoryGuardTests
{
    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")]
    public void SystemAdministratorsAndTrustedInstallerAreTrusted(string sid) =>
        Assert.True(DataDirectoryGuard.IsTrustedOwner(new SecurityIdentifier(sid)));

    [Theory]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-21-3246027979-1167926813-4013428807-1013")]
    [InlineData("S-1-12-1-3542060892-1225955714-2633365921-1831687713")]
    public void UsersAndPeopleAreNot(string sid) =>
        Assert.False(DataDirectoryGuard.IsTrustedOwner(new SecurityIdentifier(sid)));

    [Fact]
    public void NoOwnerIsNotTrusted() => Assert.False(DataDirectoryGuard.IsTrustedOwner(null));
}
