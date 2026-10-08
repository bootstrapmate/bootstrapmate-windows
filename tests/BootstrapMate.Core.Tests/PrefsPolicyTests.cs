using System.Xml.Linq;
using BootstrapMate.Core;
using Xunit;

namespace BootstrapMate.Core.Tests;

public class PrefsPolicyTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static HashSet<string> Managed(params string[] keys) =>
        new(keys, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(Fields))]
    public void EveryFieldLocksWhenItsOwnPolicyValueIsSet(string field)
    {
        Assert.False(PrefsPolicy.IsLocked(field, Managed()));
        Assert.True(PrefsPolicy.IsLocked(field, Managed(field)));
    }

    [Fact]
    public void NoDialogLocksTheShowDialogSwitch() =>
        Assert.True(PrefsPolicy.IsLocked("EnableDialog", Managed("NoDialog")));

    [Fact]
    public void ManagedKeysMatchCaseInsensitively() =>
        Assert.True(PrefsPolicy.IsLocked("EnableDialog", Managed("nodialog")));

    [Fact]
    public void AnotherFieldsPolicyDoesNotLockThisOne() =>
        Assert.False(PrefsPolicy.IsLocked("DialogTitle", Managed("DialogMessage", "NoDialog")));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void TheDialogShowsOnlyWhenEnabledAndNotSuppressed(bool enable, bool noDialog, bool shown) =>
        Assert.Equal(shown, PrefsPolicy.IsDialogShown(new BootstrapMateConfig { EnableDialog = enable, NoDialog = noDialog }));

    public static TheoryData<string> Fields()
    {
        var data = new TheoryData<string>();
        foreach (var field in PrefsPolicy.FieldKeys.Keys) data.Add(field);
        return data;
    }

    // ── Policy template coverage ───────────────────────────────────

    private static string ResourcesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "resources", "BootstrapMate.admx");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        }
        throw new FileNotFoundException("resources/BootstrapMate.admx not found above the test output directory");
    }

    private static XDocument Admx() => XDocument.Load(Path.Combine(ResourcesDir(), "BootstrapMate.admx"));
    private static XDocument Adml() => XDocument.Load(Path.Combine(ResourcesDir(), "en-US", "BootstrapMate.adml"));

    /// <summary>Policy value name → how the ADMX writes it: "boolean", "text" or "decimal".</summary>
    private static Dictionary<string, string> AdmxValues()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in Admx().Descendants(Ns + "policy"))
        {
            Assert.Equal(BootstrapMateConstants.PolicyRegistryPath, (string?)policy.Attribute("key"));
            if (policy.Attribute("valueName") is { } v && policy.Element(Ns + "enabledValue") is not null)
                result[v.Value] = "boolean";
            foreach (var element in policy.Element(Ns + "elements")?.Elements() ?? [])
                result[(string)element.Attribute("valueName")!] = element.Name.LocalName;
        }
        return result;
    }

    [Theory]
    [MemberData(nameof(PolicyKeys))]
    public void TheAdmxCoversEveryPolicyValueThatLocksAPrefsField(string key)
    {
        var values = AdmxValues();
        Assert.True(values.ContainsKey(key), $"ADMX has no policy for {key}");

        // The type must be one ManagementDetector reads for that setting.
        var expected = key switch
        {
            "SilentMode" or "VerboseMode" or "DryRun" or "EnableDialog" or "NoDialog" or "BlurScreen" => "boolean",
            "NetworkTimeout" => "decimal",
            _ => "text",
        };
        Assert.Equal(expected, values[key]);
    }

    public static TheoryData<string> PolicyKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in PrefsPolicy.FieldKeys.Values.SelectMany(k => k).Distinct()) data.Add(key);
        return data;
    }

    [Fact]
    public void EveryAdmxReferenceResolvesInTheAdml()
    {
        var admx = Admx();
        var adml = Adml();
        var strings = adml.Descendants(Ns + "string").Select(s => (string)s.Attribute("id")!).ToHashSet();
        var presentations = adml.Descendants(Ns + "presentation")
            .ToDictionary(p => (string)p.Attribute("id")!);
        var categories = admx.Descendants(Ns + "category").Select(c => (string)c.Attribute("name")!).ToHashSet();
        var prefixes = admx.Descendants(Ns + "target").Concat(admx.Descendants(Ns + "using"))
            .Select(e => (string)e.Attribute("prefix")!).ToHashSet();

        static string Ref(string value, string kind)
        {
            Assert.StartsWith($"$({kind}.", value);
            return value[(kind.Length + 3)..^1];
        }

        foreach (var c in admx.Descendants(Ns + "category"))
            Assert.Contains(Ref((string)c.Attribute("displayName")!, "string"), strings);

        var names = new HashSet<string>();
        foreach (var policy in admx.Descendants(Ns + "policy"))
        {
            var name = (string)policy.Attribute("name")!;
            Assert.True(names.Add(name), $"duplicate policy {name}");
            Assert.Contains(Ref((string)policy.Attribute("displayName")!, "string"), strings);
            Assert.Contains(Ref((string)policy.Attribute("explainText")!, "string"), strings);
            Assert.Contains((string)policy.Element(Ns + "parentCategory")!.Attribute("ref")!, categories);
            var supported = (string)policy.Element(Ns + "supportedOn")!.Attribute("ref")!;
            Assert.Contains(supported.Split(':')[0], prefixes);

            var elements = policy.Element(Ns + "elements")?.Elements().ToList() ?? [];
            if (elements.Count == 0) continue;

            var presentation = presentations[Ref((string)policy.Attribute("presentation")!, "presentation")];
            var refIds = presentation.Descendants().Select(e => (string?)e.Attribute("refId")).OfType<string>().ToHashSet();
            foreach (var element in elements)
                Assert.Contains((string)element.Attribute("id")!, refIds);
        }
    }
}
