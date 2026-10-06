using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;

namespace Warranty.Application.Policies;

/// <summary><c>PolicyVersionSummary</c> (contracts/rest-api.openapi.yaml): one effective-dated version of a tenant policy.</summary>
public sealed record PolicyVersionSummary(
    string PolicyCode,
    string Title,
    int Version,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    IReadOnlyList<Region> Regions);

/// <summary>
/// The policy versions of the current tenant (read-only), ordered by policy code and version. Only the
/// structured applicability fields are returned — coverage terms and clauses stay with retrieval.
/// </summary>
public sealed class PolicyVersionsQuery(ITenantContext tenant, IPolicyRepository policies)
{
    public async Task<IReadOnlyList<PolicyVersionSummary>> ListAsync(CancellationToken ct)
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("The policy list requires the tenant of the staff user.");
        }

        // The repository is tenant-scoped already; checking the tenant here keeps a misconfigured filter from leaking.
        var versions = (await policies.GetVersionsAsync(ct)).Where(v => v.TenantId == tenant.TenantId).ToArray();
        var summaries = new List<PolicyVersionSummary>(versions.Length);
        foreach (var group in versions.GroupBy(v => v.PolicyId))
        {
            var policy = await policies.GetPolicyAsync(group.Key, ct);
            if (policy is null || policy.TenantId != tenant.TenantId)
            {
                continue;
            }

            summaries.AddRange(group.Select(v => new PolicyVersionSummary(
                policy.Code, policy.Title, v.Version, v.EffectiveFrom, v.EffectiveTo, v.Regions.Order().ToArray())));
        }

        return summaries
            .OrderBy(s => s.PolicyCode, StringComparer.Ordinal)
            .ThenBy(s => s.Version)
            .ToArray();
    }
}
