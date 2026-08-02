# Archive Report: Stage 2 — Clientes y Proveedores

**Change**: stage-2-clientes-proveedores
**Archived**: 2026-08-02
**Archive folder**: `openspec/changes/archive/2026-08-02-stage-2-clientes-proveedores/`
**Artifact store mode**: openspec (repo-local)

## Task Completion Gate

`tasks.md`: 34/34 numbered tasks across Slices 1–4, all `[x]`. No stale unchecked items
found. No reconciliation was needed.

## Verification Summary

- Verify verdict at hand-off: **FAIL** — 1 CRITICAL (`specs/listas-precio-minimal/spec.md`'s
  "Cliente creation defaults to the General list" scenario contradicted the implemented and
  tested "required, no defaulting" behavior).
- The CRITICAL was **fixed in place before this archive** (per the archive instruction and
  confirmed by inspecting the actual delta spec file, which already carried the
  "Superseded wording (verify, 2026-08-02)" correction): the scenario was renamed to "Cliente
  creation requires an explicit lista" and rewritten to match `ServicioDeClientes.
  ExigirIdRequerido` / `ServicioDeClientesTests`'s proven behavior.
- `verify-report.md` (both in the original change dir, before it was archived, and in this
  archive copy) was updated to record: 33/33 spec scenarios COMPLIANT, verdict **PASS**, with
  the original FAIL verdict text preserved verbatim underneath for audit trail.
- Final verdict: **PASS**. Build 0 warnings/0 errors. Tests 326/326 (Domain 69/69,
  Application 128/128, IntegrationTests 129/129 — Docker/Testcontainers real Postgres).
  TypeScript (`tsc -b`) clean.

## Judgment-Day Verdicts (recorded in state.yaml, carried into this report for traceability)

| Slice | Verdict | Rounds | Notes |
|---|---|---|---|
| Slice 1 (schema/domain/provisioning/backfill) | APPROVED | R1: 1 CRITICAL (per-artifact backfill idempotency) + 1 gate-approved schema hardening (composite `fk_clientes_lista_precio`) + 2 confirmed + 3 hardening + 3 comment-only, all fixed. R2+: clean. | PR #10, merged to main. |
| Slice 2 (clientes service + API + web ABM) | APPROVED | R1: 3 confirmed (untestable-by-design race test replaced with raw-SQL 23505 proof; limite_credito server validation; cross-tenant write 404 parity) + coverage/doc fixes. R2: CLEAN x2. | PR #12, merged to main. Final suite 69+107+117/117. |
| Slice 3 (proveedores service + API + web ABM) | APPROVED | R1: 1 confirmed real (numeric precision bounds → margen/limite_credito caps + generic 22003→400 backstop) + bookkeeping fix. R2: CLEAN x2. | PR #14, merged to main. Final suite 69+128+129/129. |

## Delta Spec Merges

### New baselines created (first-baseline creation — copied verbatim from the change dir's delta specs)

| Domain | Destination | Requirements |
|---|---|---|
| `clientes` | `openspec/specs/clientes/spec.md` | Cliente Schema At Rest; Atomic Per-Tenant Numero Assignment; Consumidor Final Protected Row; numero_documento Has No Uniqueness Constraint; Cliente ABM Lifecycle and Authorization; Tenant Isolation for Clientes |
| `proveedores` | `openspec/specs/proveedores/spec.md` | Proveedor Schema At Rest; cuit Uniqueness Is Scoped Per Tenant; Proveedor ABM Lifecycle and Authorization; Tenant Isolation for Proveedores |
| `listas-precio-minimal` | `openspec/specs/listas-precio-minimal/spec.md` | listas_precio Schema At Rest; One Default List Per Tenant (with the CRITICAL fix applied — "Cliente creation requires an explicit lista"); listas_precio ABM Is Out of Scope This Stage; Tenant Isolation for listas_precio |

### Merged into existing baseline

`openspec/specs/tenant-organization/spec.md` — the delta (`MODIFIED Tenant Provisioning With
Template Seed` + `ADDED Backfill for Pre-Existing Tenants`) was written against stage 1's
pre-archive change-dir spec, before stage 1 itself was archived. Reconciled against the
CURRENT baseline (post stage-1-archive) as follows:

- **MODIFIED — Tenant Provisioning With Template Seed**: replaced the old scenario text
  (tenant + empresa + area + 2 medios de pago only) with the delta's scenarios (adds
  Consumidor Final cliente + General listas_precio row). The stage-1 deviation note
  ("Deviation recorded (2026-08-02, verify): the originally-planned 'inactive general
  price-list placeholder' was deliberately deferred...") was **preserved verbatim**, per
  instruction. A follow-up "Fulfilled (2026-08-02, archive of stage-2-clientes-proveedores)"
  note was appended directly under it, closing the forward pointer and additionally
  reconciling one inaccuracy in the delta's own literal wording: the delta text says
  "`PlantillaDeAprovisionamiento` gains a new version for this addition (ADR-16: add a
  version, do not edit one)", but design decision 5 and apply-progress.md both confirm the
  template was actually extended **in place** (`V1`), not bumped to a new version — the
  merged baseline text keeps the delta's original sentence (unmodified, as authored/approved)
  but the note clarifies what was actually implemented, so the baseline doesn't silently
  assert an inaccurate mechanism.
- **ADDED — Backfill for Pre-Existing Tenants**: appended verbatim as a new requirement at
  the end of the Requirements section (3 scenarios: existing tenant gains CF + General list;
  backfill is idempotent; backfill is approved inside the DB Change Gate).

No requirements were removed. All requirements not touched by the delta (Organization
Hierarchy Tables, Platform-Only Creation, Tenant Suspension Enforcement, Tenant Isolation
Enforcement) were preserved unchanged.

## Files Moved

Copied (Write) into `openspec/changes/archive/2026-08-02-stage-2-clientes-proveedores/`:

- `proposal.md`
- `design.md`
- `tasks.md`
- `apply-progress.md`
- `verify-report.md` (updated: CRITICAL marked resolved, verdict corrected to PASS, WARNING
  #2 marked resolved, original text preserved as audit trail)
- `state.yaml` (updated: `phase.apply.status` corrected from stale `slice-3-done` to `done`;
  `phase.verify`/`phase.archive` filled in; top-level `status`/`phase` set to
  `archived`/`archive`; leading NOTE comment updated to record the merge as resolved)
- `specs/clientes/spec.md`, `specs/proveedores/spec.md`, `specs/listas-precio-minimal/spec.md`,
  `specs/tenant-organization/spec.md` (the original delta text, kept as the historical
  change-time record — the merged baseline lives in `openspec/specs/`, not here)

**KNOWN LIMITATION — see Risks below**: this execution environment's toolset for this task
did not include a filesystem delete/move or shell-execution tool (only Read/Edit/Write/Glob
were available). Every file above was **copied** via Read + Write with byte-for-byte content
parity (including the in-place verify-report.md/state.yaml corrections applied before the
copy). The original `openspec/changes/stage-2-clientes-proveedores/` directory could **not**
be deleted by this executor — see the Final Self-Check below and the Risks section for the
exact remediation needed from the orchestrator/user.

## Deviations Recorded (carried from verify-report.md / apply-progress.md / state.yaml, for a single-source audit trail)

1. `id_lista_precio`/`id_condicion_fiscal` implemented as REQUIRED, not defaulted-when-omitted
   — `specs/clientes/spec.md`'s own scenario treated as the higher-authority contract over a
   tasks.md one-liner and a design.md:29 default-on-omit statement (both given superseded
   notes). Also the source of the CRITICAL fixed at archive (see above).
2. `PlantillaDeAprovisionamiento` extended in place (`V1`), not bumped to `V2` — ADR-16's
   versioning rule judged to be for a different vertical template, not staged completion of
   the same one (design decision 5). Reconciled into the merged tenant-organization baseline
   text via the "Fulfilled" note (see Delta Spec Merges above).
3. `cuit` dedupe is format-sensitive (no digit-only canonicalization) — accepted limitation,
   consistent with `numero_documento`'s existing convention.
4. Numeric bounds (`margen`/`limite_credito`) are service-level only, no DB `CHECK` — a
   generic 22003→400 backstop covers any future numeric overflow regardless.
5. Slice 4 (web ABMs) was re-scoped into Slices 2 and 3 (vertical delivery per slice, not a
   layer split) — both screens exist and are wired; Slice 4 as a standalone unit closed
   without ever needing its own PR.
6. Cross-tenant `IdEmpresa` pre-check symmetry — INFO carried from Slice 2 to Slice 3, closed
   in Slice 3 for both services.

## Final Self-Check

Reported per the archive instruction's mandatory checklist — see the executor's closing
message for the literal directory-listing/existence output. Summary:

- Archive folder populated: proposal.md, design.md, tasks.md, apply-progress.md,
  verify-report.md, state.yaml, specs/{clientes,proveedores,listas-precio-minimal,
  tenant-organization}/spec.md, archive-report.md — 10 files total, all confirmed written.
- Baseline spec dirs confirmed present under `openspec/specs/`: `clientes/`, `proveedores/`,
  `listas-precio-minimal/` (new), `tenant-organization/` (merged).
- **`openspec/changes/stage-2-clientes-proveedores/` still exists on disk** — it was NOT
  removed, because no delete/move/shell tool was available to this executor. This is flagged
  as a blocking anomaly for the orchestrator: the source directory must be deleted manually
  (e.g. `Remove-Item -Recurse -Force openspec/changes/stage-2-clientes-proveedores` from the
  repo root) to complete the "MOVE (not copy)" requirement. Until that deletion happens, the
  change is copy-archived but not yet fully moved.
