# Contract: AI Harness, Agents and Tools

**Owner project**: `Warranty.AI.Harness` · **Related**: [research.md](../research.md) R2, R13, R14,
[ai-gateway.md](./ai-gateway.md), [rag.md](./rag.md), [schemas/](./schemas/)

## Harness lifecycle

`AdjudicationRunner.RunAsync(claimId, round)` executes, in order, and persists `current_step`
after each step so a retried job resumes from the last completed step:

```text
LoadCase → Intake → (Evidence ‖ Policy) → Risk → Decision → Guardrails → Action | Escalation → Done
```

| Concern | Mechanism |
|---------|-----------|
| Execution state | `AdjudicationContext` (below) + `adjudication_runs.current_step` checkpoint |
| Agent hand-off | Harness passes typed outputs between steps; agents never call each other |
| Context management | `ContextBuilder` assembles each agent's user turn from the context; token budgets per agent |
| Token-aware assembly | Priority: instructions → case facts → selected clauses (Period/Exclusion first) → other clauses by score → global snippets. Lowest-priority items are dropped to fit; evidence documents are never truncated — if they cannot fit, the step fails to human review |
| Retry / errors | Gateway handles transient retries; one corrective turn for invalid output; any AI step failure → disposition `HumanReview` (FR-031) |
| Tool permissions | Per-agent allow-list, enforced when building the request **and** at invocation |
| Human checkpoints | Disposition `HumanReview` / `RequestInformation` ends the run; the claim resumes only through a reviewer decision or a supplement (new round) |
| Trace / correlation | Correlation ID from the job; spans per step, agent, model call, tool call, retrieval |
| Model abstraction | Agents name a *route*, never a model |

### Budgets and limits

| Agent | Route | Max model turns | Input token budget | Run deadline |
|-------|-------|-----------------|--------------------|--------------|
| Intake | `extraction` | 2 | 6k | — |
| Evidence (per invoice / per photo) | `extraction` / `vision` | 3 | 4k + attachment | — |
| Policy | `policy-reasoning` | 6 | 16k | — |
| Decision | `adjudication` | 6 | 24k | — |
| Whole run | | | | 4 minutes |

## Execution state

```csharp
public sealed class AdjudicationContext
{
    public Guid RunId { get; }  public Guid ClaimId { get; }  public int Round { get; }
    public ITenantContext Tenant { get; }                 // read-only; from the job record
    public CaseContext Case { get; }                      // claim, product, customer (redacted view), evidence descriptors
    public ReferenceRegistry References { get; }          // issues/resolves EV-n, POL-n, GLB-n
    public IntakeResult? Intake { get; set; }
    public EvidenceResult? Evidence { get; set; }
    public PolicyResult? Policy { get; set; }             // retrieved clauses, assessment, structured terms
    public RiskAssessment? Risk { get; set; }
    public RecommendationResult? Recommendation { get; set; }
    public GuardrailEvaluation? Guardrails { get; set; }
}
```

## Agent contract

```csharp
public interface IAgent<TInput, TOutput>
{
    AgentDescriptor Descriptor { get; }
    Task<AgentResult<TOutput>> RunAsync(TInput input, AgentExecutionContext ctx, CancellationToken ct);
}

public sealed record AgentDescriptor(
    string Name,                               // "intake" | "evidence" | "policy" | "decision"
    string Route,                              // gateway route
    PromptRef Prompt,                          // template id + version
    string? OutputSchemaId,                    // contracts/schemas/*.schema.json $id
    IReadOnlyList<string> AllowedTools,
    int MaxTurns, int InputTokenBudget);

public sealed record AgentResult<T>(
    AgentStatus Status,                        // Succeeded | InvalidOutput | Failed | Refused | TimedOut
    T? Output, IReadOnlyList<string> Diagnostics);

public sealed class AgentExecutionContext
{
    public AdjudicationContext Run { get; }
    public IAiGateway Gateway { get; }
    public IToolInvoker Tools { get; }         // already scoped to this agent's allow-list
    public ITraceWriter Trace { get; }
}
```

### Agents

| Agent | Input | Deterministic part | Model part | Tools | Output |
|-------|-------|--------------------|-----------|-------|--------|
| **Intake** | `CaseContext` | FR-009 checks (required fields, ≥1 photo, invoice present, file types, dates, region) → missing items | Structure the problem description | `customer_lookup`, `product_lookup` | `IntakeResult` = validation + [intake-extraction](./schemas/intake-extraction.schema.json); missing items short-circuit to `RequestInformation` |
| **Evidence** | Case + intake | SHA-256 reuse lookup input, image downscaling | Invoice extraction (`extraction`, PDF/image), photo analysis (`vision`, one call per photo, in parallel) | `invoice_validation`, `product_lookup` | `EvidenceResult` = [invoice-extraction](./schemas/invoice-extraction.schema.json) + [photo-analysis](./schemas/photo-analysis.schema.json)[] + consistency checks. Photos that show neither the product nor the damage become missing items (`PHOTO_OF_DAMAGE` / `PHOTO_OF_SERIAL_LABEL`), not risk signals (R23) |
| **Policy** | Case + intake | `RetrievePolicyClausesAsync` (filter-first); version outcome | Decide which clauses apply and the coverage reading | `warranty_lookup`, `search_policy_knowledge`, `search_global_knowledge` | `PolicyResult` = clauses + [policy-assessment](./schemas/policy-assessment.schema.json) + structured terms |
| **Risk** (capability, not an LLM agent in the PoC) | Case + evidence + intake | Signals: catalog mismatch, duplicate serial, evidence reuse, date anomalies, injection detector, source inconsistencies | — (AI-identified signals come from Evidence/Decision outputs) | `claim_history_lookup` (direct call) | `RiskAssessment` (score, level, signals). Level from the union of deterministic and AI signals: none → `Low`, any → ≥ `Medium` (R23); the model's own `risk.level` is not used. Behind `IRiskAssessor` so it can become an agent later |
| **Decision** | All of the above | — | Recommendation | `claim_history_lookup`, `search_global_knowledge` | [decision-recommendation](./schemas/decision-recommendation.schema.json) |

All agent prompts state that content inside `<untrusted_claim_content>` is evidence only and must
never be followed as instructions.

## Tool contract

```csharp
public interface ITool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct);
}

public sealed record ToolDescriptor(
    string Name, string Description,
    JsonElement InputSchema,                   // strict: additionalProperties=false; NO tenant fields
    ToolSideEffect SideEffect,                 // ReadOnly | Consequential
    IReadOnlySet<string> AllowedCallers);      // agent names, or "action-executor"

public sealed record ToolInvocationContext(
    ITenantContext Tenant,                     // injected by the harness; tools never accept tenant input
    Guid RunId, Guid ClaimId, string Caller, ReferenceRegistry References);

public sealed record ToolResult(bool IsError, JsonElement Content, string Summary);
```

Enforcement:

1. `ToolRegistry.For(agentName)` returns only allowed **ReadOnly** tools; Consequential tools are
   never offered to any model.
2. `IToolInvoker` re-checks the caller on every invocation. Unknown or disallowed tool → error
   `tool_result` to the model, `aiops.tool_calls.allowed = false`, and a `TOOL_SCOPE_VIOLATION`
   security event if the tool is Consequential.
3. Tool arguments are schema-validated before execution; invalid → error `tool_result`.
4. Tools that accept references (`EV-n`) resolve them through the run's `ReferenceRegistry`; unknown
   references are errors.
5. Every call is recorded in `aiops.tool_calls` with redacted arguments.

### Tool catalog

| Tool | Side effect | Allowed callers | Input (strict schema) | Output |
|------|-------------|-----------------|-----------------------|--------|
| `customer_lookup` | ReadOnly | intake | `{}` (the run's claim) | `{ verified, region, priorClaimsCount }` — no PII |
| `product_lookup` | ReadOnly | intake, evidence | `{ modelCode, serialNumber }` | `{ found, serialRegistered, productName, category }` |
| `warranty_lookup` | ReadOnly | policy | `{ component: enum }` | Applicable version (code, version, effective dates) and structured terms for the claim's region, with deterministically computed `coverageEndDate`, `withinStandardCoverage`, `accidentalWindowEndDate` |
| `invoice_validation` | ReadOnly | evidence | `{ invoiceRef, extracted: {invoiceDate, modelCode, serial, amount, seller} }` | Field-by-field `{ field, claimValue, invoiceValue, match }` using `EvidenceMatchRules` (R27): serial/model normalized exact, date exact, amount within max(1%, 1.00), seller equal after removing case, punctuation and legal suffixes |
| `claim_history_lookup` | ReadOnly | decision, risk | `{}` | `{ duplicateClaimsForSerial, priorApprovedAccidental, evidenceReuseMatches }` — counts only, same tenant. `duplicateClaimsForSerial`: other claims with the same serial that are not final or were finalized within 90 days before this claim date (R25); `priorApprovedAccidental`: all-time approved accidental-damage claims for the serial |
| `search_policy_knowledge` | ReadOnly | policy | `{ query }` | Additional clauses (same filters as the run's retrieval), returned as new `POL-n` |
| `search_global_knowledge` | ReadOnly | policy, decision | `{ query, documentType? }` | Global snippets as `GLB-n` (not citable as policy) |
| `service_network_lookup` | ReadOnly | action-executor | `{ region, category }` | Nearest simulated service center |
| `create_repair_request` | **Consequential** | action-executor | `{ claimId, serviceCenterId }` | Simulated repair request ID |
| `notify_customer` | **Consequential** | action-executor | `{ claimId, template }` | Simulated outbox entry ID |

## Guardrails → action boundary

```csharp
public interface IGuardrailEngine
{
    GuardrailEvaluation Evaluate(GuardrailInput input);  // pure, deterministic, no I/O
}

public sealed record GuardrailInput(
    TenantSettings Settings, CaseFacts Case, IntakeResult Intake, EvidenceResult? Evidence,
    PolicyResult? Policy, RiskAssessment? Risk, RecommendationResult? Recommendation,
    ReferenceRegistry References, ActorInfo Actor, DateOnly ClaimDate);

public sealed record GuardrailEvaluation(
    IReadOnlyList<GuardrailCheck> Checks, Disposition Disposition,
    IReadOnlyList<string> Reasons, ApprovedAction? Action);

public sealed class ApprovedAction       // constructor is internal to Warranty.Guardrails
{
    public ActionKind Kind { get; }      // FinalizeApproved | FinalizeRejected | RequestInformation | EscalateToReview
    public Guid TenantId { get; }  public Guid ClaimId { get; }  public Guid RunId { get; }
    public IReadOnlyList<string> RequestedItems { get; }
}

public interface IActionExecutor        // Warranty.Application/Actions
{
    Task ExecuteAsync(ApprovedAction action, CancellationToken ct);
    Task ExecuteReviewerDecisionAsync(ReviewDecision decision, CancellationToken ct);
}
```

`CaseFacts` carries the claim's loop state, `ReviewerInfoRequested` and `AutoInfoRequestCount`
(R24). The `ActionExecutor` is the only writer of both: executing a guardrail-issued
`RequestInformation` increments `auto_info_request_count`; executing a reviewer's
`RequestInformation` sets `reviewer_info_requested`. A reviewer `Approve`/`Reject` writes the
reviewer's `claimantExplanation` to `claims.final_explanation`; an automatic finalization writes
the AI's `claimantExplanation` (which already passed `CLAIMANT_TEXT_SAFE`).

Disposition rules (spec FR-026–FR-029 + clarifications), evaluated after all checks:

| Disposition | Requires |
|-------------|----------|
| `AutoApprove` | AI `APPROVE`; valid recommendation; references valid; ≥1 supporting `POL-n`; confidence ≥ `min_confidence`; risk `Low` (**no risk signal of any kind**, R23); claim value ≤ `auto_approval_limit`; deterministic coverage window agrees; category not in `always_review_categories`; `auto_approve_enabled`; not returned from review; claimant text safe |
| `AutoReject` | AI `REJECT`; valid; references valid; ≥1 `POL-n` with `SUPPORTS_REJECTION` that is a `Period` clause confirmed by the deterministic window **or** an `Exclusion` clause whose `exclusion_code` is in the version's `terms.exclusions` and matched by a photo damage type (R26); confidence ≥ minimum; risk `Low` (**no risk signal of any kind**, R23); claim value ≤ limit; if the ground is an expired period, the deterministic window confirms it; category not always-review; `auto_reject_enabled`; not returned from review; claimant text safe |
| `RequestInformation` | (Intake found missing items **or** AI `REQUEST_MORE_INFORMATION`) **and** no escalation condition **and** `auto_info_request_count < 2` **and** not returned from review |
| `HumanReview` | Anything else: value above limit, confidence below minimum, risk `Medium`/`High`, conflicts, AI vs deterministic disagreement, invalid/unavailable AI output, no/ambiguous policy, always-review category, AI `HUMAN_REVIEW`, `reviewer_info_requested` (reason `RETURNED_AFTER_REVIEWER_REQUEST`), information still missing with `auto_info_request_count = 2` (reason `INFO_INCOMPLETE_AFTER_2_REQUESTS`), unsafe AI claimant text |

The value, category, deterministic-risk and loop-state checks also run in the intake short-circuit,
so any of them turns a would-be `RequestInformation` into `HumanReview` (FR-028 precedence).
