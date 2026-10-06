using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Application;

/// <summary>Item code → claimant-friendly text for requests for information (FR-010, FR-029, T099).</summary>
public sealed class RequestedItemCatalogTests
{
    [Theory]
    [InlineData(RequestedItemCodes.Invoice, "Invoice or receipt", "Please upload the invoice or receipt for this purchase.")]
    [InlineData(RequestedItemCodes.LegibleInvoice, "Clearer copy of the invoice", "Please upload a clearer copy of the invoice so it can be read.")]
    [InlineData(RequestedItemCodes.PhotoOfDamage, "Photos of the damage", "Please upload photos that clearly show the problem with the product.")]
    [InlineData(RequestedItemCodes.PhotoOfSerialLabel, "Photo of the serial number label", "Please upload a photo of the label showing the serial number.")]
    [InlineData(RequestedItemCodes.PurchaseDate, "Purchase date", "Please confirm the date you bought the product.")]
    [InlineData(RequestedItemCodes.ProblemDetails, "More detail about the problem", "Please describe the problem in more detail.")]
    [InlineData(RequestedItemCodes.Other, "Other information", "Please send us the additional information needed to assess your claim.")]
    public void Each_item_code_maps_to_a_label_and_a_standard_request(string item, string label, string text)
    {
        RequestedItemCatalog.IsKnown(item).ShouldBeTrue();
        RequestedItemCatalog.Label(item).ShouldBe(label);
        RequestedItemCatalog.ClaimantText(item).ShouldBe(text);
        RequestedItemCatalog.Label(item.ToLowerInvariant()).ShouldBe(label, "codes match case-insensitively");
    }

    [Fact]
    public void The_catalog_covers_every_item_code_of_the_decision_schema_and_its_text_is_claimant_safe()
    {
        var codes = typeof(RequestedItemCodes).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();

        RequestedItemCatalog.All.Select(d => d.Item).ShouldBe(codes, ignoreOrder: true);
        RequestedItemCatalog.All.ShouldAllBe(d => ClaimantTextScreen.Screen(d.Label).IsSafe && ClaimantTextScreen.Screen(d.ClaimantText).IsSafe);
    }

    [Fact]
    public void An_unknown_code_is_described_as_other_information()
    {
        RequestedItemCatalog.IsKnown("WARRANTY_CARD").ShouldBeFalse();
        RequestedItemCatalog.IsKnown(null).ShouldBeFalse();
        RequestedItemCatalog.Label("WARRANTY_CARD").ShouldBe("Other information");
        RequestedItemCatalog.ClaimantText(null).ShouldBe(RequestedItemCatalog.ClaimantText(RequestedItemCodes.Other));
    }

    [Fact]
    public void A_specific_reason_that_is_safe_to_show_is_kept()
    {
        var item = RequestedItem.Create(RequestedItemCodes.Invoice, "Please add the invoice as a PDF, JPEG, PNG or WebP file.");

        RequestedItemCatalog.ForClaimant(item).ShouldBe(item);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("The photo looks reused from another claim.")]
    [InlineData("Risk is medium; please send the invoice.")]
    [InlineData("EV-1 is unreadable.")]
    [InlineData("See POL-2 for the coverage terms.")]
    public void A_blank_or_disclosing_reason_is_replaced_by_the_standard_request(string reason)
    {
        var mapped = RequestedItemCatalog.ForClaimant(new RequestedItem(RequestedItemCodes.LegibleInvoice, reason));

        mapped.ShouldBe(new RequestedItem(RequestedItemCodes.LegibleInvoice, "Please upload a clearer copy of the invoice so it can be read."));
    }

    [Fact]
    public void Codes_are_normalized_blank_codes_become_other_and_each_code_is_listed_once()
    {
        var mapped = RequestedItemCatalog.ForClaimant(
        [
            new RequestedItem(" invoice ", "Please add the invoice."),
            new RequestedItem("INVOICE", "Please add the receipt."),
            new RequestedItem("", ""),
            new RequestedItem("WARRANTY_CARD", ""),
        ]);

        mapped.ShouldBe(
        [
            new RequestedItem(RequestedItemCodes.Invoice, "Please add the invoice."),
            new RequestedItem(RequestedItemCodes.Other, RequestedItemCatalog.ClaimantText(RequestedItemCodes.Other)),
            new RequestedItem("WARRANTY_CARD", RequestedItemCatalog.ClaimantText(RequestedItemCodes.Other)),
        ]);
    }
}
