using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Warranty.AI.Gateway;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.Providers.Anthropic;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Harness;
using Warranty.Application;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Adjudication;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Policies;
using Warranty.Domain.Tenancy;
using Warranty.Guardrails;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Gateway;

/// <summary>
/// No customer identifier reaches the AI provider, and only providers that do not train on API inputs
/// may serve a chat route (FR-006a, research R28).
/// </summary>
public sealed partial class PromptPrivacyTests
{
    // Distinctive synthetic identifiers, so any occurrence in a request is a leak and not a coincidence.
    private const string CustomerName = "Philippa Quarrington-Vossberg";
    private const string CustomerEmail = "philippa.quarrington@privacy-probe.test";
    private const string CustomerPhone = "+1 (415) 555-0137";
    private const string StreetAddress = "1789 Juniper Crescent Road";

    private const string ProblemDescription =
        $"My name is {CustomerName}. The laptop screen went black a week ago and never came back. " +
        $"Please email me at {CustomerEmail} or call {CustomerPhone}; send the replacement to {StreetAddress}.";

    private const string ModelCode = "AUR-LT-14";
    private const string Serial = "SNPRIV0001";

    private static readonly Guid TenantId = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000c5");
    private static readonly Guid CustomerId = Guid.Parse("0199a000-0000-7000-8000-0000000000d5");
    private static readonly Guid ProductId = Guid.Parse("0199a000-0000-7000-8000-0000000000e5");
    private static readonly Guid PolicyId = Guid.Parse("0199a000-0000-7000-8000-0000000000f5");
    private static readonly Guid PolicyVersionId = Guid.Parse("0199a000-0000-7000-8000-0000000000f6");
    private static readonly DateOnly PurchaseDate = new(2026, 3, 14);
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

    private static readonly string[] Agents = ["intake", "evidence-invoice", "evidence-photo", "policy", "decision"];
    private static readonly string[] Placeholders =
    [
        CaseCustomerView.NamePlaceholder, CaseCustomerView.EmailPlaceholder, CaseCustomerView.PhonePlaceholder, CaseCustomerView.AddressPlaceholder,
    ];

    /// <summary>Read-only lookups the scripted model calls on an agent's first turn, so tool results are checked too.</summary>
    private static readonly HashSet<string> LookupTools = new(["customer_lookup", "product_lookup", "claim_history_lookup"], StringComparer.Ordinal);

    // A valid 1x1 PNG and a minimal PDF: evidence files are sent as submitted (R28).
    private static readonly byte[] PhotoBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static readonly byte[] InvoiceBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly Customer _customer = Customer.Create(
        CustomerId, TenantId, CustomerName, CustomerEmail, "US", CustomerPhone, StreetAddress, "Sausalito", "94965");

    [Fact(Skip = "Pending T068")]
    public async Task No_customer_identifier_reaches_any_model_request_of_a_full_run()
    {
        var model = ScriptedModel();
        await using var provider = BuildPipeline(model);
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAdjudicationRunner>().RunAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        var requests = model.Requests;
        requests.Select(r => r.Request.Context.Agent).Distinct().ShouldBe(Agents, ignoreOrder: true);
        requests.SelectMany(r => r.Messages).SelectMany(m => m.Parts).OfType<ToolResultPart>()
            .ShouldNotBeEmpty("the scripted model calls the lookup tools, so tool results are covered as well");

        var texts = requests.SelectMany(TextsOf).ToList();
        Leaks(texts).ShouldBeEmpty();
        foreach (var placeholder in Placeholders)
        {
            texts.ShouldContain(t => t.Text.Contains(placeholder, StringComparison.Ordinal), $"placeholder {placeholder} should replace the identifier");
        }
    }

    [Fact]
    public async Task The_case_context_carries_placeholders_instead_of_customer_identifiers()
    {
        await using var provider = BuildPipeline(ScriptedModel());
        await using var scope = provider.CreateAsyncScope();

        var context = await scope.ServiceProvider.GetRequiredService<ICaseKnowledgeProvider>()
            .GetCaseContextAsync(ClaimId, 1, TestContext.Current.CancellationToken);

        Leaks(StringsOf(JsonSerializer.SerializeToElement(context)).Select(s => ("case context", s)).ToList()).ShouldBeEmpty();
        foreach (var placeholder in Placeholders)
        {
            context.ProblemDescription.ShouldContain(placeholder);
        }

        context.Customer.ShouldBe(new CaseCustomerView("US", Region.NA));
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public void The_gateway_refuses_to_start_when_a_provider_section_lacks_no_training(string? noTraining)
    {
        using var provider = GatewayOnly(GatewayConfiguration(noTraining));

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        failure.Failures.ShouldContain(f =>
            f.Contains("Route 'adjudication' is refused", StringComparison.Ordinal) && f.Contains("NoTraining", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chat_provider_without_its_own_no_training_section_is_refused()
    {
        using var provider = GatewayOnly(GatewayConfiguration("true", ("AiGateway:Routes:adjudication:Provider", ScriptedModelProvider.DefaultName)));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Failures.ShouldContain(f => f.Contains("Route 'adjudication' is refused", StringComparison.Ordinal));
    }

    [Fact]
    public void The_gateway_starts_when_every_chat_provider_declares_no_training()
    {
        using var provider = GatewayOnly(GatewayConfiguration("true"));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    /// <summary>Every customer identifier, as typed and as the CRM normalizes it.</summary>
    private string[] Identifiers() =>
    [
        CustomerName,
        "Quarrington",
        "Vossberg",
        CustomerEmail,
        _customer.Email,
        CustomerPhone,
        _customer.Phone!,
        "555-0137",
        StreetAddress,
        "Juniper Crescent",
    ];

    private List<string> Leaks(IReadOnlyList<(string Where, string Text)> texts)
        => texts
            .SelectMany(t => Identifiers()
                .Where(id => t.Text.Contains(id, StringComparison.OrdinalIgnoreCase))
                .Select(id => $"{t.Where} contains '{id}'"))
            .Distinct()
            .ToList();

    /// <summary>All text a provider would receive: system prompt, tool definitions and every message part except file bytes.</summary>
    private static IEnumerable<(string Where, string Text)> TextsOf(ResolvedTurnRequest request)
    {
        var agent = request.Request.Context.Agent;
        var texts = new List<(string, string)> { ($"{agent}: system prompt", request.SystemPrompt) };
        foreach (var tool in request.Request.Tools)
        {
            texts.Add(($"{agent}: tool {tool.Name}", tool.Description));
            texts.AddRange(StringsOf(tool.InputSchema).Select(s => ($"{agent}: tool {tool.Name} schema", s)));
        }

        foreach (var part in request.Messages.SelectMany(m => m.Parts))
        {
            switch (part)
            {
                case TextPart text:
                    texts.Add(($"{agent}: message", text.Text));
                    break;
                case UntrustedTextPart untrusted:
                    texts.Add(($"{agent}: untrusted {untrusted.Label}", untrusted.Label));
                    texts.Add(($"{agent}: untrusted {untrusted.Label}", untrusted.Text));
                    break;
                case ToolCallPart call:
                    texts.AddRange(StringsOf(call.Arguments).Select(s => ($"{agent}: tool call {call.ToolName}", s)));
                    break;
                case ToolResultPart result:
                    texts.AddRange(StringsOf(result.Result).Select(s => ($"{agent}: tool result {result.CallId}", s)));
                    break;
                case ProviderOpaquePart opaque:
                    texts.AddRange(StringsOf(opaque.Payload).Select(s => ($"{agent}: provider part", s)));
                    break;
            }
        }

        return texts;
    }

    /// <summary>Decoded property names and values of a JSON element (escapes such as <c>+</c> cannot hide a value).</summary>
    private static IEnumerable<string> StringsOf(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var value in StringsOf(property.Value))
                    {
                        yield return value;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray().SelectMany(StringsOf))
                {
                    yield return value;
                }

                break;
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Number:
                yield return element.GetRawText();
                break;
        }
    }

    /// <summary>
    /// A scripted model named like the Anthropic provider (so the no-training check passes) that, per
    /// agent, first calls the offered lookup tools and then answers with a schema-valid output.
    /// </summary>
    private static ScriptedModelProvider ScriptedModel()
    {
        var model = new ScriptedModelProvider(AnthropicModelProvider.ProviderName);
        for (var i = 0; i < 40; i++)
        {
            model.Enqueue(Answer);
        }

        return model;
    }

    private static AiTurnResult Answer(ResolvedTurnRequest request)
    {
        var hasToolResults = request.Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Any();
        var lookups = request.Request.Tools.Select(t => t.Name).Where(LookupTools.Contains).ToArray();
        if (!hasToolResults && lookups.Length > 0)
        {
            return ScriptedModelProvider.ToolCalls(lookups.Select((name, i) => new AiToolCall($"call-{i + 1}", name, NoArguments)).ToArray());
        }

        var evidenceRef = EvidenceRef().Match(string.Join('\n', TextsOf(request).Select(t => t.Text))) is { Success: true } match ? match.Value : "EV-1";
        return ScriptedModelProvider.Completed(request.Request.OutputSchema?.SchemaId switch
        {
            "warranty-ai/intake-extraction/v1" => """
                {"problemCategory":"DISPLAY_DEFECT","component":"SCREEN","symptoms":["screen stays black"],
                 "claimedCause":"SPONTANEOUS_FAILURE","mentionsAccident":false,"mentionsLiquid":false,
                 "containsInstructionsToSystem":false,"summary":"The screen went black and stayed black."}
                """,
            "warranty-ai/invoice-extraction/v1" => $$"""
                {"evidenceRef":"{{evidenceRef}}","legible":true,"sellerName":"Brightline Electronics","invoiceNumber":"INV-2026-0042",
                 "invoiceDate":"2026-03-14","productDescription":"Aurora 14 laptop","modelCodeOnInvoice":"{{ModelCode}}",
                 "serialOnInvoice":"{{Serial}}","totalAmount":899.00,"currency":"USD","anomalies":[],"containsInstructionsToSystem":false}
                """,
            "warranty-ai/photo-analysis/v1" => $$"""
                {"evidenceRef":"{{evidenceRef}}","showsProduct":true,"productTypeObserved":"laptop","visibleSerial":"{{Serial}}",
                 "damageObserved":false,"damageTypes":["NONE_VISIBLE"],"consistentWithDescription":"CONSISTENT","imageQuality":"GOOD",
                 "containsInstructionsToSystem":false,"confidence":90,"observations":"A closed laptop; no physical damage is visible."}
                """,
            "warranty-ai/policy-assessment/v1" => """
                {"applicableClauses":[],"coverageAssessment":"UNDETERMINED","confidence":50,"relevantExclusions":[],
                 "ambiguity":{"isAmbiguous":false,"explanation":"Not applicable."},"summary":"No retrieved clause decides the claim."}
                """,
            "warranty-ai/decision-recommendation/v1" => """
                {"decision":"HUMAN_REVIEW","coverage":"UNDETERMINED","confidence":50,"risk":{"level":"LOW","signals":[]},
                 "evidenceRefs":[],"policyRefs":[],"missingInformation":[],
                 "reasoningSummary":"Scripted recommendation for the prompt privacy test.",
                 "claimantExplanation":"Our team will review your claim and let you know the outcome.","manipulationDetected":false}
                """,
            var other => throw new InvalidOperationException($"No scripted output for schema '{other}'."),
        });
    }

    [GeneratedRegex(@"\bEV-\d+\b")]
    private static partial Regex EvidenceRef();

    /// <summary>Gateway, guardrails, application and harness with the scripted model and substituted ports.</summary>
    private ServiceProvider BuildPipeline(ScriptedModelProvider model)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(TenantId));
        services.AddWarrantyAiGateway(GatewayConfiguration("true"));
        services.RemoveAll<IModelProvider>();
        services.AddSingleton<IModelProvider>(model);
        services.AddWarrantyGuardrails();
        services.AddWarrantyApplication();
        services.AddWarrantyAiHarness();
        RegisterPorts(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>The test claim's data behind the ports; every other port is an empty substitute.</summary>
    private void RegisterPorts(IServiceCollection services)
    {
        var product = Product.Create(ProductId, TenantId, ModelCode, "Aurora 14 laptop", "laptop", 899m, "USD");
        var claim = Claim.Submit(
            ClaimId, TenantId, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, CustomerId,
            CustomerEmail, CustomerPhone, ModelCode, ProductId, Serial, PurchaseDate, "Brightline Electronics", 899m, Region.NA,
            ProblemDescription, SubmittedAt);
        var photo = Evidence(EvidenceKind.Photo, "screen.png", "image/png", PhotoBytes);
        var invoice = Evidence(EvidenceKind.Invoice, "invoice.pdf", "application/pdf", InvoiceBytes);
        var terms = new CoverageTerms(
            new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 }, new Dictionary<string, int>(), AccidentalDamageTerms.NotCovered, []);
        var version = PolicyVersion.Create(
            PolicyVersionId, TenantId, PolicyId, 1, new DateOnly(2026, 1, 1), null, [Region.NA, Region.EU], ["laptop"], terms,
            "policies/aurora/standard-v1.md", "checksum");
        var clause = new RetrievedChunk(
            Guid.NewGuid(), "tenant-aurora", Guid.NewGuid(), "Aurora Standard Warranty", 1, "COV-1", "Coverage",
            "Manufacturing defects are covered for 12 months in North America.", version.EffectiveFrom, null, 0.92, TenantId,
            PolicyVersionId, ClauseType.Coverage);

        var claims = Substitute.For<IClaimRepository>();
        claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(claim);
        claims.GetEvidenceAsync(ClaimId, Arg.Any<CancellationToken>()).Returns([photo, invoice]);
        claims.GetEvidenceItemAsync(ClaimId, photo.Id, Arg.Any<CancellationToken>()).Returns(photo);
        claims.GetEvidenceItemAsync(ClaimId, invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        claims.GetHistoryCountsAsync(default, default!, default, default!, default)
            .ReturnsForAnyArgs(ClaimHistoryCounts.None);

        var customers = Substitute.For<ICustomerRepository>();
        customers.GetAsync(CustomerId, Arg.Any<CancellationToken>()).Returns(_customer);
        var crm = Substitute.For<ICrmClient>();
        crm.GetCustomerAsync(CustomerId, Arg.Any<CancellationToken>()).Returns(_customer);

        var catalog = Substitute.For<ICatalogRepository>();
        catalog.FindProductByModelAsync(ModelCode, Arg.Any<CancellationToken>()).Returns(product);
        catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>()).Returns(product);
        catalog.FindSerialAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(TenantId, Serial, ProductId));

        var tenants = Substitute.For<ITenantRepository>();
        tenants.GetCurrentSettingsAsync(Arg.Any<CancellationToken>()).Returns(TenantSettings.Create(TenantId, "USD", 500m, 80));

        var policies = Substitute.For<IPolicyRepository>();
        policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns([version]);
        policies.GetVersionAsync(PolicyVersionId, Arg.Any<CancellationToken>()).Returns(version);
        policies.GetClausesAsync(PolicyVersionId, Arg.Any<CancellationToken>()).Returns([]);

        var knowledge = Substitute.For<IKnowledgeRetriever>();
        knowledge.RetrievePolicyClausesAsync(default!, default).ReturnsForAnyArgs(new RetrievalResult([clause], RetrievalOutcome.Ok));
        knowledge.SearchAsync(default!, default).ReturnsForAnyArgs(RetrievalResult.Empty(RetrievalOutcome.Ok));

        var documents = Substitute.For<IDocumentStore>();
        documents.OpenEvidenceAsync(photo.BlobPath, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(PhotoBytes));
        documents.OpenEvidenceAsync(invoice.BlobPath, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(InvoiceBytes));

        services.AddScoped(_ => claims);
        services.AddScoped(_ => customers);
        services.AddScoped(_ => crm);
        services.AddScoped(_ => catalog);
        services.AddScoped(_ => tenants);
        services.AddScoped(_ => policies);
        services.AddScoped(_ => knowledge);
        services.AddScoped(_ => documents);

        // Every other port (unit of work, adjudication and AI-ops repositories, trail and security writers, …)
        // is an empty substitute. The units under test — runner, case knowledge provider, agents — stay real.
        var real = new HashSet<Type> { typeof(IAdjudicationRunner), typeof(ICaseKnowledgeProvider) };
        var ports = typeof(IAdjudicationRunner).Assembly.GetExportedTypes()
            .Where(t => t.IsInterface && !t.IsGenericTypeDefinition && !real.Contains(t)
                        && t.Namespace?.StartsWith("Warranty.Application.Abstractions", StringComparison.Ordinal) == true);
        foreach (var port in ports)
        {
            services.TryAdd(ServiceDescriptor.Scoped(port, _ => Substitute.For([port], [])));
        }
    }

    private static ClaimEvidence Evidence(EvidenceKind kind, string fileName, string contentType, byte[] content)
        => ClaimEvidence.Create(
            Guid.NewGuid(), TenantId, ClaimId, 1, kind, fileName, contentType, content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content)), SubmittedAt);

    private static ServiceProvider GatewayOnly(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(TenantId));
        services.AddScoped(_ => Substitute.For<IAiOpsRepository>());
        services.AddScoped(_ => Substitute.For<IClaimRepository>());
        services.AddWarrantyAiGateway(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>
    /// A self-contained <c>AiGateway</c> section: four Anthropic chat routes and a local hash embedding
    /// route. <paramref name="noTraining"/> null leaves <c>NoTraining</c> out of the provider section.
    /// </summary>
    private static IConfiguration GatewayConfiguration(string? noTraining, params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["AiGateway:Mode"] = AiGatewayOptions.LiveMode,
            ["AiGateway:Routes:embedding:Provider"] = HashEmbeddingGenerator.ProviderName,
            ["AiGateway:Routes:embedding:Model"] = "hash",
            ["AiGateway:Routes:embedding:Dimensions"] = "768",
            ["AiGateway:Anthropic:MaxRetries"] = "0",
            ["AiGateway:RateLimits:PerTenantRequestsPerMinute"] = "600",
        };
        foreach (var (route, model) in new[]
                 {
                     ("extraction", "claude-haiku-4-5"), ("vision", "claude-opus-5-5"), ("policy-reasoning", "claude-opus-5-5"),
                     ("adjudication", "claude-opus-5-5"),
                 })
        {
            values[$"AiGateway:Routes:{route}:Provider"] = AnthropicModelProvider.ProviderName;
            values[$"AiGateway:Routes:{route}:Model"] = model;
            values[$"AiGateway:Routes:{route}:MaxTokens"] = "4000";
            values[$"AiGateway:Routes:{route}:TimeoutSeconds"] = "30";
        }

        if (noTraining is not null)
        {
            values["AiGateway:Anthropic:NoTraining"] = noTraining;
        }

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
