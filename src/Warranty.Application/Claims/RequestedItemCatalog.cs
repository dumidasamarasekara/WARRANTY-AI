using System.Collections.Frozen;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Rules;

namespace Warranty.Application.Claims;

/// <summary>One requestable item: its code, a short claimant-facing label and the standard request text.</summary>
public sealed record RequestedItemDescription(string Item, string Label, string ClaimantText);

/// <summary>
/// Claimant-friendly text for the item codes of a request for information (FR-010, FR-029; codes in
/// <see cref="RequestedItemCodes"/>). Intake and evidence analysis write specific reasons ("Please add the
/// invoice as a PDF, JPEG, PNG or WebP file."), but a reason can also come from the model's
/// <c>missingInformation</c>, so before a reason reaches the claimant it must be non-empty and pass the
/// <see cref="ClaimantTextScreen"/>; otherwise the item's standard text is used. The labels match the
/// reviewer's requested-item picker in the SPA.
/// </summary>
public static class RequestedItemCatalog
{
    private static readonly RequestedItemDescription OtherItem = new(
        RequestedItemCodes.Other, "Other information", "Please send us the additional information needed to assess your claim.");

    private static readonly FrozenDictionary<string, RequestedItemDescription> Items = new RequestedItemDescription[]
    {
        new(RequestedItemCodes.Invoice, "Invoice or receipt", "Please upload the invoice or receipt for this purchase."),
        new(RequestedItemCodes.LegibleInvoice, "Clearer copy of the invoice", "Please upload a clearer copy of the invoice so it can be read."),
        new(RequestedItemCodes.PhotoOfDamage, "Photos of the damage", "Please upload photos that clearly show the problem with the product."),
        new(RequestedItemCodes.PhotoOfSerialLabel, "Photo of the serial number label", "Please upload a photo of the label showing the serial number."),
        new(RequestedItemCodes.PurchaseDate, "Purchase date", "Please confirm the date you bought the product."),
        new(RequestedItemCodes.ProblemDetails, "More detail about the problem", "Please describe the problem in more detail."),
        OtherItem,
    }.ToFrozenDictionary(item => item.Item, StringComparer.Ordinal);

    /// <summary>Every known item, in <see cref="RequestedItemCodes"/> order.</summary>
    public static IReadOnlyList<RequestedItemDescription> All { get; } =
    [
        .. new[]
        {
            RequestedItemCodes.Invoice, RequestedItemCodes.LegibleInvoice, RequestedItemCodes.PhotoOfDamage, RequestedItemCodes.PhotoOfSerialLabel,
            RequestedItemCodes.PurchaseDate, RequestedItemCodes.ProblemDetails, RequestedItemCodes.Other,
        }.Select(code => Items[code]),
    ];

    /// <summary>Whether <paramref name="item"/> is a known item code (case-insensitive).</summary>
    public static bool IsKnown(string? item) => item is not null && Items.ContainsKey(Normalize(item));

    /// <summary>The item's description; an unknown code is described as <see cref="RequestedItemCodes.Other"/>.</summary>
    public static RequestedItemDescription Describe(string? item)
        => item is not null && Items.TryGetValue(Normalize(item), out var description) ? description : OtherItem;

    /// <summary>The short claimant-facing label of an item code ("Invoice or receipt").</summary>
    public static string Label(string? item) => Describe(item).Label;

    /// <summary>The standard claimant-facing request text of an item code.</summary>
    public static string ClaimantText(string? item) => Describe(item).ClaimantText;

    /// <summary>
    /// The item as the claimant sees it: the same code with its specific reason when that reason is safe to show,
    /// otherwise with the item's standard text (for an unknown code, the text of <see cref="RequestedItemCodes.Other"/>).
    /// A blank code becomes <see cref="RequestedItemCodes.Other"/>.
    /// </summary>
    public static RequestedItem ForClaimant(RequestedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var code = string.IsNullOrWhiteSpace(item.Item) ? RequestedItemCodes.Other : Normalize(item.Item);
        return new RequestedItem(code, IsShowable(item.Reason) ? item.Reason.Trim() : ClaimantText(code));
    }

    /// <summary><see cref="ForClaimant(RequestedItem)"/> for each item, keeping the first occurrence of each code.</summary>
    public static IReadOnlyList<RequestedItem> ForClaimant(IEnumerable<RequestedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RequestedItem>();
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            var mapped = ForClaimant(item);
            if (seen.Add(mapped.Item))
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    private static bool IsShowable(string? reason) => !string.IsNullOrWhiteSpace(reason) && ClaimantTextScreen.Screen(reason).IsSafe;

    private static string Normalize(string item) => item.Trim().ToUpperInvariant();
}
