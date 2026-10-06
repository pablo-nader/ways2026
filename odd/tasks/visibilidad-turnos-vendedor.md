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
  PV, open or closed (shared register + POS "Cierres" reprint screen from #354), OR (web session,
  slice 2) the shift is the OPEN shift of a non-deleted Web-mode PV.
- `/abierto` returns the open shift only when visible under the same predicate, else 200 `null`.
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
- Web Vendedor reading a foreign shift → 404 on each of the five reads; list excludes it — except
  the open shift of a Web PV (slice 2) → 200.
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

## Slice 2 — `/abierto` + web shared register (2026-10-05, owner decision)
Gap: web sessions sell only on Web-mode PVs, a Web PV can be shared by several Vendedores and
selling does not check the opener. With slice 1, Vendedor B on the web gets 404 on `/resumen` and
`/detalle` of the open shift opened by A, and `/abierto` is ungated.
Rule change: web Vendedor sees own shifts + the OPEN shift of any Web-mode PV. Device session is
unchanged (own + any shift of its PV). `/abierto` returns the open shift only if it is visible under
the same predicate, else 200 `null` (contract "never an error" kept).
- [x] T5 — Widen the web clause, gate `/abierto` with the predicate, tests + mutation evidence,
      web/POS screen tests. Route: delegated. Stacked on #355 (branch pnader/visibilidad-turno-abierto).
- [x] T6 — judgment-day, PR (stacked-to-main), auto-merge.
- T5 done (delegated writer): `PoliticaDeVisibilidadDeTurnos.Predicado` takes the ids of the tenant's Web-mode PVs (resolved by `VisibilidadDeTurnos` with one query on `db.PuntosVenta`, which the EF filter already limits to live PVs: a PV dado de baja shares nothing since nobody can sell there). Web clause: `Estado == Abierto && idsWeb.Contains(IdPuntoVenta)`; device/Admin/Supervisor unchanged. `/abierto` now filters with the same predicate (`ObtenerAbiertoAsync(idPuntoVenta, visible)`), else literal `null`. Removed the duplicated integration test. Evidence: 6 unit + 12 new integration tests (incl. baja de PV web); 12 mutants killed (web ids -> true, Estado removed, PV query without Modo, /abierto gate removed, /abierto predicate true, web apertura/cierre removed, device PV clause removed, web ids never resolved, IgnoreQueryFilters on PVs); unit 752, integration 3061 (full suite), web 989 green.
- T6: judgment-day on a536c3a8 APPROVED (no CRITICAL); only suggestion was this stale Rule section (fixed). PR opened with auto-merge.
