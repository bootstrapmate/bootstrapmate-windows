using System;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BootstrapMate;

/// <summary>
/// Parses manifest content from either JSON or YAML format.
/// YAML manifests are automatically converted to JSON for the existing processing pipeline.
/// </summary>
internal static class ManifestParser
{
    /// <summary>
    /// Detects format from URL extension or content sniffing, then returns a parsed JsonDocument.
    /// </summary>
    public static JsonDocument Parse(string content, string url)
    {
        if (IsYaml(content, url))
        {
            Logger.Debug("Detected YAML manifest format — converting to JSON");
            var json = ConvertYamlToJson(content);
            return JsonDocument.Parse(json);
        }

        return JsonDocument.Parse(content);
    }

    private static bool IsYaml(string content, string url)
    {
        // Check URL extension first (strip query string)
        var path = url.Split('?', '#')[0];
        if (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            return true;

        // Content sniffing: if it doesn't start with { or [, it's likely YAML
        var trimmed = content.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] != '{' && trimmed[0] != '[')
            return true;

        return false;
    }

    private static string ConvertYamlToJson(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yamlObject = deserializer.Deserialize<object>(yamlContent);

        return JsonSerializer.Serialize(yamlObject, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }
}
