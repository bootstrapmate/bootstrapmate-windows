using System.Runtime.InteropServices;
using System.Text;

namespace BootstrapMate.Core;

/// <summary>
/// Reads an MSI's identity and tells whether that product is already installed
/// at its version or newer, through Windows Installer's own registration.
/// </summary>
public static class MsiProduct
{
    public sealed record Info(string ProductCode, string ProductVersion, string? UpgradeCode, string? ProductName);

    /// <summary>
    /// Reads ProductCode, ProductVersion, UpgradeCode and ProductName from the
    /// package's Property table. Returns null when the file cannot be opened as an MSI.
    /// </summary>
    public static Info? Read(string msiPath)
    {
        if (MsiOpenDatabase(msiPath, IntPtr.Zero, out var database) != 0) return null;
        try
        {
            var code = ReadProperty(database, "ProductCode");
            var version = ReadProperty(database, "ProductVersion");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(version)) return null;
            return new Info(code, version, ReadProperty(database, "UpgradeCode"), ReadProperty(database, "ProductName"));
        }
        finally
        {
            MsiCloseHandle(database);
        }
    }

    /// <summary>
    /// The highest installed version of this product, found by its ProductCode or,
    /// for a product that has since been upgraded to a new ProductCode, by its
    /// UpgradeCode. Null when nothing related is installed.
    /// </summary>
    public static string? InstalledVersion(Info package)
    {
        var versions = new List<string>();
        if (GetProductVersion(package.ProductCode) is { } direct) versions.Add(direct);

        if (!string.IsNullOrEmpty(package.UpgradeCode))
        {
            var related = new StringBuilder(39);
            for (uint index = 0; MsiEnumRelatedProducts(package.UpgradeCode, 0, index, related) == 0; index++)
            {
                if (GetProductVersion(related.ToString()) is { } version) versions.Add(version);
                related.Clear();
            }
        }

        return versions.Count == 0 ? null : versions.Aggregate((a, b) => CompareVersions(a, b) >= 0 ? a : b);
    }

    /// <summary>True when the package's product is installed at its version or newer.</summary>
    public static bool IsInstalledAtOrAbove(Info package, out string? installedVersion)
    {
        installedVersion = InstalledVersion(package);
        return installedVersion is not null && CompareVersions(installedVersion, package.ProductVersion) >= 0;
    }

    /// <summary>
    /// Compares dotted numeric versions field by field; a missing field counts as 0
    /// and a non-numeric field compares as 0, so "1.2" equals "1.2.0".
    /// </summary>
    public static int CompareVersions(string left, string right)
    {
        var a = left.Trim().Split('.');
        var b = right.Trim().Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && long.TryParse(a[i], out var px) ? px : 0;
            var y = i < b.Length && long.TryParse(b[i], out var py) ? py : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static string? GetProductVersion(string productCode)
    {
        uint length = 64;
        var buffer = new StringBuilder((int)length);
        return MsiGetProductInfo(productCode, "VersionString", buffer, ref length) == 0
            ? buffer.ToString()
            : null;
    }

    private static string? ReadProperty(IntPtr database, string property)
    {
        var query = $"SELECT `Value` FROM `Property` WHERE `Property`='{property}'";
        if (MsiDatabaseOpenView(database, query, out var view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0) return null;
            if (MsiViewFetch(view, out var record) != 0) return null;
            try
            {
                uint length = 256;
                var buffer = new StringBuilder((int)length);
                return MsiRecordGetString(record, 1, buffer, ref length) == 0 ? buffer.ToString() : null;
            }
            finally
            {
                MsiCloseHandle(record);
            }
        }
        finally
        {
            MsiCloseHandle(view);
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string databasePath, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);
}
