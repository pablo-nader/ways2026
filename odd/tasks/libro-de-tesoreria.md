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
- [x] T4 — Delete the old reports (endpoints, services, DTOs, web, tests), keeping shared pieces
  (`ExportacionDeCaja` histórico part, `ModalDeAjusteDeCuenta`, account ABM). Route: delegated.
- [x] T5a — Conciliation mark + freeze guard (backend). Route: delegated. Evidence in Progress (2026-11-01, T5a).
- [ ] T5b — Web: "Conciliado hasta" banner in the ledger, Admin "Conciliar" action with a date-time picker, frozen rows marked
  (rows dated at or before `conciliadoHasta`). Route: delegated.

## Known limitation (solved by T5a once conciliated)

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
- Roles (decisión del dueño, 2026-10-31): Supervisor y Admin leen el libro, sueldos incluidos; el link al pago y los ajustes son solo Admin.
- T1+T2 en PR #583 (judgment-day APPROVED, sin hallazgos críticos). T3 commits 26c8a2cf + dc40d9d6 (judgment-day: JD-T3-1 confirmado por ambos jueces, corregido y re-juzgado APPROVED).
- JD-T3-1 corregido: el ajuste de cuenta desde el libro usa `soloImporte` (solo importe con signo, sin releer la cadena ni enviar `saldoObjetivo`); la pantalla de cuentas conserva ambos modos.
- 2026-11-01: T4 done: borrados `GET /api/reportes/tesoreria`, `/tesoreria/export`, `/tesoreria/cuentas` y `/tesoreria/cuentas/{id}/libro`, `ServicioDeTesoreria`, `ServicioDeLibroDeCuentas`, sus DTOs (`MovimientoTesoreriaListado`, `PaginaDeMovimientosTesoreria`, `CuentaConSaldo`, `CuentasDeLaEmpresa`, `MedioElectronicoSinCuenta`, `TotalesDeCuentaEnElRango`, `LibroDeCuenta`), la parte de tesorería de `ExportacionDeCaja` (queda histórico y detalle de turno), `TesoreriaTests`, `TesoreriaExportTests`, `CuentasTesoreriaLecturaTests` y los clientes/tipos/tests web equivalentes. `MovimientoDeCuentaListado` queda (respuesta del ajuste). `ModalDeAjusteDeCuenta` siempre es de importe con signo (sin `soloImporte` ni relectura). Los tests de reconstrucción, cierre y ajuste leen `MovimientosCuenta` directo en vez del libro viejo, con las mismas aserciones de cadena y apertura de rango. Verificación: build 0 errores; Domain 2208 y Application 1624 verdes; integración filtrada (autorización, CuentasTesoreria*, Tesoreria*, LibroDeTesoreria*, Caja*, Export*) 401 verdes; Vitest completo, build y oxlint sin hallazgos nuevos.

### Conciliación y congelamiento (T5a)

- Tabla `conciliaciones_tesoreria` (append-only, tenant + empresa, RLS; marca vigente = max(`hasta`)), `POST /api/tesoreria/conciliaciones`
  (Admin; 404 empresa, 400 `hasta_requerido`/`nota_muy_larga`, 409 `conciliacion_futura`/`conciliacion_anterior`/`conciliacion_con_turno_abierto`),
  `conciliadoHasta` en la respuesta del libro. Detalle del modelo y de los locks en el doc 10 §7 ("Conciliación de la tesorería").
- Se congela solo lo que cambia los números del libro (verificado contra `ServicioDeLibroDeTesoreria`): pagos (fecha y líneas), turno cerrado
  (retiros, pagos del cajón, movimientos de cuenta y arqueo), ajustes manuales y el ruteo medio -> efectivo/cuenta. Siguen editables
  (no cambian lo que el libro lee): compras, aplicaciones, redondeos, vincular compra, reasignar personal, cobros y ventas.
- Sitios con guarda (409 `periodo_conciliado`): `ServicioDePagos.EjecutarEscrituraAsync` (edición y baja, POS y administración; fecha del pago o
  del cierre de su turno, la menor), `InsertarPagoAsync` (alta del POS, solo carrera), `InsertarPagoDeAdministracionAsync`, los dos ajustes manuales (efectivo y cuenta; solo carrera), la edición de `cuentas_tesoreria.saldo_inicial`,
  `EjecutarPagoDeCompraAsync`, `EjecutarPagoDeCompraConAplicacionesAsync` (solo con pago nuevo), `ServicioDePagoDePersonal.EjecutarAsync`
  (pago de sueldo), `ServicioDeTurnos` (los dos cierres, solo carrera), `ServicioDeMediosPago` (cambio de cuenta o comportamiento).
- Pago de administración fechado hoy: `Fecha` = instante actual (antes, medianoche local); las fechas pasadas conservan la medianoche local.
- Medios de pago: el catálogo es del tenant (`id_empresa` NULL = compartido), así que el chequeo mira, por cada empresa con marca, las líneas de
  pago y los movimientos de cuenta del medio fechados en o antes de ESA marca; una edición toma el lock compartido de todas las empresas.
- Reconstrucción de cuentas (PR #582, ya en `main`): omite los turnos con `FechaCierre <= marca` (borde inclusivo) en la vista previa y en la
  aplicación, en vez de responder 409: el pedido sigue sirviendo para el resto del rango y la respuesta informa `conciliadoHasta` y
  `congeladas` (acreditaciones omitidas, fuera de `turnos`/`filas`). La aplicación toma el compartido de conciliación como primer lock y lee
  la marca bajo él. Pruebas: borde igual a la marca, marca un segundo antes, sin marca, y espera al escritor de la marca; mutaciones
  registradas en `CuentasTesoreriaReconstruccionTests`.
- Judgment-day de T5a (JD-T5-1..5, corregidos): `saldo_inicial` no se puede cambiar (409 `periodo_conciliado`) apenas la empresa tiene una marca, con
  o sin movimientos, porque el libro lo suma a todos los saldos; nombre y estado siguen editables. Los ajustes manuales de efectivo y de cuenta toman
  el compartido de conciliación como primer lock, leen la marca y rechazan una fecha menor o igual (solo carrera). Cada sitio guardado tiene su
  prueba de integración con la mutación registrada en el doc-comment de `ConciliacionDeTesoreriaTests`; la prueba del turno abierto usa el
  instante exacto de la apertura.

## Next step

T5b (web de la conciliación). T4 entregada en #588.
