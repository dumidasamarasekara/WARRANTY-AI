using NSubstitute;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Tenancy;
using Warranty.Guardrails.Pipeline;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Application;

/// <summary>Case knowledge for one claim round: redacted customer, evidence up to the round, catalog product and history (T059).</summary>
public sealed class CaseKnowledgeProviderTests
{
    // Synthetic identifiers only.
    private const string Name = "Ottoline Brackwater-Fenn";
    private const string Email = "Ottoline.Brackwater@synthetic-mail.test";
    private const string Phone = "+47 912 34 567";
    private const string Street = "Storgata 17B";
    private const string ModelCode = "AUR-TAB10";
    private const string Serial = "SN-TAB-0042";

    private static readonly Guid TenantId = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly Guid ClaimId = Guid.Parse("0199b000-0000-7000-8000-0000000000c1");
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-0000000000d1");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IReplacementSerialRegistry _replacementSerials = Substitute.For<IReplacementSerialRegistry>();
    private readonly Product _product = Product.Create(ProductId, TenantId, ModelCode, "Aurora Tab 10", "tablet", 349m, "NOK");

    public CaseKnowledgeProviderTests()
    {
        _customers.GetAsync(CustomerId, Arg.Any<CancellationToken>())
            .Returns(Customer.Create(CustomerId, TenantId, Name, Email, "se", Phone, Street, "Lund", "22100"));
        _tenants.GetCurrentSettingsAsync(Arg.Any<CancellationToken>()).Returns(TenantSettings.Create(TenantId, "NOK", 5000m, 80));
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>()).Returns(_product);
        _catalog.FindSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(TenantId, Serial, ProductId));
        _claims.GetEvidenceAsync(ClaimId, Arg.Any<CancellationToken>()).Returns([]);
        _claims.GetHistoryCountsAsync(default, default!, default, default!, default).ReturnsForAnyArgs(ClaimHistoryCounts.None);
        _replacementSerials.FindBySerialAsync(default!, default).ReturnsForAnyArgs((RegisteredReplacementSerial?)null);
        _replacementSerials.FindByOriginalSerialAsync(default!, default).ReturnsForAnyArgs([]);
    }

    [Theory]
    [InlineData("I am Ottoline Brackwater-Fenn and the tablet will not charge.", "I am [CUSTOMER] and the tablet will not charge.")]
    [InlineData("Ms BRACKWATER here: the tablet will not charge.", "Ms [CUSTOMER] here: the tablet will not charge.")]
    [InlineData("Fenn speaking; the tablet will not charge at all.", "[CUSTOMER] speaking; the tablet will not charge at all.")]
    [InlineData("Write to ottoline.brackwater@SYNTHETIC-mail.test please, it will not charge.", "Write to [EMAIL] please, it will not charge.")]
    [InlineData("Call +4791234567 — the tablet will not charge.", "Call [PHONE] — the tablet will not charge.")]
    [InlineData("Call 0047 912 34 567 — the tablet will not charge.", "Call [PHONE] — the tablet will not charge.")]
    [InlineData("Call (912) 34-567, the tablet will not charge.", "Call [PHONE], the tablet will not charge.")]
    [InlineData("Ship it to storgata 17b, the tablet will not charge.", "Ship it to [ADDRESS], the tablet will not charge.")]
    [InlineData("Ship it to Storgata, the tablet will not charge.", "Ship it to [ADDRESS], the tablet will not charge.")]
    public async Task Known_customer_identifiers_in_the_description_become_placeholders(string description, string expected)
    {
        Arrange(description);

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.ProblemDescription.ShouldBe(expected);
    }

    [Fact]
    public async Task Text_that_only_resembles_an_identifier_is_kept()
    {
        const string description = "Fennel tea spilled? No. Serial SN-TAB-0042 bought 2026-03-14 for 349.00, ref 91234567890123 stays.";
        Arrange(description);

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.ProblemDescription.ShouldBe(description);
    }

    [Fact]
    public async Task Contact_details_given_on_the_claim_are_scrubbed_too()
    {
        Arrange(
            "Reach me at other.inbox@synthetic-mail.test or 22 33 44 55, the tablet will not charge.",
            contactEmail: "other.inbox@synthetic-mail.test",
            contactPhone: "22334455");

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.ProblemDescription.ShouldBe("Reach me at [EMAIL] or [PHONE], the tablet will not charge.");
    }

    [Fact]
    public async Task The_customer_is_only_country_and_region_and_the_claim_facts_are_carried_over()
    {
        var claim = Arrange("The tablet will not charge since last week.");

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.Customer.ShouldBe(new CaseCustomerView("SE", Region.EU));
        context.ShouldSatisfyAllConditions(
            () => context.ClaimId.ShouldBe(ClaimId),
            () => context.Round.ShouldBe(1),
            () => context.Reference.ShouldBe(claim.Reference),
            () => context.Channel.ShouldBe(ClaimChannel.ClaimantPortal),
            () => context.ClaimDate.ShouldBe(new DateOnly(2026, 9, 1)),
            () => context.PurchaseDate.ShouldBe(new DateOnly(2026, 2, 1)),
            () => context.PurchasePrice.ShouldBe(349m),
            () => context.Currency.ShouldBe("NOK"),
            () => context.Region.ShouldBe(Region.EU),
            () => context.ProductModelCode.ShouldBe(ModelCode),
            () => context.SerialNumber.ShouldBe(Serial),
            () => context.Product.ShouldBe(new CaseProduct(ProductId, ModelCode, "Aurora Tab 10", "tablet", 349m)),
            () => context.ReviewerInfoRequested.ShouldBeFalse(),
            () => context.AutoInfoRequestCount.ShouldBe(0));
    }

    [Fact]
    public async Task Evidence_covers_the_rounds_up_to_the_requested_one_and_feeds_the_history_lookup()
    {
        var claim = Arrange("The tablet will not charge since last week.");
        claim.StartEvaluation(SubmittedAt);
        claim.RequestInformation([RequestedItem.Create("ADDITIONAL_PHOTO", "A photo of the charging port.")], DecidedBy.System, SubmittedAt);
        claim.AddSupplement(SubmittedAt.AddDays(1));
        claim.StartEvaluation(SubmittedAt.AddDays(1));
        claim.RequestInformation([RequestedItem.Create("ADDITIONAL_PHOTO", "Another photo.")], DecidedBy.System, SubmittedAt.AddDays(1));
        claim.AddSupplement(SubmittedAt.AddDays(2));
        var invoice = Evidence(1, EvidenceKind.Invoice, "Brackwater_invoice.pdf", "application/pdf", 'a');
        var photo = Evidence(1, EvidenceKind.Photo, "tablet.png", "image/png", 'b');
        var supplement = Evidence(2, EvidenceKind.Photo, "port.jpg", "image/jpeg", 'c');
        var later = Evidence(3, EvidenceKind.Photo, "later.jpg", "image/jpeg", 'd');
        _claims.GetEvidenceAsync(ClaimId, Arg.Any<CancellationToken>()).Returns([later, supplement, photo, invoice]);
        var history = new ClaimHistoryCounts(1, 2, 3);
        _claims.GetHistoryCountsAsync(default, default!, default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(history);

        var context = await Provider().GetCaseContextAsync(ClaimId, 2, TestContext.Current.CancellationToken);

        context.Round.ShouldBe(2);
        context.Evidence.Select(e => e.EvidenceId).ShouldBe([invoice.Id, photo.Id, supplement.Id]);
        context.Evidence[0].ShouldBe(new CaseEvidence(
            invoice.Id, EvidenceKind.Invoice, "[CUSTOMER]_invoice.pdf", "application/pdf", invoice.SizeBytes, invoice.Sha256, 1, invoice.BlobPath));
        context.History.ShouldBe(history);
        await _claims.Received(1).GetHistoryCountsAsync(
            ClaimId,
            Serial,
            new DateOnly(2026, 9, 1),
            Arg.Is<IReadOnlyCollection<string>>(h => h.Order().SequenceEqual(new[] { invoice.Sha256, photo.Sha256, supplement.Sha256 }.Order())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_claim_outside_the_catalog_has_no_product()
    {
        Arrange("The tablet will not charge since last week.", withProduct: false);

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.Product.ShouldBeNull();
        await _catalog.DidNotReceiveWithAnyArgs().GetProductAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_serial_not_registered_to_the_product_has_no_product()
    {
        Arrange("The tablet will not charge since last week.");
        _catalog.FindSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns((ProductSerial?)null);

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.Product.ShouldBeNull();
    }

    [Fact]
    public async Task A_serial_registered_to_another_product_has_no_product()
    {
        Arrange("The tablet will not charge since last week.");
        _catalog.FindSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(TenantId, Serial, Guid.NewGuid()));

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.Product.ShouldBeNull();
    }

    [Fact]
    public async Task A_round_the_claim_has_not_reached_is_refused()
    {
        Arrange("The tablet will not charge since last week.");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Provider().GetCaseContextAsync(ClaimId, 2, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Provider().GetCaseContextAsync(ClaimId, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unresolved_tenant_is_refused_before_any_lookup()
    {
        Arrange("The tablet will not charge since last week.");
        var unresolved = new CaseKnowledgeProvider(new FakeTenantContext(null), _claims, _catalog, _customers, _tenants, _replacementSerials);

        await Should.ThrowAsync<InvalidOperationException>(() => unresolved.GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken));

        await _claims.DidNotReceiveWithAnyArgs().GetAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_unknown_claim_or_one_of_another_tenant_is_refused()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken));

        Arrange("The tablet will not charge since last week.");
        var otherTenant = new CaseKnowledgeProvider(new FakeTenantContext(Guid.NewGuid()), _claims, _catalog, _customers, _tenants, _replacementSerials);
        await Should.ThrowAsync<InvalidOperationException>(() => otherTenant.GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken));
    }

    // ---- Replacement units (003 FR-029, FR-030, research R11) --------------------------------------------

    /// <summary>The serial the claimed unit replaced (and, for a chain, the serial that one replaced).</summary>
    private const string OriginalSerial = "SN-TAB-0001";
    private const string FirstSerial = "SN-TAB-0000";

    [Fact]
    public async Task A_registered_replacement_unit_carries_its_replacement_cover()
    {
        Arrange("The replacement tablet will not charge.");
        RegisterReplacement(Serial, OriginalSerial, originalPurchaseDate: new DateOnly(2025, 2, 1), coverEndDate: new DateOnly(2027, 2, 1));

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.ReplacementCover.ShouldBe(new ReplacementCover(OriginalSerial, new DateOnly(2025, 2, 1), new DateOnly(2027, 2, 1)));
        context.Product.ShouldNotBeNull("the hand-over registers the unit's serial in the catalog (research R10)");
        context.History.ReturnedAndReplaced.ShouldBeFalse();
    }

    [Fact]
    public async Task A_serial_that_is_no_replacement_unit_has_no_replacement_cover()
    {
        Arrange("The tablet will not charge since last week.");

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.ReplacementCover.ShouldBeNull();
        context.History.ReturnedAndReplaced.ShouldBeFalse();
    }

    [Fact]
    public async Task A_serial_that_was_returned_and_replaced_is_flagged_in_the_history_counts()
    {
        Arrange("The tablet will not charge since last week.");
        _replacementSerials.FindByOriginalSerialAsync(Serial, Arg.Any<CancellationToken>())
            .Returns([Registration("SN-TAB-0777", Serial, new DateOnly(2026, 2, 1), new DateOnly(2028, 2, 1))]);

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.History.ReturnedAndReplaced.ShouldBeTrue();
        context.ReplacementCover.ShouldBeNull();
    }

    [Fact]
    public async Task Accidental_damage_approvals_of_the_original_serial_count_for_the_replacement_unit()
    {
        // Borealis allows one accidental-damage incident: a replacement must not reset the allowance (research R11).
        Arrange("The replacement tablet fell and the screen cracked.");
        RegisterReplacement(Serial, OriginalSerial, new DateOnly(2025, 2, 1), new DateOnly(2027, 2, 1));
        _claims.GetHistoryCountsAsync(ClaimId, Serial, Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimHistoryCounts(1, 0, 2));
        _claims.GetHistoryCountsAsync(ClaimId, OriginalSerial, Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimHistoryCounts(4, 1, 2));

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        // Only the accidental-damage count follows the chain; duplicates and evidence reuse stay those of the claimed serial.
        context.History.PriorApprovedAccidental.ShouldBe(1);
        context.History.DuplicateClaimsForSerial.ShouldBe(1);
        context.History.EvidenceReuseMatches.ShouldBe(2);
    }

    [Fact]
    public async Task Accidental_damage_approvals_are_counted_along_a_chain_of_replacements()
    {
        Arrange("The second replacement tablet fell and the screen cracked.");
        RegisterReplacement(Serial, OriginalSerial, new DateOnly(2025, 2, 1), new DateOnly(2027, 2, 1));
        RegisterReplacement(OriginalSerial, FirstSerial, new DateOnly(2025, 2, 1), new DateOnly(2027, 2, 1));
        _claims.GetHistoryCountsAsync(ClaimId, OriginalSerial, Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimHistoryCounts(0, 1, 0));
        _claims.GetHistoryCountsAsync(ClaimId, FirstSerial, Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimHistoryCounts(0, 1, 0));

        var context = await Provider().GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        context.History.PriorApprovedAccidental.ShouldBe(2);
        context.ReplacementCover.ShouldBe(new ReplacementCover(OriginalSerial, new DateOnly(2025, 2, 1), new DateOnly(2027, 2, 1)));
    }

    private void RegisterReplacement(string serial, string originalSerial, DateOnly originalPurchaseDate, DateOnly coverEndDate)
        => _replacementSerials.FindBySerialAsync(serial, Arg.Any<CancellationToken>())
            .Returns(Registration(serial, originalSerial, originalPurchaseDate, coverEndDate));

    private static RegisteredReplacementSerial Registration(string serial, string originalSerial, DateOnly originalPurchaseDate, DateOnly coverEndDate)
        => RegisteredReplacementSerial.Register(
            TenantId, serial, ProductId, originalSerial, Guid.NewGuid(), originalPurchaseDate, new DateOnly(2026, 3, 1), coverEndDate, SubmittedAt);

    private CaseKnowledgeProvider Provider() => new(new FakeTenantContext(TenantId), _claims, _catalog, _customers, _tenants, _replacementSerials);

    private Claim Arrange(string description, string? contactEmail = Email, string? contactPhone = null, bool withProduct = true)
    {
        var claim = Claim.Submit(
            ClaimId, TenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, CustomerId,
            contactEmail, contactPhone, ModelCode, withProduct ? ProductId : null, Serial, new DateOnly(2026, 2, 1),
            "Elkjop Oslo City", 349m, Region.EU, description, SubmittedAt);
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(claim);
        return claim;
    }

    private static ClaimEvidence Evidence(int round, EvidenceKind kind, string fileName, string contentType, char hash)
        => ClaimEvidence.Create(
            Guid.NewGuid(), TenantId, ClaimId, round, kind, fileName, contentType, 1024, new string(hash, 64), SubmittedAt.AddDays(round - 1).AddMinutes(hash - 'a'));
}
