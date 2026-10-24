using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using Warranty.Application;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Application;

/// <summary>
/// Staff completion of a Replacement or Refund remedy (002 T251; FR-034, FR-035): <see cref="RecordRemedyCompletion"/>
/// accepts only claims agents and claims reviewers (403); refuses a missing remedy or date, a date in the future, a
/// Repair (completed by the <c>Collected</c> repair event instead), a reference over 100 characters and a Refund
/// without its reference (400 naming the field); a missing or stale remedy ETag (412); a remedy that is not the current
/// one or not <see cref="RemedyState.InProgress"/>, and a lost race on save (409) — all without writing anything. A
/// recorded completion writes one <see cref="RemedyCompletion"/> row, completes the remedy and appends one
/// <see cref="TrailStep.RemedyCompleted"/> entry in one transaction.
/// </summary>
public sealed class RecordRemedyCompletionTests
{
    private const string AgentSub = "agent-sub-t251";
    private const string AgentName = "Aurora Agent";
    private const uint RowVersion = 6210;
    private const string ValidIfMatch = "\"6210\"";
    private const string RefundReference = "RF-2026-0042";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-000000000511");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-000000000512");
    private static readonly Guid VersionId = Guid.Parse("0199b000-0000-7000-8000-000000000513");

    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ApprovedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DecidedAt = new(2026, 9, 20, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IRemedyRepository _remedies = Substitute.For<IRemedyRepository>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<TrailRecord> _entries = [];
    private readonly List<RemedyCompletion> _completions = [];

    public RecordRemedyCompletionTests()
    {
        _tenant.IsResolved.Returns(true);
        _tenant.TenantId.Returns(Tenant);
        _tenant.PrincipalId.Returns(AgentSub);
        _tenant.PrincipalName.Returns(AgentName);
        _tenant.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { Principals.ClaimsAgentRole });
        _tenant.IsSystem.Returns(false);

        _unitOfWork.ExecuteInTransactionAsync(default!, default)
            .ReturnsForAnyArgs(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _trail.WhenForAnyArgs(t => t.AppendAsync(default, default, default!, default!, default, default))
            .Do(call => _entries.Add(new TrailRecord(
                call.ArgAt<TrailStep>(1), call.ArgAt<string>(2), JsonSerializer.SerializeToElement(call.ArgAt<object?>(4)))));
        _remedies.WhenForAnyArgs(r => r.AddCompletion(default!)).Do(call => _completions.Add(call.Arg<RemedyCompletion>()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Recorded completions (FR-034, FR-035) ──────────────────────────────────────────────────

    [Fact]
    public async Task A_replacement_handed_over_completes_the_remedy_with_one_row_and_one_trail_entry_in_one_transaction()
    {
        var (claim, remedy) = ArrangeInProgress(RemedyType.Replacement);

        var result = await Execute(claim, Command(RemedyType.Replacement, Yesterday));

        var recorded = result.ShouldBeOfType<RecordRemedyCompletionResult.Recorded>();
        recorded.Remedy.ShouldBeSameAs(remedy);
        remedy.State.ShouldBe(RemedyState.Completed);
        remedy.Remedy.ShouldBe(RemedyType.Replacement);
        var completion = _completions.ShouldHaveSingleItem();
        recorded.Completion.ShouldBeSameAs(completion);
        (completion.ClaimId, completion.TenantId, completion.Remedy, completion.CompletedOn, completion.Reference)
            .ShouldBe((claim.Id, Tenant, RemedyType.Replacement, Yesterday, (string?)null));
        (completion.RecordedBy, completion.RecordedAt, completion.RefundRequestId).ShouldBe((AgentSub, Now, (Guid?)null));

        var entry = _entries.ShouldHaveSingleItem();
        (entry.Step, entry.Actor).ShouldBe((TrailStep.RemedyCompleted, AgentSub));
        entry.Payload.GetRawText().ShouldContain(nameof(RemedyType.Replacement));
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task A_refund_completion_keeps_the_trimmed_reference_and_completes_the_remedy()
    {
        var (claim, remedy) = ArrangeInProgress(RemedyType.Refund);

        var result = await Execute(claim, Command(RemedyType.Refund, Today, $"  {RefundReference} "));

        result.ShouldBeOfType<RecordRemedyCompletionResult.Recorded>();
        remedy.State.ShouldBe(RemedyState.Completed);
        var completion = _completions.ShouldHaveSingleItem();
        (completion.Remedy, completion.CompletedOn, completion.Reference).ShouldBe((RemedyType.Refund, Today, RefundReference));
        var entry = _entries.ShouldHaveSingleItem();
        entry.Step.ShouldBe(TrailStep.RemedyCompleted);
        entry.Payload.GetRawText().ShouldContain(RefundReference);
    }

    [Fact]
    public async Task A_replacement_may_carry_an_optional_reference()
    {
        var (claim, _) = ArrangeInProgress(RemedyType.Replacement);

        var result = await Execute(claim, Command(RemedyType.Replacement, Today, "DN-55120"));

        result.ShouldBeOfType<RecordRemedyCompletionResult.Recorded>();
        _completions.ShouldHaveSingleItem().Reference.ShouldBe("DN-55120");
    }

    // ── Roles (contracts: claims-agent and claims-reviewer) ─────────────────────────────────────

    [Theory]
    [InlineData(Principals.ClaimsAgentRole)]
    [InlineData(Principals.ClaimsReviewerRole)]
    public async Task Claims_agents_and_claims_reviewers_may_record_a_completion(string role)
    {
        _tenant.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { role });
        var (claim, _) = ArrangeInProgress(RemedyType.Replacement);

        (await Execute(claim, Command(RemedyType.Replacement, Today))).ShouldBeOfType<RecordRemedyCompletionResult.Recorded>();
    }

    [Fact]
    public async Task An_auditor_is_forbidden_and_nothing_is_written()
    {
        _tenant.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { Principals.AuditorRole });
        var (claim, remedy) = ArrangeInProgress(RemedyType.Replacement);

        var result = await Execute(claim, Command(RemedyType.Replacement, Today));

        result.ShouldBeOfType<RecordRemedyCompletionResult.Forbidden>();
        await ShouldBeUnchangedAsync(remedy, RemedyType.Replacement);
    }

    // ── Request fields (contracts RemedyCompletionRequest) ─────────────────────────────────────

    public static TheoryData<string, string> MissingOrInvalidFields => new()
    {
        { "noRemedy", "remedy" },
        { "repair", "remedy" },
        { "noCompletedOn", "completedOn" },
        { "tomorrow", "completedOn" },
        { "referenceTooLong", "reference" },
    };

    [Theory]
    [MemberData(nameof(MissingOrInvalidFields))]
    public async Task Missing_or_invalid_fields_are_refused_naming_the_field(string request, string field)
    {
        var (claim, remedy) = ArrangeInProgress(RemedyType.Replacement);
        var command = request switch
        {
            "noRemedy" => Command(RemedyType.Replacement, Today) with { Remedy = null },
            "repair" => Command(RemedyType.Repair, Today),
            "noCompletedOn" => Command(RemedyType.Replacement, Today) with { CompletedOn = null },
            "tomorrow" => Command(RemedyType.Replacement, Today.AddDays(1)),
            "referenceTooLong" => Command(RemedyType.Replacement, Today, new string('R', 101)),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, null),
        };

        var result = await Execute(claim, command);

        ShouldBeInvalid(result, field);
        await ShouldBeUnchangedAsync(remedy, RemedyType.Replacement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_refund_without_its_reference_is_refused(string? reference)
    {
        var (claim, remedy) = ArrangeInProgress(RemedyType.Refund);

        var result = await Execute(claim, Command(RemedyType.Refund, Today, reference));

        ShouldBeInvalid(result, "reference");
        await ShouldBeUnchangedAsync(remedy, RemedyType.Refund);
    }

    [Fact]
    public async Task A_repair_is_refused_even_when_the_current_remedy_is_Repair()
    {
        var claim = NewClaim();
        var remedy = ClaimRemedy.SetByPolicy(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.CoveredDefect, RemedyType.Repair, [RemedyType.Repair], ApprovedAt);
        ArrangeApproved(claim, remedy);

        var result = await Execute(claim, Command(RemedyType.Repair, Today));

        var invalid = result.ShouldBeOfType<RecordRemedyCompletionResult.Invalid>();
        invalid.Errors["remedy"].ShouldContain(m => m.Contains("Collected", StringComparison.Ordinal));
        await ShouldBeUnchangedAsync(remedy, RemedyType.Repair);
    }

    // ── Remedy state (FR-034, data-model.md "Remedy") ──────────────────────────────────────────

    [Theory]
    [InlineData(RemedyType.Replacement, RemedyType.Refund)]
    [InlineData(RemedyType.Refund, RemedyType.Replacement)]
    public async Task A_remedy_that_is_not_the_current_one_conflicts_naming_the_current_remedy(RemedyType current, RemedyType requested)
    {
        var (claim, remedy) = ArrangeInProgress(current);

        var result = await Execute(claim, Command(requested, Today, RefundReference));

        var conflict = result.ShouldBeOfType<RecordRemedyCompletionResult.Conflict>();
        (conflict.State, conflict.CurrentRemedy).ShouldBe((RemedyState.InProgress, current));
        conflict.Detail.ShouldContain(current.ToString());
        await ShouldBeUnchangedAsync(remedy, current);
    }

    [Fact]
    public async Task A_remedy_awaiting_a_decision_conflicts()
    {
        var claim = NewClaim();
        var remedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.RepairFailed, [RemedyType.Replacement],
            RemedyPendingReason.RepairFailedLimit, DecidedAt);
        ArrangeApproved(claim, remedy);

        var result = await Execute(claim, Command(RemedyType.Replacement, Today));

        var conflict = result.ShouldBeOfType<RecordRemedyCompletionResult.Conflict>();
        conflict.State.ShouldBe(RemedyState.AwaitingDecision);
        conflict.Detail.ShouldContain(nameof(RemedyState.AwaitingDecision));
        remedy.State.ShouldBe(RemedyState.AwaitingDecision);
        await ShouldNotWriteAsync();
    }

    [Fact]
    public async Task A_completed_remedy_cannot_be_completed_again()
    {
        var (claim, remedy) = ArrangeInProgress(RemedyType.Replacement);
        remedy.Complete(RemedyType.Replacement, DecidedAt.AddDays(1));

        var result = await Execute(claim, Command(RemedyType.Replacement, Today));

        result.ShouldBeOfType<RecordRemedyCompletionResult.Conflict>().State.ShouldBe(RemedyState.Completed);
        await ShouldNotWriteAsync();
    }

    [Fact]
    public async Task A_claim_without_a_remedy_conflicts()
    {
        var claim = NewClaim();
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);

        var result = await Execute(claim, Command(RemedyType.Replacement, Today));

        result.ShouldBeOfType<RecordRemedyCompletionResult.Conflict>().State.ShouldBeNull();
        await ShouldNotWriteAsync();
    }

    // ── Tenant isolation ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_claim_or_a_claim_of_another_tenant_is_not_found()
    {
        (await UseCase().ExecuteAsync(Command(RemedyType.Replacement, Today) with { ClaimId = Guid.NewGuid() }, Ct))
            .ShouldBeOfType<RecordRemedyCompletionResult.NotFound>();

        var foreign = NewClaim(OtherTenant);
        _claims.GetAsync(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);
        (await Execute(foreign, Command(RemedyType.Replacement, Today))).ShouldBeOfType<RecordRemedyCompletionResult.NotFound>();

        await ShouldNotWriteAsync();
    }

    // ── Concurrency (If-Match on the remedy) ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"6210\"")]
    [InlineData("\"6209\"")]
    [InlineData("not-a-version")]
    public async Task A_missing_or_stale_If_Match_fails_the_precondition(string? ifMatch)
    {
        var (claim, remedy) = ArrangeInProgress(RemedyType.Replacement);

        var result = await Execute(claim, Command(RemedyType.Replacement, Today) with { IfMatch = ifMatch });

        result.ShouldBeOfType<RecordRemedyCompletionResult.PreconditionFailed>();
        await ShouldBeUnchangedAsync(remedy, RemedyType.Replacement);
    }

    [Fact]
    public async Task A_concurrent_change_detected_on_save_conflicts()
    {
        var (claim, _) = ArrangeInProgress(RemedyType.Replacement);
        _unitOfWork.Configure().ExecuteInTransactionAsync(default!, Ct).ThrowsAsyncForAnyArgs(new ConcurrencyConflictException("xmin changed"));

        var result = await Execute(claim, Command(RemedyType.Replacement, Today));

        result.ShouldBeOfType<RecordRemedyCompletionResult.Conflict>();
    }

    [Fact]
    public void The_application_registers_the_use_case_per_scope()
    {
        var services = new ServiceCollection();

        services.AddWarrantyApplication();

        services.Single(d => d.ServiceType == typeof(RecordRemedyCompletion)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private Task<RecordRemedyCompletionResult> Execute(Claim claim, RecordRemedyCompletionCommand command)
        => UseCase().ExecuteAsync(command with { ClaimId = claim.Id }, Ct);

    private RecordRemedyCompletion UseCase() => new(_tenant, _claims, _remedies, _trail, _unitOfWork, _time);

    private static RecordRemedyCompletionCommand Command(RemedyType remedy, DateOnly completedOn, string? reference = null)
        => new(Guid.Empty, ValidIfMatch, remedy, completedOn, reference);

    private static Claim NewClaim(Guid? tenantId = null) => Claim.Submit(
        Guid.CreateVersion7(), tenantId ?? Tenant, ClaimReference.Generate(), ClaimChannel.AgentPortal, "agent-sub-submitter", CustomerId,
        "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, "SN-2051", new DateOnly(2026, 1, 15),
        "Aurora Store", 349m, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);

    /// <summary>An approved claim whose remedy is <paramref name="remedy"/>, decided by a reviewer after an uneconomical estimate.</summary>
    private (Claim Claim, ClaimRemedy Remedy) ArrangeInProgress(RemedyType remedy)
    {
        var claim = NewClaim();
        var claimRemedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.RepairNotEconomical,
            [RemedyType.Refund, RemedyType.Replacement], RemedyPendingReason.RepairNotEconomical, DecidedAt.AddDays(-1));
        claimRemedy.Decide(remedy, DecidedAt);
        ArrangeApproved(claim, claimRemedy);
        return (claim, claimRemedy);
    }

    private void ArrangeApproved(Claim claim, ClaimRemedy remedy)
    {
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        claim.FinalizeApproved("Your tablet is covered for manufacturing defects.", DecidedBy.Reviewer, ApprovedAt);

        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _remedies.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(remedy);
        _remedies.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RowVersion);
    }

    private static void ShouldBeInvalid(RecordRemedyCompletionResult result, string field)
        => result.ShouldBeOfType<RecordRemedyCompletionResult.Invalid>().Errors.Keys.ShouldContain(field);

    private async Task ShouldBeUnchangedAsync(ClaimRemedy remedy, RemedyType current)
    {
        (remedy.State, remedy.Remedy).ShouldBe((RemedyState.InProgress, (RemedyType?)current));
        await ShouldNotWriteAsync();
    }

    private async Task ShouldNotWriteAsync()
    {
        _completions.ShouldBeEmpty();
        _entries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
    }

    private sealed record TrailRecord(TrailStep Step, string Actor, JsonElement Payload);
}
