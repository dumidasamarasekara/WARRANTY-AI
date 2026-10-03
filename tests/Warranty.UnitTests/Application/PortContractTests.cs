using System.Reflection;
using System.Text.Json;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;

namespace Warranty.UnitTests.Application;

public sealed class PortContractTests
{
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Borealis = Guid.Parse("22222222-2222-7222-8222-222222222222");

    [Fact]
    public async Task Tenant_scopes_flow_through_async_calls_and_restore_on_dispose()
    {
        TenantContextScope.Current.ShouldBeNull();

        using (TenantContextScope.Begin(Aurora, "aurora", correlationId: "c1"))
        {
            await Task.Yield();
            TenantContextScope.Current!.TenantId.ShouldBe(Aurora);
            TenantContextScope.Current.KnowledgeNamespace.ShouldBe("tenant-aurora");
            TenantContextScope.Current.IsSystem.ShouldBeTrue();
            TenantContextScope.Current.Roles.ShouldContain(Principals.AdjudicationService);

            using (TenantContextScope.Begin(Borealis, "borealis"))
            {
                TenantContextScope.Current!.TenantId.ShouldBe(Borealis);
            }

            TenantContextScope.Current!.TenantId.ShouldBe(Aurora);
        }

        TenantContextScope.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_jobs_never_see_each_others_tenant()
    {
        async Task<Guid> RunJob(Guid tenant, string slug)
        {
            using var scope = TenantContextScope.Begin(tenant, slug);
            await Task.Delay(20);
            return TenantContextScope.Current!.TenantId;
        }

        var results = await Task.WhenAll(RunJob(Aurora, "aurora"), RunJob(Borealis, "borealis"));

        results.ShouldBe([Aurora, Borealis]);
    }

    [Fact]
    public void Conversation_is_append_only_and_puts_one_turns_tool_results_in_one_message()
    {
        var conversation = new AiConversation();
        conversation.AddUser(new TextPart("Analyse the claim."), new UntrustedTextPart("description", "It will not power on."));
        conversation.AddAssistant(new AiMessage(AiRole.Assistant, [new ToolCallPart("t1", "product_lookup", Json("{}"))]));
        conversation.AddToolResults([new AiToolResult("t1", Json("{\"found\":true}"), false), new AiToolResult("t2", Json("{}"), true)]);

        conversation.Messages.Count.ShouldBe(3);
        conversation.Messages[2].Parts.Count.ShouldBe(2);
        conversation.Messages[2].Role.ShouldBe(AiRole.User);
        Should.Throw<ArgumentException>(() => conversation.AddAssistant(new AiMessage(AiRole.User, [new TextPart("x")])));

        typeof(AiConversation).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ShouldNotContain(name => name.StartsWith("Remove", StringComparison.Ordinal) || name.StartsWith("Clear", StringComparison.Ordinal));
    }

    [Fact]
    public void No_port_method_takes_a_tenant_id_parameter()
    {
        var ports = typeof(ITenantContext).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace?.StartsWith("Warranty.Application.Abstractions", StringComparison.Ordinal) == true)
            .ToList();

        ports.ShouldNotBeEmpty();
        ports.SelectMany(t => t.GetMethods())
            .SelectMany(m => m.GetParameters().Select(p => $"{m.DeclaringType!.Name}.{m.Name}({p.Name})"))
            .Where(p => p.Contains("(tenantId)", StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith("ITenantRepository.GetActiveTenantAsync", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
