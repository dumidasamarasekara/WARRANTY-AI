using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Adjudication;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Adjudication;
using Warranty.Domain.AiOps;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Evaluation.Golden;
using Warranty.Evaluation.Metrics;

namespace Warranty.Evaluation;

/// <summary>A seeded tenant: its ID and slug (seed/tenants/{slug}/tenant.json).</summary>
internal sealed record EvaluationTenant(Guid Id, string Slug);

/// <summary>
/// Runs one golden case against the evaluation environment and reads back what the system produced:
/// reset the claim history, seed the case's earlier claims, submit the claim through the claimant channel's
/// use case, bring it to the evaluated round, run the harness for that round (as the claim job worker would)
/// and collect the run's records.
/// </summary>
internal sealed class CaseRunner(IServiceProvider services, string ownerConnectionString, string evidenceRoot, TimeProvider time)
{
    /// <summary>Principal of the evaluation's claimant-channel submissions (recorded on the trail, never a staff user).</summary>
    private const string ClaimantPrincipal = "evaluation-claimant";

    public async Task<CaseObservation> RunAsync(GoldenCase golden, EvaluationTenant tenant, CaseOutcomeKind outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(golden);
        ArgumentNullException.ThrowIfNull(tenant);
        await ResetClaimHistoryAsync(ct);

        var claimDate = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        await SeedHistoryAsync(golden, tenant, ct);
        var claimId = await SubmitAsync(golden, tenant, claimDate, ct);
        await PrepareRoundAsync(golden, tenant, claimId, ct);

        using (TenantContextScope.Begin(tenant.Id, tenant.Slug, Principals.AdjudicationService))
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAdjudicationRunner>().RunAsync(claimId, golden.Round, ct);
        }

        var observation = await ObserveAsync(golden, tenant, claimId, ct);
        return observation with { Outcome = outcome, ClaimDate = claimDate };
    }

    /// <summary>
    /// Removes every claim and everything recorded about claims (runs, reviews, trail, AI-ops rows, jobs),
    /// so golden cases cannot raise duplicate-serial or evidence-reuse signals on each other (research R19).
    /// Catalog, customers, policies and the knowledge index stay as seeded. Runs as the database owner:
    /// the app role may not delete audit rows.
    /// </summary>
    private async Task ResetClaimHistoryAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "TRUNCATE claims.claims, aiops.model_calls, aiops.tool_calls, aiops.rag_queries CASCADE", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Seeds the case's earlier claims, finalized by a reviewer at their historical times like the migration
    /// service's history seed (offsets are relative to today, the claim date).
    /// </summary>
    private async Task SeedHistoryAsync(GoldenCase golden, EvaluationTenant tenant, CancellationToken ct)
    {
        if (golden.History.Count == 0)
        {
            return;
        }

        using var tenantScope = TenantContextScope.Begin(tenant.Id, tenant.Slug, Principals.AdjudicationService);
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var claims = sp.GetRequiredService<IClaimRepository>();
        var customers = sp.GetRequiredService<ICustomerRepository>();
        var catalog = sp.GetRequiredService<ICatalogRepository>();
        var now = time.GetUtcNow();
        foreach (var history in golden.History)
        {
            var customer = await customers.FindByEmailAsync(ContactNormalizer.NormalizeEmail(history.CustomerEmail), ct)
                           ?? throw new InvalidOperationException($"{golden.CaseId}: history customer is not seeded.");
            var product = await catalog.FindProductByModelAsync(history.ModelCode, ct)
                          ?? throw new InvalidOperationException($"{golden.CaseId}: history product {history.ModelCode} is not seeded.");
            var submittedAt = now.AddDays(history.SubmittedDaysAgo);
            var finalizedAt = now.AddDays(history.FinalizedDaysAgo);
            var claim = Claim.Submit(
                Guid.CreateVersion7(), tenant.Id, ClaimReference.Normalize(history.Reference), ClaimChannel.ClaimantPortal,
                Claim.ClaimantSubmitter, customer.Id, customer.Email, customer.Phone, product.ModelCode, product.Id,
                history.SerialNumber, DateOnly.FromDateTime(now.UtcDateTime).AddDays(history.PurchasedDaysAgo),
                history.PurchasePlace, history.PurchasePrice, Enum.Parse<Region>(history.Region), history.ProblemDescription, submittedAt);
            claim.StartEvaluation(submittedAt);
            claim.EscalateToReview(submittedAt);
            if (history.Outcome == nameof(FinalOutcome.Approved))
            {
                claim.FinalizeApproved(history.Explanation, DecidedBy.Reviewer, finalizedAt);
            }
            else
            {
                claim.FinalizeRejected(history.Explanation, DecidedBy.Reviewer, finalizedAt);
            }

            claims.Add(claim);
        }

        await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
    }

    /// <summary>
    /// Submits the claim with all its evidence through the claimant channel's use case (no job runs: the worker
    /// is off). A part the case lacks is submitted as <see cref="PlaceholderEvidence"/> and removed again.
    /// </summary>
    private async Task<Guid> SubmitAsync(GoldenCase golden, EvaluationTenant tenant, DateOnly claimDate, CancellationToken ct)
    {
        using var tenantScope = TenantContextScope.Begin(tenant.Id, tenant.Slug, ClaimantPrincipal);
        await using var scope = services.CreateAsyncScope();
        var data = golden.ClaimJson(claimDate).Deserialize<ClaimSubmissionData>(JsonSerializerOptions.Web);
        var invoices = golden.InvoicePath is { } invoice ? [Upload(invoice)] : new[] { Placeholder() };
        var photos = golden.NeedsPhotoPlaceholder ? [Placeholder()] : golden.PhotoPaths.Select(Upload).ToList();

        var result = await scope.ServiceProvider.GetRequiredService<SubmitClaim>()
            .ExecuteAsync(new SubmitClaimCommand(ClaimChannel.ClaimantPortal, data, invoices, photos), ct);
        return result switch
        {
            SubmitClaimResult.Accepted accepted => await RemovePlaceholdersAsync(golden, accepted.ClaimId, scope.ServiceProvider, ct),
            SubmitClaimResult.Invalid invalid => throw new InvalidOperationException(
                $"{golden.CaseId}: submission rejected: {string.Join("; ", invalid.Errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"))}"),
            SubmitClaimResult.UnsupportedMediaType unsupported => throw new InvalidOperationException($"{golden.CaseId}: {unsupported.Detail}"),
            _ => throw new InvalidOperationException($"{golden.CaseId}: unexpected submission result {result}."),
        };
    }

    /// <summary>
    /// Deletes the evidence rows of the placeholder parts, so the claim's round 1 lacks them as the case says.
    /// Runs as the database owner (the app role may not delete evidence); the blob and the submission's
    /// <c>EvidenceStored</c> trail entry stay, and neither is read by the harness.
    /// </summary>
    private async Task<Guid> RemovePlaceholdersAsync(GoldenCase golden, Guid claimId, IServiceProvider sp, CancellationToken ct)
    {
        if (!golden.NeedsInvoicePlaceholder && !golden.NeedsPhotoPlaceholder)
        {
            return claimId;
        }

        var placeholders = (await sp.GetRequiredService<IClaimRepository>().GetEvidenceAsync(claimId, ct))
            .Where(e => e.Kind == EvidenceKind.Invoice ? golden.NeedsInvoicePlaceholder : golden.NeedsPhotoPlaceholder)
            .Select(e => e.Id)
            .ToArray();
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("DELETE FROM claims.claim_evidence WHERE claim_id = @claim AND id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("claim", claimId);
        command.Parameters.AddWithValue("ids", placeholders);
        var deleted = await command.ExecuteNonQueryAsync(ct);
        return deleted == placeholders.Length && deleted > 0
            ? claimId
            : throw new InvalidOperationException($"{golden.CaseId}: expected to remove the placeholder evidence, removed {deleted} row(s).");
    }

    /// <summary>
    /// Brings the claim to the evaluated round with the case's counters (setup.round, autoInfoRequestCount,
    /// reviewerInfoRequested; research R24) through the claim's own transitions: each earlier round ends in
    /// an automatic request (while any remain), otherwise in a reviewer request, followed by a supplement.
    /// All evidence was uploaded with the submission, so the evaluated round sees the supplement too.
    /// </summary>
    private async Task PrepareRoundAsync(GoldenCase golden, EvaluationTenant tenant, Guid claimId, CancellationToken ct)
    {
        if (golden.Round == 1 && golden.AutoInfoRequestCount == 0 && !golden.ReviewerInfoRequested)
        {
            return;
        }

        using var tenantScope = TenantContextScope.Begin(tenant.Id, tenant.Slug, Principals.AdjudicationService);
        await using var scope = services.CreateAsyncScope();
        var claim = await scope.ServiceProvider.GetRequiredService<IClaimRepository>().GetAsync(claimId, ct)
                    ?? throw new InvalidOperationException($"{golden.CaseId}: the submitted claim is not visible.");
        var now = time.GetUtcNow();
        var automatic = golden.AutoInfoRequestCount;
        var reviewer = golden.ReviewerInfoRequested;
        RequestedItem[] items = [RequestedItem.Create(RequestedItemCodes.Other, "Evaluation setup: an earlier round requested information.")];
        for (var round = 1; round < golden.Round; round++)
        {
            claim.StartEvaluation(now);
            if (automatic > 0)
            {
                claim.RequestInformation(items, DecidedBy.System, now);
                automatic--;
            }
            else if (reviewer)
            {
                claim.EscalateToReview(now);
                claim.RequestInformation(items, DecidedBy.Reviewer, now);
                reviewer = false;
            }
            else
            {
                throw new InvalidOperationException($"{golden.CaseId}: round {golden.Round} needs more earlier requests than the setup lists.");
            }

            claim.AddSupplement(now);
        }

        if (automatic > 0 || reviewer)
        {
            throw new InvalidOperationException($"{golden.CaseId}: the setup lists more requests than rounds before round {golden.Round}.");
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
    }

    private async Task<CaseObservation> ObserveAsync(GoldenCase golden, EvaluationTenant tenant, Guid claimId, CancellationToken ct)
    {
        using var tenantScope = TenantContextScope.Begin(tenant.Id, tenant.Slug, Principals.AdjudicationService);
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var claims = sp.GetRequiredService<IClaimRepository>();
        var adjudication = sp.GetRequiredService<IAdjudicationRepository>();
        var claim = await claims.GetAsync(claimId, ct) ?? throw new InvalidOperationException($"{golden.CaseId}: claim not visible.");
        var run = await adjudication.GetLatestRunAsync(claimId, ct);
        var record = run is null ? null : await adjudication.GetRunRecordAsync(run.Id, ct);
        var calls = run is null ? [] : (await sp.GetRequiredService<IAiOpsRepository>().GetForRunAsync(run.Id, ct)).ModelCalls;
        var evidence = (await claims.GetEvidenceAsync(claimId, ct)).OrderBy(e => e.Round).ThenBy(e => e.UploadedAt).ToList();

        var findings = record?.EvidenceFindings ?? [];
        var recommendation = record?.Recommendation;
        return new CaseObservation
        {
            CaseId = golden.CaseId,
            Tenant = golden.Tenant,
            Recommendation = recommendation?.Decision is { } decision ? WireName.Of(decision) : null,
            RecommendationValid = recommendation?.IsValid ?? false,
            Confidence = recommendation?.Confidence,
            Coverage = recommendation?.Coverage is { } coverage ? WireName.Of(coverage) : null,
            Disposition = record?.Guardrails?.Disposition.ToString(),
            Status = claim.Status.ToString(),
            EscalationReasons = record?.Guardrails?.Reasons.Select(WireName.Of).ToList() ?? [],
            RiskLevel = record?.Risk?.Level.ToString(),
            RiskSignals = record?.Risk?.Signals.Select(s => WireName.Of(s.Code)).Distinct().ToList() ?? [],
            RequestedItems = claim.RequestedItems.Select(i => i.Item).ToList(),
            RunFailure = run?.FailureReason,
            PolicyStepRan = record?.PolicyAssessment is not null,
            IntakeExtraction = ParseObject(record?.Intake?.ExtractionJson),
            InvoiceExtraction = ParseObject(findings.FirstOrDefault(f => f.Kind == EvidenceFindingKind.InvoiceExtraction)?.ResultJson),
            PhotoAnalyses = evidence
                .Where(e => e.Kind == EvidenceKind.Photo)
                .Select(e => ParseObject(findings.FirstOrDefault(f => f.EvidenceId == e.Id && f.Kind == EvidenceFindingKind.PhotoAnalysis)?.ResultJson))
                .ToList(),
            RetrievedClauses = record?.PolicyReferences.Select(r => new RetrievedClause(r.RefId, r.ClauseKey, r.Score)).ToList() ?? [],
            IssuedReferences = run?.ReferenceMap.Keys.ToList() ?? [],
            CitedReferences = UnsupportedReferences.FromRecommendationJson(recommendation?.RawOutputJson),
            Usage = Totals(calls),
            UsageByAgent = calls.GroupBy(c => c.Agent, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Totals(g.ToList()), StringComparer.Ordinal),
        };
    }

    private EvidenceUpload Upload(string relativePath)
    {
        var bytes = File.ReadAllBytes(Path.Combine(evidenceRoot, relativePath));
        return new EvidenceUpload(Path.GetFileName(relativePath), bytes.Length, () => new MemoryStream(bytes, writable: false));
    }

    private static EvidenceUpload Placeholder()
        => new(PlaceholderEvidence.FileName, PlaceholderEvidence.Png.Length, () => new MemoryStream(PlaceholderEvidence.Png.ToArray(), writable: false));

    private static UsageTotals Totals(IReadOnlyCollection<ModelCall> calls)
        => new(
            calls.Count,
            calls.Count(c => c.Status != ModelCallStatus.Ok),
            calls.Sum(c => (long)c.InputTokens),
            calls.Sum(c => (long)c.OutputTokens),
            calls.Sum(c => (long)c.CacheReadTokens),
            calls.Sum(c => (long)c.CacheWriteTokens),
            calls.Sum(c => c.EstimatedCost));

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
