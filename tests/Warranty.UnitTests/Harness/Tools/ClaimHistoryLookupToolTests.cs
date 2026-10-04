using NSubstitute;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Infrastructure.Persistence.Repositories;
using static Warranty.UnitTests.Harness.Tools.ToolTestKit;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary><c>claim_history_lookup</c> (Decision, Risk): same-tenant counts only (research R25).</summary>
public sealed class ClaimHistoryLookupToolTests
{
    private const string PhotoHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string InvoiceHash = "2222222222222222222222222222222222222222222222222222222222222222";

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly Claim _claim = NewClaim();

    public ClaimHistoryLookupToolTests()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(_claim);
        _claims.GetEvidenceAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(
        [
            Evidence(EvidenceKind.Photo, PhotoHash, round: 1),
            Evidence(EvidenceKind.Invoice, InvoiceHash, round: 1),
            Evidence(EvidenceKind.Photo, PhotoHash, round: 2),
        ]);
        _claims.GetHistoryCountsAsync(default, default!, default, default!, default).ReturnsForAnyArgs(new ClaimHistoryCounts(99, 1, 2));
        _claims.ListForSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns([_claim]);
    }

    private ClaimHistoryLookupTool Tool => new(_claims);

    [Fact]
    public void It_takes_no_arguments_and_serves_the_decision_agent_and_the_risk_capability()
    {
        ShouldBeStrictReadOnlyTool(Tool, "claim_history_lookup", AgentNames.Decision, AgentNames.Risk);
        Tool.Descriptor.InputSchema.GetProperty("properties").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task It_returns_counts_only()
    {
        var result = await Tool.InvokeAsync(NoArguments, Context(AgentNames.Decision), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Content.EnumerateObject().Select(p => p.Name).ShouldBe(["duplicateClaimsForSerial", "priorApprovedAccidental", "evidenceReuseMatches"]);
        (result.Content.GetProperty("duplicateClaimsForSerial").GetInt32(),
            result.Content.GetProperty("priorApprovedAccidental").GetInt32(),
            result.Content.GetProperty("evidenceReuseMatches").GetInt32()).ShouldBe((0, 1, 2));
    }

    [Fact]
    public async Task Accidental_approvals_and_evidence_reuse_are_asked_for_this_claims_serial_date_and_file_hashes()
    {
        await Tool.LookupAsync(ClaimId, TestContext.Current.CancellationToken);

        await _claims.Received(1).GetHistoryCountsAsync(
            ClaimId, Serial, _claim.ClaimDate,
            Arg.Is<IReadOnlyCollection<string>>(h => h.Order().SequenceEqual(new[] { PhotoHash, InvoiceHash })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicates_follow_the_90_day_window_and_exclude_this_claim()
    {
        var claimDate = _claim.ClaimDate;
        _claims.ListForSerialAsync(Serial, Arg.Any<CancellationToken>()).Returns(
        [
            _claim,
            NewClaim(Guid.NewGuid()),                                          // open → duplicate
            Finalized(claimDate.AddDays(-DuplicateClaimWindow.DuplicateClaimWindowDays)), // finalized on the window's first day → duplicate
            Finalized(claimDate.AddDays(-DuplicateClaimWindow.DuplicateClaimWindowDays - 1)), // finalized before the window → not
        ]);

        var counts = await Tool.LookupAsync(ClaimId, TestContext.Current.CancellationToken);

        counts.DuplicateClaimsForSerial.ShouldBe(2);
        (counts.PriorApprovedAccidental, counts.EvidenceReuseMatches).ShouldBe((1, 2));
    }

    [Fact]
    public async Task This_claim_alone_is_no_duplicate()
        => (await Tool.LookupAsync(ClaimId, TestContext.Current.CancellationToken)).DuplicateClaimsForSerial.ShouldBe(0);

    [Fact]
    public void The_repository_uses_the_same_window_as_the_rule()
        => ClaimRepository.DuplicateClaimWindowDays.ShouldBe(DuplicateClaimWindow.DuplicateClaimWindowDays);

    private static Claim Finalized(DateOnly finalizedOn)
    {
        var claim = NewClaim(Guid.NewGuid(), submittedAt: new DateTimeOffset(2025, 1, 2, 9, 0, 0, TimeSpan.Zero), purchaseDate: new DateOnly(2024, 12, 1));
        var at = new DateTimeOffset(finalizedOn.ToDateTime(new TimeOnly(23, 30)), TimeSpan.Zero);
        claim.StartEvaluation(at);
        claim.FinalizeApproved("Your claim was approved and a repair will be arranged.", DecidedBy.System, at);
        return claim;
    }

    private static ClaimEvidence Evidence(EvidenceKind kind, string hash, int round)
        => ClaimEvidence.Create(
            Guid.NewGuid(), Aurora, ClaimId, round, kind, "file", kind == EvidenceKind.Photo ? "image/png" : "application/pdf", 100, hash, SubmittedAt);
}
