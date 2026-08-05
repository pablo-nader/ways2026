# Tesorería Specification

## Purpose

Defines `movimientos_tesoreria` (doc 10 §7, ex `cajaz`): the chained
`inicio → ingreso/egreso → final` ledger of the fondo fuera de la caja
diaria. Stage 6 ships only the automatic `retiro_caja` row written at cierre
— manual `deposito`/`gasto`/`ajuste` entries and any tesorería reporting UI
are out of scope (decision 4).

## Requirements

### Requirement: Movimiento Tesorería Schema At Rest

`movimientos_tesoreria` MUST be operativa-scoped (`id_punto_venta`) and carry
`fecha`, `tipo_movimiento_tesoreria` enum (`retiro_caja | deposito | gasto |
ajuste`), `id_turno_caja NULL`, `concepto`, `inicio`, `ingreso`, `egreso`,
`final`, `id_empleado`.

#### Scenario: A retiro_caja row references its originating turno
- GIVEN a turno closes with net retiros of `300`
- WHEN the tesorería row is written
- THEN `tipo = retiro_caja` and `id_turno_caja` references the closed turno

### Requirement: Exactly One Row Per Cierre, Chained From The Last Final

Cierre MUST write exactly one `movimientos_tesoreria` row, in the same
transaction, with `inicio` equal to the last row's `final` for that punto de
venta (`0` if none exists yet), `ingreso` equal to the turno's total retiros,
`egreso` equal to the turno's total gastos, and `final = inicio + ingreso −
egreso`.

#### Scenario: First-ever cierre at a punto de venta starts from zero
- GIVEN punto de venta 7 has no prior `movimientos_tesoreria` row
- WHEN its first turno closes with `ingreso = 100`, `egreso = 40`
- THEN the new row has `inicio = 0` and `final = 60`

#### Scenario: A second cierre chains from the first's final
- GIVEN the last `movimientos_tesoreria` row for punto de venta 7 has
  `final = 60`
- WHEN the next turno closes with `ingreso = 50`, `egreso = 10`
- THEN the new row has `inicio = 60` and `final = 100`

#### Scenario: Cierre never writes more than one tesorería row
- GIVEN a turno with three retiros and two gastos
- WHEN it closes
- THEN exactly one `movimientos_tesoreria` row is inserted, aggregating all
  retiros into `ingreso` and all gastos into `egreso`

### Requirement: Manual Tesorería Entries Are Out Of Scope

No endpoint MUST exist for manually entering `deposito`, `gasto`, or
`ajuste` tesorería rows, nor for tesorería reporting, in this stage.

#### Scenario: No manual tesorería endpoint exists
- GIVEN the API surface of this stage
- WHEN it is inspected for a manual tesorería entry endpoint
- THEN none exists (404)

### Requirement: Tesorería Write Is Part Of The Cierre Transaction

The tesorería row MUST be written inside the same transaction as the arqueo
and the turno's `estado` change — a failed cierre MUST leave no tesorería
row.

#### Scenario: A failed cierre leaves no tesorería row
- GIVEN a cierre that fails after writing the arqueos but before committing
- WHEN the transaction aborts
- THEN no `movimientos_tesoreria` row exists for that attempt
