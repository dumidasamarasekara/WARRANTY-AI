using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Policies;

namespace Warranty.Infrastructure.Persistence.Repositories;

internal sealed class CatalogRepository(WarrantyDbContext db) : ICatalogRepository
{
    public Task<Product?> FindProductByModelAsync(string modelCode, CancellationToken ct)
    {
        var code = Product.NormalizeModelCode(modelCode);
        return db.Products.SingleOrDefaultAsync(p => p.ModelCode == code, ct);
    }

    public Task<Product?> GetProductAsync(Guid productId, CancellationToken ct)
        => db.Products.SingleOrDefaultAsync(p => p.Id == productId, ct);

    public Task<ProductSerial?> FindSerialAsync(string serialNumber, CancellationToken ct)
    {
        var serial = ProductSerial.NormalizeSerial(serialNumber);
        return db.ProductSerials.SingleOrDefaultAsync(s => s.SerialNumber == serial, ct);
    }
}

internal sealed class PolicyRepository(WarrantyDbContext db) : IPolicyRepository
{
    public async Task<IReadOnlyList<PolicyVersion>> GetVersionsAsync(CancellationToken ct)
        => await db.PolicyVersions.OrderBy(v => v.PolicyId).ThenBy(v => v.Version).ToListAsync(ct);

    public Task<PolicyVersion?> GetVersionAsync(Guid policyVersionId, CancellationToken ct)
        => db.PolicyVersions.SingleOrDefaultAsync(v => v.Id == policyVersionId, ct);

    public async Task<IReadOnlyList<PolicyClause>> GetClausesAsync(Guid policyVersionId, CancellationToken ct)
        => await db.PolicyClauses.Where(c => c.PolicyVersionId == policyVersionId).OrderBy(c => c.ClauseKey).ToListAsync(ct);

    public Task<WarrantyPolicy?> FindPolicyByCodeAsync(string code, CancellationToken ct)
        => db.WarrantyPolicies.SingleOrDefaultAsync(p => p.Code == code, ct);

    public Task<WarrantyPolicy?> GetPolicyAsync(Guid policyId, CancellationToken ct)
        => db.WarrantyPolicies.SingleOrDefaultAsync(p => p.Id == policyId, ct);

    public void AddPolicy(WarrantyPolicy policy) => db.WarrantyPolicies.Add(policy);

    public void AddVersion(PolicyVersion version) => db.PolicyVersions.Add(version);

    public void AddClause(PolicyClause clause) => db.PolicyClauses.Add(clause);
}
