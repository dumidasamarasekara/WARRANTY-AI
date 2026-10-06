using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Domain.Review;

namespace Warranty.Application.Claims;

/// <summary><c>ClaimSummary</c> (contracts/rest-api.openapi.yaml): one row of the staff claim list.</summary>
public sealed record ClaimSummary(
    Guid ClaimId,
    string Reference,
    ClaimStatus Status,
    string ProductName,
    DateTimeOffset SubmittedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AiDecision? AiDecision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Disposition? Disposition);

/// <summary><c>ClaimPage</c>: a page of the tenant's claims, newest first.</summary>
public sealed record ClaimSummaryPage(IReadOnlyList<ClaimSummary> Items, int Page, int PageSize, int Total);

/// <summary>
/// <c>ClaimDetail</c> (FR-033): the claim, its evidence, the latest evaluation and the review decisions.
/// Members a role may not see are null and left out of the JSON (FR-005).
/// </summary>
public sealed record ClaimDetail(
    Guid ClaimId,
    string Reference,
    ClaimStatus Status,
    ClaimChannel Channel,
    DateOnly ClaimDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Region? Region,
    ClaimCustomer Customer,
    ClaimProduct Product,
    ClaimPurchase Purchase,
    string ProblemDescription,
    IReadOnlyList<EvidenceItem> Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Evaluation? LatestEvaluation,
    IReadOnlyList<ReviewDecisionView> ReviewDecisions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FinalOutcome? FinalOutcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecidedBy? FinalDecidedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FinalExplanation,
    IReadOnlyList<RequestedItem> RequestedItems,
    int AutoInfoRequestCount,
    bool ReviewerInfoRequested);

public sealed record ClaimCustomer(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FullName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Country);

/// <summary>The claimed unit; name, category and claim value only when it is in the tenant's catalog (FR-003).</summary>
public sealed record ClaimProduct(
    string ModelCode,
    string SerialNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category,
    bool InCatalog,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ClaimValue);

public sealed record ClaimPurchase(DateOnly Date, string Place, decimal Price, string Currency);

/// <summary><c>EvidenceItem</c>; <c>ref</c> is the <c>EV-n</c> the latest evaluation issued for the file.</summary>
public sealed record EvidenceItem(
    Guid EvidenceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref,
    EvidenceKind Kind,
    string FileName,
    string ContentType,
    int Round);

/// <summary><c>Evaluation</c>: what the latest run recorded, with the Policy and Evidence agents' confidence.</summary>
public sealed record Evaluation(
    Guid RunId,
    int Round,
    RunStatus Status,
    IReadOnlyList<CheckResult> Validation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Extraction,
    IReadOnlyList<EvidenceFindingView> EvidenceFindings,
    IReadOnlyList<PolicyReferenceView> PolicyReferences,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PolicyAssessmentView? PolicyAssessment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RiskView? Risk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecommendationView? Recommendation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuardrailsView? Guardrails,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureReason);

/// <summary><c>CheckResult</c>: a validation or guardrail check.</summary>
public sealed record CheckResult(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stage,
    bool Passed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Expected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Actual,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

/// <summary>An Evidence Agent finding with the agent's confidence (photo analysis only).</summary>
public sealed record EvidenceFindingView(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref,
    Guid EvidenceId,
    EvidenceFindingKind Kind,
    JsonObject? Result,
    IReadOnlyList<ConsistencyCheck> Consistency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Confidence);

/// <summary><c>PolicyReference</c>: a retrieved clause with its version, dates and an excerpt of its wording.</summary>
public sealed record PolicyReferenceView(
    string Ref,
    string ClauseKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClauseTitle,
    ClauseType ClauseType,
    string DocumentTitle,
    int Version,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Excerpt,
    bool Cited);

/// <summary>The Policy Agent's reading (policy-assessment.schema.json), version outcome and confidence.</summary>
public sealed record PolicyAssessmentView(
    PolicyVersionOutcome VersionOutcome,
    JsonObject? Assessment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Confidence,
    string Model,
    string PromptVersion);

public sealed record RiskView(int Score, RiskLevel Level, RiskAssessmentStage Stage, IReadOnlyList<RiskSignalView> Signals);

public sealed record RiskSignalView(RiskSignalCode Code, RiskSignalSource Source, RiskSeverity Severity, string Detail);

/// <summary><c>Recommendation</c>; the reasoning summary is left out for claims agents (FR-005).</summary>
public sealed record RecommendationView(
    bool IsValid,
    IReadOnlyList<string> ValidationErrors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AiDecision? Decision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CoverageDetermination? Coverage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Confidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningSummary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClaimantExplanation,
    IReadOnlyList<EvidenceCitation> EvidenceRefs,
    IReadOnlyList<PolicyCitation> PolicyRefs,
    IReadOnlyList<RequestedItem> MissingInformation,
    string Model,
    string PromptVersion);

/// <summary>Guardrail result; reasons are wire codes, checks are left out for claims agents (FR-005).</summary>
public sealed record GuardrailsView(
    Disposition Disposition,
    IReadOnlyList<string> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CheckResult>? Checks);

/// <summary><c>ReviewDecision</c>; the justification is left out for claims agents (FR-005).</summary>
public sealed record ReviewDecisionView(
    Guid Id,
    ReviewDecisionKind Decision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Justification,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClaimantExplanation,
    IReadOnlyList<RequestedItem> RequestedItems,
    bool OverridesAi,
    string ReviewerName,
    DateTimeOffset DecidedAt);

/// <summary>A claim detail with its ETag (the claim's row version, quoted), required as <c>If-Match</c> for review decisions.</summary>
public sealed record ClaimDetailResult(ClaimDetail Detail, string ETag);

/// <summary>An evidence file to stream: its blob path in the tenant's container and its content type.</summary>
public sealed record EvidenceContent(string BlobPath, string ContentType);

/// <summary>
/// Staff claim queries of the current tenant (FR-033, FR-005). Every lookup goes through the
/// tenant-scoped repositories, so another tenant's claim reads as missing. A caller holding only the
/// <c>claims-agent</c> role gets the restricted projection: no risk, reasoning summary, guardrail
/// checks or reviewer justifications, and the risk-related escalation reasons collapsed into one
/// "Additional checks required"; reviewers and auditors (also when they are agents too) see everything.
/// </summary>
public sealed class ClaimQueries(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    ICustomerRepository customers,
    ITenantRepository tenants,
    IPolicyRepository policies,
    IAdjudicationRepository adjudication,
    IReviewRepository reviews)
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    /// <summary>Characters of clause wording shown as a policy reference excerpt.</summary>
    private const int ExcerptLength = 600;

    /// <summary>One page of the tenant's claims, optionally of one status.</summary>
    public async Task<ClaimSummaryPage> ListAsync(ClaimStatus? status, int page, int pageSize, CancellationToken ct)
    {
        EnsureTenant();
        var result = await claims.ListAsync(status, page, pageSize, ct);
        var outcomes = await adjudication.GetLatestOutcomesAsync(result.Items.Select(c => c.Id).ToList(), ct);

        var products = new Dictionary<Guid, Product?>();
        foreach (var productId in result.Items.Select(c => c.ProductId).OfType<Guid>().Distinct())
        {
            products[productId] = await catalog.GetProductAsync(productId, ct);
        }

        var items = result.Items
            .Select(c =>
            {
                var outcome = outcomes.GetValueOrDefault(c.Id);
                var product = c.ProductId is { } id ? products.GetValueOrDefault(id) : null;
                return new ClaimSummary(
                    c.Id, c.Reference, c.Status, product?.Name ?? c.ProductModelCode, c.CreatedAt, outcome?.AiDecision, outcome?.Disposition);
            })
            .ToList();
        return new ClaimSummaryPage(items, result.Page, result.PageSize, result.Total);
    }

    /// <summary>The claim detail with its ETag, or null when the claim is not visible in the current tenant.</summary>
    public async Task<ClaimDetailResult?> GetDetailAsync(Guid claimId, CancellationToken ct)
    {
        EnsureTenant();
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId || await claims.GetRowVersionAsync(claimId, ct) is not { } rowVersion)
        {
            return null;
        }

        var restricted = IsRestrictedToAgentView(tenant.Roles);
        var customer = await customers.GetAsync(claim.CustomerId, ct);
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var settings = await tenants.GetCurrentSettingsAsync(ct);
        var evidence = await claims.GetEvidenceAsync(claimId, ct);

        var run = await adjudication.GetLatestRunAsync(claimId, ct) is { } latest
            ? await adjudication.GetRunRecordAsync(latest.Id, ct)
            : null;
        var evidenceRefs = run is null
            ? new Dictionary<Guid, string>()
            : run.Run.ReferenceMap
                .Where(p => p.Key.StartsWith("EV-", StringComparison.Ordinal))
                .GroupBy(p => p.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key);

        var decisions = (await reviews.GetForClaimAsync(claimId, ct))
            .Select(d => new ReviewDecisionView(
                d.Id,
                d.Decision,
                restricted ? null : d.Justification,
                d.ClaimantExplanation,
                d.RequestedItems,
                d.OverridesAi,
                d.ReviewerName,
                d.DecidedAt))
            .ToList();

        var detail = new ClaimDetail(
            claim.Id,
            claim.Reference,
            claim.Status,
            claim.Channel,
            claim.ClaimDate,
            claim.Region,
            new ClaimCustomer(customer?.FullName, customer?.Email, customer?.Country),
            new ClaimProduct(claim.ProductModelCode, claim.SerialNumber, product?.Name, product?.Category, product is not null, product?.ClaimValue),
            new ClaimPurchase(claim.PurchaseDate, claim.PurchasePlace, claim.PurchasePrice, settings.Currency),
            claim.ProblemDescription,
            evidence.Select(e => new EvidenceItem(e.Id, evidenceRefs.GetValueOrDefault(e.Id), e.Kind, e.FileName, e.ContentType, e.Round)).ToList(),
            run is null ? null : await EvaluationAsync(run, evidenceRefs, restricted, ct),
            decisions,
            claim.FinalOutcome,
            claim.FinalDecidedBy,
            claim.FinalExplanation,
            claim.RequestedItems,
            claim.AutoInfoRequestCount,
            claim.ReviewerInfoRequested);

        return new ClaimDetailResult(detail, FormatETag(rowVersion));
    }

    /// <summary>The evidence file to stream, or null when the claim or the file is not visible in the current tenant.</summary>
    public async Task<EvidenceContent?> GetEvidenceContentAsync(Guid claimId, Guid evidenceId, CancellationToken ct)
    {
        EnsureTenant();
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return null;
        }

        var item = await claims.GetEvidenceItemAsync(claimId, evidenceId, ct);
        return item is null || item.TenantId != tenant.TenantId ? null : new EvidenceContent(item.BlobPath, item.ContentType);
    }

    /// <summary>The ETag for a row version: the quoted decimal value.</summary>
    public static string FormatETag(uint rowVersion) => $"\"{rowVersion.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Only claims agents without a reviewer or auditor role get the restricted view (FR-005, research R29).</summary>
    public static bool IsRestrictedToAgentView(IReadOnlySet<string> roles)
        => !roles.Contains(Principals.ClaimsReviewerRole) && !roles.Contains(Principals.AuditorRole);

    /// <summary>
    /// Guardrail reasons as wire codes; for the restricted view the risk-related ones are replaced by a
    /// single <see cref="EscalationReasonExtensions.AgentSafeLabel"/> at the position of the first one.
    /// </summary>
    public static IReadOnlyList<string> ReasonsFor(IEnumerable<EscalationReason> reasons, bool restricted)
    {
        var result = new List<string>();
        foreach (var reason in reasons)
        {
            if (restricted && reason.IsRiskRelated())
            {
                if (!result.Contains(EscalationReasonExtensions.AgentSafeLabel))
                {
                    result.Add(EscalationReasonExtensions.AgentSafeLabel);
                }
            }
            else
            {
                result.Add(WireName.Of(reason));
            }
        }

        return result;
    }

    private async Task<Evaluation> EvaluationAsync(
        RunRecord run, IReadOnlyDictionary<Guid, string> evidenceRefs, bool restricted, CancellationToken ct)
    {
        var validation = run.Intake?.Validation
            .Select(v => new CheckResult(v.Check, null, v.Passed, null, null, v.Detail))
            .ToList() ?? [];

        var findings = run.EvidenceFindings
            .Select(f => new EvidenceFindingView(
                evidenceRefs.GetValueOrDefault(f.EvidenceId), f.EvidenceId, f.Kind, ParseObject(f.ResultJson), f.Consistency, f.Confidence))
            .ToList();

        var policyAssessment = run.PolicyAssessment is { } assessment
            ? new PolicyAssessmentView(
                assessment.VersionOutcome, ParseObject(assessment.AssessmentJson), assessment.Confidence, assessment.Model, assessment.PromptVersion)
            : null;

        var risk = !restricted && run.Risk is { } r
            ? new RiskView(r.Score, r.Level, r.Stage, r.Signals.Select(s => new RiskSignalView(s.Code, s.Source, s.Severity, s.Detail)).ToList())
            : null;

        var recommendation = run.Recommendation is { } rec
            ? new RecommendationView(
                rec.IsValid,
                rec.ValidationErrors,
                rec.Decision,
                rec.Coverage,
                rec.Confidence,
                restricted || string.IsNullOrWhiteSpace(rec.ReasoningSummary) ? null : rec.ReasoningSummary,
                string.IsNullOrWhiteSpace(rec.ClaimantExplanation) ? null : rec.ClaimantExplanation,
                rec.EvidenceRefs,
                rec.PolicyRefs,
                rec.MissingInformation,
                rec.Model,
                rec.PromptVersion)
            : null;

        var guardrails = run.Guardrails is { } g
            ? new GuardrailsView(
                g.Disposition,
                ReasonsFor(g.Reasons, restricted),
                restricted
                    ? null
                    : g.Checks.Select(c => new CheckResult(WireName.Of(c.Code), c.Stage, c.Passed, c.Expected, c.Actual, c.Message)).ToList())
            : null;

        return new Evaluation(
            run.Run.Id,
            run.Run.Round,
            run.Run.Status,
            validation,
            run.Intake is { } intake ? ParseObject(intake.ExtractionJson) : null,
            findings,
            await PolicyReferencesAsync(run.PolicyReferences, ct),
            policyAssessment,
            risk,
            recommendation,
            guardrails,
            run.Run.FailureReason);
    }

    /// <summary>The retrieved clauses, cited first, each with its title and wording excerpt from the tenant's policy version.</summary>
    private async Task<IReadOnlyList<PolicyReferenceView>> PolicyReferencesAsync(IReadOnlyList<RetrievedPolicyRef> references, CancellationToken ct)
    {
        var clauses = new Dictionary<(Guid VersionId, string ClauseKey), PolicyClause>();
        foreach (var versionId in references.Select(r => r.PolicyVersionId).Distinct())
        {
            foreach (var clause in await policies.GetClausesAsync(versionId, ct))
            {
                clauses.TryAdd((versionId, clause.ClauseKey), clause);
            }
        }

        return references
            .OrderByDescending(r => r.Cited)
            .ThenBy(r => r.RefId.Length).ThenBy(r => r.RefId, StringComparer.Ordinal)
            .Select(r =>
            {
                var clause = clauses.GetValueOrDefault((r.PolicyVersionId, r.ClauseKey));
                return new PolicyReferenceView(
                    r.RefId, r.ClauseKey, clause?.Title, r.ClauseType, r.DocumentTitle, r.Version, r.EffectiveFrom, r.EffectiveTo,
                    clause is null ? null : Excerpt(clause.Text), r.Cited);
            })
            .ToList();
    }

    private static string Excerpt(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= ExcerptLength ? trimmed : string.Concat(trimmed.AsSpan(0, ExcerptLength).TrimEnd(), "…");
    }

    /// <summary>Stored JSON as an object; anything else (or unreadable JSON) gives none.</summary>
    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void EnsureTenant()
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("Claim queries require the tenant of the staff user.");
        }
    }
}
