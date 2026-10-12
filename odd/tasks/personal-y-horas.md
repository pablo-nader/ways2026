# Personal y horas trabajadas

Locator: `odd/tasks/personal-y-horas.md` · Engram: `odd/personal-y-horas/tasks`

## Objetivo

Cargar al personal de la empresa, registrar las horas trabajadas por día a un valor hora y
anotar los pagos. Es una cuenta corriente informal de personal: las horas generan saldo a
favor del empleado y los pagos lo cancelan. No hay recibos de sueldo ni liquidación formal.

## Problema y motivo

Hoy no existe el concepto de empleado: `id_empleado` en el modelo es el usuario que opera y
"sueldos" es solo una categoría de gasto. El dueño lleva las horas y los pagos por fuera del
sistema, y los descuentos por compras en cuenta corriente se calculan a mano.

## Alcance

- Catálogo `personal` con vínculo opcional a un usuario y a un cliente. Ninguno de los dos
  es obligatorio; cada usuario y cada cliente se vinculan a lo sumo a un registro de personal.
- Libro `movimientos_personal` append-only: carga de horas, pago y ajuste.
- Pago en una sola operación con varias líneas: descuento de la cuenta corriente del cliente
  vinculado y entrega de dinero por uno o más medios de pago.
- Pantallas de back office: ABM de personal y cuenta de cada empleado.

Fuera de alcance: recibos de sueldo, cargas sociales, liquidación, fichaje de entrada y
salida, carga de horas desde el POS.

## Decisiones

- D1 (dueño, 2026-10-12): el origen del dinero entregado se elige en cada pago, entre la
  tesorería de la empresa (sin turno, admite fecha retroactiva) y el cajón del turno abierto
  (impacta el arqueo).
- D2: `personal` es catálogo (`id_tenant` + `id_empresa NULL`). El nombre evita `empleado`
  porque `id_empleado` ya significa usuario en el modelo; la PK es `id_personal`.
- D3: el valor hora se congela en cada carga de horas. El valor del registro de personal es
  solo el valor por defecto de la próxima carga.
- D4: el libro es append-only (principio 7 de docs/10). Una carga mal hecha se corrige con
  un contra-asiento de tipo ajuste, nunca editando.
- D5: cada línea con medio de pago genera un gasto de categoría `sueldos`, para que el pago
  aparezca en los reportes de gastos y mueva tesorería o cajón por el camino ya existente.
- D6: el descuento de cuenta corriente no mueve dinero: escribe un movimiento que baja la
  deuda del cliente con un valor nuevo del enum `tipo_movimiento_cc`, sin turno ni comprobante.
- D7: permisos de Admin y Supervisor para cargar horas y pagar; el ABM de personal es de Admin.

## Restricciones

- docs/10 es el contrato del esquema: scoping, nombres, RLS con `HabilitarRlsDeTenant`, FKs
  compuestas `(id_x, id_tenant)` salvo las que apuntan a `usuarios`.
- Los saldos se mueven solo por escrituras atómicas `UPDATE ... RETURNING` más el asiento.
- Guardas estructurales: inventario de dependientes, superficie de autorización, rutas de la
  SPA, escrituras sin reintento. Se corre el proyecto `Ways.Application.Tests` completo.

## Entrega

Estrategia: `auto-chain`, cadena `stacked-to-main`. Estimado: unas 3.500 líneas de autoría,
una rebanada por tarea. Cada rebanada pasa judgment-day y sale como PR con auto-merge.

## Tareas

- [x] T1 — Catálogo `personal` (backend). Ruta: delegada (varios archivos no triviales).
  Entidad, configuración EF, migración con RLS, servicio, endpoints de alta, edición, baja y
  listado, traducción de errores de unicidad y FK, docs/10.
  Aceptación: se da de alta un empleado sin vínculos, con usuario, con cliente o con ambos;
  un usuario o cliente ya vinculado responde 409; un cliente consumidor final se rechaza;
  otro tenant no ve los registros.
  Checks: tests de dominio, aplicación e integración; guardas estructurales.
  Evidencia: commit `1005dcdd`. Domain.Tests 1345/1345, Application.Tests completo 858/858,
  integración filtrada 197/197; verificación del padre: integración de usuarios y personal
  72/72. Evaluación de riesgo nativa no disponible (RDD apagado en el clon): se trata como
  alta y la revisión independiente es judgment-day. Namespace `Plantel`, clase `Personal`.
  Pendiente heredado por T2: la baja de personal no tiene guarda de referencias porque aún
  ninguna tabla lo referencia; se agrega junto con `movimientos_personal`.
- [ ] T2 — Libro de personal y carga de horas (backend). Ruta: delegada.
  Tabla `movimientos_personal`, saldo cacheado en `personal`, carga de horas con valor hora
  congelado, ajuste con detalle obligatorio, estado de cuenta paginado.
  Aceptación: horas × valor hora da el importe y sube el saldo; el ajuste lo corrige con
  signo; dos cargas concurrentes dejan el saldo exacto.
  Checks: tests de dominio del cálculo, integración del libro y de concurrencia.
- [ ] T3 — Pago de personal (backend). Ruta: delegada.
  Operación única con líneas de descuento de cuenta corriente y de medios de pago, origen de
  fondos por pago (tesorería o cajón del turno), gasto `sueldos` por línea de medio, valor
  nuevo del enum `tipo_movimiento_cc`.
  Aceptación: el pago baja el saldo del empleado por el total; el descuento baja la deuda del
  cliente vinculado y se rechaza sin cliente vinculado; el origen cajón exige turno abierto y
  aparece en el arqueo; todo ocurre en una transacción.
  Checks: integración por cada combinación de líneas y origen; atomicidad ante falla.
- [ ] T4 — Pantalla de personal (web). Ruta: delegada.
  ABM con selector de usuario y de cliente, ruta, entrada de menú.
  Checks: tests de helpers de mapeo y de componente.
- [ ] T5 — Cuenta de personal (web). Ruta: delegada.
  Estado de cuenta, carga de horas, ajuste y pago con líneas.
  Checks: tests de componente y del cálculo de totales del pago.

## Progreso

T1 cerrada (1.551 líneas de autoría sin contar el código generado de la migración).
Próximo paso: judgment-day y PR de T1, luego T2.
