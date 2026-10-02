# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

WARRANTY-AI is a **Spec-Driven Development (SDD)** project scaffolded with [GitHub Spec Kit](https://github.com/github/spec-kit) v0.16.3, configured for the **Claude** integration using **PowerShell** helper scripts (Windows). There is no application code yet — the repo currently contains only the Spec Kit tooling. Features are built by first writing a specification, then a plan, then tasks, then implementing against them.

## Tech stack and commands (planned in `specs/001-ai-claim-adjudication/plan.md`)

The stack is chosen but the code is not yet implemented (`/speckit-implement` creates it). Planned stack: .NET 10 / ASP.NET Core modular monolith (`src/Warranty.*`), React + TypeScript + Vite SPA (`src/web`), PostgreSQL + pgvector, Azurite blobs, Keycloak, Ollama embeddings, Anthropic models via a provider-agnostic AI Gateway, orchestrated locally by .NET Aspire on **Podman**. Planned commands (verify once the projects exist):

- Run everything: `aspire run` (requires `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman`; Anthropic key in AppHost user secrets as `Parameters:anthropic-api-key`)
- Backend tests: `dotnet test tests/Warranty.UnitTests`, `dotnet test tests/Warranty.IntegrationTests` (Testcontainers on Podman: set `DOCKER_HOST`, `TESTCONTAINERS_RYUK_DISABLED=true`); single test: `dotnet test --filter "FullyQualifiedName~<Name>"`
- Frontend: `npm --prefix src/web install`, `npm --prefix src/web test`
- AI evaluation (separate from production): `dotnet run --project tests/Warranty.Evaluation -- --mode replay` (`--mode live` costs money)

Architecture rules that tests enforce: only `Warranty.AI.Gateway` references a vendor AI SDK; `Warranty.Guardrails` and `Warranty.Domain` reference no AI project; consequential actions run only through `ActionExecutor` with a guardrail-issued `ApprovedAction`; tenant identity never comes from request bodies or model output. See `quickstart.md` in the feature folder for full setup.

## The workflow (how work gets done here)

Features flow through a fixed pipeline, each stage invoked as a Claude skill (slash command). The canonical order:

1. `/speckit-constitution` — fill in `.specify/memory/constitution.md` (see below; currently an unfilled template).
2. `/speckit-specify <description>` — create a new feature: scaffolds `specs/NNN-<short-name>/spec.md` (the *what* and *why*, no implementation details) plus a `checklists/requirements.md` quality gate.
3. `/speckit-clarify` *(optional)* — ask up to 5 targeted questions and encode answers back into the spec. Run before planning if the spec has open questions.
4. `/speckit-plan` — generate design artifacts in the feature dir: `plan.md` (tech stack + architecture) and, as needed, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`.
5. `/speckit-tasks` — generate `tasks.md`: a dependency-ordered, phased task list (Setup → Tests → Core → Integration → Polish), with `[P]` marking tasks that may run in parallel.
6. `/speckit-analyze` *(optional)* — non-destructive cross-check of spec ↔ plan ↔ tasks for consistency.
7. `/speckit-checklist` *(optional)* — generate extra reviewer-owned quality checklists.
8. `/speckit-implement` — execute `tasks.md` phase by phase, marking tasks `[X]` as they complete.

Supporting skills: `/speckit-converge` (re-scan the codebase vs. spec/plan/tasks and append remaining work to `tasks.md`), `/speckit-taskstoissues` (convert tasks into GitHub issues).

The `speckit` workflow (`.specify/workflows/speckit/workflow.yml`) chains specify → plan → tasks → implement with **approval gates** after spec and after plan.

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

`.specify/memory/constitution.md` is meant to hold project-wide principles and governance constraints that every spec/plan/implementation must respect. **It is still the unfilled template** (contains `[PLACEHOLDER]` tokens). Run `/speckit-constitution` to populate it before doing serious feature work; once filled, it is loaded by `specify`, `plan`, and `implement` as binding guidance.

## Editing guidance

- Treat everything under `.specify/` and `.claude/skills/speckit-*/` as **vendored Spec Kit tooling** — don't hand-edit it to change behavior; it is tracked in `.specify/integrations/claude.manifest.json` by SHA-256 and is meant to be regenerated by the `specify` CLI.
- Keep specs free of implementation detail (tech/stack/APIs) — that belongs in `plan.md`. The spec quality checklist enforces this.
