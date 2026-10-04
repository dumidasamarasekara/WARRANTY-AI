namespace Warranty.Domain.Adjudication;

/// <summary>The FR-009 validation checks stored in <c>adjudication.intake_results.validation</c> (data-model.md).</summary>
public static class ValidationCheckCodes
{
    /// <summary>Product model, serial number, purchase date, place and price, and the problem description are filled in.</summary>
    public const string RequiredFields = "REQUIRED_FIELDS";

    /// <summary>At least one photo is attached.</summary>
    public const string PhotoPresent = "PHOTO_PRESENT";

    /// <summary>An invoice is attached.</summary>
    public const string InvoicePresent = "INVOICE_PRESENT";

    /// <summary>The invoice is legible; judged by the Evidence step's invoice extraction.</summary>
    public const string InvoiceLegible = "INVOICE_LEGIBLE";

    /// <summary>Every file has a supported type for its kind (types are detected from content at upload).</summary>
    public const string FileTypes = "FILE_TYPES";

    /// <summary>The purchase date is not after today.</summary>
    public const string PurchaseDateNotFuture = "PURCHASE_DATE_NOT_FUTURE";

    /// <summary>The purchase date is not after the claim date.</summary>
    public const string PurchaseDateBeforeClaim = "PURCHASE_DATE_BEFORE_CLAIM";

    /// <summary>The claim's region (NA or EU) is known from the purchase information or the customer's address.</summary>
    public const string RegionDetermined = "REGION_DETERMINED";
}
