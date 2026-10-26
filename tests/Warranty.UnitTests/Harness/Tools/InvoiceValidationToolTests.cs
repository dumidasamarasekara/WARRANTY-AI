using System.Text.Json;
using NSubstitute;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Guardrails.Pipeline;
using Warranty.Guardrails.Rules;
using static Warranty.UnitTests.Harness.Tools.ToolTestKit;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary><c>invoice_validation</c> (Evidence): field-by-field matching through <see cref="EvidenceMatchRules"/> (R27).</summary>
public sealed class InvoiceValidationToolTests
{
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ReferenceRegistry _references = new();
    private readonly ClaimEvidence _invoice = Evidence(EvidenceKind.Invoice, "application/pdf");
    private readonly ClaimEvidence _photo = Evidence(EvidenceKind.Photo, "image/png");
    private readonly string _invoiceRef;
    private readonly string _photoRef;

    public InvoiceValidationToolTests()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(NewClaim());
        _claims.GetEvidenceItemAsync(ClaimId, _invoice.Id, Arg.Any<CancellationToken>()).Returns(_invoice);
        _claims.GetEvidenceItemAsync(ClaimId, _photo.Id, Arg.Any<CancellationToken>()).Returns(_photo);
        _invoiceRef = _references.IssueEvidence(_invoice.Id);
        _photoRef = _references.IssueEvidence(_photo.Id);
    }

    private InvoiceValidationTool Tool => new(_claims);

    [Fact]
    public void It_takes_an_evidence_reference_and_the_extracted_fields_and_is_for_the_evidence_agent()
    {
        ShouldBeStrictReadOnlyTool(Tool, "invoice_validation", AgentNames.Evidence);
        Tool.Descriptor.ReferenceArguments!["invoiceRef"].ShouldBe(ReferenceKind.Evidence);
        var extracted = Tool.Descriptor.InputSchema.GetProperty("properties").GetProperty("extracted");
        extracted.GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False);
        extracted.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["invoiceDate", "modelCode", "serial", "amount", "seller"]);
    }

    [Fact]
    public async Task Matching_fields_match_under_the_tolerances()
    {
        // Claim: SN-TAB-0001, AUR-TAB10, 2026-01-15, 449.00, "Brightline Electronics Inc."
        var result = await Validate("2026-01-15", "aur tab10", "sn.tab.0001", 452.50m, "BRIGHTLINE ELECTRONICS, LTD.");

        result.IsError.ShouldBeFalse();
        var fields = Fields(result.Content);
        fields.ShouldBe(
        [
            ("serialNumber", "SN-TAB-0001", "sn.tab.0001", "Match"),
            ("modelCode", "AUR-TAB10", "aur tab10", "Match"),
            ("purchaseDate", "2026-01-15", "2026-01-15", "Match"),
            ("purchasePrice", "449.00", "452.50", "Match"),
            ("seller", Seller, "BRIGHTLINE ELECTRONICS, LTD.", "Match"),
        ]);
        result.Content.GetProperty("invoiceRef").GetString().ShouldBe(_invoiceRef);
        result.Summary.ShouldBe($"{_invoiceRef}: 5 match, 0 mismatch, 0 not compared");
    }

    [Fact]
    public async Task Differing_fields_are_mismatches()
    {
        var result = await Validate("2026-01-16", "AUR-TAB11", "SN-TAB-0002", 460m, "Other Shop");

        Fields(result.Content).Select(f => f.Match).ShouldAllBe(m => m == "Mismatch");
        result.Summary.ShouldContain("5 mismatch (serialNumber, modelCode, purchaseDate, purchasePrice, seller)");
    }

    [Fact]
    public async Task Fields_the_invoice_does_not_show_are_not_compared_and_never_a_mismatch()
    {
        var result = await Validate("UNKNOWN", "UNKNOWN", "  ", 0m, "unknown");

        var fields = Fields(result.Content);
        fields.Select(f => f.Match).ShouldAllBe(m => m == "NotCompared");
        fields.Select(f => f.InvoiceValue).ShouldAllBe(v => v == null);
        fields.Select(f => f.ClaimValue).ShouldAllBe(v => v != null);
    }

    [Fact]
    public void Compare_is_the_shared_deterministic_rule()
    {
        var checks = InvoiceValidationTool.Compare(
            ClaimedPurchase.From(NewClaim()), new InvoiceFields(new DateOnly(2026, 1, 15), null, "SN-TAB-0009", 449m, null));

        checks.Select(c => (c.Field, c.Match)).ShouldBe(
        [
            (InvoiceFieldNames.SerialNumber, EvidenceMatch.Mismatch),
            (InvoiceFieldNames.ModelCode, EvidenceMatch.NotCompared),
            (InvoiceFieldNames.PurchaseDate, EvidenceMatch.Match),
            (InvoiceFieldNames.PurchasePrice, EvidenceMatch.Match),
            (InvoiceFieldNames.Seller, EvidenceMatch.NotCompared),
        ]);
    }

    [Theory]
    [InlineData("15.01.2026", 449)]
    [InlineData("2026-02-30", 449)]
    [InlineData("2026-01-15", -1)]
    public async Task A_malformed_date_or_negative_amount_is_an_error(string invoiceDate, decimal amount)
    {
        var result = await Validate(invoiceDate, "AUR-TAB10", Serial, amount, Seller);

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task The_reference_must_be_an_invoice_of_the_claim()
    {
        var result = await Validate("2026-01-15", "AUR-TAB10", Serial, 449m, Seller, _photoRef);

        result.IsError.ShouldBeTrue();
        result.Content.GetProperty("error").GetString().ShouldBe($"{_photoRef} is not an invoice of this claim.");
    }

    // ---- Replacement units (003 FR-029, research R11) ------------------------------------------------------

    /// <summary>A replacement unit handed out in place of <see cref="Serial"/>; the customer's invoice shows the original.</summary>
    private const string ReplacementSerial = "SN-TAB-0777";

    [Theory(Skip = "Pending T351")]
    [InlineData("SN-TAB-0001")]
    [InlineData("sn tab 0001")]
    public void The_original_serial_on_the_invoice_matches_a_replacement_unit(string invoiceSerial)
    {
        var claim = ClaimedPurchase.From(NewClaim(serial: ReplacementSerial)) with { OriginalSerial = Serial };

        var serial = InvoiceValidationTool.Compare(claim, Invoice(invoiceSerial)).First();

        serial.Field.ShouldBe(InvoiceFieldNames.SerialNumber);
        serial.Match.ShouldBe(EvidenceMatch.Match);
    }

    [Fact]
    public void The_replacement_units_own_serial_still_matches()
    {
        var claim = ClaimedPurchase.From(NewClaim(serial: ReplacementSerial)) with { OriginalSerial = Serial };

        InvoiceValidationTool.Compare(claim, Invoice(ReplacementSerial)).First().Match.ShouldBe(EvidenceMatch.Match);
    }

    [Fact]
    public void A_serial_that_is_neither_the_unit_nor_the_original_is_a_mismatch_for_a_replacement_unit()
    {
        var claim = ClaimedPurchase.From(NewClaim(serial: ReplacementSerial)) with { OriginalSerial = Serial };

        InvoiceValidationTool.Compare(claim, Invoice("SN-TAB-0009")).First().Match.ShouldBe(EvidenceMatch.Mismatch);
    }

    [Fact]
    public void Another_serial_on_the_invoice_is_a_mismatch_when_the_claimed_serial_is_not_a_replacement_unit()
    {
        // Without a registered replacement there is no accepted alternative: the original-serial rule applies to replacement units only.
        var claim = ClaimedPurchase.From(NewClaim(serial: ReplacementSerial));

        claim.OriginalSerial.ShouldBeNull();
        InvoiceValidationTool.Compare(claim, Invoice(Serial)).First().Match.ShouldBe(EvidenceMatch.Mismatch);
    }

    [Fact(Skip = "Pending T351")]
    public void The_case_of_a_replacement_unit_carries_the_original_serial_as_the_accepted_alternative()
    {
        var @case = Case() with
        {
            SerialNumber = ReplacementSerial,
            ReplacementCover = new ReplacementCover(Serial, PurchaseDate, PurchaseDate.AddMonths(24)),
        };

        var claim = ClaimedPurchase.From(@case);

        claim.SerialNumber.ShouldBe(ReplacementSerial);
        claim.OriginalSerial.ShouldBe(Serial);
        InvoiceValidationTool.Compare(claim, Invoice(Serial)).First().Match.ShouldBe(EvidenceMatch.Match);
    }

    [Fact]
    public void The_case_of_an_ordinary_claim_has_no_accepted_alternative()
        => ClaimedPurchase.From(Case()).OriginalSerial.ShouldBeNull();

    private static InvoiceFields Invoice(string serial) => new(PurchaseDate, ModelCode, serial, 449m, Seller);

    private static CaseContext Case()
        => new(
            ClaimId, 1, "WC-TEST", ClaimChannel.ClaimantPortal, new DateOnly(2026, 9, 30), PurchaseDate, Seller, 449m, "EUR", Region.EU,
            ModelCode, Serial, "The tablet battery drains within an hour.", null, new CaseCustomerView("NO", Region.EU), [],
            ClaimHistoryCounts.None, false, 0);

    private Task<Warranty.AI.Harness.Tools.ToolResult> Validate(
        string invoiceDate, string modelCode, string serial, decimal amount, string seller, string? invoiceRef = null)
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            invoiceRef = invoiceRef ?? _invoiceRef,
            extracted = new { invoiceDate, modelCode, serial, amount, seller },
        });
        return Tool.InvokeAsync(arguments, Context(AgentNames.Evidence, _references), TestContext.Current.CancellationToken);
    }

    private static List<(string Field, string? ClaimValue, string? InvoiceValue, string Match)> Fields(JsonElement content)
        => content.GetProperty("fields").EnumerateArray()
            .Select(f => (
                f.GetProperty("field").GetString()!,
                f.GetProperty("claimValue").GetString(),
                f.GetProperty("invoiceValue").GetString(),
                f.GetProperty("match").GetString()!))
            .ToList();

    private static ClaimEvidence Evidence(EvidenceKind kind, string contentType)
        => ClaimEvidence.Create(Guid.NewGuid(), Aurora, ClaimId, 1, kind, "file", contentType, 100, new string('a', 64), SubmittedAt);
}
