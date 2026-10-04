using System.Text.Json;
using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.UnitTests.Application;

/// <summary>Claimant access by reference + contact and the claimant view (T058, FR-037, FR-037a).</summary>
public sealed class ClaimantAccessTests : IDisposable
{
    private const string Email = "sample.claimant@example.test";
    private const string Phone = "+1 (555) 010-0042";

    private static readonly Guid TenantId = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly ISecurityEventWriter _events = Substitute.For<ISecurityEventWriter>();
    private readonly TenantContextScope _tenant = TenantContextScope.Begin(TenantId, "aurora", "claimant");
    private readonly Claim _claim = NewClaim();

    public ClaimantAccessTests()
    {
        _claims.FindByReferenceAsync(_claim.Reference, Arg.Any<CancellationToken>()).Returns(_claim);
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ProductId, TenantId, "AUR-TAB10", "Aurora Tab 10", "tablet", 349m, "USD"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenant.Dispose();

    [Theory]
    [InlineData(Email)]
    [InlineData("  SAMPLE.Claimant@EXAMPLE.test ")]
    [InlineData("+15550100042")]
    [InlineData("1 555 010 0042")]
    [InlineData("(1) 555.010.0042")]
    public async Task The_reference_with_the_submitted_email_or_phone_grants_access(string contact)
    {
        var result = await Access().VerifyAsync(_claim.Reference, contact, Ct);

        result.Granted.ShouldBeTrue();
        result.ClaimId.ShouldBe(_claim.Id);
        await _events.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_reference_is_normalized_like_user_input()
    {
        var typed = _claim.Reference.ToLowerInvariant().Insert(5, "-");

        var result = await Access().VerifyAsync(typed, Email, Ct);

        result.Granted.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_reference_and_a_wrong_contact_are_denied_identically_and_recorded()
    {
        var unknown = await Access().VerifyAsync("ZZZZZZZZZZ", Email, Ct);
        var mismatch = await Access().VerifyAsync(_claim.Reference, "someone.else@example.test", Ct);

        unknown.ShouldBe(ClaimantAccessResult.Denied);
        mismatch.ShouldBe(ClaimantAccessResult.Denied);
        await _events.Received(1).RecordAsync(
            SecurityEventKind.ClaimantAccessFailed, ClaimantAccess.ClaimantChannelActor, "ZZZZZZZZZZ", Arg.Any<object?>(), Arg.Any<CancellationToken>());
        await _events.Received(1).RecordAsync(
            SecurityEventKind.ClaimantAccessFailed, ClaimantAccess.ClaimantChannelActor, _claim.Reference, Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-a-reference", Email)]
    [InlineData(null, Email)]
    [InlineData("", "")]
    public async Task A_malformed_reference_is_denied_without_a_lookup(string? reference, string contact)
    {
        var result = await Access().VerifyAsync(reference, contact, Ct);

        result.Granted.ShouldBeFalse();
        await _claims.DidNotReceiveWithAnyArgs().FindByReferenceAsync(default!, Arg.Any<CancellationToken>());
        await _events.ReceivedWithAnyArgs(1).RecordAsync(default, default!, default, default, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", Email, Phone, false)]
    [InlineData("   ", Email, Phone, false)]
    [InlineData("abc", Email, null, false)]
    [InlineData("555 010 0042", Email, Phone, false)]
    [InlineData("sample.claimant@example.test.evil", Email, Phone, false)]
    [InlineData("sample.claimant@example.tes", Email, Phone, false)]
    [InlineData(Email, null, Phone, false)]
    [InlineData("+1 555 010 0042", null, Phone, true)]
    [InlineData("SAMPLE.CLAIMANT@EXAMPLE.TEST", Email, null, true)]
    public void Contacts_match_only_after_normalization_and_exactly(string contact, string? claimEmail, string? claimPhone, bool expected)
        => ClaimantAccess.ContactMatches(contact, claimEmail, claimPhone).ShouldBe(expected);

    [Fact]
    public async Task The_view_of_a_submitted_claim_has_status_and_product_but_no_outcome()
    {
        var view = (await Access().GetViewAsync(_claim.Id, _claim.Reference, Ct)).ShouldNotBeNull();

        view.Reference.ShouldBe(_claim.Reference);
        view.Status.ShouldBe(ClaimStatus.Submitted);
        view.SubmittedAt.ShouldBe(SubmittedAt);
        view.ProductName.ShouldBe("Aurora Tab 10");
        view.OutcomeExplanation.ShouldBeNull();
        view.RequestedItems.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_view_of_a_final_claim_carries_the_claimant_explanation()
    {
        _claim.StartEvaluation(SubmittedAt);
        _claim.FinalizeRejected("Your tablet was bought 18 months ago; the 12-month warranty has ended.", DecidedBy.System, SubmittedAt);

        var view = (await Access().GetViewAsync(_claim.Id, _claim.Reference, Ct)).ShouldNotBeNull();

        view.Status.ShouldBe(ClaimStatus.Rejected);
        view.OutcomeExplanation.ShouldBe("Your tablet was bought 18 months ago; the 12-month warranty has ended.");
    }

    [Fact]
    public async Task The_view_of_a_claim_pending_information_lists_the_requested_items()
    {
        _claim.StartEvaluation(SubmittedAt);
        _claim.RequestInformation([RequestedItem.Create("LEGIBLE_INVOICE", "The invoice photo is too blurry to read.")], DecidedBy.System, SubmittedAt);

        var view = (await Access().GetViewAsync(_claim.Id, _claim.Reference, Ct)).ShouldNotBeNull();

        view.Status.ShouldBe(ClaimStatus.PendingInformation);
        view.OutcomeExplanation.ShouldBeNull();
        view.RequestedItems.ShouldBe([new ClaimantRequestedItem("LEGIBLE_INVOICE", "The invoice photo is too blurry to read.")]);
    }

    [Fact]
    public async Task A_token_for_another_claim_or_an_unknown_reference_sees_nothing()
    {
        (await Access().GetViewAsync(Guid.CreateVersion7(), _claim.Reference, Ct)).ShouldBeNull();
        (await Access().GetViewAsync(_claim.Id, "ZZZZZZZZZZ", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_serialized_view_has_only_claimant_fields()
    {
        var view = (await Access().GetViewAsync(_claim.Id, _claim.Reference, Ct)).ShouldNotBeNull();

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        json.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["reference", "status", "submittedAt", "productName", "requestedItems"], ignoreOrder: true);
    }

    private ClaimantAccess Access() => new(_tenant, _claims, _catalog, _events);

    private static Claim NewClaim() => Claim.Submit(
        Guid.CreateVersion7(), TenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, Guid.CreateVersion7(),
        Email, Phone, "AUR-TAB10", ProductId, "AT10-99-0042", new DateOnly(2026, 6, 1), "Aurora Store", 450m, Region.NA,
        "The tablet stopped turning on and does not charge.", SubmittedAt);
}
