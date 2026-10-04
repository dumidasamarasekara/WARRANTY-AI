using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Schemas;

/// <summary>Outcome of validating one structured model output; <see cref="Errors"/> is empty when valid.</summary>
/// <param name="Errors">
/// One readable message per violation. Each message names the offending property by its JSON name
/// (for example <c>confidence</c> or <c>policyRefs</c>) so it can be stored with the invalid recommendation.
/// </param>
public sealed record SchemaValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates structured agent outputs against the five output schemas of contracts/schemas (embedded
/// resources in this folder) with JsonSchema.Net, plus the deterministic rules stated in the schema
/// descriptions that the model-side schemas do not enforce (T054): <c>confidence</c> 0–100; maximum
/// lengths (intake <c>summary</c> 400, at most 8 <c>symptoms</c>, photo <c>observations</c> 600, policy
/// <c>summary</c> 800, decision <c>reasoningSummary</c> 1,500 and <c>claimantExplanation</c> 800);
/// invoice <c>invoiceDate</c> as <c>YYYY-MM-DD</c> or <c>UNKNOWN</c> and <c>currency</c> as an ISO-4217 code
/// or <c>UNKNOWN</c>; decision <c>evidenceRefs</c> not empty and at least one <c>policyRefs</c> entry for
/// <c>APPROVE</c>/<c>REJECT</c>. Whether references were issued is checked by
/// <see cref="Context.ReferenceRegistry"/>, not here.
/// </summary>
/// <remarks>
/// The schemas are loaded and built once per process; instances are stateless and thread-safe.
/// </remarks>
public sealed partial class SchemaValidator
{
    public const string IntakeExtraction = "intake-extraction";
    public const string InvoiceExtraction = "invoice-extraction";
    public const string PhotoAnalysis = "photo-analysis";
    public const string PolicyAssessment = "policy-assessment";
    public const string DecisionRecommendation = "decision-recommendation";

    private const string Unknown = "UNKNOWN";
    private const int MaxSymptoms = 8;

    /// <summary>The short IDs of every known output schema.</summary>
    public static IReadOnlyList<string> SchemaIds { get; } =
        [IntakeExtraction, InvoiceExtraction, PhotoAnalysis, PolicyAssessment, DecisionRecommendation];

    /// <summary>Each schema, keyed by both its short ID and its full <c>$id</c>.</summary>
    private static readonly Dictionary<string, Entry> Entries = LoadEntries();

    /// <summary>
    /// The schema to send with a model call. <paramref name="schemaId"/> is a short ID from
    /// <see cref="SchemaIds"/> or the schema's full <c>$id</c> (e.g. <c>warranty-ai/decision-recommendation/v1</c>);
    /// throws <see cref="ArgumentException"/> for an unknown ID.
    /// </summary>
    public AiOutputSchema GetOutputSchema(string schemaId) => Find(schemaId).OutputSchema;

    /// <summary>
    /// Validates <paramref name="output"/> against the schema and the description rules, reporting every
    /// violation (description rules are checked even when the schema already failed); never throws for
    /// invalid output (a non-object is reported as an error). Throws <see cref="ArgumentException"/> for an
    /// unknown <paramref name="schemaId"/> (same forms as <see cref="GetOutputSchema"/>).
    /// </summary>
    public SchemaValidationResult Validate(string schemaId, JsonElement output)
    {
        var entry = Find(schemaId);

        if (output.ValueKind != JsonValueKind.Object)
        {
            var kind = output.ValueKind == JsonValueKind.Undefined ? "nothing" : output.ValueKind.ToString().ToLowerInvariant();
            return new SchemaValidationResult([$": the output must be a JSON object, but was {kind}."]);
        }

        var errors = new List<string>();
        errors.AddRange(SchemaErrors(entry.Schema, output));
        errors.AddRange(RuleErrors(entry.ShortId, output));
        return new SchemaValidationResult(errors.Distinct(StringComparer.Ordinal).ToList());
    }

    private static Entry Find(string schemaId)
    {
        ArgumentNullException.ThrowIfNull(schemaId);
        return Entries.TryGetValue(schemaId, out var entry)
            ? entry
            : throw new ArgumentException($"Unknown output schema '{schemaId}'.", nameof(schemaId));
    }

    private static IEnumerable<string> SchemaErrors(JsonSchema schema, JsonElement output)
    {
        var evaluation = schema.Evaluate(output, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return evaluation.IsValid
            ? []
            : (evaluation.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .DefaultIfEmpty(": the output does not match the schema.");
    }

    // ---- Description rules -------------------------------------------------------------------------

    private static IEnumerable<string> RuleErrors(string shortId, JsonElement output) => shortId switch
    {
        IntakeExtraction => MaxLength(output, "summary", 400).Concat(MaxItems(output, "symptoms", MaxSymptoms)),
        InvoiceExtraction => InvoiceDate(output).Concat(Currency(output)),
        PhotoAnalysis => Confidence(output).Concat(MaxLength(output, "observations", 600)),
        PolicyAssessment => Confidence(output).Concat(MaxLength(output, "summary", 800)),
        DecisionRecommendation => Confidence(output)
            .Concat(MaxLength(output, "reasoningSummary", 1500))
            .Concat(MaxLength(output, "claimantExplanation", 800))
            .Concat(DecisionReferences(output)),
        _ => [],
    };

    private static IEnumerable<string> Confidence(JsonElement output)
    {
        if (output.TryGetProperty("confidence", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var confidence)
            && confidence is < 0 or > 100)
        {
            yield return $"/confidence: must be between 0 and 100, but was {value.GetRawText()}.";
        }
    }

    private static IEnumerable<string> MaxLength(JsonElement output, string property, int maxLength)
    {
        if (output.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.EnumerateRunes().Count();
            if (length > maxLength)
            {
                yield return $"/{property}: must be at most {maxLength} characters, but has {length}.";
            }
        }
    }

    private static IEnumerable<string> MaxItems(JsonElement output, string property, int maxItems)
    {
        if (output.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (count > maxItems)
            {
                yield return $"/{property}: must have at most {maxItems} items, but has {count}.";
            }
        }
    }

    private static IEnumerable<string> InvoiceDate(JsonElement output)
    {
        if (output.TryGetProperty("invoiceDate", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var valid = string.Equals(text, Unknown, StringComparison.Ordinal)
                || (IsoDate().IsMatch(text)
                    && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            if (!valid)
            {
                yield return $"/invoiceDate: must be an ISO date (YYYY-MM-DD) or '{Unknown}'.";
            }
        }
    }

    private static IEnumerable<string> Currency(JsonElement output)
    {
        if (output.TryGetProperty("currency", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (!string.Equals(text, Unknown, StringComparison.Ordinal) && !CurrencyCode().IsMatch(text))
            {
                yield return $"/currency: must be an ISO-4217 currency code (three capital letters) or '{Unknown}'.";
            }
        }
    }

    private static IEnumerable<string> DecisionReferences(JsonElement output)
    {
        if (output.TryGetProperty("evidenceRefs", out var evidence)
            && evidence.ValueKind == JsonValueKind.Array
            && evidence.GetArrayLength() == 0)
        {
            yield return "/evidenceRefs: at least one evidence reference is required.";
        }

        if (output.TryGetProperty("decision", out var decision)
            && decision.ValueKind == JsonValueKind.String
            && decision.GetString() is "APPROVE" or "REJECT"
            && (!output.TryGetProperty("policyRefs", out var policy)
                || (policy.ValueKind == JsonValueKind.Array && policy.GetArrayLength() == 0)))
        {
            yield return $"/policyRefs: at least one policy reference is required for {decision.GetString()}.";
        }
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyCode();

    // ---- Loading -----------------------------------------------------------------------------------

    private static Dictionary<string, Entry> LoadEntries()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var shortId in SchemaIds)
        {
            var entry = Load(shortId);
            entries.Add(shortId, entry);
            entries.Add(entry.OutputSchema.SchemaId, entry);
        }

        return entries;
    }

    private static Entry Load(string shortId)
    {
        var resourceName = $"{typeof(SchemaValidator).Namespace}.{shortId}.schema.json";
        using var stream = typeof(SchemaValidator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded output schema '{resourceName}' not found.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement.Clone();

        var fullId = root.TryGetProperty("$id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : throw new InvalidOperationException($"Output schema '{shortId}' has no $id.");

        // A private registry per schema keeps the relative $id values out of the global registry.
        var schema = JsonSchema.Build(root, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        return new Entry(shortId, new AiOutputSchema(fullId, root), schema);
    }

    private sealed record Entry(string ShortId, AiOutputSchema OutputSchema, JsonSchema Schema);
}
