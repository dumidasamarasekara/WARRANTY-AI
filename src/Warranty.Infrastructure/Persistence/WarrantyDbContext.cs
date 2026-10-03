using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions;
using Warranty.Domain.Adjudication;
using Warranty.Domain.AiOps;
using Warranty.Domain.Audit;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Crm;
using Warranty.Domain.Integration;
using Warranty.Domain.Policies;
using Warranty.Domain.Review;
using Warranty.Domain.Tenancy;

namespace Warranty.Infrastructure.Persistence;

/// <summary>
/// Transactional database <c>warranty</c>, one PostgreSQL schema per domain. Every tenant-owned
/// entity has a global query filter on the current tenant; with no resolved tenant the filter
/// compares with <see cref="Guid.Empty"/> and returns nothing (fail closed). PostgreSQL RLS enforces
/// the same rule independently (research R8).
/// </summary>
public sealed class WarrantyDbContext(DbContextOptions<WarrantyDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    /// <summary>Platform-owned entities: not tenant-filtered (tenant resolution reads them).</summary>
    internal static readonly IReadOnlySet<Type> PlatformEntities = new HashSet<Type> { typeof(Tenant), typeof(TenantChannel) };

    /// <summary>Tenant used by the query filters; evaluated per query for this context instance.</summary>
    internal Guid CurrentTenantId => tenantContext.IsResolved ? tenantContext.TenantId : Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantChannel> TenantChannels => Set<TenantChannel>();

    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductSerial> ProductSerials => Set<ProductSerial>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<WarrantyPolicy> WarrantyPolicies => Set<WarrantyPolicy>();

    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();

    public DbSet<PolicyClause> PolicyClauses => Set<PolicyClause>();

    public DbSet<Claim> Claims => Set<Claim>();

    public DbSet<ClaimEvidence> ClaimEvidence => Set<ClaimEvidence>();

    public DbSet<ClaimJob> ClaimJobs => Set<ClaimJob>();

    public DbSet<AdjudicationRun> AdjudicationRuns => Set<AdjudicationRun>();

    public DbSet<IntakeResult> IntakeResults => Set<IntakeResult>();

    public DbSet<EvidenceFinding> EvidenceFindings => Set<EvidenceFinding>();

    public DbSet<RetrievedPolicyRef> RetrievedPolicyRefs => Set<RetrievedPolicyRef>();

    public DbSet<PolicyAssessment> PolicyAssessments => Set<PolicyAssessment>();

    public DbSet<RiskAssessment> RiskAssessments => Set<RiskAssessment>();

    public DbSet<Recommendation> Recommendations => Set<Recommendation>();

    public DbSet<GuardrailEvaluation> GuardrailEvaluations => Set<GuardrailEvaluation>();

    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();

    public DbSet<DecisionTrailEntry> DecisionTrailEntries => Set<DecisionTrailEntry>();

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    public DbSet<ModelCall> ModelCalls => Set<ModelCall>();

    public DbSet<ToolCall> ToolCalls => Set<ToolCall>();

    public DbSet<RagQuery> RagQueries => Set<RagQuery>();

    public DbSet<ServiceCenter> ServiceCenters => Set<ServiceCenter>();

    public DbSet<RepairRequest> RepairRequests => Set<RepairRequest>();

    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(12, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WarrantyDbContext).Assembly, t => t.Namespace?.EndsWith(".Configurations", StringComparison.Ordinal) == true);
        ApplyTenantFilters(modelBuilder);
        modelBuilder.ApplySnakeCaseNames();
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        var currentTenant = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entity.ClrType;
            var tenantProperty = clrType.GetProperty("TenantId");
            if (PlatformEntities.Contains(clrType) || tenantProperty is null)
            {
                continue;
            }

            var parameter = Expression.Parameter(clrType, "e");
            Expression tenantId = Expression.Property(parameter, tenantProperty);
            Expression expected = currentTenant;
            if (tenantProperty.PropertyType == typeof(Guid?))
            {
                expected = Expression.Convert(currentTenant, typeof(Guid?));
            }

            modelBuilder.Entity(clrType).HasQueryFilter(Expression.Lambda(Expression.Equal(tenantId, expected), parameter));
        }
    }
}
