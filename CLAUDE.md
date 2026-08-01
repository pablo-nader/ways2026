# Ways — Project Instructions

ERP/POS rewrite (multi-tenant SaaS). Stack: .NET 10 (`src/Ways.Api|Domain|Infrastructure`) + React 19/Vite/TS (`src/Ways.Web`) + PostgreSQL. Legacy PHP system in `alsina/` (reference only — never modify its logic).

## Authoritative docs

- `docs/01-features-existentes.md` — functional parity contract (audited against the legacy code).
- `docs/09-multi-tenancy.md` — tenancy hierarchy, scoping rules, isolation layers.
- `docs/10-modelo-de-datos.md` — the definitive schema. Every new table must match its scoping category and naming convention (`id_x` PKs, Spanish snake_case plurals).

## Database change gate (MANDATORY)

Before creating or modifying ANY database schema (EF Core migration, DDL, entity model change), present the user a concise summary of the model about to be created — tables, key columns, constraints, tenancy scoping — and WAIT for explicit approval. Never generate or apply a migration without that validation step. This applies to every agent, including delegated sub-agents: sub-agents must return the proposed model summary to the orchestrator instead of applying migrations themselves.

## Code style

- `.editorconfig` at the repo root is law (C# rules from alborbackend; general/front rules from the albor-3 MFE). Fix violations; never suppress without justification.
- Comments: only when the code cannot express the intent by itself. When a comment is warranted, write it in Spanish (neutral, professional). No narrative, redundant, or changelog comments (S125 enforces no commented-out code).
- Identifiers: C#/TS code in English; database objects follow the Spanish naming convention of doc 10.

## Testing policy

- Every feature ships with unit tests at minimum. Domain logic must be testable without a database (see `PoliticaDeRoles` as the reference pattern).
- Add integration tests for API endpoints and e2e tests for critical user flows whenever feasible.
- A task is not done until its tests pass.

## Skills (self-improving)

- Project skills live in `.claude/skills/` and are created on demand — when a real pattern emerges, not speculatively.
- When an agent detects a recurring mistake or repeated correction (same feedback twice or more), it must create or update the relevant skill so it does not happen again, then run `gentle-ai skill-registry refresh`.

## PR validation gate (solo-dev review protocol, MANDATORY)

The project owner works alone: no human reviews PRs. Every PR must therefore pass an
adversarial automated review before it is sent:

1. After generating the commits for a PR slice, run the **judgment-day** protocol
   (`judgment-day` skill): two independent blind review agents judge the diff, verdicts
   are compared, confirmed issues are fixed, and the diff is re-judged. Iterate until a
   clean round (no confirmed issues).
2. Only then create the PR (per `branch-pr` skill), merge it, and continue with the next
   slice. Never merge a PR that has not passed a clean judgment-day round.
3. Review findings that reveal a recurring mistake must feed the skills loop (see Skills
   section): update or create the skill so the next slice doesn't repeat it.

## Agent orchestration

- Parallelize delegated work whenever tasks are independent.
- Choose the lowest-cost model acceptable for each task: haiku for mechanical work, sonnet for standard implementation/exploration, opus only for genuinely hard architecture or design decisions.
