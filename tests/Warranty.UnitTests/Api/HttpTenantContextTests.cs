using Warranty.Api.Tenancy;
using Warranty.Application.Abstractions;

namespace Warranty.UnitTests.Api;

public sealed class HttpTenantContextTests
{
    private static readonly ResolvedTenant Aurora = new(Guid.Parse("11111111-1111-7111-8111-111111111111"), "aurora");

    [Fact]
    public void An_unresolved_context_throws_instead_of_returning_a_default_tenant()
    {
        var context = new HttpTenantContext();

        context.IsResolved.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => context.TenantId);
        Should.Throw<InvalidOperationException>(() => context.KnowledgeNamespace);
        Should.Throw<InvalidOperationException>(() => context.PrincipalId);
    }

    [Fact]
    public void The_request_tenant_is_set_once()
    {
        var context = new HttpTenantContext();
        context.Resolve(Aurora, "staff-sub-1", "", ["auditor"], "trace-1");

        context.TenantId.ShouldBe(Aurora.Id);
        context.KnowledgeNamespace.ShouldBe("tenant-aurora");
        context.PrincipalName.ShouldBe("staff-sub-1");
        context.Roles.ShouldBe(["auditor"]);
        context.CorrelationId.ShouldBe("trace-1");
        Should.Throw<InvalidOperationException>(() => context.Resolve(Aurora with { Slug = "borealis" }, "x", "x", [], "trace-2"));
        context.TenantSlug.ShouldBe("aurora");
    }

    [Fact]
    public async Task In_a_worker_scope_it_reflects_the_ambient_tenant_scope_of_that_flow_only()
    {
        var context = new HttpTenantContext();

        using (TenantContextScope.Begin(Aurora.Id, Aurora.Slug, correlationId: "job-1"))
        {
            context.IsResolved.ShouldBeTrue();
            context.TenantId.ShouldBe(Aurora.Id);
            context.PrincipalId.ShouldBe(Principals.AdjudicationService);
            context.IsSystem.ShouldBeTrue();
            context.CorrelationId.ShouldBe("job-1");
            (await Task.Run(() => context.TenantId)).ShouldBe(Aurora.Id);
        }

        context.IsResolved.ShouldBeFalse();
    }
}
