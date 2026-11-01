# Gastos que debitan la cuenta de tesorería

## Objetivo

Que cada pago (gasto, pago a proveedor, pago de personal) con un medio que tiene cuenta de tesorería
debite esa cuenta en `movimientos_cuenta`, y que la reconstrucción histórica cargue los débitos de los
pagos viejos.

## Problema y por qué

El dueño corrió "Reconstruir historial" (#582/#584) y la cuenta quedó con un saldo positivo irreal:
la reconstrucción y el cierre de turno solo escriben cobro, comisión y retenciones. Un pago con medio
digital baja el esperado del arqueo pero nunca debitó la cuenta (límite documentado en
`EscriturasDeCuentaDeTesoreria`). El libro de tesorería sí los resta porque se calcula desde las fuentes.

## Diseño

- Tipo nuevo `pago` en `tipo_movimiento_cuenta` y columna `id_pago` (FK compuesta a `pagos`, Restrict, nullable).
- Una sola función reconcilia un pago: objetivo por cuenta = suma de sus `items_pago` vivos ruteados por la
  cuenta actual del medio (solo cuentas vivas de la empresa del pago); ya debitado = suma de filas `pago` con
  ese `id_pago` por cuenta (egreso − ingreso). Escribe una fila por cuenta con la diferencia
  (egreso si sube, ingreso si baja), fechada `FechaMonotona(momento)`. Alta, edición y baja la llaman.
- Sin índice único: un pago puede tener varias filas (ediciones). La serialización es el lock del pago +
  advisory de empresa + cuentas `FOR NO KEY UPDATE`; las filas existentes se leen después del lock de cuentas.
- Orden de locks: conciliación → turno → compra → pago → proveedor → advisory de empresa → medios `FOR SHARE`
  → cuentas `FOR NO KEY UPDATE`.
- Concepto `Pago {id}` (no lleva el nombre del personal: reasignar no lo toca).
- La reconstrucción agrega los pagos vivos sin filas `pago`, con fecha `pago.Fecha` (inserción retroactiva con
  recálculo de la cadena, igual que los cierres), salteando los pagos a la fecha de conciliación o antes.
- El libro de tesorería no cambia (el tipo nuevo queda fuera de `EsDeCierre` y `Ajuste`). Divergencia
  conocida: si a un medio se le cambia la cuenta, el libro re-rutea la historia y la cadena no.
- Fuera de alcance: devoluciones (NC) de ventas digitales.

## Tareas

- [x] T1 — Backend: migración (enum + `id_pago`), reconciliador, enganche en todos los escritores de pagos, tests.
  Ruta: delegada (2+ archivos no triviales, locks).
  - Migraciones: `20261101111549_DebitosDePagoEnCuenta` (`ALTER TYPE tipo_movimiento_cuenta ADD VALUE IF NOT EXISTS 'pago'`,
    columna `id_pago` int NULL, `fk_movimientos_cuenta_pago` `(id_pago, id_tenant)` → `pagos` RESTRICT, índice
    `ix_movimientos_cuenta_pago`; el `Down` no quita el valor del enum) y `20261101111623_ChecksDeDebitosDePagoEnCuenta`
    (`ck_movimientos_cuenta_pago`: `(tipo = 'pago') = (id_pago IS NOT NULL)`, aparte porque Postgres no deja usar el valor nuevo
    en la transacción que lo agrega).
  - Modelo: `MovimientoCuenta.IdPago`, `TipoMovimientoCuenta.Pago`; doc 10 §7 (parte 07) actualizada; espejo web en `tipos.ts`.
  - Dominio: `CalculadorDeDebitosDePago` (objetivo − ya debitado por (cuenta, medio)), 9 pruebas unitarias.
  - Aplicación: `DebitosDePagoEnCuenta.ReconciliarAsync`, llamado al final de `InsertarPagoAsync`,
    `PersistirPagoDeAdministracionAsync` (alta admin, pagar compra, pago de compra con aplicaciones),
    `PersistirPagoDeSueldoAsync` y `EjecutarEscrituraAsync` (edición y baja). Una fila por (cuenta, medio) con diferencia; sin
    locks si ningún ítem tiene medio con cuenta y el pago no tiene débitos.
  - Evidencia (2026-11-01): `dotnet build Ways.slnx` 0 errores; `dotnet test tests/Ways.Domain.Tests` 2246/2246;
    `dotnet test tests/Ways.Application.Tests` 1638/1638; integración filtrada (CuentasTesoreria|Pago|LibroDeTesoreria|
    MovimientosCuenta|MediosPagoCuenta|Conciliacion|Aislamiento|Migracion) 935/936 con la falla esperada en
    `CuentasTesoreriaCierreTests.ElRecalculo…` (contaba todas las filas de la cuenta; ajustada a las del cierre y renombrada
    `…NoTocaLasFilasDelCierre`), luego `CuentasTesoreriaCierreTests|PagosDebitanCuenta|MovimientosCuentaEsquema` 71/71;
    web `npx vitest run src/api` 1943/1943 y `npx tsc -b --noEmit` sin errores.
  - RED observado: calculador (no compilaba) y `PagosDebitanCuentaTests` con el reconciliador anulado (9/11 rojas). Mutaciones:
    sin filtro de empresa → rojo `UnaCuentaDeOtraEmpresaNoSeDebita`; ignorar lo ya debitado → 5 rojas (edición, baja, cambio de
    medio, POS, concurrencia).
- [ ] T2 — Backend: la reconstrucción hace el backfill de débitos; contrato de la vista previa con los débitos; tests.
  Ruta: delegada.
- [ ] T3 — Web: el modal de reconstrucción muestra los débitos y su efecto por cuenta; tests.
  Ruta: delegada.

## Criterios de aceptación

- Alta, edición (importe y medio) y baja de un pago con medio con cuenta dejan el saldo de la cuenta igual a
  saldo previo − líneas vivas del pago en esa cuenta.
- Pago con medio sin cuenta o efectivo: no escribe filas.
- La reconstrucción, corrida dos veces, no duplica débitos; respeta la conciliación.
- Guardas estructurales (sin reintento, inventario de dependientes, enum) pasan.

## Entrega

`stacked-to-main`, un PR por tarea, judgment-day antes de cada PR.

## Progreso

- 2026-11-01: diagnóstico confirmado en main; diseño cerrado.
- 2026-11-01: T1 terminada (ver evidencia en la tarea).

## Siguiente paso

T2. Notas para T2: la reconstrucción debe tomar el advisory de la empresa antes de leer las filas `pago` existentes (el
reconciliador cuenta con eso); el tipo `TipoDeComponenteDeReconstruccion` de `src/Ways.Web/src/api/cuentasTesoreria.ts` no
incluye `Pago`.
