# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

WARRANTY-AI is a **Spec-Driven Development (SDD)** project scaffolded with [GitHub Spec Kit](https://github.com/github/spec-kit) v0.16.3, configured for the **Claude** integration using **PowerShell** helper scripts (Windows). Features are built by first writing a specification, then a plan, then tasks, then implementing against them. The first feature, `specs/001-ai-claim-adjudication` (an AI-powered warranty claim adjudication PoC), is implemented in `src/` and `tests/`.

## Tech stack and commands (see `specs/001-ai-claim-adjudication/plan.md`)

Stack: .NET 10 / ASP.NET Core modular monolith (`src/Warranty.*`), React + TypeScript + Vite SPA (`src/web`), PostgreSQL + pgvector, Azurite blobs, Keycloak, Ollama embeddings, Anthropic models via a provider-agnostic AI Gateway, orchestrated locally by .NET Aspire on **Podman**. The SPA's look and behaviour follow the WarrantyOS design system in `specs/001-ai-claim-adjudication/ui-design.md` (tokens, component kit, AI/human/system visual language, screen layouts); the Claude Design source it was taken from is in that feature's `design/` folder (reference only — don't import it). Commands (run from the repo root; full setup in the feature's `quickstart.md`):

- Build: `dotnet build Warranty.slnx` (warnings are errors; `nuget.config` restricts restore to nuget.org).
- Backend tests (xunit.v3 on Microsoft Testing Platform — `global.json` opts `dotnet test` into MTP mode): `dotnet test --project tests/Warranty.UnitTests`; `dotnet test --project tests/Warranty.IntegrationTests -- --filter-not-trait "Category=Smoke"`; whole solution `dotnet test --solution Warranty.slnx -- --filter-not-trait "Category=Smoke"`. Single test: `dotnet test --project tests/Warranty.UnitTests -- --filter-method "*<Name>*"` (also `--filter-class`, `--filter-trait`). Exit code 8 means zero tests ran. Integration tests and the evaluation runner use Testcontainers on Podman: `$env:DOCKER_HOST = "npipe://./pipe/podman-machine-default"` (exactly two slashes after `npipe:`) and `$env:TESTCONTAINERS_RYUK_DISABLED = "true"`, with the Podman machine running.
- Aspire smoke test (trait `Category=Smoke`; boots the whole AppHost in replay mode on its fixed ports and completes S1; pulls every image incl. Ollama and Keycloak): excluded from CI and from the commands above by `--filter-not-trait "Category=Smoke"`; run it alone with `-- --filter-trait "Category=Smoke"` (`DOTNET_ASPIRE_CONTAINER_RUNTIME=podman`, no `aspire run` active).
- Frontend: `npm --prefix src/web ci`, then `npm --prefix src/web run lint`, `npm --prefix src/web test`, `npm --prefix src/web run build` (what CI runs). Prefer `ci` to `install`: `npm install` (and `aspire run`, which runs it for the `web` resource) rewrites `src/web/package-lock.json` (drops `"peer": true` lines) — don't commit that churn, `git checkout -- src/web/package-lock.json`. Vitest timeouts can appear when the integration tests run at the same time; rerun alone.
- Run everything: `aspire run` (needs `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman`). The AppHost defaults to `AiGateway:Mode=live`, which needs the Anthropic key in AppHost user secrets as `Parameters:anthropic-api-key`; `$env:AiGateway__Mode = "replay"` runs on recordings with no key.
- AI evaluation (separate from production; isolated Testcontainers database): `dotnet run --project tests/Warranty.Evaluation -- --mode replay`. Options: `--mode replay|live`, `--record` (live only: rewrites fixtures), `--tenants a,b`, `--cases G-AUR-01,...`, `--output <dir>` (default `artifacts/eval/{timestamp}/report.md` + `report.json`), `--review-db <conn>`, `--embeddings <conn>`. Replay reads `tests/fixtures/ai-recordings/golden/{caseId}/`; no golden case has recordings yet, so replay only checks the AI-unavailable fallback. **Never run `--mode live` unasked** — it costs money and needs `ANTHROPIC_API_KEY`.

Other mechanics: public-endpoint rate limits are configurable in the API's `RateLimiting` section (`ClaimSubmission`, `ClaimantAccess`: `PermitLimit`, `Window`); `.gitattributes` checks text files out with LF on every platform (tests compare literal `\n`), binaries (evidence images/PDFs) are never normalized.

Architecture rules that tests enforce: only `Warranty.AI.Gateway` references a vendor AI SDK; `Warranty.Guardrails` and `Warranty.Domain` reference no AI project; consequential actions run only through `ActionExecutor` with a guardrail-issued `ApprovedAction`; tenant identity never comes from request bodies or model output. See `quickstart.md` in the feature folder for full setup.

## The workflow (how work gets done here)

Features flow through a fixed pipeline, each stage invoked as a Claude skill (slash command). The canonical order:

1. `/speckit-constitution` — create or amend `.specify/memory/constitution.md` (see below).
2. `/speckit-specify <description>` — create a new feature: scaffolds `specs/NNN-<short-name>/spec.md` (the *what* and *why*, no implementation details) plus a `checklists/requirements.md` quality gate.
3. `/speckit-clarify` *(optional)* — ask up to 5 targeted questions and encode answers back into the spec. Run before planning if the spec has open questions.
4. `/speckit-plan` — generate design artifacts in the feature dir: `plan.md` (tech stack + architecture) and, as needed, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`.
5. `/speckit-tasks` — generate `tasks.md`: a dependency-ordered, phased task list (Setup → Tests → Core → Integration → Polish), with `[P]` marking tasks that may run in parallel.
6. `/speckit-analyze` *(optional)* — non-destructive cross-check of spec ↔ plan ↔ tasks for consistency.
7. `/speckit-checklist` *(optional)* — generate extra reviewer-owned quality checklists.
8. `/speckit-implement` — execute `tasks.md` phase by phase, marking tasks `[X]` as they complete.

Supporting skills: `/speckit-converge` (re-scan the codebase vs. spec/plan/tasks and append remaining work to `tasks.md`), `/speckit-taskstoissues` (convert tasks into GitHub issues).

The `speckit` workflow (`.specify/workflows/speckit/workflow.yml`) chains specify → plan → tasks → implement with **approval gates** after spec and after plan.

## Git workflow: one branch and one pull request per task

Never commit or push directly to `master` (a PreToolUse hook in `.claude/settings.json` denies `git commit`/`git push` on `master`/`main` and any push targeting them; branch protection isn't available on this private free-plan repo, so human developers must follow the same rule by convention). Every task in `tasks.md` (tasks marked `[P]` too) gets its own branch and PR:

1. `pwsh -NoProfile -File scripts/git/task-flow.ps1 start T0xx` — requires a clean tree; branches `task/T0xx-<slug>` from the latest `origin/master` in the current checkout. Add `-Worktree` to create it in a new git worktree at `.worktrees/T0xx` instead (any tree state; gitignored). `start` refuses a task that is already `[X]` on `origin/master`, has a pushed task branch, or whose GitHub issue is assigned to someone else, and self-assigns the issue (`-Force` overrides).
2. Implement the task and mark it `[X]` in `tasks.md` on that branch.
3. Commit with a message starting with the task ID, e.g. `T012: Declare Application ports`.
4. `pwsh -NoProfile -File scripts/git/task-flow.ps1 finish T0xx` — rebases onto `origin/master` (conflicts that are only `tasks.md` checkboxes from parallel tasks are resolved automatically; any other conflict aborts the rebase for you to resolve and rerun), pushes, opens a PR titled `T0xx: …` that closes the task's issue (pass the PR attribution line via `-ExtraBody`), waits for CI (`.github/workflows/ci.yml`; `-SkipChecks` to skip), squash-merges and deletes the branch. The main checkout returns to an up-to-date `master`; a worktree is left detached at `origin/master`, ready for the next `start` or for `task-flow.ps1 cleanup T0xx` (run from the main checkout). With `-NoMerge` the PR stays open for review.

If a task fails or its tests don't pass, stop on its branch and report — don't start a task that depends on an unmerged one. Non-task changes (docs, tooling, spec edits) use a `chore/<slug>` or `docs/<slug>` branch and a PR as well. `task-flow.ps1 name T0xx` prints a task's branch name without changing anything.

### Working in parallel (from Phase 3 on)

Several developers — people on their own clones and Claude Code sessions/agents on one machine — can work on Phase 3+ tasks at the same time:

- **Claiming work**: each remaining task has a GitHub issue titled `T0xx: …`, labelled `us1`–`us6`/`polish`, `parallel` and `tests` (issues are managed with the `gh` CLI; the GitHub MCP connector is not used). Pick an open, unassigned issue whose dependencies are merged (see the issue body and "Dependencies & Execution Order" / "Parallel Opportunities" in `tasks.md`); `start` assigns it to you and `finish` closes it through the PR. Prefer whole stories per developer after US1, as in tasks.md's "Parallel Team Strategy".
- **One task per checkout**: a Claude session working alongside others uses `start T0xx -Worktree` and then works only inside `.worktrees/T0xx`; an agent spawned with `isolation: "worktree"` already has its own worktree and runs plain `start T0xx` there. Never run two tasks in the same checkout at once.
- **Order still matters**: `[P]` only means "different files"; a task that depends on another must wait until that one is merged to `master` (start from a fresh `origin/master`). Each issue's **Blocked by** line (and GitHub's native blocked-by link) lists the direct prerequisites.
- **Test-first tasks keep `master` green**: a test task (label `tests`) lands its tests with `Skip = "Pending T0xx"` on every case whose implementing task isn't merged yet, adding minimal `NotImplementedException` stubs only if needed to compile; cases whose implementation is already on `master` land unskipped and must pass. The implementing task removes those `Skip`s and makes them pass (its issue's **Unskip** line names the test files). CI must never be red on `master` — a failing test there blocks every other developer's `finish`.
- **Shared hot spots**: EF Core migrations (`src/Warranty.Infrastructure/Persistence/Migrations/`) can't be merged textually — if `finish` reports a conflict in the model snapshot, drop your migration, rebase, and regenerate it. Expect small manual conflicts in DI registration and endpoint mapping files; resolve, rerun the tests, rerun `finish`.
- **Local resources**: integration tests use Testcontainers with random ports, so they run in parallel worktrees; `aspire run` uses fixed ports — run it from one checkout at a time.

## Key conventions and mechanics

- **Feature directories live under `specs/`**, named `NNN-<short-name>` (e.g. `001-user-auth`). Numbering is **sequential** (`feature_numbering: "sequential"` in `.specify/init-options.json`) — the next number is the highest existing prefix + 1.
- **`.specify/feature.json`** records the active feature directory. Downstream commands (`plan`, `tasks`, `implement`) locate the feature through this file, **not** through the git branch name — spec dir and branch name are independent.
- **Scripts are PowerShell** under `.specify/scripts/powershell/`. The two most relevant:
  - `check-prerequisites.ps1` — validates that required artifacts exist and lists available docs. `implement` runs it as `check-prerequisites.ps1 -Json -RequireTasks -IncludeTasks`; use `-PathsOnly` to just resolve feature paths without validation.
  - `create-new-feature.ps1` — computes the feature number/short-name and scaffolds the spec dir (`-DryRun` to preview, `-ShortName`/`-Number`/`-Timestamp` to override).
  - Shared helpers are in `common.ps1`; templates resolve via `resolve-template.ps1`.
- **Templates** that drive generated artifacts live in `.specify/templates/` (`spec-template.md`, `plan-template.md`, `tasks-template.md`, `checklist-template.md`, `constitution-template.md`). Changing a template changes all future generated artifacts.
- **Extension hooks**: skills look for `.specify/extensions.yml` (`hooks.before_*` / `hooks.after_*`). It does not currently exist, so hook steps are skipped. Hook command names map dots→hyphens (`speckit.git.commit` → `/speckit-git-commit`).
- **Checklists are a gate, not a to-do list.** `/speckit-implement` treats `checklists/*.md` as read-only: it reports pass/fail and asks before proceeding when items are unchecked — it does not tick boxes itself.

## The constitution

`.specify/memory/constitution.md` (v1.0.0, ratified 2026-10-02) holds the project-wide principles every spec/plan/implementation must respect — among them security-first tenant isolation, AI never directly controlling critical business operations, explainable decisions, human-in-the-loop for high-risk decisions, model independence and observable AI. It is loaded by `specify`, `plan`, and `implement` as binding guidance; amend it only through `/speckit-constitution`.

## Editing guidance

- Treat everything under `.specify/` and `.claude/skills/speckit-*/` as **vendored Spec Kit tooling** — don't hand-edit it to change behavior; it is tracked in `.specify/integrations/claude.manifest.json` by SHA-256 and is meant to be regenerated by the `specify` CLI.
- Keep specs free of implementation detail (tech/stack/APIs) — that belongs in `plan.md`. The spec quality checklist enforces this.
