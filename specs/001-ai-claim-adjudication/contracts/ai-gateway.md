# Contract: AI Gateway

**Owner project**: `Warranty.AI.Gateway` (implementation) · **Port declared in**:
`Warranty.Application/Abstractions/AI` · **Related**: [research.md](../research.md) R3–R5, R15, R16, R21

The gateway is the only path from the application to any AI model. Callers (the harness and the
knowledge ingestor) depend on the interfaces below; no caller references a vendor SDK.

## Port

```csharp
public interface IAiGateway
{
    // One model turn. The harness drives multi-turn tool loops by calling this repeatedly
    // with an append-only conversation.
    Task<AiTurnResult> CompleteAsync(AiTurnRequest request, CancellationToken ct);

    Task<AiEmbeddingResult> EmbedAsync(AiEmbeddingRequest request, CancellationToken ct);
}

public sealed record AiCallContext(          // supplied by the harness from ITenantContext,
    Guid TenantId, Guid? ClaimId, Guid? RunId, // never from model output
    string Agent, string CorrelationId);

public sealed record AiTurnRequest(
    AiCallContext Context,
    string Route,                              // "extraction" | "vision" | "policy-reasoning" | "adjudication"
    PromptRef Prompt,                          // platform-owned template id + version
    IReadOnlyDictionary<string, string> PromptVariables, // platform values only (no case data)
    AiConversation Conversation,               // append-only; see below
    IReadOnlyList<AiToolDefinition> Tools,     // already filtered to the agent's allow-list
    AiOutputSchema? OutputSchema,              // JSON Schema from contracts/schemas
    TimeSpan Timeout,
    int? MaxTokensOverride = null);            // the harness's truncation retry doubles the limit

public sealed record AiTurnResult(
    AiStopKind Stop,                           // Completed | ToolCalls | Refused | Truncated | Failed
    AiMessage AssistantMessage,                // to append to the conversation unchanged
    IReadOnlyList<AiToolCall> ToolCalls,       // when Stop == ToolCalls
    JsonElement? StructuredOutput,             // when Stop == Completed and OutputSchema != null
    AiUsage Usage,
    AiFailure? Failure,                        // Transient | RateLimited | Timeout | ProviderError | InvalidOutput
    int? MaxTokens = null);                    // output token limit the turn ran with

public sealed record AiUsage(
    string Provider, string Model, int InputTokens, int OutputTokens,
    int CacheReadTokens, int CacheWriteTokens, TimeSpan Latency, decimal EstimatedCost);
```

### Provider-neutral conversation

```csharp
public sealed class AiConversation              // append-only: Add* methods only, no edit/remove
{
    public IReadOnlyList<AiMessage> Messages { get; }
    public void AddUser(params AiContentPart[] parts);
    public void AddAssistant(AiMessage message);  // exactly as returned by the gateway
    public void AddToolResults(IReadOnlyList<AiToolResult> results); // all results of one turn in ONE message
}

public abstract record AiContentPart;
public sealed record TextPart(string Text) : AiContentPart;
public sealed record UntrustedTextPart(string Label, string Text) : AiContentPart; // wrapped in
                                                  // <untrusted_claim_content label="..."> by the gateway
public sealed record ImagePart(string EvidenceRef, ReadOnlyMemory<byte> Data, string MediaType) : AiContentPart;
public sealed record DocumentPart(string EvidenceRef, ReadOnlyMemory<byte> PdfData) : AiContentPart;
public sealed record ToolCallPart(string CallId, string ToolName, JsonElement Arguments) : AiContentPart;
public sealed record ToolResultPart(string CallId, JsonElement Result, bool IsError) : AiContentPart;
public sealed record ProviderOpaquePart(string Provider, JsonElement Payload) : AiContentPart;
```

`ProviderOpaquePart` carries provider blocks the harness must echo back unchanged within a tool
loop (e.g., thinking blocks with signatures). The harness never inspects or edits them.

## Routing and model profiles

Configuration (`appsettings.json` → `AiGateway`), never hard-coded in agents:

```json
{
  "AiGateway": {
    "Routes": {
      "extraction":       { "Provider": "anthropic", "Model": "claude-haiku-4-5", "MaxTokens": 4000,  "TimeoutSeconds": 30 },
      "vision":           { "Provider": "anthropic", "Model": "claude-opus-5-5",  "Effort": "medium", "MaxTokens": 8000,  "TimeoutSeconds": 90 },
      "policy-reasoning": { "Provider": "anthropic", "Model": "claude-opus-5-5",  "Effort": "medium", "MaxTokens": 8000,  "TimeoutSeconds": 90 },
      "adjudication":     { "Provider": "anthropic", "Model": "claude-opus-5-5",  "Effort": "high",   "MaxTokens": 16000, "TimeoutSeconds": 120 },
      "embedding":        { "Provider": "ollama",    "Model": "nomic-embed-text", "Dimensions": 768 }
    },
    "Anthropic": { "RefusalFallback": "default", "MaxRetries": 2, "NoTraining": true },
    "RateLimits": { "PerTenantRequestsPerMinute": 60 },
    "Pricing": {
      "claude-opus-5-5":  { "InputPerMTok": 4.00, "OutputPerMTok": 20.00, "CacheReadPerMTok": 0.20 },
      "claude-haiku-4-5": { "InputPerMTok": 1.00, "OutputPerMTok": 5.00 }
    }
  }
}
```

**Model profile** (built at startup from config + the provider's Models API): context window,
max output, supports images / PDFs / structured outputs / effort / thinking configuration. The
adapter uses the profile to shape requests, e.g. it never sends an effort setting to
`claude-haiku-4-5`, never sends forced `tool_choice` or assistant prefill to `claude-opus-5-5`,
and falls back to "schema in prompt + strict JSON parse" when a model lacks native structured
outputs.

## Provider port (internal to the gateway)

```csharp
internal interface IModelProvider
{
    string Name { get; }                                   // "anthropic", "replay", ...
    Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct);
}
```

Implementations: `AnthropicModelProvider` (official `Anthropic` NuGet SDK — the only reference to
it in the solution), `ReplayModelProvider` (recorded responses keyed by scenario + agent + turn),
`ScriptedModelProvider` (unit tests). Future: `ChatClientModelProvider` over
`Microsoft.Extensions.AI.IChatClient`.

Anthropic adapter rules: structured outputs via `OutputConfig.Format` (`JsonOutputFormat`);
adaptive thinking on Opus routes (thinking cannot be disabled on `claude-opus-5-5`); effort set
explicitly from the route; tools sent with `strict: true` and `tool_choice: auto`; images as base64
image blocks, PDFs as `DocumentBlockParam` with `Base64PdfSource`; stable platform content
(tools, system prompt, global knowledge) placed before a `cache_control` breakpoint, tenant/case
content after it; server-side refusal fallback enabled; `stop_reason` mapped to `AiStopKind`
(`refusal` → `Refused`, `max_tokens` → `Truncated`).

## Prompt templates

- Stored as versioned, platform-owned files: `PromptTemplates/{agent}.v{n}.md` (system prompt) with
  front matter `{ id, version, route, outputSchema }`.
- Templates contain **no tenant or case data**; case data is supplied in the user turn by the
  harness's context builder. `prompt_id` and `prompt_version` are stored on every model call.

## Privacy controls

```csharp
public interface IPiiRedactor { RedactionResult Redact(string text); } // emails, phones → [EMAIL], [PHONE]
```

Applied to every `TextPart`/`UntrustedTextPart` before sending and before logging. Customer
identity fields (name, email, phone, street address) are never placed in prompts by the context
builder (`[CUSTOMER]`, `[EMAIL]`, `[PHONE]`, `[ADDRESS]` placeholders) — spec FR-006a, research
R28. Evidence files are sent as submitted.

Provider data use: every chat provider entry declares `"NoTraining": true` (e.g.
`"Anthropic": { "NoTraining": true, ... }`); at startup the gateway refuses to register a route
whose provider lacks it.

## Usage, logging and cost

Every `CompleteAsync`/`EmbedAsync` writes one `aiops.model_calls` row (see
[data-model.md](../data-model.md)) and an OpenTelemetry span with GenAI attributes
(`gen_ai.system`, `gen_ai.request.model`, `gen_ai.usage.input_tokens`,
`gen_ai.usage.output_tokens`) plus `warranty.tenant_id`, `warranty.claim_id`, `warranty.run_id`,
`warranty.agent`, `warranty.route`, `warranty.prompt_version`. Estimated cost = usage × `Pricing`.

## Failure semantics

| Failure | Gateway behaviour | Harness behaviour |
|---------|-------------------|-------------------|
| `429` / `5xx` / network | SDK retries (max 2) with backoff | If still failing → `AiStepFailed` → human review (FR-031) |
| Per-tenant rate limit exceeded | Waits up to the call timeout, then `RateLimited` | Same as above |
| Timeout | `Timeout` | Same as above |
| `stop_reason: refusal` (after fallback) | `Refused` | Same as above |
| `stop_reason: max_tokens` | `Truncated` | One retry with a larger `MaxTokens`, then human review |
| Output fails schema | `InvalidOutput` with validation errors | One corrective turn (errors appended), then recommendation marked invalid → human review (FR-023) |
