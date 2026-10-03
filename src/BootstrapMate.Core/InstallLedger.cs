using System.Security.Cryptography;
using System.Text.Json;

namespace BootstrapMate.Core;

/// <summary>
/// Remembers which package files BootstrapMate has installed, by SHA-256 hash.
/// </summary>
/// <remarks>
/// A baseline run repeats on a machine in use, so it must not reinstall a package
/// it already put there. The MSI product check covers packages whose ProductCode or
/// UpgradeCode is registered; it cannot cover a script or an EXE, which leave no
/// product registration, or an MSI that does not register the version it carries.
/// The ledger covers those: the same file, by hash, is installed once.
/// </remarks>
public sealed class InstallLedger
{
    public static readonly string DefaultPath =
        Path.Combine(@"C:\ProgramData\ManagedBootstrap", "installed.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string FilePath { get; }

    public InstallLedger(string? path = null)
    {
        FilePath = path ?? DefaultPath;
    }

    public bool Contains(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return false;
        return Load().ContainsKey(hash.ToLowerInvariant());
    }

    public void Record(string hash, string name, DateTime? installed = null)
    {
        if (string.IsNullOrWhiteSpace(hash)) return;
        var entries = Load();
        entries[hash.ToLowerInvariant()] = new LedgerEntry
        {
            Name = name,
            Installed = (installed ?? DateTime.Now).ToString("yyyy-MM-ddTHH:mm:sszzz")
        };

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var sorted = new SortedDictionary<string, LedgerEntry>(entries, StringComparer.Ordinal);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(sorted, WriteOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // The ledger saves a reinstall; failing to write it must never fail an install.
        }
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private Dictionary<string, LedgerEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, LedgerEntry>>(File.ReadAllText(FilePath))
                ?? new();
        }
        catch
        {
            return new();
        }
    }

    public sealed class LedgerEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("installed")]
        public string Installed { get; set; } = "";
    }
}
