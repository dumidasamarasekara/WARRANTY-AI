using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// Checks the <c>remedies</c> front-matter block of a policy source against the embedded
/// <c>remedy-rules.schema.json</c> (T202, a copy of contracts/remedy-rules.schema.json). The YAML block
/// is converted to JSON first: plain scalars that read as a boolean, an integer or <c>null</c> become
/// that JSON type, every other scalar a string.
/// </summary>
internal static class RemedyRulesSchema
{
    private const string ResourceName = "Warranty.MigrationService.Seeding.Schemas.remedy-rules.schema.json";
    private const string Delimiter = "---";
    private const string BlockKey = "remedies";

    private static readonly Lazy<JsonSchema> Schema = new(Load);

    /// <summary>
    /// The schema violations of the source's <c>remedies</c> block, each prefixed with its location;
    /// empty when the block is valid or absent (or the source has no readable front matter, which
    /// the ingestion parser reports).
    /// </summary>
    public static IReadOnlyList<string> Validate(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (FindBlock(content) is not { } block)
        {
            return [];
        }

        var instance = ToJson(block);
        var evaluation = Schema.Value.Evaluate(
            JsonSerializer.SerializeToElement(instance), new EvaluationOptions { OutputFormat = OutputFormat.List });
        return evaluation.IsValid
            ? []
            : (evaluation.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{BlockKey}{d.InstanceLocation}: {e.Value}"))
                .Distinct(StringComparer.Ordinal)
                .DefaultIfEmpty($"{BlockKey}: the block does not match the remedy rules schema")
                .ToList();
    }

    private static YamlNode? FindBlock(string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Delimiter)
        {
            return null;
        }

        var end = Array.FindIndex(lines, 1, line => line.Trim() == Delimiter);
        if (end < 0)
        {
            return null;
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(string.Join('\n', lines[1..end])));
        }
        catch (YamlException)
        {
            return null;
        }

        return stream.Documents.Count > 0
            && stream.Documents[0].RootNode is YamlMappingNode root
            && root.Children.TryGetValue(new YamlScalarNode(BlockKey), out var block)
                ? block
                : null;
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(
            pair => KeyValuePair.Create(((YamlScalarNode)pair.Key).Value ?? string.Empty, ToJson(pair.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => null,
    };

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(text);
        }

        return text switch
        {
            "" or "~" or "null" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
            _ => JsonValue.Create(text),
        };
    }

    private static JsonSchema Load()
    {
        using var stream = typeof(RemedyRulesSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded schema '{ResourceName}' not found.");
        using var document = JsonDocument.Parse(stream);

        // A private registry keeps the schema's $id out of the global registry.
        return JsonSchema.Build(document.RootElement.Clone(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    }
}
