using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions;
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
            db.ServiceCenters.Add(ServiceCenter.Create(center.Id, tenantId, center.Region, center.Name, center.Capabilities));
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

    /// <summary>A clock stopped at one instant, so a seeded trail entry carries its historical time.</summary>
    private sealed class FixedTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
