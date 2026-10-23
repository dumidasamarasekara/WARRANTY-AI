namespace Warranty.Domain.Claims;

/// <summary>
/// Item codes of a <see cref="RequestedItem"/>; the values of the decision schema's
/// <c>missingInformation.item</c> enum (contracts/schemas/decision-recommendation.schema.json), plus
/// <see cref="ProtectionPlanDocument"/>, which only the deterministic intake validation requests.
/// </summary>
public static class RequestedItemCodes
{
    public const string Invoice = "INVOICE";

    public const string LegibleInvoice = "LEGIBLE_INVOICE";

    public const string PhotoOfDamage = "PHOTO_OF_DAMAGE";

    public const string PhotoOfSerialLabel = "PHOTO_OF_SERIAL_LABEL";

    public const string PurchaseDate = "PURCHASE_DATE";

    public const string ProblemDetails = "PROBLEM_DETAILS";

    public const string Other = "OTHER";

    /// <summary>Requested by intake validation only, never by the model, so it is not in the decision schema (002 research R9).</summary>
    public const string ProtectionPlanDocument = "PROTECTION_PLAN_DOCUMENT";
}
