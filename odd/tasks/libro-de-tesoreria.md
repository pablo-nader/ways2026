# Libro de tesorería

## Objective

Replace the two treasury reports (general cash ledger and digital accounts section, `/caja/tesoreria`)
with a single ledger per empresa: `fecha | detalle | efectivo | cuenta 1 | … | cuenta N`, plus a
liquidity summary. Later, a conciliation mark that freezes everything up to it.

## Why

The owner finds both current reports useless: they do not show, at a glance, how much cash and how
much in each digital account the business has, nor where each movement came from.

## Rules (agreed with the owner, 2026-10-31)

- One row per event. Positive amounts in normal color, negative in red (contrast-safe in both themes).
- **Caja #N** (turno close): links to `/caja/turnos/:id/z` in a new tab. Hover shows opening and
  closing date-time.
  - Efectivo = everything the cashier took out of the drawer: retiros (mid-turn and at close)
    plus cash pagos made from the drawer (proveedores, sueldos, anything).
  - Each digital account column = sum of the turno's medios mapped to that account, net of
    comisión and retenciones. Hover shows one line per medio: Medio | Importe | Comisión | Neto.
  - Arqueo difference shown as `Caja #137 ($ -1.500)` / `Caja #150 ($ +1.300)` (declared minus
    expected; faltante negative). Hidden when zero or when its absolute value is within
    `tolerancia_pago` resolved for the turno's punto de venta (the same tolerance the POS uses).
- **Pago**: one row per pago, from the turno (any medio) and from the Pagos menu. Detail = what the
  Pagos screen shows; link opens that pago (`/pagos?id=N`). Negative in efectivo for cash lines, in
  the medio's account for digital lines.
- **Cobros** are not separate rows: they are already inside the turno close.
- **Ajustes manuales**: allowed on every column, cash included, until frozen. Shown as their own row.
- Summary: available balance in efectivo and in each account.
- **Conciliation + freeze** (last stage): an Admin marks "reviewed up to here"; everything that feeds
  the ledger up to that mark becomes read-only (compras, pagos, cobros, ventas, …); corrections go
  through counter-movements. ~25 write paths need the guard (see exploration notes below).

## Exploration notes (main @ fbd71f98)

- Close writes `MovimientoTesoreria` RetiroCaja with `Ingreso = Σ MovimientoCaja Retiro`
  (`ServicioDeTurnos.cs:472`). Drawer pagos (`OrigenFondos = CajaTurno`) only reduce the arqueo
  expected and write no treasury row.
- `ArqueoTurno` stores `ImporteEsperado`, `ImporteDeclarado`, `Diferencia` (esperado − declarado).
- `MovimientoCuenta` keeps per turno and medio: Cobro (BaseBruta), Comision, Retencion* rows.
- No cash manual adjustment endpoint exists; account adjustment is
  `POST /api/cuentas-tesoreria/{id}/ajustes` (Admin).
- `/pagos` has no deep link; `GET /api/pagos/administracion/{id}` exists.
- Today digital pagos and NC refunds do not debit the account ledger: the new ledger is computed
  from the sources (turnos, pagos, ajustes), not from that chain.

## Delivery strategy

`ask-on-risk`, chain `stacked-to-main` (owner's standing choice). One PR per task.

## Tasks

- [x] T1 — Cash manual adjustment (backend): endpoint + writer, distinguishable from the system
  adjustments written by pago edits. Route: delegated.
- [x] T2 — Ledger read model (backend): `GET /api/reportes/libro-tesoreria` with rows, per-account
  breakdown, arqueo difference with tolerance, previous balance and current balances. Route: delegated.
- [x] T3 — Web screen replacing `/caja/tesoreria` + `/pagos?id=N` deep link + cash adjustment modal.
  Route: delegated.
- [ ] T4 — Delete the old reports (endpoints, services, DTOs, web, tests), keeping shared pieces
  (`ExportacionDeCaja` histórico part, `ModalDeAjusteDeCuenta`, account ABM). Route: delegated.
- [ ] T5 — Conciliation + freeze guard on every write path that feeds the ledger. Route: delegated.

## Known limitation to revisit in T5

A drawer pago edited after the close changes the Caja row (retiros + current drawer pagos) and its
pago row together. The freeze removes this once conciliated.

## Progress

- 2026-10-31: exploration done, document created.
- 2026-11-01: T1 done: `POST /api/tesoreria/efectivo/ajustes` (Admin, importe con signo; sin saldo objetivo porque la cadena
  no es el saldo del libro nuevo), enum `ajuste_manual` (migración ADD VALUE), 10 integration + domain tests verdes.

- 2026-11-01: T1 commit 5a939ae7. T2 done: `GET /api/reportes/libro-tesoreria?idEmpresa&desde&hasta` (LecturaDeReportes, rango <= 366 días),
  `ServicioDeLibroDeTesoreria` desde las fuentes; domain 18 + application 4 + integration 8 tests nuevos, mutaciones de tolerancia y de empresa
  registradas en `LibroDeTesoreriaTests`.

- 2026-11-01: T3 done (commit en el siguiente registro): `/caja/tesoreria` ahora es el libro (resumen de liquidez, tabla con saldo anterior, tooltips de caja y de medios por cuenta, ajustes de efectivo y de cuenta solo Admin), `/pagos?id=N` abre la edición del pago. Rojo = `--ws-danger` (6,23:1 claro / 6,62:1 oscuro sobre `--ws-surface`; 5,40 y 6,95 sobre `--ws-surface-sunken`). Borrados: `SeccionDeCuentas`, `tesoreria/cuentas.ts` y sus tests. Quedan para T4 los clientes `reportes.tesoreria`/`libroDeCuenta`/`rutasDeExportacion.tesoreria` (los importa `reportes.test.ts`). Vitest completo 299 archivos / 9219 tests verdes, build y oxlint sin hallazgos nuevos.

### Decisiones del libro (T2)

- Caja #N = id del turno (columna Turno del histórico de cajas). Medio efectivo = comportamiento efectivo del catálogo o el ancla
  `turnos_caja.id_medio_pago_efectivo` fijada al cerrar.
- Cada línea de pago se reparte por su medio (efectivo, cuenta viva de la empresa, o `sinCuenta`), sin importar `origen_fondos`: un pago de
  tesorería con medio digital resta de la cuenta, no del efectivo.
- Los ajustes del sistema (`Ajuste` de tesorería) se ignoran: la fila del pago ya muestra su valor vigente. Transferencia/depósito/retiro de
  cuenta no tienen escritor y quedan fuera.
- Un pago del cajón de un turno todavía abierto aparece negativo en su fila de pago sin la fila de caja que lo compensa, hasta el cierre.
- JD-T3-1 corregido: el ajuste de cuenta desde el libro usa `soloImporte` (solo importe con signo, sin releer la cadena ni enviar `saldoObjetivo`); la pantalla de cuentas conserva ambos modos.

## Next step

T4.
