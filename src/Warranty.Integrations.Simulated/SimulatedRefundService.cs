using Microsoft.Extensions.Logging;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;

namespace Warranty.Integrations.Simulated;

/// <summary>
/// Simulated refund option (research R10): no refund option exists in this repository, so the request is
/// recorded as a structured log entry and its ID is returned. No money moves and no table is written; the
/// ActionExecutor keeps the returned ID with the claim's remedy (<c>refund_request_id</c>) and in its trail,
/// which are the durable record of the request.
/// </summary>
internal sealed partial class SimulatedRefundService(
    ITenantContext tenantContext, TimeProvider time, ILogger<SimulatedRefundService> logger) : IRefundService
{
    public Task<Guid> RequestRefundAsync(Guid claimId, decimal amount, CancellationToken ct)
    {
        if (claimId == Guid.Empty)
        {
            throw new ArgumentException("A refund needs a claim.", nameof(claimId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ct.ThrowIfCancellationRequested();

        var refundRequestId = Guid.CreateVersion7(time.GetUtcNow());
        LogRefundRequested(logger, refundRequestId, tenantContext.TenantId, claimId, amount);
        return Task.FromResult(refundRequestId);
    }

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Information,
        Message = "Simulated refund request {RefundRequestId} for claim {ClaimId} of tenant {TenantId}: {Amount} (no money moved)")]
    private static partial void LogRefundRequested(ILogger logger, Guid refundRequestId, Guid tenantId, Guid claimId, decimal amount);
}
