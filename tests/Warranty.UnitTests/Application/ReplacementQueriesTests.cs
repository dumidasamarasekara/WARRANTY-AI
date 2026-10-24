using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Warranty.Application;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;

namespace Warranty.UnitTests.Application;

/// <summary>
/// The replacement read models (003 T328): the <see cref="ReplacementView"/> lists the events allowed for the caller's
/// role (none for an auditor), its history in sequence order with staff display names, its unit, proposal and location,
/// and the replacement's own ETag; a claim without a replacement has none. The eligible replacement locations are only
/// the tenant's centres that issue replacements, those serving the claim's region and category first.
/// </summary>
public sealed class ReplacementQueriesTests
{
    private const string AgentSub = "agent-sub-t328";
    private const string AgentName = "Aurora Agent";
    private const string ReviewerSub = "reviewer-sub-t328";
    private const uint RowVersion = 7328;
    private const string ClaimSerial = "SN-3280";
    private const string UnitSerial = "SN-AUR-TAB-3281";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("0199c000-0000-7000-8000-0000000328d7");
    private static readonly Guid ProductId = Guid.Parse("0199c000-0000-7000-8000-0000000328e7");
    private static readonly Guid OtherProductId = Guid.Parse("0199c000-0000-7000-8000-0000000328e8");
    private static readonly Guid VersionId = Guid.Parse("0199c000-0000-7000-8000-0000000328b2");
    private static readonly Guid SuggestedLocation = Guid.Parse("33333333-3333-7333-8333-333333332801");
    private static readonly Guid OtherLocation = Guid.Parse("33333333-3333-7333-8333-333333332802");
    private static readonly Guid EuropeLocation = Guid.Parse("33333333-3333-7333-8333-333333332803");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ApprovedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IReplacementRepository _replacements = Substitute.For<IReplacementRepository>();
    private readonly IReviewRepository _reviews = Substitute.For<IReviewRepository>();
    private readonly IIntegrationRepository _integration = Substitute.For<IIntegrationRepository>();
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();

    public ReplacementQueriesTests()
    {
        _tenant.IsResolved.Returns(true);
        _tenant.TenantId.Returns(Tenant);
        UseRoles(Principals.ClaimsAgentRole);
        _reviews.GetForClaimAsync(default, default).ReturnsForAnyArgs([]);
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ProductId, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", 349m, "USD"));
        _catalog.GetProductAsync(OtherProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(OtherProductId, Tenant, "AUR-TAB11", "Aurora Tab 11", "tablet", 520m, "USD"));
        _integration.GetServiceCentersAsync(default, default).ReturnsForAnyArgs([]);
        _integration.GetServiceCentersAsync(Region.NA, Arg.Any<CancellationToken>()).Returns(
        [
            ServiceCenter.Create(SuggestedLocation, Tenant, Region.NA, "Aurora Service Center Springfield", ["tablet"], "1 Main St", "Mon-Fri 9-17", "555-0100", issuesReplacements: true),
            ServiceCenter.Create(OtherLocation, Tenant, Region.NA, "Aurora Service Center Shelbyville", ["tablet"], "2 Oak Ave", "Mon-Sat 9-18", "555-0101", issuesReplacements: true),
        ]);
        _network.ListEligibleAsync(default, default!, default, default).ReturnsForAnyArgs([]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── ReplacementView ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_claim_without_a_replacement_has_no_replacement_view()
    {
        var claim = Arrange(replacement: null);

        (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Another_tenants_claim_has_no_replacement_view()
    {
        var claim = NewClaim(OtherTenant);
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _replacements.GetByClaimAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(NewReplacement(claim));
        _replacements.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RowVersion);

        (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_view_carries_the_rules_the_suggested_location_and_the_replacement_etag()
    {
        var claim = NewClaim();
        Arrange(claim, NewReplacement(claim, ReplacementProductRule.SameCategoryAtLeastClaimValue, minimumCoverMonths: 6));

        var view = (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldNotBeNull();

        view.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
        view.ProductRule.ShouldBe(ReplacementProductRule.SameCategoryAtLeastClaimValue);
        view.ReturnRule.ShouldBe(ReplacementReturnRule.AtCollection);
        view.MinimumCoverMonths.ShouldBe(6);
        view.ReturnState.ShouldBe(ReturnState.NotYetReturned);
        view.FaultyProductReturn.ShouldBeNull();
        view.CurrentUnit.ShouldBeNull();
        view.PendingProposal.ShouldBeNull();
        view.ApprovedModel.ShouldBeNull();
        view.ETag.ShouldBe("\"7328\"");

        var location = view.ActiveLocation.ShouldNotBeNull();
        location.ShouldBe(new RepairCentreView(
            SuggestedLocation, "Aurora Service Center Springfield", Region.NA, "1 Main St", "Mon-Fri 9-17", "555-0100", Suggested: true, ServesClaim: true));
    }

    [Theory]
    [InlineData(Principals.ClaimsAgentRole)]
    [InlineData(Principals.ClaimsReviewerRole)]
    [InlineData(Principals.AuditorRole)]
    public async Task The_allowed_events_are_those_of_the_callers_role(string role)
    {
        UseRoles(role);
        var claim = NewClaim();
        var replacement = NewReplacement(claim, ReplacementProductRule.SameCategoryAtLeastClaimValue);
        Arrange(claim, replacement);

        var view = (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldNotBeNull();

        var expected = role switch
        {
            Principals.ClaimsReviewerRole => replacement.AllowedEvents(StaffRole.ClaimsReviewer),
            Principals.ClaimsAgentRole => replacement.AllowedEvents(StaffRole.ClaimsAgent),
            _ => [],
        };
        view.AllowedEvents.ShouldBe(expected);
        if (role == Principals.AuditorRole)
        {
            view.AllowedEvents.ShouldBeEmpty();
        }
        else
        {
            view.AllowedEvents.ShouldContain(ReplacementEventType.UnitReserved);
        }
    }

    [Fact]
    public async Task A_reviewer_who_is_also_an_agent_gets_the_reviewer_events()
    {
        UseRoles(Principals.ClaimsAgentRole, Principals.ClaimsReviewerRole);
        var claim = NewClaim();
        var replacement = NewReplacement(claim);
        Arrange(claim, replacement);

        var view = (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldNotBeNull();

        view.AllowedEvents.ShouldBe(replacement.AllowedEvents(StaffRole.ClaimsReviewer));
    }

    [Fact]
    public async Task Events_are_in_sequence_order_with_staff_display_names_and_the_reserved_unit()
    {
        var claim = NewClaim();
        var replacement = NewReplacement(claim);
        replacement.ChangeLocation(OtherLocation, "The customer asked for a location closer to home.", Report(1, AgentSub, AgentName));
        replacement.ReserveUnit(ProductId, UnitSerial, Report(2, ReviewerSub, actorName: null, StaffRole.ClaimsReviewer));
        Arrange(claim, replacement);
        _reviews.GetForClaimAsync(claim.Id, Arg.Any<CancellationToken>()).Returns([ReviewDecisionBy(claim, ReviewerSub, "Aurora Reviewer")]);

        var view = (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldNotBeNull();

        view.Events.Select(e => (e.Seq, e.Type, e.Actor)).ShouldBe(
        [
            (1, ReplacementEventType.LocationSuggested, ReplacementEvent.SystemActor),
            (2, ReplacementEventType.LocationChanged, AgentName),
            (3, ReplacementEventType.UnitReserved, "Aurora Reviewer"),
        ]);
        view.Events[2].FromStatus.ShouldBe(ReplacementStatus.AwaitingUnit);
        view.Events[2].ToStatus.ShouldBe(ReplacementStatus.ReadyForCollection);
        view.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        view.ActiveLocation.ShouldNotBeNull().Id.ShouldBe(OtherLocation);
        view.ActiveLocation.Suggested.ShouldBeFalse();
        view.CurrentUnit.ShouldBe(new ReplacementUnitView(1, "AUR-TAB10", UnitSerial, ReplacementUnitResult.Reserved));
        view.AllowedEvents.ShouldBe(replacement.AllowedEvents(StaffRole.ClaimsAgent));
    }

    [Fact]
    public async Task An_actor_without_a_known_name_is_shown_by_sub()
    {
        var claim = NewClaim();
        var replacement = NewReplacement(claim);
        replacement.ChangeLocation(OtherLocation, "The customer asked for a location closer to home.", Report(1, AgentSub, actorName: null));
        Arrange(claim, replacement);

        var view = (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldNotBeNull();

        view.Events[1].Actor.ShouldBe(AgentSub);
    }

    [Fact]
    public async Task A_pending_proposal_shows_the_model_the_reason_and_the_proposers_display_name()
    {
        var claim = NewClaim();
        var replacement = NewReplacement(claim, ReplacementProductRule.SameCategoryAtLeastClaimValue);
        replacement.ChangeLocation(OtherLocation, "The customer asked for a location closer to home.", Report(1, AgentSub, AgentName));
        typeof(Replacement).GetMethod("AddProposal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(replacement, [OtherProductId, "The claimed model is discontinued.", AgentSub, ApprovedAt.AddDays(3)]);
        Arrange(claim, replacement);

        var view = (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldNotBeNull();

        var proposal = view.PendingProposal.ShouldNotBeNull();
        proposal.Model.ShouldBe(new ReplacementModelView(OtherProductId, "AUR-TAB11", "Aurora Tab 11", "tablet", 520m));
        proposal.Reason.ShouldBe("The claimed model is discontinued.");
        proposal.ProposedBy.ShouldBe(AgentName);
        proposal.ProposedAt.ShouldBe(ApprovedAt.AddDays(3));
    }

    [Fact]
    public async Task A_replacement_without_a_location_has_no_active_location()
    {
        var claim = NewClaim();
        Arrange(claim, NewReplacement(claim, withLocation: false));

        var view = (await Queries().GetReplacementAsync(claim.Id, Ct)).ShouldNotBeNull();

        view.ActiveLocation.ShouldBeNull();
        view.Events.ShouldBeEmpty();
    }

    // ── Eligible replacement locations ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Locations_are_the_replacement_centres_serving_the_claim_with_the_first_suggested()
    {
        var claim = Arrange(replacement: null);
        _network.ListEligibleAsync(Region.NA, "tablet", ServiceCenterPurpose.Replacement, Arg.Any<CancellationToken>()).Returns(
        [
            new EligibleServiceCenter(SuggestedLocation, "Aurora Service Center Springfield", Region.NA, "1 Main St", "Mon-Fri 9-17", "555-0100", 10, true),
            new EligibleServiceCenter(Guid.CreateVersion7(), "Aurora Phone Shop", Region.NA, "4 Pine St", null, null, 15, false),
            new EligibleServiceCenter(OtherLocation, "Aurora Service Center Shelbyville", Region.NA, "2 Oak Ave", "Mon-Sat 9-18", "555-0101", 20, true),
        ]);

        var locations = (await Queries().GetReplacementLocationsAsync(claim.Id, Ct)).ShouldNotBeNull();

        locations.Select(l => (l.Id, l.Suggested, l.ServesClaim)).ShouldBe([(SuggestedLocation, true, true), (OtherLocation, false, true)]);
        locations[0].Contact.ShouldBe("555-0100");
        await _network.DidNotReceive().ListEligibleAsync(Arg.Any<Region>(), Arg.Any<string>(), ServiceCenterPurpose.Repair, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_serving_location_every_replacement_location_is_listed_without_a_suggestion()
    {
        var claim = Arrange(replacement: null);
        _network.ListEligibleAsync(Region.EU, "tablet", ServiceCenterPurpose.Replacement, Arg.Any<CancellationToken>()).Returns(
        [
            new EligibleServiceCenter(EuropeLocation, "Aurora Service Center Lyon", Region.EU, "5 Rue X", "Mon-Fri 9-17", null, 5, true),
        ]);

        var locations = (await Queries().GetReplacementLocationsAsync(claim.Id, Ct)).ShouldNotBeNull();

        locations.ShouldHaveSingleItem().ShouldBe(new RepairCentreView(
            EuropeLocation, "Aurora Service Center Lyon", Region.EU, "5 Rue X", "Mon-Fri 9-17", null, Suggested: false, ServesClaim: false));
    }

    [Fact]
    public async Task Another_tenants_claim_has_no_locations()
    {
        var claim = NewClaim(OtherTenant);
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);

        (await Queries().GetReplacementLocationsAsync(claim.Id, Ct)).ShouldBeNull();
        (await Queries().GetReplacementLocationsAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
        await _network.DidNotReceiveWithAnyArgs().ListEligibleAsync(default, default!, default, Ct);
    }

    [Fact]
    public void The_application_registers_the_queries_per_scope()
    {
        var services = new ServiceCollection();

        services.AddWarrantyApplication();

        services.Single(d => d.ServiceType == typeof(ReplacementQueries)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private ReplacementQueries Queries() => new(_tenant, _claims, _catalog, _replacements, _reviews, _integration, _network);

    private void UseRoles(params string[] roles) => _tenant.Roles.Returns(new HashSet<string>(roles, StringComparer.Ordinal));

    private Claim Arrange(Replacement? replacement) => Arrange(NewClaim(), replacement);

    private Claim Arrange(Claim claim, Replacement? replacement)
    {
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _replacements.GetByClaimAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(replacement);
        _replacements.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(replacement is null ? null : RowVersion);
        return claim;
    }

    private static Claim NewClaim(Guid? tenantId = null) => Claim.Submit(
        Guid.CreateVersion7(), tenantId ?? Tenant, ClaimReference.Generate(), ClaimChannel.AgentPortal, "agent-sub-submitter", CustomerId,
        "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, ClaimSerial, new DateOnly(2026, 1, 15),
        "Aurora Store", 349m, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);

    /// <summary>The claim's replacement awaiting a unit (returned at collection), at <see cref="SuggestedLocation"/> unless <paramref name="withLocation"/> is false.</summary>
    private static Replacement NewReplacement(
        Claim claim,
        ReplacementProductRule productRule = ReplacementProductRule.SameModel,
        int minimumCoverMonths = 0,
        bool withLocation = true)
        => Replacement.Create(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, productRule, ReplacementReturnRule.AtCollection, minimumCoverMonths,
            ReplacementStatus.AwaitingUnit, withLocation ? SuggestedLocation : null, ApprovedAt);

    private static ReplacementReport Report(int day, string actor, string? actorName, StaffRole role = StaffRole.ClaimsAgent)
    {
        var reportedAt = ApprovedAt.AddDays(day);
        return new ReplacementReport(reportedAt, ReportSource.Phone, actor, role, null, reportedAt.AddMinutes(5), actorName);
    }

    private static Warranty.Domain.Review.ReviewDecision ReviewDecisionBy(Claim claim, string sub, string name)
        => Warranty.Domain.Review.ReviewDecision.Create(
            Guid.CreateVersion7(), Tenant, claim.Id, Guid.CreateVersion7(), sub, name,
            ReviewDecisionKind.Approve, "Covered manufacturing defect, evidence consistent.",
            "Your tablet is covered.", [], latestRecommendation: null, ApprovedAt);
}
