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
- [x] T2 — Libro de personal y carga de horas (backend). Ruta: delegada.
  Tabla `movimientos_personal`, saldo cacheado en `personal`, carga de horas con valor hora
  congelado, ajuste con detalle obligatorio, estado de cuenta paginado.
  Aceptación: horas × valor hora da el importe y sube el saldo; el ajuste lo corrige con
  signo; dos cargas concurrentes dejan el saldo exacto.
  Checks: tests de dominio del cálculo, integración del libro y de concurrencia.
  Evidencia: commit `7c04fe94`. Domain.Tests 1368/1368, Application.Tests completo 867/867,
  integración filtrada 301/301; verificación del padre: Application.Tests 867/867 e
  integración del libro 49/49. RED observado solo en la regla de dominio; servicio, endpoints
  y esquema se probaron por mutación. Una mutación sobrevive: el desempate por id en el
  `ORDER BY` del estado de cuenta no es matable de forma determinista.
  Modelo: `personal.saldo`; `movimientos_personal` (fecha `date`, enum
  `tipo_movimiento_personal` = horas|pago|ajuste, un CHECK de forma por tipo, FK compuesta a
  personal, RLS). Desvío deliberado del libro de proveedores: sin `id_punto_venta`.
  Sin resolver: no se rechaza una carga con fecha futura (la zona horaria es por empresa).
  Judgment-day: ronda 1 con un crítico reproducido (el enum nuevo faltaba en los fixtures
  que arman su lista de enums: 4 tests rotos) y advertencias de calidad de tests. Corregido
  en `85251b0a`, `75f3a8c1` y `3adcaf3c` (lección en el skill structural-guard-tests). La
  re-evaluación encontró dos fixtures más con el mismo defecto (2 tests rotos), corregidos
  en el commit siguiente. Verificación final: el CI del PR #407 corrió la suite completa en
  verde. La corrida local reportó 2522 tests y la suite tiene unos 3830: ese conteo no cierra.
  Cambios aceptados en la revisión: la fecha del ajuste es obligatoria (el servidor no
  adivina el día); la ventana por defecto del estado de cuenta no tiene tope superior; se
  quitó del CHECK de horas la cláusula `importe >= 0`, implicada por las otras.
- [x] T3 — Pago de personal (backend). Ruta: delegada.
  Operación única con líneas de descuento de cuenta corriente y de medios de pago, origen de
  fondos por pago (tesorería o cajón del turno), gasto `sueldos` por línea de medio, valor
  nuevo del enum `tipo_movimiento_cc`.
  Aceptación: el pago baja el saldo del empleado por el total; el descuento baja la deuda del
  cliente vinculado y se rechaza sin cliente vinculado; el origen cajón exige turno abierto y
  aparece en el arqueo; todo ocurre en una transacción.
  Checks: integración por cada combinación de líneas y origen; atomicidad ante falla.
  Evidencia: commits `cf9591a9` y `8de4c5ba`. Domain.Tests 1405/1405, Application.Tests
  875/875, integración completa 3871/3871 (42 min); verificación del padre: integración de
  pago, esquema fiscal y gastos 218/218. RED observado en las reglas de dominio y en las
  guardas del escritor de cuenta corriente; la integración se escribió después y se probó
  por mutación, sin mutaciones sobrevivientes. Sin prueba propia: la serialización por la
  fila de personal (sale del UPDATE, no de un lock explícito).
  Modelo: valor `descuento_personal` en `tipo_movimiento_cc`; `id_movimiento_personal`
  nullable en `gastos` y en `movimientos_cuenta_corriente` (el asiento de pago es la
  cabecera); CHECKs que nombran el valor nuevo en una segunda migración.
  Orden de locks: turno, fila de personal, fila de cliente, inserts, lock de tesorería.
  Los gastos vinculados a un pago no se editan ni eliminan sueltos (409).
  Decisiones del escritor aceptadas: el descuento exige punto de venta (el libro de cuenta
  corriente lo requiere); con origen cajón el gasto lleva el instante real y `fecha` solo
  fecha el asiento; la referencia de la línea se guarda en el detalle del gasto; tope de 20
  líneas. No se valida que la empresa del empleado coincida con la que paga.
  Judgment-day: ronda 1 sin críticos; los dos jueces sin ciclos de locks ni caminos que
  dejen los libros inconsistentes. Corregido en `42c0d4ff`, `cf880c5b` y `f63b2ff6`: línea
  nula y origen numérico inválido respondían 500; etiqueta web del tipo nuevo; el descuento
  alcanzaba a un cliente dado de baja en carrera (ahora el UPDATE exige cliente vivo); los
  listados de gastos exponen `idMovimientoPersonal` y las pantallas deshabilitan editar,
  eliminar y vincular. La re-evaluación confirmó todo y encontró un defecto del arreglo
  (campo ausente tratado como vinculado), corregido en el commit siguiente.
  Cambio de alcance aceptado en la revisión: pagar desde tesorería exige Admin (403 al
  Supervisor), igual que el gasto de administración; cajón y descuento siguen para ambos.
  Verificación tras las correcciones: Domain 1408/1408, Application 875/875, integración
  filtrada 431/431, web 5912/5912 (una falla intermitente sin identificar en la primera
  corrida); verificación del padre: integración de pago 52/52, web de gastos 110/110.
  Seguimientos: anular un pago; `CompraEditor` con `?desdeGasto=` de un gasto vinculado no
  se frena en pantalla (termina en 409); la etiqueta nueva del POS requiere release.
- [x] T4 — Pantalla de personal (web). Ruta: delegada.
  ABM con selector de usuario y de cliente, ruta, entrada de menú.
  Checks: tests de helpers de mapeo y de componente.
  Evidencia: commits `a618af83` y `3c5db178`. Web completa 6119/6119, lint y build sin
  errores, `RutasDeLaSpa` 106/106; verificación del padre: tests de la pantalla y del menú
  93/93. RED observado en los helpers; los tests de pantalla se escribieron después y se
  probaron por mutación.
  Ruta `/personal` para Admin y Supervisor (solo lectura), en Administración > Terceros. El
  Supervisor gana un grupo Administración en el menú. Usuario y cliente se eligen con
  `SelectorConBusqueda` (el desplegable existente solo trae 25 clientes).
  Judgment-day: ronda 1 sin críticos. Corregido en `ad503152` y `1b8827fc`: tests del
  esqueleto asíncrono, listado que quedaba en "Cargando" tras un guardado rechazado,
  enlace "Cuenta" inerte con la pantalla bloqueada, accesibilidad de los selectores, saldo
  con signo legible. La re-evaluación dio todo por resuelto, sin defectos del arreglo; web
  completa 6150/6150.
  Seguimientos: el mismo defecto de "Cargando" existe en Proveedores (tarea sugerida aparte);
  el término de búsqueda vigente se anota al iniciar la lectura y no al aplicarla.
  El enlace "Cuenta" apunta a una ruta que se registra en T5.
- [ ] T5 — Cuenta de personal (web). Ruta: delegada.
  Estado de cuenta, carga de horas, ajuste y pago con líneas.
  Checks: tests de componente y del cálculo de totales del pago.

## Progreso

T1 cerrada (1.551 líneas de autoría sin contar el código generado de la migración).
T1 mergeada en main (PR #402). T2 cerrada (1.712 líneas de autoría).
T2 mergeada en main (PR #407). T3 cerrada (2.492 líneas de autoría).
T3 mergeada en main (PR #415). T4 cerrada (unas 2.500 líneas, más de la mitad tests).
Próximo paso: PR de T4, luego T5.
