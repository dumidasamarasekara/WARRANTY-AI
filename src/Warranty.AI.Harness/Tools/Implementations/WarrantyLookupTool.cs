using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.AI.Harness.Agents;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// <c>warranty_lookup</c> (Policy): the policy version that applies to the run's claim (in effect on the
/// purchase date, for the claim's region and product category — clarification Q1), its structured terms
/// for the claim's region, and the coverage window computed deterministically by
/// <see cref="CoverageWindowCalculator"/> for the claimed component. The model only names the component;
/// region, dates and product come from the claim. No version → <c>NoApplicablePolicy</c>; more than one
/// → <c>AmbiguousPolicyVersion</c>; in both cases nothing is computed (FR-014).
/// </summary>
public sealed class WarrantyLookupTool(IClaimRepository claims, ICatalogRepository catalog, IPolicyRepository policies) : ITool
{
    /// <summary>The intake extraction's component values (contracts/schemas/intake-extraction.schema.json).</summary>
    public static IReadOnlyList<string> Components { get; } = ["BATTERY", "SCREEN", "MAINBOARD", "CHARGING_PORT", "HEATING", "OTHER", "UNKNOWN"];

    private static readonly string Schema = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "component": {
              "type": "string",
              "enum": [{{string.Join(", ", Components.Select(c => $"\"{c}\""))}}],
              "description": "The failed component, as in the intake extraction. OTHER and UNKNOWN use the standard coverage period."
            }
          },
          "required": ["component"]
        }
        """;

    public ToolDescriptor Descriptor { get; } = new(
        ToolNames.WarrantyLookup,
        "Returns the warranty policy version that applies to this claim (by purchase date, region and product category), "
        + "its structured coverage terms for the claim's region, and the deterministically computed coverage end date and "
        + "coverage flags for the given component. Use these computed values; do not recompute them.",
        ToolSupport.Schema(Schema),
        ToolSideEffect.ReadOnly,
        ToolSupport.Callers(AgentNames.Policy));

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var result = await LookupAsync(ctx.ClaimId, arguments.GetProperty("component").GetString(), ct);
        var summary = result.Coverage is { } coverage
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{result.Policy?.Code} v{result.Policy?.Version}: coverage ends {coverage.CoverageEndDate:yyyy-MM-dd}, within={coverage.WithinComponentCoverage}")
            : result.Outcome.ToString();
        return ToolResult.Ok(result, summary);
    }

    /// <summary>The applicable version, terms and coverage window of a claim for <paramref name="component"/>.</summary>
    public async Task<WarrantyLookupResult> LookupAsync(Guid claimId, string? component, CancellationToken ct)
    {
        var claim = await ToolSupport.RequireClaimAsync(claims, claimId, ct);
        var componentName = string.IsNullOrWhiteSpace(component) ? "UNKNOWN" : component.Trim().ToUpperInvariant();
        var result = new WarrantyLookupResult(
            PolicyVersionOutcome.NoApplicablePolicy, claim.Region?.ToString(), claim.PurchaseDate, claim.ClaimDate, componentName, null, null, null);
        if (claim.Region is not { } region)
        {
            return result;
        }

        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var applicable = (await policies.GetVersionsAsync(ct))
            .Where(v => v.AppliesTo(claim.PurchaseDate, region, product?.Category))
            .ToList();
        if (applicable.Count != 1)
        {
            return applicable.Count == 0 ? result : result with { Outcome = PolicyVersionOutcome.AmbiguousPolicyVersion };
        }

        var version = applicable[0];
        var policy = await policies.GetPolicyAsync(version.PolicyId, ct);
        var terms = version.Terms;
        var window = CoverageWindowCalculator.Calculate(terms, region, ComponentKey(componentName), claim.PurchaseDate, claim.ClaimDate);
        if (window.Outcome != CoverageWindowOutcome.Determined)
        {
            return result;
        }

        var accidental = terms.AccidentalDamage;
        var accidentalEnd = accidental.Covered ? claim.PurchaseDate.AddMonths(accidental.WindowMonths) : (DateOnly?)null;
        return result with
        {
            Outcome = PolicyVersionOutcome.Ok,
            Policy = new WarrantyPolicyVersionInfo(policy?.Code, policy?.Title, version.Version, version.EffectiveFrom, version.EffectiveTo)
            {
                PolicyVersionId = version.Id,
            },
            Terms = new WarrantyTermsInfo(
                terms.StandardCoverageMonths[region],
                terms.ComponentCoverageMonths.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal),
                new AccidentalDamageInfo(accidental.Covered, accidental.WindowMonths, accidental.MaxIncidents),
                terms.Exclusions.Select(WireName.Of).ToList()),
            Coverage = new WarrantyCoverageInfo(
                window.CoverageEndDate!.Value,
                window.WithinStandardCoverage!.Value,
                window.WithinComponentCoverage!.Value,
                accidentalEnd,
                accidentalEnd is { } end ? claim.ClaimDate <= end : null),
        };
    }

    /// <summary><c>OTHER</c> and <c>UNKNOWN</c> are not components: they use the standard months.</summary>
    private static string? ComponentKey(string component) => component is "OTHER" or "UNKNOWN" ? null : component.ToLowerInvariant();
}

/// <summary>Result of <c>warranty_lookup</c>.</summary>
/// <param name="Outcome">Version selection outcome; the other parts are null unless it is <c>Ok</c>.</param>
/// <param name="Region">The claim's region; null when it could not be determined (then no policy applies).</param>
/// <param name="PurchaseDate">The claim's purchase date.</param>
/// <param name="ClaimDate">The claim date the window is evaluated against.</param>
/// <param name="Component">The component the window was computed for.</param>
/// <param name="Policy">The applicable version.</param>
/// <param name="Terms">Its structured terms for the claim's region.</param>
/// <param name="Coverage">The deterministic coverage window.</param>
public sealed record WarrantyLookupResult(
    [property: JsonConverter(typeof(JsonStringEnumConverter<PolicyVersionOutcome>))] PolicyVersionOutcome Outcome,
    string? Region,
    DateOnly PurchaseDate,
    DateOnly ClaimDate,
    string Component,
    WarrantyPolicyVersionInfo? Policy,
    WarrantyTermsInfo? Terms,
    WarrantyCoverageInfo? Coverage);

/// <summary>The applicable policy version, without database IDs for the model.</summary>
public sealed record WarrantyPolicyVersionInfo(string? Code, string? Title, int Version, DateOnly EffectiveFrom, DateOnly? EffectiveTo)
{
    /// <summary>For harness callers; never shown to a model.</summary>
    [JsonIgnore]
    public Guid PolicyVersionId { get; init; }
}

/// <summary>Structured terms of the version for the claim's region.</summary>
/// <param name="StandardCoverageMonths">Standard coverage in the claim's region.</param>
/// <param name="ComponentCoverageMonths">Component-specific coverage (lower-case component → months).</param>
/// <param name="AccidentalDamage">Accidental-damage allowance.</param>
/// <param name="Exclusions">Exclusion codes, e.g. <c>ACCIDENTAL_DAMAGE</c>.</param>
public sealed record WarrantyTermsInfo(
    int StandardCoverageMonths,
    IReadOnlyDictionary<string, int> ComponentCoverageMonths,
    AccidentalDamageInfo AccidentalDamage,
    IReadOnlyList<string> Exclusions);

public sealed record AccidentalDamageInfo(bool Covered, int WindowMonths, int MaxIncidents);

/// <summary>Deterministic coverage window (data-model.md "Coverage window rule").</summary>
/// <param name="CoverageEndDate">Last covered day for the component (its months, else the standard months).</param>
/// <param name="WithinStandardCoverage">Claim date within the region's standard window.</param>
/// <param name="WithinComponentCoverage">Claim date within the window that applies to the component — the deciding flag.</param>
/// <param name="AccidentalWindowEndDate">Last day of the accidental-damage allowance; null when accidental damage is not covered.</param>
/// <param name="WithinAccidentalWindow">Claim date within that allowance; null when not covered.</param>
public sealed record WarrantyCoverageInfo(
    DateOnly CoverageEndDate,
    bool WithinStandardCoverage,
    bool WithinComponentCoverage,
    DateOnly? AccidentalWindowEndDate,
    bool? WithinAccidentalWindow);
