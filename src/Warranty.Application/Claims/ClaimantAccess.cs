using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Application.Claims;

/// <summary>
/// The claimant's view of a claim (contracts/rest-api.openapi.yaml, <c>ClaimantClaimView</c>; FR-037):
/// status, product, the claimant-facing explanation of a final outcome and the items requested from
/// the claimant. It has no risk signals, fraud indicators, internal reasoning or internal IDs.
/// </summary>
public sealed record ClaimantClaimView(
    string Reference,
    ClaimStatus Status,
    DateTimeOffset SubmittedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProductName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OutcomeExplanation,
    IReadOnlyList<ClaimantRequestedItem> RequestedItems);

public sealed record ClaimantRequestedItem(string Item, string Reason);

/// <summary>Outcome of a claimant access check: the claim to issue a token for, or none.</summary>
public sealed record ClaimantAccessResult(Guid? ClaimId)
{
    public static readonly ClaimantAccessResult Denied = new((Guid?)null);

    public bool Granted => ClaimId is not null;
}

/// <summary>
/// Claimant access through the tenant's own channel (FR-037a, research R10): a claim is opened only
/// with its reference plus the email address or phone number given at submission. Contacts are
/// normalized (email trimmed and lower-cased, phone reduced to its digits) and compared in constant
/// time; an unknown reference and a non-matching contact give the same result and both record a
/// <see cref="SecurityEventKind.ClaimantAccessFailed"/> event for the channel's tenant (research R30).
/// The tenant is the one of <see cref="ITenantContext"/> (the channel Host), so a reference of
/// another tenant is simply unknown here.
/// </summary>
public sealed class ClaimantAccess(
    ITenantContext tenant, IClaimRepository claims, ICatalogRepository catalog, ISecurityEventWriter securityEvents)
{
    /// <summary>The actor recorded on claimant security events.</summary>
    public const string ClaimantChannelActor = "claimant channel";

    private const int MaxTargetLength = 64;

    public async Task<ClaimantAccessResult> VerifyAsync(string? reference, string? contact, CancellationToken ct)
    {
        RequireTenant();
        var normalizedReference = reference is null ? string.Empty : ClaimReference.Normalize(reference);
        var claim = ClaimReference.IsValid(normalizedReference) ? await claims.FindByReferenceAsync(normalizedReference, ct) : null;

        // Compare even when the claim is unknown, so both failures take the same path.
        var matches = ContactMatches(contact, claim?.ContactEmail, claim?.ContactPhone);
        if (claim is not null && matches)
        {
            return new ClaimantAccessResult(claim.Id);
        }

        await securityEvents.RecordAsync(
            SecurityEventKind.ClaimantAccessFailed,
            ClaimantChannelActor,
            Clip(reference?.Trim()),
            new { reason = claim is null ? "UnknownReference" : "ContactMismatch" },
            ct);
        return ClaimantAccessResult.Denied;
    }

    /// <summary>
    /// The claimant view of the claim behind <paramref name="reference"/>, or null when the reference
    /// is unknown in the tenant or names another claim than the claimant token's <paramref name="tokenClaimId"/>.
    /// </summary>
    public async Task<ClaimantClaimView?> GetViewAsync(Guid tokenClaimId, string reference, CancellationToken ct)
    {
        RequireTenant();
        var claim = await claims.FindByReferenceAsync(reference, ct);
        if (claim is null || claim.Id != tokenClaimId || claim.TenantId != tenant.TenantId)
        {
            return null;
        }

        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        return new ClaimantClaimView(
            claim.Reference,
            claim.Status,
            claim.CreatedAt,
            product?.Name,
            claim.Status.IsFinal() ? claim.FinalExplanation : null,
            claim.Status == ClaimStatus.PendingInformation
                ? RequestedItemCatalog.ForClaimant(claim.RequestedItems).Select(i => new ClaimantRequestedItem(i.Item, i.Reason)).ToList()
                : []);
    }

    /// <summary>
    /// Whether <paramref name="contact"/> is the submitted email (case-insensitive) or phone number
    /// (same digits). Each candidate is compared in constant time against a padded value.
    /// </summary>
    public static bool ContactMatches(string? contact, string? claimEmail, string? claimPhone)
    {
        var supplied = contact?.Trim() ?? string.Empty;
        var asEmail = supplied.Length == 0 ? string.Empty : ContactNormalizer.NormalizeEmail(supplied);
        var asPhone = PhoneDigits(supplied);

        var emailMatches = FixedTimeEquals(asEmail, claimEmail is null ? null : ContactNormalizer.NormalizeEmail(claimEmail));
        var phoneMatches = FixedTimeEquals(asPhone, PhoneDigits(claimPhone));
        return emailMatches | phoneMatches;
    }

    /// <summary>The digits of a phone number (spaces, <c>+</c>, brackets, dashes and dots dropped).</summary>
    public static string PhoneDigits(string? phone)
        => string.IsNullOrWhiteSpace(phone) ? string.Empty : new string(phone.Where(char.IsAsciiDigit).ToArray());

    private static bool FixedTimeEquals(string supplied, string? expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);

        // Compare equal-length buffers so the time does not depend on where the values differ.
        var length = Math.Max(suppliedBytes.Length, expectedBytes.Length);
        var a = new byte[length];
        var b = new byte[length];
        suppliedBytes.CopyTo(a, 0);
        expectedBytes.CopyTo(b, 0);
        var equal = CryptographicOperations.FixedTimeEquals(a, b);
        return equal & suppliedBytes.Length == expectedBytes.Length & expectedBytes.Length > 0;
    }

    private static string? Clip(string? value)
        => value is { Length: > MaxTargetLength } ? value[..MaxTargetLength] : value;

    private void RequireTenant()
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("Claimant access requires the tenant of the claimant channel.");
        }
    }
}
