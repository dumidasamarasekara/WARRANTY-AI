using System.Globalization;
using System.Text.Json;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// <c>claim_history_lookup</c> (Decision, Risk): counts only, never other claims' details, and only within
/// the current tenant (repositories are tenant-scoped):
/// <list type="bullet">
/// <item><c>duplicateClaimsForSerial</c> — other claims with the same serial that are not final or were
/// finalized within 90 days before this claim date (<see cref="DuplicateClaimWindow"/>, research R25);
/// this claim's own rounds never count;</item>
/// <item><c>priorApprovedAccidental</c> — all-time approved accidental-damage claims for the serial;</item>
/// <item><c>evidenceReuseMatches</c> — evidence files of other claims with the SHA-256 of one of this claim's files.</item>
/// </list>
/// Takes no arguments. The risk capability calls <see cref="LookupAsync"/> directly.
/// </summary>
public sealed class ClaimHistoryLookupTool(IClaimRepository claims) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        ToolNames.ClaimHistoryLookup,
        "Returns counts from this tenant's claim history for this claim: open or recently finalized claims for the same "
        + "serial number (duplicates), prior approved accidental-damage claims for the serial, and evidence files reused "
        + "from other claims. Counts only.",
        ToolSupport.Schema(ToolSupport.NoArgumentsSchema),
        ToolSideEffect.ReadOnly,
        ToolSupport.Callers(AgentNames.Decision, AgentNames.Risk));

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var counts = await LookupAsync(ctx.ClaimId, ct);
        return ToolResult.Ok(
            counts,
            string.Create(
                CultureInfo.InvariantCulture,
                $"duplicates={counts.DuplicateClaimsForSerial}, priorApprovedAccidental={counts.PriorApprovedAccidental}, evidenceReuse={counts.EvidenceReuseMatches}"));
    }

    /// <summary>The history counts of a claim of the current tenant.</summary>
    public async Task<ClaimHistoryCounts> LookupAsync(Guid claimId, CancellationToken ct)
    {
        var claim = await ToolSupport.RequireClaimAsync(claims, claimId, ct);
        var hashes = (await claims.GetEvidenceAsync(claimId, ct)).Select(e => e.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var counts = await claims.GetHistoryCountsAsync(claimId, claim.SerialNumber, claim.ClaimDate, hashes, ct);

        var claimsForSerial = await claims.ListForSerialAsync(claim.SerialNumber, ct);
        var duplicates = DuplicateClaimWindow.CountDuplicates(
            claimId, claim.ClaimDate, claimsForSerial.Select(c => new SerialClaim(c.Id, c.Status, c.FinalizedAt)));
        return counts with { DuplicateClaimsForSerial = duplicates };
    }
}
