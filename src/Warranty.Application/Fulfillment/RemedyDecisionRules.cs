using Warranty.Domain.Fulfillment;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// The rules a reviewer's remedy decision shares between <see cref="RecordRemedyDecision"/> (which answers with
/// 409/400) and the <see cref="Actions.ActionExecutor"/> (which re-checks them and fails closed). 002 FR-015, research R4.
/// </summary>
public static class RemedyDecisionRules
{
    /// <summary>
    /// Why the remedy cannot be decided now, or null when it can: a remedy awaiting a decision can always be decided; a
    /// remedy in progress can be changed until a Repair is handed over to a centre or a Replacement/Refund is
    /// completed (a completed remedy is final), and only to another remedy.
    /// </summary>
    public static string? WhyNotDecidable(ClaimRemedy remedy, Repair? repair, RemedyType requested)
    {
        ArgumentNullException.ThrowIfNull(remedy);
        return remedy.State switch
        {
            RemedyState.AwaitingDecision => null,
            RemedyState.Completed => "The remedy of this claim is completed and can no longer be changed.",
            _ when remedy.Remedy == requested => $"{requested} is already the remedy of this claim.",
            _ when remedy.Remedy == RemedyType.Repair && repair is not null && repair.Status != RepairStatus.AwaitingHandover
                => "The product was already handed over for repair; the remedy changes only after the repair outcome.",
            _ => null,
        };
    }

    /// <summary>Every decision after the first remedy needs a reason (FR-015): the first one is the first <c>Decided</c> row.</summary>
    public static bool RequiresReason(IEnumerable<RemedyDecision> history)
        => history.Any(d => d.Kind == RemedyDecisionKind.Decided);

    /// <summary>Whether <paramref name="reason"/> is acceptable: present when <paramref name="required"/>, and 10–1000 characters after trimming when given.</summary>
    public static bool IsValidReason(string? reason, bool required)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return !required;
        }

        return trimmed.Length is >= RemedyDecision.MinReasonLength and <= RemedyDecision.MaxReasonLength;
    }
}
