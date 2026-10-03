using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Prompts;

/// <summary>
/// A platform-owned system prompt (contracts/ai-gateway.md). Templates hold no tenant or case data;
/// <c>{{name}}</c> placeholders take platform values only, and case data goes in the user turn.
/// </summary>
public sealed partial record PromptTemplate(string Id, int Version, string Route, string? OutputSchema, string Body)
{
    public PromptRef Ref => new(Id, Version);

    /// <summary>Fills every placeholder; a missing or unused variable is an error, so prompts never ship half-rendered.</summary>
    public string Render(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rendered = Placeholder().Replace(Body, match =>
        {
            var name = match.Groups[1].Value;
            if (!variables.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"Prompt {Ref} needs variable '{name}'.");
            }

            used.Add(name);
            return value;
        });

        var unused = variables.Keys.Where(k => !used.Contains(k)).ToList();
        if (unused.Count > 0)
        {
            throw new InvalidOperationException($"Prompt {Ref} has no placeholder for: {string.Join(", ", unused)}.");
        }

        return rendered;
    }

    [GeneratedRegex(@"\{\{([A-Za-z0-9_]+)\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// Loads the versioned templates embedded as <c>PromptTemplates/{id}.v{n}.md</c>. Each starts with
/// front matter naming <c>id</c>, <c>version</c>, <c>route</c> and optionally <c>outputSchema</c>,
/// which must agree with the file name.
/// </summary>
public sealed partial class PromptTemplateRegistry
{
    private const string ResourceFolder = ".PromptTemplates.";

    private readonly Dictionary<PromptRef, PromptTemplate> _templates;

    public PromptTemplateRegistry(IEnumerable<(string FileName, string Content)> sources)
    {
        _templates = [];
        foreach (var (fileName, content) in sources)
        {
            var template = Parse(fileName, content);
            if (!_templates.TryAdd(template.Ref, template))
            {
                throw new InvalidOperationException($"Prompt template {template.Ref} is defined twice.");
            }
        }
    }

    public IReadOnlyCollection<PromptTemplate> Templates => _templates.Values;

    /// <summary>The templates embedded in the gateway assembly.</summary>
    public static PromptTemplateRegistry FromEmbeddedResources() => FromAssembly(typeof(PromptTemplateRegistry).Assembly);

    public static PromptTemplateRegistry FromAssembly(Assembly assembly)
    {
        var sources = new List<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var folder = name.IndexOf(ResourceFolder, StringComparison.Ordinal);
            if (folder < 0 || !name.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            sources.Add((name[(folder + ResourceFolder.Length)..], reader.ReadToEnd()));
        }

        return new PromptTemplateRegistry(sources);
    }

    public PromptTemplate Get(PromptRef prompt)
        => _templates.TryGetValue(prompt, out var template)
            ? template
            : throw new ArgumentException($"Prompt template {prompt} does not exist.", nameof(prompt));

    internal static PromptTemplate Parse(string fileName, string content)
    {
        var fileMatch = FileNamePattern().Match(fileName);
        if (!fileMatch.Success)
        {
            throw new FormatException($"Prompt template file '{fileName}' must be named {{id}}.v{{n}}.md.");
        }

        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new FormatException($"Prompt template '{fileName}' has no front matter.");
        }

        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException($"Prompt template '{fileName}' has unterminated front matter.");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text[4..end].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
            {
                throw new FormatException($"Prompt template '{fileName}' has an invalid front matter line '{line}'.");
            }
        }

        string Required(string key) => fields.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new FormatException($"Prompt template '{fileName}' front matter needs '{key}'.");

        var id = Required("id");
        var version = int.Parse(Required("version"), NumberStyles.None, CultureInfo.InvariantCulture);
        if (id != fileMatch.Groups["id"].Value || version != int.Parse(fileMatch.Groups["version"].Value, CultureInfo.InvariantCulture))
        {
            throw new FormatException($"Prompt template '{fileName}' front matter names {id}.v{version}.");
        }

        var body = text[(end + 5)..].Trim();
        if (body.Length == 0)
        {
            throw new FormatException($"Prompt template '{fileName}' is empty.");
        }

        return new PromptTemplate(id, version, Required("route"), fields.GetValueOrDefault("outputSchema"), body);
    }

    [GeneratedRegex(@"^(?<id>[a-z][a-z0-9-]*)\.v(?<version>[1-9][0-9]*)\.md$")]
    private static partial Regex FileNamePattern();
}
