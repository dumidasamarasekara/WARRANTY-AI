using System.Globalization;
using System.Text.Json;
using Warranty.AI.Harness.Agents;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// <c>customer_lookup</c> (Intake): whether the run's claim belongs to a known customer of the tenant,
/// the customer's region and how many claims the customer made before this one. Takes no arguments —
/// the customer is the run's claim's. The result carries no personal data (FR-006a, research R28):
/// never name, email, phone or address, only the region.
/// </summary>
public sealed class CustomerLookupTool(IClaimRepository claims, ICrmClient crm) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        ToolNames.CustomerLookup,
        "Looks up the customer of this claim in the CRM. Returns whether the customer is verified, the customer's region "
        + "(NA or EU, null when unknown) and the number of claims the customer made before this one. Contains no personal data.",
        ToolSupport.Schema(ToolSupport.NoArgumentsSchema),
        ToolSideEffect.ReadOnly,
        ToolSupport.Callers(AgentNames.Intake));

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var result = await LookupAsync(ctx.ClaimId, ct);
        return ToolResult.Ok(
            result,
            string.Create(
                CultureInfo.InvariantCulture,
                $"verified={result.Verified}, region={result.Region ?? "unknown"}, priorClaimsCount={result.PriorClaimsCount}"));
    }

    /// <summary>
    /// The customer facts of a claim. <see cref="CustomerLookupResult.Verified"/> is true when the claim's
    /// customer is in the tenant's CRM and the contact email given with the claim, if any, is the customer's.
    /// </summary>
    public async Task<CustomerLookupResult> LookupAsync(Guid claimId, CancellationToken ct)
    {
        var claim = await ToolSupport.RequireClaimAsync(claims, claimId, ct);
        var customer = await crm.GetCustomerAsync(claim.CustomerId, ct);
        if (customer is null)
        {
            return new CustomerLookupResult(false, null, 0);
        }

        var verified = claim.ContactEmail is null || string.Equals(claim.ContactEmail, customer.Email, StringComparison.OrdinalIgnoreCase);
        var priorClaims = await claims.CountCustomerClaimsAsync(customer.Id, claim.CreatedAt, ct);
        return new CustomerLookupResult(verified, customer.Region?.ToString(), priorClaims);
    }
}

/// <summary>Result of <c>customer_lookup</c>; no personal data.</summary>
/// <param name="Verified">The claim's customer is known and matches the claim's contact email.</param>
/// <param name="Region"><c>NA</c>, <c>EU</c>, or null when the customer's country is outside both or the customer is unknown.</param>
/// <param name="PriorClaimsCount">Claims of the same customer and tenant created before this claim.</param>
public sealed record CustomerLookupResult(bool Verified, string? Region, int PriorClaimsCount);
