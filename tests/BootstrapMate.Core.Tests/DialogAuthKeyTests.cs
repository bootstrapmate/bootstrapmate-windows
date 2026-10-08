using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class DialogAuthKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bm-authkey-" + Guid.NewGuid().ToString("N"));

    public DialogAuthKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string? NoEnvironment(string _) => null;

    [Fact]
    public void CallerEnvironmentWins()
    {
        var file = Path.Combine(_dir, "authkey");
        File.WriteAllText(file, "from-file");
        Assert.Equal("from-env", DialogAuthKey.Resolve(file, _ => " from-env "));
    }

    [Fact]
    public void FileIsReadAndTrimmed()
    {
        var file = Path.Combine(_dir, "authkey");
        File.WriteAllText(file, "  secret-key\r\n");
        Assert.Equal("secret-key", DialogAuthKey.Resolve(file, NoEnvironment));
    }

    [Fact]
    public void MissingFileMeansNoKey() =>
        Assert.Null(DialogAuthKey.Resolve(Path.Combine(_dir, "absent"), NoEnvironment));

    [Fact]
    public void EmptyFileMeansNoKey()
    {
        var file = Path.Combine(_dir, "authkey");
        File.WriteAllText(file, "\r\n");
        Assert.Null(DialogAuthKey.Resolve(file, NoEnvironment));
    }
}
