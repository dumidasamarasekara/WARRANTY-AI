using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.UnitTests.Harness;

public sealed class ToolFrameworkTests
{
    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid RunId = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");
    private static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000cc");

    private readonly IAiOpsRepository _aiOps = Substitute.For<IAiOpsRepository>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly List<ToolCall> _toolCalls = [];
    private readonly FakeTool _warranty = new(Descriptor("warranty_lookup", ToolSideEffect.ReadOnly, ["policy"],
        """{"type":"object","additionalProperties":false,"properties":{"component":{"type":"string","enum":["battery","screen"]}},"required":["component"]}"""));
    private readonly FakeTool _invoice = new(Descriptor("invoice_validation", ToolSideEffect.ReadOnly, ["evidence"],
        """{"type":"object","additionalProperties":false,"properties":{"invoiceRef":{"type":"string"},"note":{"type":"string"}},"required":["invoiceRef"]}""",
        new Dictionary<string, ReferenceKind> { ["invoiceRef"] = ReferenceKind.Evidence }));
    private readonly FakeTool _repair = new(Descriptor("create_repair_request", ToolSideEffect.Consequential, [ToolDescriptor.ActionExecutorCaller],
        """{"type":"object","additionalProperties":false,"properties":{"serviceCenterId":{"type":"string"}}}"""));
    private readonly AdjudicationContext _run = new(RunId, new Tenant(), Case(), new ReferenceRegistry());

    public ToolFrameworkTests() => _aiOps.When(a => a.AddToolCall(Arg.Any<ToolCall>())).Do(c => _toolCalls.Add(c.Arg<ToolCall>()));

    [Fact]
    public void An_agent_is_offered_only_its_read_only_tools()
    {
        var registry = Registry();

        registry.For("policy").Select(t => t.Descriptor.Name).ShouldBe(["warranty_lookup"]);
        registry.For("evidence").Select(t => t.Descriptor.Name).ShouldBe(["invoice_validation"]);
        registry.For(ToolDescriptor.ActionExecutorCaller).ShouldBeEmpty();
        registry.DefinitionsFor("decision").ShouldBeEmpty();
        Invoker().For("policy", _run).Definitions.Select(d => d.Name).ShouldBe(["warranty_lookup"]);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{}}""", "additionalProperties to false")]
    [InlineData("""{"type":"object","additionalProperties":false,"properties":{"tenantId":{"type":"string"}}}""", "tenant field")]
    [InlineData("""{"type":"string"}""", "must be of type object")]
    public void Registration_rejects_loose_schemas_and_tenant_inputs(string schema, string problem)
        => Should.Throw<InvalidOperationException>(() => new ToolRegistry([new FakeTool(Descriptor("bad_tool", ToolSideEffect.ReadOnly, ["policy"], schema))]))
            .Message.ShouldContain(problem);

    [Fact]
    public void Registration_rejects_duplicate_names()
        => Should.Throw<InvalidOperationException>(() => new ToolRegistry([_warranty, _warranty])).Message.ShouldContain("twice");

    [Fact]
    public async Task An_allowed_call_runs_with_the_run_context_and_is_recorded()
    {
        var result = await Invoker().For("policy", _run).InvokeAsync(Call("warranty_lookup", """{"component":"battery"}"""), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.CallId.ShouldBe("call-1");
        var context = _warranty.Contexts.ShouldHaveSingleItem();
        context.Tenant.TenantId.ShouldBe(Aurora);
        (context.RunId, context.ClaimId, context.Caller).ShouldBe((RunId, ClaimId, "policy"));
        context.References.ShouldBeSameAs(_run.References);

        var row = _toolCalls.ShouldHaveSingleItem();
        (row.TenantId, row.RunId, row.Agent, row.Tool, row.Allowed, row.Status).ShouldBe((Aurora, (Guid?)RunId, "policy", "warranty_lookup", true, "ok"));
        row.DenialReason.ShouldBeNull();
        row.ResultSummary.ShouldBe("looked up");
    }

    [Theory]
    [InlineData("warranty_lookup", "evidence", "caller_not_allowed")]
    [InlineData("no_such_tool", "policy", "unknown_tool")]
    public async Task Unknown_and_disallowed_tools_return_the_same_error_without_running(string tool, string caller, string reason)
    {
        var result = await Invoker().InvokeAsync(caller, _run, Call(tool, "{}"), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.Result.GetProperty("error").GetString().ShouldBe($"Tool '{tool}' is not available.");
        _warranty.Contexts.ShouldBeEmpty();
        var row = _toolCalls.ShouldHaveSingleItem();
        (row.Allowed, row.DenialReason, row.Status).ShouldBe((false, reason, "denied"));
        await _securityEvents.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_consequential_tool_requested_by_an_agent_is_a_scope_violation()
    {
        var result = await Invoker().InvokeAsync("decision", _run, Call("create_repair_request", "{}"), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        _repair.Contexts.ShouldBeEmpty();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.ToolScopeViolation, "decision", "tool:create_repair_request", Arg.Any<object>(), Arg.Any<CancellationToken>());
        (_toolCalls.Single().Allowed, _toolCalls.Single().DenialReason).ShouldBe((false, "consequential_tool"));
    }

    [Fact]
    public async Task The_action_executor_may_call_consequential_tools()
    {
        var result = await Invoker().InvokeAsync(ToolDescriptor.ActionExecutorCaller, _run, Call("create_repair_request", "{}"), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        _repair.Contexts.ShouldHaveSingleItem();
        await _securityEvents.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("""{"component":"wheel"}""")]
    [InlineData("""{"component":"battery","tenantId":"0199a000-0000-7000-8000-000000000002"}""")]
    [InlineData("""{}""")]
    public async Task Arguments_must_match_the_strict_schema(string arguments)
    {
        var result = await Invoker().InvokeAsync("policy", _run, Call("warranty_lookup", arguments), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.Result.GetProperty("error").GetString()!.ShouldStartWith("Invalid arguments:");
        _warranty.Contexts.ShouldBeEmpty();
        (_toolCalls.Single().Allowed, _toolCalls.Single().Status).ShouldBe((true, "invalid_arguments"));
    }

    [Fact]
    public async Task Reference_arguments_must_be_issued_for_the_run()
    {
        _run.References.IssueEvidence(Guid.NewGuid());
        var invoker = Invoker();

        var unknown = await invoker.InvokeAsync("evidence", _run, Call("invoice_validation", """{"invoiceRef":"EV-9"}"""), TestContext.Current.CancellationToken);
        var issued = await invoker.InvokeAsync("evidence", _run, Call("invoice_validation", """{"invoiceRef":"EV-1"}"""), TestContext.Current.CancellationToken);

        unknown.IsError.ShouldBeTrue();
        unknown.Result.GetProperty("error").GetString()!.ShouldContain("EV-9 was not issued for this run");
        issued.IsError.ShouldBeFalse();
        _invoice.Contexts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Recorded_arguments_are_redacted()
    {
        await Invoker().InvokeAsync(
            "evidence", _run, Call("invoice_validation", """{"invoiceRef":"EV-1","note":"mail jane@example.com"}"""), TestContext.Current.CancellationToken);

        _toolCalls.Single().ArgumentsJson.ShouldBe("""{"invoiceRef":"EV-1","note":"mail [EMAIL]"}""");
    }

    [Fact]
    public async Task A_failing_tool_returns_a_generic_error_to_the_model()
    {
        _warranty.Throw = true;

        var result = await Invoker().InvokeAsync("policy", _run, Call("warranty_lookup", """{"component":"screen"}"""), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.Result.GetProperty("error").GetString().ShouldBe("Tool 'warranty_lookup' failed.");
        _toolCalls.Single().Status.ShouldBe("error");
        _toolCalls.Single().ResultSummary.ShouldBe("InvalidOperationException: database down");
    }

    private ToolRegistry Registry() => new([_warranty, _invoice, _repair]);

    private ToolInvoker Invoker() => new(Registry(), _aiOps, _securityEvents, new EmailRedactor(), new FakeTimeProvider());

    private static ToolDescriptor Descriptor(
        string name, ToolSideEffect sideEffect, string[] callers, string schema, IReadOnlyDictionary<string, ReferenceKind>? references = null)
        => new(name, $"The {name} tool.", JsonDocument.Parse(schema).RootElement, sideEffect, callers.ToHashSet(), references);

    private static AiToolCall Call(string tool, string arguments) => new("call-1", tool, JsonDocument.Parse(arguments).RootElement);

    private static CaseContext Case() => new(
        ClaimId, 1, "ABCDEFGHJK", ClaimChannel.ClaimantPortal, new DateOnly(2026, 9, 1), new DateOnly(2026, 3, 1), "Store", 450m, "EUR",
        Region.EU, "AUR-TAB10", "SN-1", "Screen flickers.", null, new CaseCustomerView("NO", Region.EU), [], ClaimHistoryCounts.None, false, 0);

    private sealed class FakeTool(ToolDescriptor descriptor) : ITool
    {
        public ToolDescriptor Descriptor => descriptor;

        public List<ToolInvocationContext> Contexts { get; } = [];

        public bool Throw { get; set; }

        public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
        {
            if (Throw)
            {
                throw new InvalidOperationException("database down");
            }

            Contexts.Add(ctx);
            return Task.FromResult(ToolResult.Ok(new { ok = true }, "looked up"));
        }
    }

    private sealed class EmailRedactor : IPiiRedactor
    {
        public RedactionResult Redact(string text)
        {
            var redacted = text.Replace("jane@example.com", "[EMAIL]", StringComparison.Ordinal);
            return new RedactionResult(redacted, redacted == text ? 0 : 1);
        }
    }

    private sealed class Tenant : ITenantContext
    {
        public bool IsResolved => true;

        public Guid TenantId => Aurora;

        public string TenantSlug => "aurora";

        public string KnowledgeNamespace => "tenant-aurora";

        public string PrincipalId => Principals.AdjudicationService;

        public string PrincipalName => Principals.AdjudicationService;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string> { Principals.AdjudicationService };

        public string CorrelationId => "corr-1";

        public bool IsSystem => true;
    }
}
