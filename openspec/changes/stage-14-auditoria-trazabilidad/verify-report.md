# Verification Report — stage-14-auditoria-trazabilidad

**Change**: Stage 14 — Auditoría y trazabilidad de operaciones sensibles
**HEAD verified**: `8c262ba` (7 slices merged: PRs #123–#128 and #132; plus 3 related chip PRs
merged afterwards: #129, #130, #131)
**Date**: 2026-08-17
**Verdict: PASS WITH WARNINGS** (0 CRITICAL · 2 WARNING · 1 informational)

*(Produced by the sdd-verify agent in-session; persisted verbatim by the orchestrator at archive
time. The verify pass ran read-only and could not write files.)*

## Method note

The full suite was **not** re-run by verify (the orchestrator ran it in parallel; docker was
occupied). Verification combined static source inspection against every binding artifact with
DB-free spot-checks:

| Check | Result |
|---|---|
| `dotnet build src/Ways.Api` | clean, 0 errors |
| `dotnet ef migrations has-pending-model-changes` | clean |
| `dotnet test tests/Ways.Domain.Tests --filter ~Auditoria` | 38/38 green |
| `dotnet test tests/Ways.Application.Tests --filter ~Auditoria` | 6/6 green |
| `npx vitest run` (4 auditoría files) | 33/33 green |
| `npx tsc -b` (Ways.Web) | clean |

Integration tests were verified by reading their actual assertions against the real call-site
code, plus the apply-time mutation evidence recorded verbatim in `tasks.md`.

## 1. Spec → implementation → test mapping

Actual counts (measured, not estimated): **`auditoria-de-operaciones` 7 requirements / 25
scenarios**; `precios`, `comprobantes-venta`, `comprobantes-compra` 1 req / 2 scen each →
**10 requirements / 31 scenarios**, all traced to passing tests. Highlights:

- Append-only tenant fact incl. the platform-actor case (tenant of the SUBJECT, never the
  session) → `RegistrarEscribeElTenantDelSujetoNoElDeLaSesion`.
- RLS isolation over `ways_app` (cross-tenant read blocked; foreign-tenant INSERT → `42501`).
- Fail-closed in both writer modes (EF and ADO), incl. the checkout guard staying at 16.
- The 12-action catalog: price close/reopen = one row; the 100%-servicio-sin-CC flagship;
  zero-diff conteo writes nothing; `stock.transferencia` structurally excluded **and** proven
  absent by test.
- Bounded payload: key-subset invariant, NULL semantics, and no `hash_password` in any
  `usuario.*` payload (asserted against the raw jsonb).
- `GET /api/auditoria` + `/export` with Admin-only policy and the shared query.

## 2. Binding criteria

| # | Criterion | Result |
|---|---|---|
| 1 | Requirement/scenario ↔ impl ↔ test | **PASS** (31/31 traced) |
| 2 | Exactly ONE migration `AuditoriaEtapa14`, matching the gate + amendment 1 | **PASS** — 10 columns, PK, 3 FKs, 2 CHECKs, **5 indexes** (3 contract + 2 FK-support per amendment 1), RLS standard; `has-pending-model-changes` clean |
| 3 | Checkout protection: `VentasCheckoutTests.cs` absent from the diff | **PASS** — empty across the whole stage range *and* the 3 chip PRs; `Assert.Equal(16, …)` intact |
| 4 | 12 actions with call site + coverage; `stock.transferencia` excluded | **PASS** — 12 catalog instances, 12 usages across 11 files (`stock.conteo` has 2 call sites, aggregate and per-lot, each writing one row per *operation*) |
| 5 | Orchestrator Decisions #1 (one row per conteo operation) and #2 (`usuario.baja = {deleted_at, estado}`) | **PASS** — verified in `ServicioDeStock.cs` and `ServicioDeUsuarios.cs` source, not just in prose |
| 6 | Fail-closed proven by DATA, not a test seam | **PASS** — real `23503` / `fk_auditoria_actor`, real rollback, then the same exception through the real `ManejadorDeErrores` → 400 `referencia_invalida`. No `IEscritorDeAuditoria` seam exists |
| 7 | Registered deviations are the complete list | **PASS on sampled deviations** (optional writer param in `ServicioDePrecios`; `RETURNING id_movimiento`; entity PK `Id`) — no undocumented scope violation found |
| 8 | Documental drift | **WARNING 1** below |

## Warnings

**WARNING 1 — doc-10's "Estado (Etapa 14)" annotation was frozen at slice 1** ("tabla + writer
implementados, sin call sites todavía… los call sites, la consulta y la pantalla llegan en
slices posteriores"), written once in slice 1 and never revisited across slices 2-7 — the
stage-13 doc-10 drift class, recurring. **Remediated pre-archive by the orchestrator**: the
annotation now describes the completed feature (12 call sites, the structural exclusion of
`stock.transferencia`, one-row-per-conteo-operation, the Admin-only query and its export, the
screen, and the data-proven fail-closed rule).

**WARNING 2 — informational, already fixed on `main`, no action for this change.** PR #129 (a
post-stage-14 chip) applied a repo-wide UTC normalization to the raw-ADO parameter helpers,
touching among 14 files four that stage 14 itself writes through. Before that fix, writing
`creado_el` from a non-UTC session clock would have thrown Npgsql's
`only offset 0 (UTC) is supported`. **Stage 14's own testing strategy pins `RelojFijo` at
`…T12:00:00Z` — always UTC — so this gap was structurally invisible to its own suite.** Already
closed on `main`; the same chip PR added rule 10 to `mutation-proof-tests` so future stages send
the client's real offset.

## Compliance

`db-error-backstops`, `dto-contract-honesty`, `mutation-proof-tests` (28/28 targets placed and
evidenced), `react-async-state` / `web-descriptor-tests` — all verified present in code and in
the tasks.md evidence blocks.

**Next recommended**: `sdd-archive` (WARNING 1 remediated).
**Risks**: none blocking.
