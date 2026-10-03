using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Prompts;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.RateLimiting;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Gateway.Usage;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Domain.AiOps;

namespace Warranty.AI.Gateway;

/// <summary>
/// The <see cref="IAiGateway"/> (contracts/ai-gateway.md, research R3): routes a turn by task to its
/// provider and model, renders the platform prompt template, redacts personal data from text parts,
/// applies the per-tenant rate limit and the call timeout, validates structured output against the
/// requested schema, prices the usage and records one model call with a GenAI span. Model failures
/// come back as <see cref="AiStopKind.Failed"/> results for the harness to escalate; only programming
/// errors (unknown route or prompt, a call attributed to another tenant) throw.
/// </summary>
internal sealed class AiGateway(
    ModelProfileRegistry routes,
    PromptTemplateRegistry prompts,
    IEnumerable<IModelProvider> providers,
    IEnumerable<IEmbeddingGenerator<string, Embedding<float>>> embeddingGenerators,
    IPiiRedactor redactor,
    TenantRateLimiter rateLimiter,
    UsageRecorder usage,
    ITenantContext tenantContext,
    IOptions<AiGatewayOptions> options,
    TimeProvider time,
    ILogger<AiGateway> logger) : IAiGateway
{
    public const string ActivitySourceName = "Warranty.AI.Gateway";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    public async Task<AiTurnResult> CompleteAsync(AiTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant(request.Context);

        var route = routes.Resolve(request.Route);
        if (route.Options.Dimensions is not null)
        {
            throw new ArgumentException($"Route '{request.Route}' is an embedding route.", nameof(request));
        }

        var template = prompts.Get(request.Prompt);
        if (template.Route != request.Route)
        {
            throw new ArgumentException($"Prompt {template.Ref} belongs to route '{template.Route}', not '{request.Route}'.", nameof(request));
        }

        if (request.OutputSchema is { } schema && template.OutputSchema is { } expected && schema.SchemaId != expected)
        {
            throw new ArgumentException($"Prompt {template.Ref} expects output schema '{expected}', not '{schema.SchemaId}'.", nameof(request));
        }

        var providerName = options.Value.Mode == AiGatewayOptions.ReplayMode ? AiGatewayOptions.ReplayMode : route.Options.Provider;
        var provider = providers.FirstOrDefault(p => p.Name == providerName)
                       ?? throw new InvalidOperationException($"AI provider '{providerName}' is not registered.");
        var timeout = request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.FromSeconds(route.Options.TimeoutSeconds);
        var maxTokens = Math.Min(request.MaxTokensOverride ?? route.Options.MaxTokens, route.Profile.MaxOutputTokens);
        var resolved = new ResolvedTurnRequest(
            request, route, template, template.Render(request.PromptVariables), Redact(request.Conversation.Messages), maxTokens);

        using var activity = StartActivity("chat", request.Context, route, providerName)
            ?.SetTag("warranty.prompt_id", template.Id)
            .SetTag("warranty.prompt_version", template.Version);
        var startedAt = time.GetUtcNow();
        var start = time.GetTimestamp();

        var result = await InvokeAsync(provider, resolved, timeout, start, ct);
        result = Validate(result, request.OutputSchema);

        var raw = result.Usage;
        result = result with
        {
            Usage = raw with
            {
                Latency = time.GetElapsedTime(start),
                EstimatedCost = usage.EstimateCost(raw.Model, raw.InputTokens, raw.OutputTokens, raw.CacheReadTokens, raw.CacheWriteTokens),
            },
        };

        usage.Record(
            request.Context, request.Route, template.Ref, result.Usage, StatusOf(result), StopReasonOf(result.Stop), result.Failure?.Message, startedAt);
        activity?.SetTag("gen_ai.usage.input_tokens", result.Usage.InputTokens)
            .SetTag("gen_ai.usage.output_tokens", result.Usage.OutputTokens)
            .SetTag("gen_ai.response.finish_reasons", StopReasonOf(result.Stop))
            .SetTag("warranty.cost_usd", result.Usage.EstimatedCost);
        if (result.Failure is { } failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure.Kind.ToString());
        }

        logger.LogInformation(
            "AI turn {Route} via {Provider}/{Model} for {Agent}: {Stop} ({InputTokens} in, {OutputTokens} out, {LatencyMs} ms).",
            request.Route, result.Usage.Provider, result.Usage.Model, request.Context.Agent, result.Stop,
            result.Usage.InputTokens, result.Usage.OutputTokens, (long)result.Usage.Latency.TotalMilliseconds);
        return result;
    }

    public async Task<AiEmbeddingResult> EmbedAsync(AiEmbeddingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant(request.Context);

        var route = routes.Resolve(request.Route);
        var dimensions = route.Options.Dimensions
                         ?? throw new ArgumentException($"Route '{request.Route}' is not an embedding route.", nameof(request));
        var generator = embeddingGenerators.FirstOrDefault()
                        ?? throw new InvalidOperationException("No embedding generator is registered.");

        using var activity = StartActivity("embeddings", request.Context, route, route.Options.Provider);
        var startedAt = time.GetUtcNow();
        var start = time.GetTimestamp();
        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await generator.GenerateAsync(
                request.Inputs.Select(input => redactor.Redact(input).Text).ToList(),
                new EmbeddingGenerationOptions { ModelId = route.Options.Model },
                ct);
            if (generated.Count != request.Inputs.Count || generated.Any(e => e.Vector.Length != dimensions))
            {
                throw new InvalidOperationException(
                    $"Embedding model '{route.Options.Model}' returned vectors that do not match {request.Inputs.Count} × {dimensions}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failed = AiUsage.None(route.Options.Provider, route.Options.Model) with { Latency = time.GetElapsedTime(start) };
            usage.Record(request.Context, request.Route, null, failed, ModelCallStatus.Error, null, ex.Message, startedAt);
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }

        var inputTokens = (int)(generated.Usage?.InputTokenCount ?? 0);
        var callUsage = new AiUsage(
            route.Options.Provider, route.Options.Model, inputTokens, 0, 0, 0, time.GetElapsedTime(start),
            usage.EstimateCost(route.Options.Model, inputTokens, 0, 0, 0));
        usage.Record(request.Context, request.Route, null, callUsage, ModelCallStatus.Ok, null, null, startedAt);
        activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);

        return new AiEmbeddingResult(generated.Select(e => e.Vector).ToList(), route.Options.Model, dimensions, callUsage);
    }

    /// <summary>Waits for the tenant's rate limit and calls the provider within what is left of the timeout.</summary>
    private async Task<AiTurnResult> InvokeAsync(
        IModelProvider provider, ResolvedTurnRequest resolved, TimeSpan timeout, long start, CancellationToken ct)
    {
        var context = resolved.Request.Context;
        var model = resolved.Route.Options.Model;
        if (context.TenantId != Guid.Empty && !await rateLimiter.TryAcquireAsync(context.TenantId, timeout, ct))
        {
            return Failure(provider.Name, model, AiFailureKind.RateLimited, "The tenant's AI request rate limit was exceeded.");
        }

        var remaining = timeout - time.GetElapsedTime(start);
        if (remaining <= TimeSpan.Zero)
        {
            return Failure(provider.Name, model, AiFailureKind.Timeout, "The AI call timed out waiting for the rate limit.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(remaining);
        try
        {
            return await provider.CompleteAsync(resolved, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(provider.Name, model, AiFailureKind.Timeout, $"The AI call exceeded {timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI provider {Provider} failed unexpectedly.", provider.Name);
            return Failure(provider.Name, model, AiFailureKind.ProviderError, ex.GetType().Name);
        }
    }

    /// <summary>A completed turn that owes structured output must deliver it, valid against the schema.</summary>
    private static AiTurnResult Validate(AiTurnResult result, AiOutputSchema? schema)
    {
        if (result.Stop != AiStopKind.Completed || schema is null)
        {
            return result;
        }

        if (result.StructuredOutput is not { } output)
        {
            return result with
            {
                Stop = AiStopKind.Failed,
                Failure = new AiFailure(AiFailureKind.InvalidOutput, "The model returned no structured output."),
            };
        }

        var evaluation = SchemaFor(schema).Evaluate(output, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid)
        {
            return result;
        }

        var errors = (evaluation.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return result with
        {
            Stop = AiStopKind.Failed,
            StructuredOutput = null,
            Failure = new AiFailure(AiFailureKind.InvalidOutput, $"The output does not match schema '{schema.SchemaId}'.", errors),
        };
    }

    private static JsonSchema SchemaFor(AiOutputSchema schema)
        => Schemas.GetOrAdd(
            $"{schema.SchemaId}\n{schema.Schema.GetRawText()}",
            _ => JsonSchema.Build(schema.Schema, new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

    /// <summary>Text the harness built from case data or claimant input never leaves unredacted.</summary>
    private List<AiMessage> Redact(IReadOnlyList<AiMessage> messages)
        => messages.Select(message => message with
        {
            Parts = message.Parts.Select(part => part switch
            {
                TextPart text => new TextPart(redactor.Redact(text.Text).Text),
                UntrustedTextPart untrusted => untrusted with { Text = redactor.Redact(untrusted.Text).Text },
                _ => part,
            }).ToList(),
        }).ToList();

    /// <summary>A call must belong to the tenant in scope (or to no tenant, for platform work).</summary>
    private void EnsureTenant(AiCallContext context)
    {
        if (context.TenantId == Guid.Empty)
        {
            return;
        }

        if (!tenantContext.IsResolved || tenantContext.TenantId != context.TenantId)
        {
            throw new InvalidOperationException("An AI call was attributed to a tenant other than the one in scope.");
        }
    }

    private static Activity? StartActivity(string operation, AiCallContext context, ResolvedRoute route, string provider)
        => ActivitySource.StartActivity($"{operation} {route.Options.Model}", ActivityKind.Client)
            ?.SetTag("gen_ai.operation.name", operation)
            .SetTag("gen_ai.system", provider)
            .SetTag("gen_ai.request.model", route.Options.Model)
            .SetTag("warranty.tenant_id", context.TenantId)
            .SetTag("warranty.claim_id", context.ClaimId)
            .SetTag("warranty.run_id", context.RunId)
            .SetTag("warranty.agent", context.Agent)
            .SetTag("warranty.route", route.Name);

    private static AiTurnResult Failure(string provider, string model, AiFailureKind kind, string message)
        => new(AiStopKind.Failed, new AiMessage(AiRole.Assistant, []), [], null, AiUsage.None(provider, model), new AiFailure(kind, message));

    private static ModelCallStatus StatusOf(AiTurnResult result) => result.Stop switch
    {
        AiStopKind.Completed or AiStopKind.ToolCalls => ModelCallStatus.Ok,
        AiStopKind.Refused => ModelCallStatus.Refused,
        AiStopKind.Truncated => ModelCallStatus.Invalid,
        _ => result.Failure?.Kind switch
        {
            AiFailureKind.Timeout => ModelCallStatus.Timeout,
            AiFailureKind.InvalidOutput => ModelCallStatus.Invalid,
            _ => ModelCallStatus.Error,
        },
    };

    private static string StopReasonOf(AiStopKind stop) => stop switch
    {
        AiStopKind.Completed => "completed",
        AiStopKind.ToolCalls => "tool_calls",
        AiStopKind.Refused => "refused",
        AiStopKind.Truncated => "truncated",
        _ => "failed",
    };
}
