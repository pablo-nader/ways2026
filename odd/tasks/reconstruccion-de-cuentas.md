# Reconstrucción histórica de cuentas de tesorería

## Objetivo

Que el Admin pueda acreditar en las cuentas de tesorería, con su fecha original, los cobros digitales de turnos ya
cerrados que no quedaron registrados porque el medio de pago todavía no tenía cuenta asignada.

## Problema y por qué

La acreditación (cobro, comisión, retenciones) solo se escribe al cerrar el turno
(`EscriturasDeCuentaDeTesoreria.AcreditarAsync`). Un medio sin cuenta en ese momento se salteaba sin marca. El dueño
cargó cuentas y comisiones después, y todos los turnos previos quedaron afuera del libro.

El dueño eligió **reconstruir la cadena con fechas históricas** (no apendear con fecha de hoy). Confirmó que todavía
no hay ajustes manuales en las cuentas (2026-10-31).

## Alcance

- Pantalla de cuentas de tesorería: acción "Reconstruir historial" con empresa + rango de fechas, vista previa
  (turnos, medios, cuentas, importes por componente) y confirmación. Solo Admin.
- Para cada turno cerrado de la empresa en el rango y cada medio con cuenta **actual** (misma empresa, no dada de
  baja) con cobros != 0 y sin filas de cierre ya escritas para (turno, medio): se escriben las mismas filas que el
  cierre (cobro, comisión, retenciones) con la fecha de cierre del turno y los porcentajes **actuales** del medio en
  snapshot.
- El libro pasa a ordenarse por `(fecha, id)` en vez de `id`; al insertar filas históricas se recalculan
  `inicio`/`final` de las filas posteriores de la cuenta.

## Restricciones y reglas

- Los porcentajes del medio no tienen historia: se aplica el valor vigente hoy.
- Idempotente: el índice `ux_movimientos_cuenta_cierre` impide duplicar; correrlo dos veces no escribe nada nuevo.
- Los ajustes manuales conservan su diferencia (no se guarda el saldo objetivo). Limitación documentada.
- Mismo orden de locks que el cierre: lock advisory de la empresa, medios `FOR SHARE`, cuentas `FOR NO KEY UPDATE`
  por id ascendente. Sin reintento (`FabricaDeEstrategiaSinReintento`).
- Rango máximo 366 días.

## Tareas

- [x] **R1 — Backend.** Commit `ca43a8a0`, PR #582, judgment-day APPROVED (dos jueces sin hallazgos). Orden `(fecha, id)` en lectores del libro y de la cola; reconstructor puro de la cadena;
  servicio con vista previa y aplicación; endpoints Admin; tests unitarios + integración. Ruta: delegada (escritor
  único, 2+ archivos no triviales).
- [x] **R2 — Web.** Commit `71958f5b`, PR #584 apilado sobre #582, judgment-day APPROVED (solo WARNING/SUGGESTION). Modal en `CuentasTesoreria.tsx` con empresa, rango, vista previa y confirmación; cliente API;
  tests Vitest. Ruta: delegada.

## Criterios de aceptación

- Un turno cerrado antes de asignar la cuenta queda acreditado con su fecha de cierre y el saldo final de la cuenta
  incluye esos importes; las filas posteriores tienen `inicio` = `final` anterior.
- Correr la reconstrucción dos veces sobre el mismo rango no cambia nada la segunda vez.
- Un turno ya acreditado al cierre no se toca.
- Vendedor/Supervisor reciben 403.

## Progreso

- 2026-10-31: exploración hecha, decisión del dueño tomada, documento creado.
- 2026-11-01: R1 escrita (sin commitear): servicio, endpoints, `ReconstructorDeCadenaDeCuenta`, orden `(fecha, id)` y migración
  `OrdenDelLibroDeCuentaPorFecha` (modelo: `ix_movimientos_cuenta_cuenta_id` pasa a `ix_movimientos_cuenta_cuenta_fecha_id`
  `(id_cuenta_tesoreria, id_tenant, fecha, id_movimiento_cuenta)`; sin tablas ni columnas nuevas). Tests: dominio, integración
  (`CuentasTesoreriaReconstruccionTests`, incluida la carrera 409 `reconstruccion_concurrente`) y guardas estructurales.
- 2026-11-01: R2 escrita (sin commitear): `ModalDeReconstruccionDeCuentas`, botón en `CuentasTesoreria`, cliente `clienteDeReconstruccion` y tests; build, oxlint y vitest completo en verde.

## Siguiente paso

Al mergear #582, reapuntar #584 a `main` y activarle auto-merge. Después, que el dueño corra la vista previa en
producción con el rango desde las primeras ventas.

Seguimientos informativos de R2: releer saldos tras un resultado incierto (5xx/409); rango por defecto en la zona de la
empresa; el test de respuesta vieja no mata la guarda de generación; dos botones "Cerrar" en el estado de éxito.
