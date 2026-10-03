namespace Warranty.Domain.Claims;

/// <summary>
/// One item requested from the submitter, e.g. <c>LEGIBLE_INVOICE</c> with the reason shown to the
/// claimant (FR-010, FR-029). Item codes follow the decision schema's <c>missingInformation.item</c>.
/// </summary>
public sealed record RequestedItem(string Item, string Reason)
{
    public static RequestedItem Create(string item, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new RequestedItem(item.Trim().ToUpperInvariant(), reason.Trim());
    }
}
