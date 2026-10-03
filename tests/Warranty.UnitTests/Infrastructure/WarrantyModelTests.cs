using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warranty.Application.Abstractions;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Audit;
using Warranty.Domain.Claims;
using Warranty.Domain.Tenancy;
using Warranty.Infrastructure.Persistence;

namespace Warranty.UnitTests.Infrastructure;

/// <summary>Checks the EF Core model against data-model.md without a database connection.</summary>
public sealed class WarrantyModelTests
{
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private static WarrantyDbContext CreateContext(ITenantContext tenant)
        => new(
            new DbContextOptionsBuilder<WarrantyDbContext>()
                .UseNpgsql("Host=localhost;Database=warranty;Username=unused")
                .Options,
            tenant);

    private static IModel Model => CreateContext(new FakeTenantContext(Aurora)).Model;

    [Fact]
    public void Every_tenant_owned_entity_has_a_query_filter_and_platform_tables_do_not()
    {
        foreach (var entity in Model.GetEntityTypes())
        {
            var isPlatform = entity.ClrType == typeof(Tenant) || entity.ClrType == typeof(TenantChannel);
            var hasFilter = entity.GetDeclaredQueryFilters().Count > 0;
            hasFilter.ShouldBe(!isPlatform, $"{entity.ClrType.Name} query filter");
        }
    }

    [Fact]
    public void Tenant_filter_is_applied_to_generated_sql_for_the_current_tenant()
    {
        using var context = CreateContext(new FakeTenantContext(Aurora));

        var sql = context.Claims.Where(c => c.Status == ClaimStatus.UnderReview).ToQueryString();

        sql.ShouldContain("tenant_id");
        sql.ShouldContain(Aurora.ToString());
        sql.ShouldContain("FROM claims.claims");
    }

    [Fact]
    public void Without_a_resolved_tenant_the_filter_matches_no_tenant()
    {
        using var context = CreateContext(new FakeTenantContext(null));

        context.Claims.ToQueryString().ShouldContain(Guid.Empty.ToString());
    }

    [Theory]
    [InlineData(typeof(Tenant), "tenancy", "tenants")]
    [InlineData(typeof(Claim), "claims", "claims")]
    [InlineData(typeof(ClaimJob), "claims", "claim_jobs")]
    [InlineData(typeof(AdjudicationRun), "adjudication", "adjudication_runs")]
    [InlineData(typeof(Recommendation), "adjudication", "recommendations")]
    [InlineData(typeof(DecisionTrailEntry), "audit", "decision_trail_entries")]
    [InlineData(typeof(SecurityEvent), "audit", "security_events")]
    public void Entities_map_to_their_domain_schema(Type clrType, string schema, string table)
    {
        var entity = Model.FindEntityType(clrType)!;

        entity.GetSchema().ShouldBe(schema);
        entity.GetTableName().ShouldBe(table);
    }

    [Fact]
    public void Columns_are_snake_case_and_json_properties_are_jsonb_without_suffix()
    {
        var claim = Model.FindEntityType(typeof(Claim))!;
        claim.FindProperty(nameof(Claim.ProblemDescription))!.GetColumnName().ShouldBe("problem_description");
        claim.FindProperty(nameof(Claim.AutoInfoRequestCount))!.GetColumnName().ShouldBe("auto_info_request_count");
        claim.FindProperty(nameof(Claim.RequestedItems))!.GetColumnType().ShouldBe("jsonb");

        var recommendation = Model.FindEntityType(typeof(Recommendation))!;
        var raw = recommendation.FindProperty(nameof(Recommendation.RawOutputJson))!;
        raw.GetColumnName().ShouldBe("raw_output");
        raw.GetColumnType().ShouldBe("jsonb");
    }

    [Fact]
    public void Claim_uses_xmin_as_its_concurrency_token()
    {
        var rowVersion = Model.FindEntityType(typeof(Claim))!.FindProperty("RowVersion")!;

        rowVersion.GetColumnName().ShouldBe("xmin");
        rowVersion.IsConcurrencyToken.ShouldBeTrue();
    }

    [Fact]
    public void Foreign_keys_between_tenant_owned_rows_include_the_tenant()
    {
        foreach (var entity in Model.GetEntityTypes())
        {
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                if (foreignKey.PrincipalEntityType.ClrType == typeof(Tenant))
                {
                    continue;
                }

                foreignKey.Properties.Select(p => p.Name).ShouldContain(
                    "TenantId", $"{entity.ClrType.Name} -> {foreignKey.PrincipalEntityType.ClrType.Name}");
            }
        }
    }

    [Theory]
    [InlineData("ProblemDescription", "problem_description")]
    [InlineData("TenantId", "tenant_id")]
    [InlineData("Sha256", "sha256")]
    [InlineData("AKClaimsTenantId", "ak_claims_tenant_id")]
    [InlineData("FK_claims_customers_TenantId_CustomerId", "fk_claims_customers_tenant_id_customer_id")]
    public void Snake_case_conversion(string input, string expected)
        => ModelBuilderExtensions.ToSnakeCase(input).ShouldBe(expected);
}
