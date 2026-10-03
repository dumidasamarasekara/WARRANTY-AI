using System.Globalization;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Warranty.Knowledge.Ingestion;

/// <summary>A knowledge source with its front matter typed (contracts/rag.md) and its Markdown body.</summary>
public sealed record KnowledgeSource(
    string SourcePath,
    string Namespace,
    DocumentType DocumentType,
    string Title,
    int Version,
    string? PolicyCode,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    IReadOnlyList<Region> Regions,
    IReadOnlyList<string> ProductCategories,
    string? ProductModel,
    DocumentClassification? Classification,
    IReadOnlyList<string> AllowedRoles,
    CoverageTerms? Terms,
    IReadOnlyDictionary<string, ClauseDeclaration> Clauses,
    string Body);

/// <summary>A clause's type from the front-matter <c>clauses</c> map; exclusions name their code (research R26).</summary>
public sealed record ClauseDeclaration(ClauseType Type, ExclusionCode? ExclusionCode);

/// <summary>
/// Reads the YAML front matter of a knowledge source (contracts/rag.md): the block between a first
/// line <c>---</c> and the next <c>---</c> line, followed by the Markdown body. Unknown keys and
/// unknown enum values are errors, so a typo cannot silently drop a filter or a clause type.
/// </summary>
public static class FrontMatterParser
{
    private const string Delimiter = "---";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Throws <see cref="InvalidKnowledgeSourceException"/> when the front matter is missing or malformed.</summary>
    public static KnowledgeSource Parse(KnowledgeSourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (yaml, body) = Split(document);

        FrontMatter matter;
        try
        {
            matter = Yaml.Deserialize<FrontMatter?>(yaml) ?? throw Invalid(document, "the front matter is empty");
        }
        catch (YamlException ex)
        {
            throw Invalid(document, $"the front matter is not valid YAML ({ex.Start.Line}:{ex.Start.Column}: {ex.InnerException?.Message ?? ex.Message})");
        }

        var errors = new List<string>();
        var source = new KnowledgeSource(
            document.SourcePath,
            Required(matter.Namespace, "namespace", errors),
            ParseEnum<DocumentType>(Required(matter.DocumentType, "documentType", errors), "documentType", errors),
            Required(matter.Title, "title", errors),
            matter.Version ?? Missing(0, "version", errors),
            Optional(matter.PolicyCode)?.ToUpperInvariant(),
            ParseDate(matter.EffectiveFrom, "effectiveFrom", errors),
            ParseDate(matter.EffectiveTo, "effectiveTo", errors),
            (matter.Regions ?? []).Select(r => ParseEnum<Region>(r, "regions", errors)).ToList(),
            (matter.ProductCategories ?? []).Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            Optional(matter.ProductModel),
            Optional(matter.Classification) is { } classification ? ParseEnum<DocumentClassification>(classification, "classification", errors) : null,
            (matter.AllowedRoles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            matter.Terms is { } terms ? ToTerms(terms, errors) : null,
            (matter.Clauses ?? []).ToDictionary(c => c.Key.Trim(), c => ToClause(c.Key, c.Value, errors), StringComparer.Ordinal),
            body);

        return errors.Count == 0 ? source : throw new InvalidKnowledgeSourceException(document.SourcePath, errors);
    }

    private static (string Yaml, string Body) Split(KnowledgeSourceDocument document)
    {
        var lines = document.Content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Delimiter)
        {
            throw Invalid(document, "it does not start with a '---' front-matter block");
        }

        var end = Array.FindIndex(lines, 1, line => line.Trim() == Delimiter);
        if (end < 0)
        {
            throw Invalid(document, "the front-matter block is not closed with '---'");
        }

        return (string.Join('\n', lines[1..end]), string.Join('\n', lines[(end + 1)..]));
    }

    private static CoverageTerms ToTerms(TermsMatter terms, List<string> errors)
    {
        var standard = new Dictionary<Region, int>();
        foreach (var (region, months) in terms.StandardCoverageMonths ?? [])
        {
            standard[ParseEnum<Region>(region, "terms.standardCoverageMonths", errors)] = months;
        }

        var accidental = terms.AccidentalDamage is { } damage
            ? new AccidentalDamageTerms(damage.Covered, damage.WindowMonths, damage.MaxIncidents)
            : AccidentalDamageTerms.NotCovered;
        return new CoverageTerms(
            standard,
            (terms.ComponentCoverageMonths ?? []).ToDictionary(c => c.Key.Trim().ToLowerInvariant(), c => c.Value, StringComparer.Ordinal),
            accidental,
            (terms.Exclusions ?? []).Select(e => ParseWire<ExclusionCode>(e, "terms.exclusions", errors)).ToList());
    }

    private static ClauseDeclaration ToClause(string key, ClauseMatter? clause, List<string> errors)
    {
        if (clause?.Type is not { Length: > 0 } type)
        {
            errors.Add($"clauses.{key} has no type.");
            return new ClauseDeclaration(ClauseType.Coverage, null);
        }

        return new ClauseDeclaration(
            ParseEnum<ClauseType>(type, $"clauses.{key}.type", errors),
            Optional(clause.ExclusionCode) is { } code ? ParseWire<ExclusionCode>(code, $"clauses.{key}.exclusionCode", errors) : null);
    }

    private static TEnum ParseEnum<TEnum>(string value, string field, List<string> errors)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        if (value.Length > 0)
        {
            errors.Add($"{field} '{value}' is not one of {string.Join(", ", Enum.GetNames<TEnum>())}.");
        }

        return default;
    }

    private static TEnum ParseWire<TEnum>(string value, string field, List<string> errors)
        where TEnum : struct, Enum
    {
        if (WireName.TryParse<TEnum>(value.Trim(), out var parsed))
        {
            return parsed;
        }

        errors.Add($"{field} '{value}' is not one of {string.Join(", ", WireName.All<TEnum>())}.");
        return default;
    }

    private static DateOnly? ParseDate(string? value, string field, List<string> errors)
    {
        if (Optional(value) is not { } text || text == "null")
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add($"{field} '{text}' is not a yyyy-MM-dd date.");
        return null;
    }

    private static string Required(string? value, string field, List<string> errors)
    {
        if (Optional(value) is { } text)
        {
            return text;
        }

        errors.Add($"{field} is required.");
        return string.Empty;
    }

    private static T Missing<T>(T fallback, string field, List<string> errors)
    {
        errors.Add($"{field} is required.");
        return fallback;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InvalidKnowledgeSourceException Invalid(KnowledgeSourceDocument document, string reason)
        => new(document.SourcePath, [reason]);

    // YAML shapes; property names follow the front matter in camelCase.
    private sealed class FrontMatter
    {
        public string? Namespace { get; set; }

        public string? DocumentType { get; set; }

        public string? PolicyCode { get; set; }

        public string? Title { get; set; }

        public int? Version { get; set; }

        public string? EffectiveFrom { get; set; }

        public string? EffectiveTo { get; set; }

        public List<string>? Regions { get; set; }

        public List<string>? ProductCategories { get; set; }

        public string? ProductModel { get; set; }

        public string? Classification { get; set; }

        public List<string>? AllowedRoles { get; set; }

        public TermsMatter? Terms { get; set; }

        public Dictionary<string, ClauseMatter?>? Clauses { get; set; }
    }

    private sealed class TermsMatter
    {
        public Dictionary<string, int>? StandardCoverageMonths { get; set; }

        public Dictionary<string, int>? ComponentCoverageMonths { get; set; }

        public AccidentalDamageMatter? AccidentalDamage { get; set; }

        public List<string>? Exclusions { get; set; }
    }

    private sealed class AccidentalDamageMatter
    {
        public bool Covered { get; set; }

        public int WindowMonths { get; set; }

        public int MaxIncidents { get; set; }
    }

    private sealed class ClauseMatter
    {
        public string? Type { get; set; }

        public string? ExclusionCode { get; set; }
    }
}

/// <summary>A knowledge source that cannot be ingested; lists every problem found.</summary>
public sealed class InvalidKnowledgeSourceException(string sourcePath, IReadOnlyList<string> errors)
    : Exception($"Knowledge source '{sourcePath}' cannot be ingested: {string.Join(" ", errors)}")
{
    public string SourcePath { get; } = sourcePath;

    public IReadOnlyList<string> Errors { get; } = errors;
}
