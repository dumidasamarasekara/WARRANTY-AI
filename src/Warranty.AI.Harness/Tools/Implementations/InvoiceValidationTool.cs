using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// <c>invoice_validation</c> (Evidence): compares the fields the model extracted from an invoice with the
/// claim, field by field, through <see cref="EvidenceMatchRules"/> (FR-016, research R27): serial and
/// model code normalized exact, purchase date exact, price within max(1%, 1.00), seller after removing
/// case, punctuation and legal suffixes. A field the invoice does not show (<c>UNKNOWN</c>, blank, or an
/// amount of 0, as in the invoice-extraction schema) is <c>NotCompared</c> — never a mismatch. The
/// invoice is named by its <c>EV-n</c> reference, which must be an invoice of the run's claim.
/// </summary>
public sealed class InvoiceValidationTool(IClaimRepository claims) : ITool
{
    /// <summary>The extraction's placeholder for a value the document does not show.</summary>
    public const string Unknown = "UNKNOWN";

    private const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "invoiceRef": { "type": "string", "description": "EV-n reference of the invoice." },
            "extracted": {
              "type": "object",
              "additionalProperties": false,
              "description": "Values read from the invoice; UNKNOWN (amount 0) for values the invoice does not show.",
              "properties": {
                "invoiceDate": { "type": "string", "description": "Invoice date as YYYY-MM-DD, or UNKNOWN." },
                "modelCode": { "type": "string", "description": "Model code on the invoice, or UNKNOWN." },
                "serial": { "type": "string", "description": "Serial number on the invoice, or UNKNOWN." },
                "amount": { "type": "number", "description": "Amount for the claimed product; 0 when unknown." },
                "seller": { "type": "string", "description": "Seller name, or UNKNOWN." }
              },
              "required": ["invoiceDate", "modelCode", "serial", "amount", "seller"]
            }
          },
          "required": ["invoiceRef", "extracted"]
        }
        """;

    public ToolDescriptor Descriptor { get; } = new(
        ToolNames.InvoiceValidation,
        "Compares the values extracted from an invoice with the claim form, field by field (serial number, model code, "
        + "purchase date, purchase price, seller), using the platform's matching tolerances. Each field is Match, Mismatch, "
        + "or NotCompared when the invoice does not show it.",
        ToolSupport.Schema(Schema),
        ToolSideEffect.ReadOnly,
        ToolSupport.Callers(AgentNames.Evidence),
        new Dictionary<string, ReferenceKind> { ["invoiceRef"] = ReferenceKind.Evidence });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var invoiceRef = arguments.GetProperty("invoiceRef").GetString()!;
        if (!TryReadInvoice(arguments.GetProperty("extracted"), out var invoice, out var error))
        {
            return ToolResult.Error(error);
        }

        var evidence = await claims.GetEvidenceItemAsync(ctx.ClaimId, ctx.References.Resolve(invoiceRef, ReferenceKind.Evidence).TargetId, ct);
        if (evidence is not { Kind: EvidenceKind.Invoice })
        {
            return ToolResult.Error($"{invoiceRef} is not an invoice of this claim.");
        }

        var claim = await ToolSupport.RequireClaimAsync(claims, ctx.ClaimId, ct);
        var fields = Compare(ClaimedPurchase.From(claim), invoice);
        var result = new InvoiceValidationResult(invoiceRef, fields);
        return ToolResult.Ok(result, Summarize(invoiceRef, fields));
    }

    /// <summary>Field-by-field comparison of the claimed purchase with the invoice, in a fixed field order.</summary>
    public static IReadOnlyList<InvoiceFieldCheck> Compare(ClaimedPurchase claim, InvoiceFields invoice)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(invoice);
        return
        [
            new(InvoiceFieldNames.SerialNumber, claim.SerialNumber, invoice.Serial, EvidenceMatchRules.IdentifiersMatch(claim.SerialNumber, invoice.Serial)),
            new(InvoiceFieldNames.ModelCode, claim.ModelCode, invoice.ModelCode, EvidenceMatchRules.IdentifiersMatch(claim.ModelCode, invoice.ModelCode)),
            new(
                InvoiceFieldNames.PurchaseDate,
                Format(claim.PurchaseDate),
                invoice.InvoiceDate is { } date ? Format(date) : null,
                EvidenceMatchRules.DatesMatch(claim.PurchaseDate, invoice.InvoiceDate)),
            new(
                InvoiceFieldNames.PurchasePrice,
                Format(claim.PurchasePrice),
                invoice.Amount is { } amount ? Format(amount) : null,
                EvidenceMatchRules.PriceMatches(claim.PurchasePrice, invoice.Amount)),
            new(InvoiceFieldNames.Seller, claim.Seller, invoice.Seller, EvidenceMatchRules.SellersMatch(claim.Seller, invoice.Seller)),
        ];
    }

    /// <summary>
    /// Reads the <c>extracted</c> argument: <c>UNKNOWN</c> or blank text and an amount of 0 become null
    /// (not shown on the invoice). A date that is neither <c>YYYY-MM-DD</c> nor <c>UNKNOWN</c>, or a negative
    /// amount, is an argument error.
    /// </summary>
    public static bool TryReadInvoice(JsonElement extracted, out InvoiceFields invoice, out string error)
    {
        invoice = new InvoiceFields(null, null, null, null, null);
        var dateText = Shown(extracted.GetProperty("invoiceDate").GetString());
        DateOnly? date = null;
        if (dateText is not null)
        {
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                error = "extracted.invoiceDate must be a date as YYYY-MM-DD or UNKNOWN.";
                return false;
            }

            date = parsed;
        }

        var amount = extracted.GetProperty("amount").GetDecimal();
        if (amount < 0)
        {
            error = "extracted.amount must not be negative; use 0 when the invoice does not show it.";
            return false;
        }

        invoice = new InvoiceFields(
            date,
            Shown(extracted.GetProperty("modelCode").GetString()),
            Shown(extracted.GetProperty("serial").GetString()),
            amount == 0 ? null : amount,
            Shown(extracted.GetProperty("seller").GetString()));
        error = string.Empty;
        return true;
    }

    private static string? Shown(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), Unknown, StringComparison.OrdinalIgnoreCase) ? null : value.Trim();

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Summarize(string invoiceRef, IReadOnlyList<InvoiceFieldCheck> fields)
    {
        var mismatches = fields.Where(f => f.Match == EvidenceMatch.Mismatch).Select(f => f.Field).ToList();
        var mismatchList = mismatches.Count > 0 ? $" ({string.Join(", ", mismatches)})" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{invoiceRef}: {fields.Count(f => f.Match == EvidenceMatch.Match)} match, {mismatches.Count} mismatch{mismatchList}, "
            + $"{fields.Count(f => f.Match == EvidenceMatch.NotCompared)} not compared");
    }
}

/// <summary>Field names of the invoice checks; the same names as the evidence consistency checks.</summary>
public static class InvoiceFieldNames
{
    public const string SerialNumber = "serialNumber";

    public const string ModelCode = "modelCode";

    public const string PurchaseDate = "purchaseDate";

    public const string PurchasePrice = "purchasePrice";

    public const string Seller = "seller";
}

/// <summary>What the claim states about the purchase, as compared with an invoice.</summary>
public sealed record ClaimedPurchase(string SerialNumber, string ModelCode, DateOnly PurchaseDate, decimal PurchasePrice, string Seller)
{
    public static ClaimedPurchase From(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new(claim.SerialNumber, claim.ProductModelCode, claim.PurchaseDate, claim.PurchasePrice, claim.PurchasePlace);
    }

    public static ClaimedPurchase From(CaseContext @case)
    {
        ArgumentNullException.ThrowIfNull(@case);
        return new(@case.SerialNumber, @case.ProductModelCode, @case.PurchaseDate, @case.PurchasePrice, @case.PurchasePlace);
    }
}

/// <summary>Invoice values; null means the invoice does not show the value.</summary>
public sealed record InvoiceFields(DateOnly? InvoiceDate, string? ModelCode, string? Serial, decimal? Amount, string? Seller);

/// <summary>One field of <c>invoice_validation</c>.</summary>
public sealed record InvoiceFieldCheck(
    string Field,
    string? ClaimValue,
    string? InvoiceValue,
    [property: JsonConverter(typeof(JsonStringEnumConverter<EvidenceMatch>))] EvidenceMatch Match);

/// <summary>Result of <c>invoice_validation</c>.</summary>
public sealed record InvoiceValidationResult(string InvoiceRef, IReadOnlyList<InvoiceFieldCheck> Fields);
