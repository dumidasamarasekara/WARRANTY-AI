using System.Text.Json;
using Warranty.Domain.Adjudication;

namespace Warranty.AI.Harness.Agents;

/// <summary>
/// Typed view of the Intake Agent's structured output (contracts/schemas/intake-extraction.schema.json),
/// as stored in <see cref="IntakeResult.ExtractionJson"/>. Later steps read it from there: the Policy
/// agent's <c>warranty_lookup</c> takes <see cref="Component"/>, and the risk capability reads
/// <see cref="ContainsInstructionsToSystem"/>.
/// </summary>
public sealed record IntakeExtraction(
    string ProblemCategory,
    string Component,
    IReadOnlyList<string> Symptoms,
    string ClaimedCause,
    bool MentionsAccident,
    bool MentionsLiquid,
    bool ContainsInstructionsToSystem,
    string Summary)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The extraction of an intake result; null when the model step did not produce one (stored as <c>{}</c>).</summary>
    public static IntakeExtraction? From(IntakeResult intake)
    {
        ArgumentNullException.ThrowIfNull(intake);
        return Parse(intake.ExtractionJson);
    }

    /// <summary>Parses an extraction document; null when it is empty or not a complete extraction.</summary>
    public static IntakeExtraction? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var extraction = JsonSerializer.Deserialize<IntakeExtraction>(json, Json);
            return extraction is { ProblemCategory: not null, Component: not null, ClaimedCause: not null, Summary: not null, Symptoms: not null }
                ? extraction
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
