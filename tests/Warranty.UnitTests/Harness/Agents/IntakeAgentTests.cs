using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.AI.Gateway;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.Providers.Anthropic;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Harness;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Harness.Agents;

/// <summary>
/// The Intake Agent (T061): FR-009 checks and missing items, the <c>extraction</c> model call through the
/// real gateway with a scripted or replayed model, the lookups through the scoped invoker, and persistence.
/// </summary>
public sealed class IntakeAgentTests : IAsyncDisposable
{
    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid RunId = Guid.Parse("0199a000-0000-7000-8000-0000000000a6");
    private static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000c6");
    private static readonly DateOnly Today = new(2026, 10, 4);
    private static readonly DateOnly PurchaseDate = new(2026, 6, 4);

    private const string ModelCode = "AUR-TAB10";
    private const string Serial = "AT10-24-0001";

    // Synthetic identifiers the CRM knows; none of them may reach a model request (FR-006a).
    private const string CustomerName = "Philippa Quarrington-Vossberg";
    private const string CustomerEmail = "philippa.quarrington@privacy-probe.test";
    private const string CustomerPhone = "+1 (415) 555-0137";

    private const string Description =
        "My Aurora Tab 10 suddenly stopped turning on. The screen stays black and there is no charging light. "
        + "I am [CUSTOMER], reach me at [EMAIL].";

    private const string ValidExtraction = """
        {"problemCategory":"POWER_FAILURE","component":"MAINBOARD","symptoms":["does not power on","screen stays black"],
         "claimedCause":"SPONTANEOUS_FAILURE","mentionsAccident":false,"mentionsLiquid":false,
         "containsInstructionsToSystem":false,"summary":"The tablet stopped powering on during normal use."}
        """;

    private static readonly string[] CheckOrder =
    [
        "REQUIRED_FIELDS", "PHOTO_PRESENT", "INVOICE_PRESENT", "FILE_TYPES", "PURCHASE_DATE_NOT_FUTURE", "PURCHASE_DATE_BEFORE_CLAIM", "REGION_DETERMINED",
    ];

    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly FakeLookups _tools = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Today, new TimeOnly(9, 0), TimeSpan.Zero));
    private readonly ScriptedModelProvider _model = new(AnthropicModelProvider.ProviderName);
    private ServiceProvider? _services;

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    // ---- Descriptor and registration ----------------------------------------------------------------

    [Fact]
    public void The_descriptor_names_the_extraction_route_the_intake_prompt_and_the_intake_schema()
    {
        var agent = IntakeAgent.Agent;

        agent.Name.ShouldBe(AgentNames.Intake);
        agent.Route.ShouldBe("extraction");
        agent.Prompt.ShouldBe(new PromptRef("intake", 1));
        agent.OutputSchemaId.ShouldBe(new SchemaValidator().GetOutputSchema(SchemaValidator.IntakeExtraction).SchemaId);
        agent.AllowedTools.ShouldBe([ToolNames.CustomerLookup, ToolNames.ProductLookup]);
        agent.MaxTurns.ShouldBe(2);
        agent.InputTokenBudget.ShouldBe(6_000);
    }

    [Fact]
    public async Task The_harness_registers_the_intake_agent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Aurora));
        foreach (var port in new[]
                 {
                     typeof(IClaimRepository), typeof(ICatalogRepository), typeof(IPolicyRepository), typeof(ICrmClient), typeof(IKnowledgeRetriever),
                     typeof(IAiOpsRepository), typeof(ISecurityEventWriter), typeof(IPiiRedactor), typeof(IAdjudicationRepository),
                 })
        {
            services.AddScoped(port, _ => Substitute.For([port], []));
        }

        services.AddWarrantyAiHarness();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IAgent<CaseContext, IntakeResult>>()
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<IntakeAgent>());
    }

    // ---- A complete claim ---------------------------------------------------------------------------

    [Fact]
    public async Task A_complete_claim_passes_every_check_and_stores_the_extraction()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var intake = result.Output.ShouldNotBeNull();
        intake.RunId.ShouldBe(RunId);
        intake.TenantId.ShouldBe(Aurora);
        intake.Validation.Select(v => v.Check).ShouldBe(CheckOrder);
        intake.Validation.ShouldAllBe(v => v.Passed);
        intake.MissingItems.ShouldBeEmpty();
        intake.IsComplete.ShouldBeTrue();
        var extraction = IntakeExtraction.From(intake).ShouldNotBeNull();
        extraction.Component.ShouldBe("MAINBOARD");
        extraction.ProblemCategory.ShouldBe("POWER_FAILURE");
        extraction.Symptoms.ShouldBe(["does not power on", "screen stays black"]);
        _adjudication.Received(1).AddIntakeResult(intake);
    }

    [Fact]
    public async Task The_agent_runs_customer_lookup_and_product_lookup_itself_through_the_scoped_invoker()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        await RunAsync(Case());

        _tools.Calls.Select(c => c.ToolName).ShouldBe([ToolNames.CustomerLookup, ToolNames.ProductLookup]);
        _tools.Calls[0].Arguments.EnumerateObject().ShouldBeEmpty();
        _tools.Calls[1].Arguments.GetProperty("modelCode").GetString().ShouldBe(ModelCode);
        _tools.Calls[1].Arguments.GetProperty("serialNumber").GetString().ShouldBe(Serial);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void Only_a_serial_registered_to_the_model_is_in_the_catalog(bool modelFound, bool serialRegistered, bool expected)
    {
        var lookup = new ProductLookupResult(modelFound, serialRegistered, modelFound ? "Aurora Tab 10" : null, modelFound ? "tablet" : null);

        IntakeAgent.IsInCatalog(Case() with { Product = null }, lookup).ShouldBe(expected);
        IntakeAgent.IsInCatalog(Case() with { Product = null }, null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_known_model_with_an_unknown_serial_is_presented_to_the_model_as_not_in_the_catalog()
    {
        _tools.Product = new ProductLookupResult(true, false, "Aurora Tab 10", "tablet");
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        var result = await RunAsync(Case() with { Product = null });

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var text = string.Join('\n', _model.Requests.ShouldHaveSingleItem().Messages.SelectMany(m => m.Parts).SelectMany(TextsOf));
        text.ShouldContain("- Product: the model and serial number are not in the catalog");
        text.ShouldNotContain("category: tablet");
        _tools.Calls.Select(c => c.ToolName).ShouldBe([ToolNames.CustomerLookup, ToolNames.ProductLookup], "only the tenant-scoped lookups run");
        IntakeAgent.CaseFacts(Case(), _tools.Product).ShouldContain("- Product: Aurora Tab 10 (category: tablet)", customMessage: "the case's catalog product wins");
    }

    [Fact]
    public async Task The_model_may_call_the_offered_lookup_tools_before_it_answers()
    {
        _model.Enqueue(ScriptedModelProvider.ToolCalls(new AiToolCall("m1", ToolNames.ProductLookup, Json($$"""{"modelCode":"{{ModelCode}}","serialNumber":"{{Serial}}"}"""))));
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        _model.Requests.Count.ShouldBe(2);
        _tools.Calls.Select(c => c.CallId).ShouldContain("m1");
        _model.Requests[1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().ShouldHaveSingleItem().CallId.ShouldBe("m1");
    }

    [Fact]
    public async Task A_failed_lookup_is_a_diagnostic_and_the_checks_fall_back_to_the_case()
    {
        _tools.Fail(ToolNames.CustomerLookup);
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Succeeded);
        result.Diagnostics.ShouldContain(d => d.StartsWith("customer_lookup:", StringComparison.Ordinal));
        result.Output!.Validation.Single(v => v.Check == "REGION_DETERMINED").Passed.ShouldBeTrue();
    }

    // ---- The model request --------------------------------------------------------------------------

    [Fact]
    public async Task The_description_travels_only_as_untrusted_content_on_the_extraction_route()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        await RunAsync(Case());

        var request = _model.Requests.ShouldHaveSingleItem();
        request.Request.Context.Agent.ShouldBe("intake");
        request.Request.Context.TenantId.ShouldBe(Aurora);
        request.Request.Context.ClaimId.ShouldBe(ClaimId);
        request.Request.Context.RunId.ShouldBe(RunId);
        request.Request.Route.ShouldBe("extraction");
        request.Request.Prompt.ShouldBe(new PromptRef("intake", 1));
        request.Request.OutputSchema!.SchemaId.ShouldBe("warranty-ai/intake-extraction/v1");
        var variable = request.Request.PromptVariables.ShouldHaveSingleItem();
        variable.Key.ShouldBe(UntrustedContent.PreambleVariable);
        variable.Value.ShouldBe(UntrustedContent.Preamble);
        request.SystemPrompt.ShouldContain(UntrustedContent.Preamble);
        request.SystemPrompt.ShouldNotContain(Serial);
        request.Request.Tools.Select(t => t.Name).ShouldBe([ToolNames.CustomerLookup, ToolNames.ProductLookup], ignoreOrder: true);

        var parts = request.Messages.ShouldHaveSingleItem().Parts;
        var untrusted = parts.OfType<UntrustedTextPart>().ShouldHaveSingleItem();
        untrusted.Label.ShouldBe(UntrustedContent.DescriptionLabel);
        untrusted.Text.ShouldBe(Description);
        parts.OfType<TextPart>().ShouldAllBe(p => !p.Text.Contains("stopped turning on", StringComparison.Ordinal));
        var facts = string.Join('\n', parts.OfType<TextPart>().Select(p => p.Text));
        facts.ShouldContain(ModelCode);
        facts.ShouldContain(Serial);
        facts.ShouldContain("Aurora Tab 10 (category: tablet)");
        facts.ShouldContain("Region: NA");
        facts.ShouldContain("Purchase date: 2026-06-04");
        facts.ShouldContain("Claim date: 2026-10-04");
    }

    [Fact]
    public async Task No_customer_identifier_reaches_the_model_request()
    {
        _model.Enqueue(ScriptedModelProvider.ToolCalls(new AiToolCall("m1", ToolNames.CustomerLookup, Json("{}"))));
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        await RunAsync(Case());

        var texts = _model.Requests.SelectMany(r => r.Messages).SelectMany(m => m.Parts).SelectMany(TextsOf).Append(_model.Requests[0].SystemPrompt).ToList();
        foreach (var identifier in new[] { CustomerName, "Quarrington", CustomerEmail, CustomerPhone, "555-0137" })
        {
            texts.ShouldAllBe(t => !t.Contains(identifier, StringComparison.OrdinalIgnoreCase));
        }

        texts.ShouldContain(t => t.Contains(CaseCustomerView.NamePlaceholder, StringComparison.Ordinal));
        _model.Requests[1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().ShouldHaveSingleItem("the customer_lookup result is checked too");
    }

    // ---- Deterministic checks -----------------------------------------------------------------------

    [Fact]
    public void Missing_required_fields_fail_REQUIRED_FIELDS_and_ask_for_the_details()
    {
        var (validation, missing) = IntakeAgent.Validate(Case() with { ProblemDescription = " ", PurchasePlace = "", PurchasePrice = 0m }, Customer(), Today);

        var check = Single(validation, "REQUIRED_FIELDS");
        check.Passed.ShouldBeFalse();
        var detail = check.Detail.ShouldNotBeNull();
        detail.ShouldContain("purchase.place");
        detail.ShouldContain("purchase.price");
        detail.ShouldContain("problemDescription");
        missing.Select(m => m.Item).ShouldBe(["PROBLEM_DETAILS", "OTHER"], ignoreOrder: true);
        missing.Single(m => m.Item == "OTHER").Reason.ShouldContain("purchase.place");
        validation.Where(v => v.Check != "REQUIRED_FIELDS").ShouldAllBe(v => v.Passed);
    }

    [Fact]
    public void A_claim_without_photos_fails_PHOTO_PRESENT_and_asks_for_a_photo_of_the_damage()
    {
        var (validation, missing) = IntakeAgent.Validate(Case(evidence: [Invoice()]), Customer(), Today);

        Single(validation, "PHOTO_PRESENT").Passed.ShouldBeFalse();
        missing.ShouldHaveSingleItem().Item.ShouldBe("PHOTO_OF_DAMAGE");
        validation.Where(v => v.Check != "PHOTO_PRESENT").ShouldAllBe(v => v.Passed);
    }

    [Fact]
    public void A_claim_without_an_invoice_fails_INVOICE_PRESENT_and_asks_for_the_invoice()
    {
        var (validation, missing) = IntakeAgent.Validate(Case(evidence: [Photo()]), Customer(), Today);

        Single(validation, "INVOICE_PRESENT").Passed.ShouldBeFalse();
        missing.ShouldHaveSingleItem().Item.ShouldBe("INVOICE");
        validation.Where(v => v.Check != "INVOICE_PRESENT").ShouldAllBe(v => v.Passed);
    }

    [Fact]
    public void A_file_of_an_unsupported_type_for_its_kind_fails_FILE_TYPES()
    {
        var (validation, missing) = IntakeAgent.Validate(Case(evidence: [Invoice(), Photo("application/pdf")]), Customer(), Today);

        var check = Single(validation, "FILE_TYPES");
        check.Passed.ShouldBeFalse();
        check.Detail!.ShouldContain("Photo application/pdf");
        missing.ShouldHaveSingleItem().Item.ShouldBe("PHOTO_OF_DAMAGE");
        Single(validation, "PHOTO_PRESENT").Passed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("image/heic")]
    [InlineData("text/html")]
    public void An_invoice_of_an_unsupported_type_fails_FILE_TYPES_and_asks_for_the_invoice(string contentType)
    {
        var (validation, missing) = IntakeAgent.Validate(Case(evidence: [Invoice(contentType), Photo()]), Customer(), Today);

        Single(validation, "FILE_TYPES").Passed.ShouldBeFalse();
        missing.ShouldHaveSingleItem().Item.ShouldBe("INVOICE");
    }

    [Fact]
    public void A_purchase_date_in_the_future_fails_both_date_checks_and_asks_for_the_date_once()
    {
        var future = Today.AddDays(3);

        var (validation, missing) = IntakeAgent.Validate(Case() with { PurchaseDate = future, ClaimDate = Today }, Customer(), Today);

        Single(validation, "PURCHASE_DATE_NOT_FUTURE").Passed.ShouldBeFalse();
        Single(validation, "PURCHASE_DATE_BEFORE_CLAIM").Passed.ShouldBeFalse();
        missing.ShouldHaveSingleItem().Item.ShouldBe("PURCHASE_DATE");
    }

    [Fact]
    public void A_purchase_date_after_the_claim_date_fails_PURCHASE_DATE_BEFORE_CLAIM_only()
    {
        var (validation, missing) = IntakeAgent.Validate(
            Case() with { PurchaseDate = new DateOnly(2026, 9, 10), ClaimDate = new DateOnly(2026, 9, 1) }, Customer(), Today);

        Single(validation, "PURCHASE_DATE_NOT_FUTURE").Passed.ShouldBeTrue();
        Single(validation, "PURCHASE_DATE_BEFORE_CLAIM").Passed.ShouldBeFalse();
        missing.ShouldHaveSingleItem().Item.ShouldBe("PURCHASE_DATE");
    }

    [Fact]
    public void A_purchase_on_the_claim_date_passes_the_date_checks()
    {
        var (validation, _) = IntakeAgent.Validate(Case() with { PurchaseDate = Today, ClaimDate = Today }, Customer(), Today);

        Single(validation, "PURCHASE_DATE_NOT_FUTURE").Passed.ShouldBeTrue();
        Single(validation, "PURCHASE_DATE_BEFORE_CLAIM").Passed.ShouldBeTrue();
    }

    [Fact]
    public void An_undeterminable_region_fails_REGION_DETERMINED_and_asks_for_the_country_of_purchase()
    {
        var (validation, missing) = IntakeAgent.Validate(
            Case() with { Region = null, Customer = new CaseCustomerView("BR", null) }, Customer(region: null), Today);

        Single(validation, "REGION_DETERMINED").Passed.ShouldBeFalse();
        missing.ShouldHaveSingleItem().Item.ShouldBe("OTHER");
    }

    [Theory]
    [InlineData(null, "EU", null, "EU")]
    [InlineData(null, null, "EU", "EU")]
    [InlineData("NA", "EU", "EU", "NA")]
    public void The_region_comes_from_the_purchase_then_the_customer_address_then_customer_lookup(
        string? purchase, string? address, string? lookup, string expected)
    {
        var input = Case() with { Region = Parse(purchase), Customer = new CaseCustomerView("XX", Parse(address)) };

        var (validation, missing) = IntakeAgent.Validate(input, Customer(lookup), Today);

        var check = Single(validation, "REGION_DETERMINED");
        check.Passed.ShouldBeTrue();
        check.Detail.ShouldBe(expected);
        missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_items_are_returned_with_a_successful_extraction()
    {
        _model.Enqueue(ScriptedModelProvider.Completed(ValidExtraction));

        var result = await RunAsync(Case(evidence: [Invoice()]));

        result.Status.ShouldBe(AgentStatus.Succeeded);
        result.Output!.IsComplete.ShouldBeFalse();
        result.Output.MissingItems.ShouldHaveSingleItem().Item.ShouldBe("PHOTO_OF_DAMAGE");
        _model.Requests.ShouldHaveSingleItem();
    }

    // ---- Model failures -----------------------------------------------------------------------------

    [Fact]
    public async Task Output_that_breaks_the_schema_is_InvalidOutput_with_the_validation_kept_and_persisted()
    {
        _model.Enqueue(ScriptedModelProvider.Completed("""{"problemCategory":"POWER_FAILURE","component":"FLUX_CAPACITOR"}"""));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        result.Succeeded.ShouldBeFalse();
        var intake = result.Output.ShouldNotBeNull();
        intake.ExtractionJson.ShouldBe(IntakeAgent.NoExtraction);
        IntakeExtraction.From(intake).ShouldBeNull();
        intake.Validation.Select(v => v.Check).ShouldBe(CheckOrder);
        result.Diagnostics.ShouldNotBeEmpty();
        _adjudication.Received(1).AddIntakeResult(intake);
    }

    [Fact]
    public async Task Output_that_breaks_a_description_rule_is_InvalidOutput()
    {
        var tooLong = ValidExtraction.Replace("The tablet stopped powering on during normal use.", new string('x', 401), StringComparison.Ordinal);
        _model.Enqueue(ScriptedModelProvider.Completed(tooLong));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.InvalidOutput);
        result.Diagnostics.ShouldContain(d => d.StartsWith("/summary", StringComparison.Ordinal));
        result.Output!.ExtractionJson.ShouldBe(IntakeAgent.NoExtraction);
    }

    [Theory]
    [InlineData(AiStopKind.Refused, AgentStatus.Refused)]
    [InlineData(AiStopKind.Truncated, AgentStatus.Failed)]
    public async Task A_refused_or_truncated_turn_maps_to_a_failing_status(AiStopKind stop, AgentStatus expected)
    {
        _model.Enqueue(ScriptedModelProvider.Stopped(stop));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(expected);
        result.Output!.ExtractionJson.ShouldBe(IntakeAgent.NoExtraction);
        _adjudication.Received(1).AddIntakeResult(Arg.Any<IntakeResult>());
    }

    [Theory]
    [InlineData(AiFailureKind.Timeout, AgentStatus.TimedOut)]
    [InlineData(AiFailureKind.ProviderError, AgentStatus.Failed)]
    public async Task A_failed_turn_maps_to_a_failing_status_instead_of_throwing(AiFailureKind kind, AgentStatus expected)
    {
        _model.Enqueue(ScriptedModelProvider.Failed(kind, "boom"));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(expected);
        result.Diagnostics.ShouldContain(d => d.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_that_keeps_calling_tools_fails_after_two_turns()
    {
        _model.Enqueue(ScriptedModelProvider.ToolCalls(new AiToolCall("m1", ToolNames.CustomerLookup, Json("{}"))));
        _model.Enqueue(ScriptedModelProvider.ToolCalls(new AiToolCall("m2", ToolNames.CustomerLookup, Json("{}"))));

        var result = await RunAsync(Case());

        result.Status.ShouldBe(AgentStatus.Failed);
        _model.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_case_of_another_claim_is_rejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => RunAsync(Case() with { ClaimId = Guid.NewGuid() }));
    }

    // ---- Replay -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_S1_intake_fixture_replays_through_the_agent()
    {
        var replay = new ReplayModelProvider(
            new FixedScenario("S1"),
            new ReplayCallCounter(),
            Options.Create(new AiGatewayOptions { Mode = AiGatewayOptions.ReplayMode }),
            NullLogger<ReplayModelProvider>.Instance);

        var result = await RunAsync(Case(), replay, AiGatewayOptions.ReplayMode);

        result.Status.ShouldBe(AgentStatus.Succeeded);
        var extraction = IntakeExtraction.From(result.Output!).ShouldNotBeNull();
        extraction.Component.ShouldBe("MAINBOARD");
        extraction.ProblemCategory.ShouldBe("POWER_FAILURE");
        extraction.ClaimedCause.ShouldBe("SPONTANEOUS_FAILURE");
        extraction.ContainsInstructionsToSystem.ShouldBeFalse();
        result.Output!.IsComplete.ShouldBeTrue();
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private Task<AgentResult<IntakeResult>> RunAsync(CaseContext input) => RunAsync(input, _model, AiGatewayOptions.LiveMode);

    private async Task<AgentResult<IntakeResult>> RunAsync(CaseContext input, IModelProvider provider, string mode)
    {
        _services = Gateway(provider, mode);
        await using var scope = _services.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IAiGateway>();
        var run = new AdjudicationContext(RunId, new FakeTenantContext(Aurora), Case(), new ReferenceRegistry());
        var agent = new IntakeAgent(new AgentTurnLoop(), new ContextBuilder(), new SchemaValidator(), _adjudication, _time);

        return await agent.RunAsync(input, new AgentExecutionContext(run, gateway, _tools, new ActivityTraceWriter()), TestContext.Current.CancellationToken);
    }

    /// <summary>The real gateway (templates, schema validation, redaction) in front of <paramref name="provider"/>.</summary>
    private static ServiceProvider Gateway(IModelProvider provider, string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiGateway:Mode"] = mode,
            ["AiGateway:Routes:extraction:Provider"] = AnthropicModelProvider.ProviderName,
            ["AiGateway:Routes:extraction:Model"] = "claude-haiku-4-5",
            ["AiGateway:Routes:extraction:MaxTokens"] = "4000",
            ["AiGateway:Routes:extraction:TimeoutSeconds"] = "30",
            ["AiGateway:Routes:embedding:Provider"] = HashEmbeddingGenerator.ProviderName,
            ["AiGateway:Routes:embedding:Model"] = "hash",
            ["AiGateway:Routes:embedding:Dimensions"] = "768",
            ["AiGateway:Anthropic:NoTraining"] = "true",
            ["AiGateway:RateLimits:PerTenantRequestsPerMinute"] = "600",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Aurora));
        services.AddScoped(_ => Substitute.For<IAiOpsRepository>());
        services.AddScoped(_ => Substitute.For<IClaimRepository>());
        services.AddWarrantyAiGateway(configuration);
        services.RemoveAll<IModelProvider>();
        services.AddSingleton(provider);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static CaseContext Case(IReadOnlyList<CaseEvidence>? evidence = null) => new(
        ClaimId, 1, "WC-2026-000001", ClaimChannel.ClaimantPortal, Today, PurchaseDate, "Aurora Store", 450m, "USD", Region.NA, ModelCode, Serial,
        Description, new CaseProduct(Guid.NewGuid(), ModelCode, "Aurora Tab 10", "tablet", 450m), new CaseCustomerView("US", Region.NA),
        evidence ?? [Invoice(), Photo(), Photo()], ClaimHistoryCounts.None, ReviewerInfoRequested: false, AutoInfoRequestCount: 0);

    private static CaseEvidence Invoice(string contentType = "application/pdf") => Evidence(EvidenceKind.Invoice, "invoice.pdf", contentType);

    private static CaseEvidence Photo(string contentType = "image/jpeg") => Evidence(EvidenceKind.Photo, "photo.jpg", contentType);

    private static CaseEvidence Evidence(EvidenceKind kind, string fileName, string contentType)
        => new(Guid.NewGuid(), kind, fileName, contentType, 2_048, new string('a', 64), 1, $"claims/{ClaimId}/1/{Guid.NewGuid()}");

    private static CustomerLookupResult Customer(string? region = "NA") => new(true, region, 0);

    private static Region? Parse(string? region) => region is null ? null : Enum.Parse<Region>(region);

    private static ValidationCheck Single(IReadOnlyList<ValidationCheck> validation, string check) => validation.Single(v => v.Check == check);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IEnumerable<string> TextsOf(AiContentPart part) => part switch
    {
        TextPart text => [text.Text],
        UntrustedTextPart untrusted => [untrusted.Label, untrusted.Text],
        ToolResultPart result => [result.Result.GetRawText()],
        ToolCallPart call => [call.Arguments.GetRawText()],
        _ => [],
    };

    /// <summary>The intake lookups with fixed answers (no personal data, as the real tools); records every call.</summary>
    private sealed class FakeLookups : IToolInvoker
    {
        private readonly HashSet<string> _failing = new(StringComparer.Ordinal);
        private readonly List<AiToolCall> _calls = [];

        public IReadOnlyList<AiToolCall> Calls => _calls;

        public IReadOnlyList<AiToolDefinition> Definitions { get; } =
        [
            new(ToolNames.CustomerLookup, "Looks up the claim's customer.", Json("""{"type":"object","additionalProperties":false,"properties":{}}""")),
            new(ToolNames.ProductLookup, "Looks up a product.", Json("""{"type":"object","additionalProperties":false,"properties":{"modelCode":{"type":"string"},"serialNumber":{"type":"string"}},"required":["modelCode","serialNumber"]}""")),
        ];

        public ProductLookupResult Product { get; set; } = new(true, true, "Aurora Tab 10", "tablet");

        public void Fail(string tool) => _failing.Add(tool);

        public Task<AiToolResult> InvokeAsync(AiToolCall call, CancellationToken ct)
        {
            _calls.Add(call);
            object content = _failing.Contains(call.ToolName)
                ? new { error = "CRM unavailable." }
                : call.ToolName switch
                {
                    ToolNames.CustomerLookup => new CustomerLookupResult(true, "NA", 0),
                    ToolNames.ProductLookup => Product,
                    _ => new { error = $"Tool '{call.ToolName}' is not available." },
                };
            var isError = _failing.Contains(call.ToolName) || call.ToolName is not (ToolNames.CustomerLookup or ToolNames.ProductLookup);
            return Task.FromResult(new AiToolResult(call.CallId, JsonSerializer.SerializeToElement(content, ToolJson.Options), isError));
        }
    }

    private sealed class FixedScenario(string scenario) : IReplayScenarioSelector
    {
        public Task<string?> SelectScenarioAsync(AiCallContext context, CancellationToken ct) => Task.FromResult<string?>(scenario);
    }
}
