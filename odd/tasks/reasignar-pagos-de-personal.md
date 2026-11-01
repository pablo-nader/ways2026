# Reasignar pagos de personal

## Objetivo

Corregir un pago de sueldo cargado al empleado equivocado desde Administración → Pagos, moviéndolo a otro
empleado sin borrar ni volver a cargar el pago.

## Problema

El 2026-10-31 el dueño registró 22 pagos en la cuenta de Agustín Carte que eran de Diego Nader. Ways no tiene
forma de corregirlo: los pagos de sueldo no se editan ni se eliminan por separado (409
`gasto_de_pago_de_personal`), y anularlos tampoco existe.

## Decisiones

- **Mover el movimiento** (decisión del dueño, 2026-10-31): la fila de `movimientos_personal` cambia de
  `id_personal` y los saldos resultantes de los dos libros se corrigen. Rompe a propósito la regla "el saldo
  resultante nunca se recalcula" solo para esta operación; queda el rastro en la auditoría.
- Corrección de los saldos resultantes por desplazamiento, sin recalcular desde cero:
  - origen: las filas con id mayor al movido suman `-importe` (el pago deja de restar);
  - destino: la fila movida toma el saldo resultante de la fila del destino con el id inmediato anterior (0 si no
    hay) más su importe, y las filas del destino con id mayor suman `importe`;
  - `personal.saldo`: origen `-importe`, destino `+importe`.
- Los textos con el nombre del empleado se actualizan: `pagos.concepto` y `movimientos_tesoreria.concepto`.
- Fuera de alcance: los pagos con descuento de cuenta corriente (409); reasignar varios de una vez.
- En las filas de sueldo de la pantalla Pagos, "Reasignar" reemplaza a "Vincular a compra" y "Crear compra", que
  hoy se ven deshabilitados y no aplican a un sueldo.

## Contrato

`POST /api/pagos/administracion/{idPago}/reasignar-personal`, solo Admin (`Politicas.GestionDeCatalogo`).

- Cuerpo: `{ "idPersonal": number }`.
- 200: `{ "idPago", "idPersonalAnterior", "idPersonal", "concepto" }`.
- 404: pago inexistente o de otro tenant (primero, antes de validar el cuerpo).
- 400 `cuerpo_invalido`/validación: `idPersonal` ausente o <= 0.
- 409 `pago_sin_movimiento_de_personal`: el pago no es un pago de sueldo.
- 404: empleado destino inexistente o dado de baja.
- 409 `reasignacion_al_mismo_personal`: el destino es el empleado actual.
- 409 `pago_con_descuento_de_cuenta_corriente`: el pago tiene un descuento de cuenta corriente.

## Tareas

- [x] T1 — Backend: servicio, endpoint, auditoría `pago.reasignacion`, tests unitarios e integración. Ruta: delegada (2+ archivos no triviales). Rama `pnader/reasignar-pago-personal-backend`. Hecha: commit 5a3a4f54.
- [ ] T2 — Web: acción "Reasignar" en las filas de sueldo de Pagos, modal con el empleado destino, tests. Ruta: delegada. Rama `pnader/reasignar-pago-personal-web`, apilada sobre T1.

## Verificación

- T1: tests unitarios de Application y Domain; integración de la reasignación y guardas estructurales (en CI).
- T2: `npm run test` de Ways.Web, `npm run build`.

## Progreso

- 2026-10-31: documento creado; T1 y T2 lanzadas en paralelo con el contrato fijo.
- 2026-10-31: T1 hecha (commit 5a3a4f54). Evidencia: `dotnet build` 0 errores; `dotnet test tests/Ways.Domain.Tests` 2110 correctas; `dotnet test tests/Ways.Application.Tests` 1510 correctas (incluye `EscriturasSinReintentoEstructuralesTests` y `ServicioDeAuditoriaGuardTests`); integración `ReasignacionDePagoDePersonal|SuperficieDeAutorizacion|EscriturasSinReintento` 32 correctas. Mutaciones probadas y revertidas: invertir el signo del desplazamiento del origen y cambiar el 0 del destino sin asientos previos hacen fallar los tests de saldos. Ruta: delegada (escritor único; 2+ archivos no triviales). Sin etiqueta de `pago.reasignacion` en `EtiquetasDeAccion` ni en el catálogo web: queda para T2 (espejo en `tipos.ts`).
