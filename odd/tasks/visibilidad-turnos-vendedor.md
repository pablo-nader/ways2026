# Visibilidad de turnos de caja para el Vendedor

## Objective
A Vendedor can only read the cash shifts they are entitled to; every other id answers 404.

## Problem
`/api/caja/turnos/{id}`, `/resumen`, `/detalle`, `/detalle/export`, `/resumen-de-cierre` and the
history list `GET /api/caja/turnos` only gate by `OperacionDePos` + tenant. Any Vendedor reads any
shift of the tenant by id.

## Rule (decided with the owner, 2026-10-05)
- Supervisor / Admin: every shift of the tenant (unchanged).
- Vendedor: a shift is visible when `IdEmpleadoApertura` or `IdEmpleadoCierre` is the Vendedor's
  empleado, OR the request comes from a POS device session and the shift belongs to that device's
  PV, open or closed (shared register + POS "Cierres" reprint screen from #354).
- Non-visible id → 404 (ADR-8 indistinguishable from not found).
- Scope: the five reads + the list (filtered). Writes (`/movimientos`, `/cierre`,
  `/cierre-por-retiro`) unchanged.

## Constraints
- No schema change (no migration gate needed).
- Integration tests with mutation evidence (`.claude/skills/mutation-proof-tests`).
- Web `CajaZ` and POS `CierreDeCaja`/`CajaZ`/`GastosDelTurno` keep working for their roles.
- TDD mode: not configured (source: none) — ordinary functional checks + mutation evidence.

## Tasks
- [x] T1 — Visibility policy + enforcement on the five reads and the list, integration tests with
      mutation evidence. Route: delegated (writer trigger: 2+ non-trivial files).
- [x] T2 — Web check: CajaZ / CierreDeCaja / GastosDelTurno tests still green; no web change expected.

## Acceptance
- Web Vendedor reading a foreign shift → 404 on each of the five reads; list excludes it.
- Vendedor reading own (opened or closed) → 200.
- Vendedor on a device → 200 on any shift (open or closed) of the device's PV; 404 on foreign
  shifts of other PVs.
- Supervisor / Admin → 200 on any shift.

## Progress
- Branch `pnader/visibilidad-turnos-vendedor` from origin/main.
- T1 done (delegated writer): predicate `PoliticaDeVisibilidadDeTurnos` + `VisibilidadDeTurnos` in
  Ways.Application/Caja; 5 reads call `ExigirVisibleAsync` first; list filtered. 10 unit + 13
  integration tests; 16/16 mutants killed (check placement after data read is unobservable by status).
  Full integration suite 3048/3048 green; parent spot check 13/13. Commit: see git log of branch.
- T2 done: web vitest CajaZ/CierreDeCaja/GastosDelTurno 88/88, no web change.
- Next: judgment-day on the commit, then PR (owner decision).
- 2026-10-05 rule change (owner): #354 merged the POS "Cierres" screen (lists closed shifts of the
  device's PV for reprint). Device session now sees own shifts + EVERY shift (open or closed) of the
  device's PV. Web Vendedor unchanged (own only). T1 reopened as T3.
- [x] T3 — Rebase onto origin/main (#354, `estado` filter in ListarAsync), widen the device clause,
      update tests + mutation evidence, verify CierresDeCaja/CajaTurnosEndpointsTests. Route: delegated.
- [x] T4 — Re-run judgment-day on the rebased commit, open PR, enable auto-merge.
- T3 done (delegated writer): rebased on origin/main (#354); conflict only in the list endpoint,
  resolved so `estado` and `visibles` both reach `ListarAsync`. Device clause now `IdPuntoVenta == PV
  del dispositivo` for any estado. Unit 13 tests, integration 17 in VisibilidadDeTurnos (list for
  device = own + whole PV, estado filter with device and web). 12 mutants (PV clause removed, PV->true,
  Estado==Abierto re-added, apertura/cierre in both branches, role bypass x2, list ignores visibles,
  estado dropped, visibles dropped with estado, device PV unresolved) all killed.
- T4: judgment-day round 1 (old base) clean; after the rebase and the rule change, round 2 on 84c1bb6d returned no CRITICAL findings (APPROVED). Suggestions: this doc was stale (fixed), one duplicated integration test, /abierto still has no visibility gate (pre-existing, follow-up).
