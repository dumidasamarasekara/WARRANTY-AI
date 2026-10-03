namespace Warranty.Integrations.Simulated;

/// <summary>
/// Placeholder for payouts and refunds, which are out of scope for the PoC: it records nothing and
/// moves no money.
/// </summary>
internal sealed class PaymentStub
{
    public Task<Guid?> RequestPayoutAsync(Guid claimId, decimal amount, CancellationToken ct) => Task.FromResult<Guid?>(null);
}
