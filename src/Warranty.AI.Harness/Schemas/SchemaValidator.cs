using System.Text.Json;
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
public sealed class SchemaValidator
{
    public const string IntakeExtraction = "intake-extraction";
    public const string InvoiceExtraction = "invoice-extraction";
    public const string PhotoAnalysis = "photo-analysis";
    public const string PolicyAssessment = "policy-assessment";
    public const string DecisionRecommendation = "decision-recommendation";

    /// <summary>The short IDs of every known output schema.</summary>
    public static IReadOnlyList<string> SchemaIds { get; } =
        [IntakeExtraction, InvoiceExtraction, PhotoAnalysis, PolicyAssessment, DecisionRecommendation];

    /// <summary>
    /// The schema to send with a model call. <paramref name="schemaId"/> is a short ID from
    /// <see cref="SchemaIds"/> or the schema's full <c>$id</c> (e.g. <c>warranty-ai/decision-recommendation/v1</c>);
    /// throws <see cref="ArgumentException"/> for an unknown ID.
    /// </summary>
    public AiOutputSchema GetOutputSchema(string schemaId)
        => throw new NotImplementedException("Pending T054.");

    /// <summary>
    /// Validates <paramref name="output"/> against the schema and the description rules, reporting every
    /// violation (description rules are checked even when the schema already failed); never throws for
    /// invalid output (a non-object is reported as an error). Throws <see cref="ArgumentException"/> for an
    /// unknown <paramref name="schemaId"/> (same forms as <see cref="GetOutputSchema"/>).
    /// </summary>
    public SchemaValidationResult Validate(string schemaId, JsonElement output)
        => throw new NotImplementedException("Pending T054.");
}
