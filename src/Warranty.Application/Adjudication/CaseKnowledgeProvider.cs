using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Pipeline;

namespace Warranty.Application.Adjudication;

/// <summary>
/// Assembles the <see cref="CaseContext"/> of one claim round for the current tenant (contracts/rag.md,
/// case knowledge). The customer appears only as country and region; the claimant's free text (problem
/// description, purchase place, evidence file names) carries placeholders instead of the customer's
/// name, email, phone and street address (FR-006a, research R28). Evidence covers every round up to the
/// requested one, and history counts come from the claim repository (research R25).
/// <para>
/// A claimed serial handed out as a replacement unit carries its <see cref="ReplacementCover"/> (003 FR-029, research
/// R11), and its accidental-damage count includes the approved accidental-damage claims of the serials it replaced,
/// along the whole chain, so a replacement never resets the incident allowance. A claimed serial that was itself
/// returned as faulty and replaced is flagged in the history counts (<see cref="ClaimHistoryCounts.ReturnedAndReplaced"/>, FR-030).
/// </para>
/// </summary>
public sealed class CaseKnowledgeProvider(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    ICustomerRepository customers,
    ITenantRepository tenants,
    IReplacementSerialRegistry replacementSerials) : ICaseKnowledgeProvider
{
    public async Task<CaseContext> GetCaseContextAsync(Guid claimId, int round, CancellationToken ct)
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("Case knowledge requires a resolved tenant.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);

        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException($"Claim {claimId} was not found for the current tenant.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(round, claim.CurrentRound);

        var customer = await customers.GetAsync(claim.CustomerId, ct)
                       ?? throw new InvalidOperationException($"The customer of claim {claimId} was not found.");
        var scrubber = CustomerIdentifierScrubber.For(customer, claim.ContactEmail, claim.ContactPhone);

        var evidence = (await claims.GetEvidenceAsync(claimId, ct))
            .Where(e => e.Round <= round)
            .OrderBy(e => e.Round)
            .ThenBy(e => e.UploadedAt)
            .ToList();
        var history = await claims.GetHistoryCountsAsync(
            claim.Id, claim.SerialNumber, claim.ClaimDate, evidence.Select(e => e.Sha256).Distinct().ToList(), ct);
        var registration = await replacementSerials.FindBySerialAsync(claim.SerialNumber, ct);
        history = history with
        {
            PriorApprovedAccidental = history.PriorApprovedAccidental + await ReplacedSerialsAccidentalAsync(claim, registration, ct),
            ReturnedAndReplaced = (await replacementSerials.FindByOriginalSerialAsync(claim.SerialNumber, ct)).Count > 0,
        };
        var settings = await tenants.GetCurrentSettingsAsync(ct);

        return new CaseContext(
            claim.Id,
            round,
            claim.Reference,
            claim.Channel,
            claim.ClaimDate,
            claim.PurchaseDate,
            scrubber.Scrub(claim.PurchasePlace),
            claim.PurchasePrice,
            settings.Currency,
            claim.Region,
            claim.ProductModelCode,
            claim.SerialNumber,
            scrubber.Scrub(claim.ProblemDescription),
            await FindProductAsync(claim, ct),
            new CaseCustomerView(customer.Country, customer.Region),
            evidence.Select(e => Describe(e, scrubber)).ToList(),
            history,
            claim.ReviewerInfoRequested,
            claim.AutoInfoRequestCount,
            claim.HasProtectionPlan,
            registration is null
                ? null
                : new ReplacementCover(registration.OriginalSerial, registration.OriginalPurchaseDate, registration.CoverEndDate));
    }

    /// <summary>
    /// The approved accidental-damage claims of the serials the claimed unit replaced: its original serial, that
    /// serial's original if it was a replacement too, and so on (003 research R11). Zero when the unit is no replacement.
    /// </summary>
    private async Task<int> ReplacedSerialsAccidentalAsync(Claim claim, RegisteredReplacementSerial? registration, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { ProductSerial.NormalizeSerial(claim.SerialNumber) };
        var count = 0;
        while (registration is not null && visited.Add(registration.OriginalSerial))
        {
            count += (await claims.GetHistoryCountsAsync(claim.Id, registration.OriginalSerial, claim.ClaimDate, [], ct)).PriorApprovedAccidental;
            registration = await replacementSerials.FindBySerialAsync(registration.OriginalSerial, ct);
        }

        return count;
    }

    /// <summary>The catalog product, only when the claimed serial is registered to it (FR-003).</summary>
    private async Task<CaseProduct?> FindProductAsync(Claim claim, CancellationToken ct)
    {
        if (claim.ProductId is not { } productId)
        {
            return null;
        }

        var serial = await catalog.FindSerialAsync(claim.SerialNumber, ct);
        if (serial is null || serial.ProductId != productId)
        {
            return null;
        }

        return await catalog.GetProductAsync(productId, ct) is { } product ? Describe(product) : null;
    }

    private static CaseProduct Describe(Product product)
        => new(product.Id, product.ModelCode, product.Name, product.Category, product.ClaimValue);

    private static CaseEvidence Describe(ClaimEvidence evidence, CustomerIdentifierScrubber scrubber)
        => new(
            evidence.Id,
            evidence.Kind,
            scrubber.Scrub(evidence.FileName),
            evidence.ContentType,
            evidence.SizeBytes,
            evidence.Sha256,
            evidence.Round,
            evidence.BlobPath);
}
