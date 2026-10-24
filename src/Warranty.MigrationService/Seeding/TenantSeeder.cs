using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Integration;
using Warranty.Domain.Tenancy;
using Warranty.Infrastructure.Audit;
using Warranty.Infrastructure.Persistence;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// Seeds one tenant from its seed folder, adding only what is missing so every start is idempotent:
/// the platform rows (tenant, channel) without a tenant session, then — inside the tenant's scope, so
/// row-level security checks every write — settings, products, serials, customers, service centers
/// and the finalized history claims, each with a minimal trail marked as seeded history.
/// </summary>
internal sealed class TenantSeeder(WarrantyDbContext db, ITenantContext tenantContext, TimeProvider time, ILogger<TenantSeeder> logger)
{
    /// <summary>Trail actor of the seeded history entries.</summary>
    public const string HistoryActor = "seeded-history";

    public async Task SeedAsync(TenantSeedSet seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var now = time.GetUtcNow();
        var tenant = seed.Tenant;

        using (NoTenantScope.Begin())
        {
            if (await db.Tenants.FindAsync([tenant.Id], ct) is null)
            {
                db.Tenants.Add(Tenant.Create(tenant.Id, tenant.Slug, tenant.DisplayName, now));
            }

            var host = TenantChannel.NormalizeHost(tenant.Channel);
            if (await db.TenantChannels.FindAsync([host], ct) is null)
            {
                db.TenantChannels.Add(TenantChannel.Create(host, tenant.Id));
            }

            await db.SaveChangesAsync(ct);
        }

        using var scope = TenantContextScope.Begin(tenant.Id, tenant.Slug, SeedingPipeline.Principal);
        await SeedTenantDataAsync(seed, ct);
        var history = await SeedHistoryAsync(seed, now, ct);
        logger.LogInformation(
            "Seeded tenant {Tenant}: {Products} products, {Serials} serials, {Customers} customers, {Centers} service centers, {History} new history claims",
            tenant.Slug, seed.Products.Count, seed.Serials.Count, seed.Customers.Count, seed.ServiceCenters.Count, history);
    }

    private async Task SeedTenantDataAsync(TenantSeedSet seed, CancellationToken ct)
    {
        var tenantId = seed.Tenant.Id;
        var settings = seed.Tenant.Settings;
        if (!await db.TenantSettings.AnyAsync(ct))
        {
            db.TenantSettings.Add(TenantSettings.Create(
                tenantId,
                settings.Currency,
                settings.AutoApprovalLimit,
                settings.MinConfidence,
                settings.AutoApproveEnabled,
                settings.AutoRejectEnabled,
                settings.AlwaysReviewCategories,
                settings.RiskHighThreshold));
        }

        var products = await db.Products.ToDictionaryAsync(p => p.ModelCode, StringComparer.Ordinal, ct);
        foreach (var product in seed.Products.Where(p => !products.ContainsKey(Product.NormalizeModelCode(p.ModelCode))))
        {
            var created = Product.Create(product.Id, tenantId, product.ModelCode, product.Name, product.Category, product.ClaimValue, settings.Currency);
            db.Products.Add(created);
            products[created.ModelCode] = created;
        }

        var serials = (await db.ProductSerials.Select(s => s.SerialNumber).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var serial in seed.Serials.Where(s => !serials.Contains(ProductSerial.NormalizeSerial(s.SerialNumber))))
        {
            db.ProductSerials.Add(ProductSerial.Create(
                tenantId, serial.SerialNumber, products[Product.NormalizeModelCode(serial.ModelCode)].Id, serial.ManufacturedOn));
        }

        var customers = (await db.Customers.Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        foreach (var customer in seed.Customers.Where(c => !customers.Contains(c.Id)))
        {
            db.Customers.Add(Customer.Create(
                customer.Id, tenantId, customer.FullName, customer.Email, customer.Country,
                customer.Phone, customer.AddressLine, customer.City, customer.PostalCode));
        }

        var centers = (await db.ServiceCenters.Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        foreach (var center in seed.ServiceCenters.Where(c => !centers.Contains(c.Id)))
        {
            db.ServiceCenters.Add(ServiceCenter.Create(
                center.Id, tenantId, center.Region, center.Name, center.Capabilities,
                center.Address, center.OpeningHours, center.Contact, center.IsActive, center.Priority));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds each missing history claim, finalized by a reviewer on <c>seeding day + finalizedDaysAgo</c>,
    /// with a <see cref="TrailStep.ClaimSubmitted"/> and a <see cref="TrailStep.ReviewerDecided"/> entry
    /// stamped at the historical times and marked <c>seededHistory</c>, all in one transaction.
    /// </summary>
    private async Task<int> SeedHistoryAsync(TenantSeedSet seed, DateTimeOffset now, CancellationToken ct)
    {
        var added = 0;
        foreach (var history in seed.HistoricalClaims)
        {
            var reference = ClaimReference.Normalize(history.Reference);
            if (await db.Claims.AnyAsync(c => c.Reference == reference, ct))
            {
                continue;
            }

            var email = ContactNormalizer.NormalizeEmail(history.CustomerEmail);
            var customer = await db.Customers.SingleAsync(c => c.Email == email, ct);
            var product = await db.Products.SingleAsync(p => p.ModelCode == Product.NormalizeModelCode(history.ModelCode), ct);
            var submittedAt = history.SubmittedAt(now);
            var finalizedAt = history.FinalizedAt(now);
            if (!string.Equals(history.Outcome, nameof(FinalOutcome.Approved), StringComparison.Ordinal)
                && !string.Equals(history.Outcome, nameof(FinalOutcome.Rejected), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"History claim {history.Reference} has outcome '{history.Outcome}'; use Approved or Rejected.");
            }

            var claim = Claim.Submit(
                history.Id, tenantContext.TenantId, reference, ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, customer.Id,
                customer.Email, customer.Phone, product.ModelCode, product.Id, history.SerialNumber, history.PurchaseDate(now),
                history.PurchasePlace, history.PurchasePrice, history.Region, history.ProblemDescription, submittedAt);
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

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(
                async token =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(token);
                    db.Claims.Add(claim);
                    AddSeededPhotoAnalysis(history, claim, submittedAt, finalizedAt);
                    var marker = new { seededHistory = true, history.Outcome };
                    await new DecisionTrailWriter(db, tenantContext, new FixedTime(submittedAt)).AppendAsync(
                        claim.Id, TrailStep.ClaimSubmitted, HistoryActor, "Seeded history: claim submitted.", marker, token);
                    await new DecisionTrailWriter(db, tenantContext, new FixedTime(finalizedAt)).AppendAsync(
                        claim.Id, TrailStep.ReviewerDecided, HistoryActor, $"Seeded history: {history.Outcome.ToLowerInvariant()} by a reviewer.", marker, token);
                    await transaction.CommitAsync(token);
                },
                ct);
            added++;
        }

        return added;
    }

    /// <summary>
    /// For a history claim with <see cref="HistoricalClaimSeed.PhotoDamageTypes"/>: a seeded photo (metadata only,
    /// no blob), a completed round-1 adjudication run and its photo analysis, so <c>claim_history_lookup</c> counts
    /// an approved claim that shows accidental damage as a used accidental-damage incident (002 quickstart R14).
    /// IDs are derived from the claim ID, so a re-seed adds nothing twice (the claim itself is skipped then).
    /// </summary>
    private void AddSeededPhotoAnalysis(HistoricalClaimSeed history, Claim claim, DateTimeOffset submittedAt, DateTimeOffset finalizedAt)
    {
        if (history.PhotoDamageTypes is not { Count: > 0 } damageTypes)
        {
            return;
        }

        var evidenceId = DerivedId(claim.Id, 1);
        var runId = DerivedId(claim.Id, 2);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(evidenceId.ToByteArray()));
        db.ClaimEvidence.Add(ClaimEvidence.Create(
            evidenceId, claim.TenantId, claim.Id, 1, EvidenceKind.Photo, "seeded-history-photo.jpg", "image/jpeg", 1, sha256, submittedAt));

        var run = AdjudicationRun.Start(runId, claim.TenantId, claim.Id, 1, $"seeded-history-{claim.Reference}", submittedAt);
        run.Complete(Disposition.HumanReview, submittedAt);
        db.AdjudicationRuns.Add(run);

        var analysis = JsonSerializer.Serialize(new
        {
            evidenceRef = "EV-1",
            showsProduct = true,
            productTypeObserved = "UNKNOWN",
            visibleSerial = "NOT_VISIBLE",
            damageObserved = true,
            damageTypes,
            consistentWithDescription = "CONSISTENT",
            imageQuality = "GOOD",
            containsInstructionsToSystem = false,
            confidence = 90,
            observations = $"Seeded history: the photo showed {string.Join(", ", damageTypes)}; finalized on {finalizedAt:yyyy-MM-dd}.",
        });
        db.EvidenceFindings.Add(EvidenceFinding.Create(
            DerivedId(claim.Id, 3), claim.TenantId, runId, evidenceId, EvidenceFindingKind.PhotoAnalysis, analysis, [], 90));
    }

    /// <summary>A stable ID for the <paramref name="index"/>-th seeded row of a history claim (its ID with the last byte replaced).</summary>
    private static Guid DerivedId(Guid claimId, byte index)
    {
        var bytes = claimId.ToByteArray();
        bytes[^1] = (byte)(0xA0 + index);
        return new Guid(bytes);
    }

    /// <summary>A clock stopped at one instant, so a seeded trail entry carries its historical time.</summary>
    private sealed class FixedTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
