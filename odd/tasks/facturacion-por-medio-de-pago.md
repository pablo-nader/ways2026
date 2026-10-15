# Facturación fiscal por medio de pago

## Objetivo

Que cada medio de pago defina qué porcentaje de lo vendido debe salir facturado por ARCA
(ex AFIP), y que el POS facture los tickets que correspondan sin frenar la caja.

## Problema y por qué

El núcleo fiscal de la etapa 19a (certificados, WSAA/WSFE, numeración, CAE) existe pero no
está conectado a la venta del POS: `ServicioDeVentas` rechaza los tipos fiscales y la
emisión fiscal es un endpoint aparte que no conoce pagos. El dueño necesita decidir la
facturación por medio de pago (ej.: transferencia 100 %, efectivo 10 %).

## Decisiones del dueño (2026-10-12)

1. Porcentaje 0–100 por medio de pago: parte de lo vendido que debe facturarse.
2. Los tickets no se parten: se factura el ticket entero o nada. El porcentaje es una meta
   aproximada que se alcanza eligiendo tickets enteros.
3. Pago mixto: si al menos un medio exige factura, se factura el ticket completo.
4. Forzado manual: el cajero puede marcar un ticket para facturarlo sí o sí.
5. Sin conexión o sin respuesta de ARCA: se vende igual con ticket común y la factura se
   emite sola después (queda para reimprimir). Con conexión, sale con CAE y QR en el momento.

## Diseño aceptado

- **Venta ↔ factura:** la factura es un comprobante fiscal espejo, enlazado a la venta TX por
  `comprobantes_venta.id_venta_origen`. El TX sigue siendo el documento económico; el espejo
  escribe solo comprobante + ítems (sin stock, pagos, cuenta corriente ni turno) y se excluye
  de los agregados de ventas.
- **Selección:** `PoliticaDeFacturacionPorMedio` (dominio puro, sin base). Forzado ⇒ factura;
  algún medio al 100 % ⇒ factura; si no, difusión de error por medio:
  `saldo' = saldo + importe × pct / 100` y factura si algún medio con pct > 0 queda con
  `saldo' > importe / 2`. Al facturar, cada medio del ticket descuenta su importe del saldo.
- **Alcance del acumulado:** por `(id_empresa, id_medio_pago, periodo)`: el cupo se cumple dentro de cada mes y el saldo arranca en cero el día 1 (decisión del dueño, 2026-10-12).
- **Dónde se decide:** en el servidor, al ingerir la venta, en una transacción corta propia.
  En la solicitud solo viaja `ForzarFactura`; nunca la decisión, el porcentaje ni el CAE.
- **Diferido:** fila de decisión en `pendiente`; la procesa un endpoint que el POS dispara en
  cada drenaje y rendición. Sin certificado activo no se decide ni se mueve el acumulado.

## Modelo (resumen)

- `medios_pago.porcentaje_facturacion numeric(5,2) NOT NULL DEFAULT 0`, CHECK 0..100.
- `comprobantes_venta.id_venta_origen integer NULL` (FK compuesta, UNIQUE parcial).
- `comprobantes_venta.factura_forzada boolean NOT NULL DEFAULT false`.
- `facturaciones_de_venta` (operativa, RLS): decisión, estado, aportes, comprobante fiscal.
- `acumulados_de_facturacion`: PK `(id_empresa, id_medio_pago, periodo)`, saldo y totales.

## Alcance autorizado

Las tareas T1–T9. Fuera de alcance: CAEA, alta del certificado real y homologación (19b).

## Entrega

Estrategia `ask-on-risk` con cadena `stacked-to-main` (ya elegida por el dueño). Pronóstico:
~3.000 líneas, una PR por tarea. Cada PR pasa judgment-day antes de abrirse.

## Tareas

- [x] **T1 — Porcentaje en el medio de pago.** Migración, `MedioPago`, contratos, servicio,
  descriptor web e instantánea del POS. Checks: integración del ABM (0..100, rechazo fuera de
  rango), Vitest del descriptor. Ruta: delegada (2+ archivos no triviales).
- [x] **T2 — `PoliticaDeFacturacionPorMedio`.** Dominio puro. Checks: unitarios sin base
  (0 %, 100 %, mixto, forzado, convergencia sobre N tickets, reversión). Ruta: delegada.
- [x] **T3 — Esquema y no doble conteo.** Enlace, `factura_forzada`, las dos tablas, RLS,
  predicado compartido en reportes y listado. Checks: integración con espejo sembrado,
  guardas estructurales, prueba de mutación del predicado. Ruta: delegada.
- [x] **T4 — Decisión al ingerir.** Servicio de decisión, `ForzarFactura` en la solicitud y en
  `ExigirMismoContenido`, estado fiscal en la respuesta. Depende de T1–T3.
- [x] **T5a — Emisión del espejo (núcleo).** Test que fija el comportamiento actual (un fallo
  de ARCA revierte fila y número), `ServicioDeFacturacionDeVentas.ProcesarDecisionAsync` en
  dos transacciones, liberación del número ante rechazo, reversión del acumulado, anulación
  bloqueada para ventas facturadas, endpoint de una decisión. Ver "Diseño de T5". Depende de T4.
- [x] **T5b — Procesador por lote.** `POST /api/fiscal/facturaciones/procesar`, espera entre
  intentos, orden por serie, concurrencia. Incluye lo que T5a dejó anotado: tope de tiempo de
  las llamadas a WSFE y que invalidar el ticket de acceso solo descarte el ticket que falló
  (hoy descarta el que esté guardado, y dos llamadas simultáneas pueden pisarse). Depende de T5a.
- [x] **T5c — Emisión en línea.** Intento al cobrar con presupuesto corto y datos del
  comprobante en la respuesta de la venta. Depende de T5b.
- [x] **T6 — POS.** Toggle de forzado, bandera en la cola, espera acotada, captura de la
  respuesta del drenaje. Depende de T4 y T5. Reglas del dueño (2026-10-14) en "Reglas de T6".
  - [x] **T6a — Estado fiscal en las lecturas.** `GET /api/ventas/{id}` informa `fiscal`
    (con los datos del comprobante para reimprimir) y `GET /api/ventas/por-turno/{id}` informa
    el estado por venta con una lectura en lote. Error como código estable. Ruta: delegada.
  - [x] **T6b — POS: forzado y espera.** Botón "Facturar", `forzarFactura` fijado al encolar,
    espera de hasta 5 s solo si la venta seguro se factura y hay conexión; factura aprobada en
    lugar del ticket. Ruta: delegada.
  - [x] **T6c — POS: lote y "Ventas del turno".** Lote tras cada venta y en cada
    sincronización; estado fiscal por venta y reimpresión. Ruta: delegada.
  - Release del POS (bump de Ways.Desktop + tag `pos-v*`): no antes de T7 (la factura impresa
    necesita el QR), y siempre avisando al dueño antes.
- [x] **T7 — Impresión.** Factura con CAE y QR en `VistaDeTicket` y en el POS de escritorio,
  reimpresión. Depende de T5.
- [ ] **T8 — Anulación y nota de crédito de una venta facturada.** `CbtesAsoc`, espejo de NC,
  reversión del acumulado. Depende de T5.
- [ ] **T9 — Back office.** Pendientes y rechazadas, reporte vendido vs. facturado por medio.

## Diseño de T5 (2026-10-13)

`SFF` = `src/Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs`.

### Reglas del dueño

1. Si ARCA rechaza, no queda comprobante fiscal: el pedido se borra y su número vuelve a la
   serie. El ticket interno (la venta) no se toca nunca. El motivo queda en la decisión.
2. La fecha de la factura es el día de emisión, no el de la venta.
3. Un rechazo devuelve el cupo: el ticket no cuenta como facturado.
4. Una decisión `rechazada` no se reintenta sola; se reintenta a mano (T9).
5. Una venta facturada no se anula: se corrige con nota de crédito (T8). La anulación se
   rechaza si la venta tiene espejo, aprobado o en trámite. Con decisión `pendiente` y sin
   espejo sí se puede anular: la decisión pasa a `cancelada` y devuelve el cupo.
6. Al cobrar se admiten hasta 4 s más de espera en el servidor para intentar la factura; el
   tope total tras confirmar la venta es 8 s (T5c).

### Hechos verificados en el código

- `EmitirAsync` numera, inserta y llama a ARCA en una sola transacción sin `catch`
  (SFF:181-268): cualquier excepción revierte la fila, los ítems y `proximo_numero`.
- Resultado definitivo de WSFE: una `RespuestaCae` devuelta (aprobado, aprobado con
  observaciones, rechazado) y el error `arca_rechazo`. Todo lo demás es no definitivo
  (`arca_no_definitivo`, `wsfe_fault_soap`, ticket de acceso inválido,
  `numeracion_fiscal_desincronizada`, errores de lectura, cancelación): usar lista blanca.
- Un `rechazado` persistido conserva el número N con el contador en N+1, y ARCA sigue
  esperando N: la serie queda trabada.
- No hay timeout HTTP configurado para WSFE, y el cortacircuitos de `ClienteWsfe` no sobrevive
  entre solicitudes.
- `ReintentarAsync` (SFF:287) aceptaría un espejo.

### Forma

Servicio nuevo `ServicioDeFacturacionDeVentas` (scoped). En SFF solo cambia la visibilidad de
los auxiliares que se reutilizan (`private` → `internal`, sin tocar cuerpos) y la búsqueda de
`ReintentarAsync` excluye espejos.

`ProcesarDecisionAsync(idFacturacion)`:

1. Lecturas sin lock ni red: decisión, venta, espejo por `id_venta_origen`. Si la decisión no
   está `pendiente`, devuelve su estado.
2. Venta no emitida y sin espejo: cancela la decisión y revierte el acumulado.
3. Compuertas sin red. Si alguna falla, la decisión sigue `pendiente` con `ultimo_error`,
   `intentos + 1` y espera; no hay espejo ni número. Compuertas: `empresa_sin_condicion_fiscal`,
   `punto_venta_sin_numero_fiscal`, `empresa_sin_cuit_valido`, `certificado_fiscal_ausente`,
   `condicion_fiscal_receptor_no_mapeada`, `receptor_sin_cuit`, `alicuota_sin_mapeo_afip`,
   `total_fiscal_no_coincide`, `tipo_fiscal_invalido`.
4. Receptor: el cliente de la venta; Consumidor Final va como documento 99/0. La letra sale de
   `ResolvedorDeLetraComprobante` y el tipo es el fiscal activo de esa letra.
5. Líneas y totales desde los ítems guardados de la venta, no desde el catálogo.
6. Ticket de acceso WSAA antes de tomar número: una falla de WSAA no consume número.
7. Transacción A (sin reintento): lock de la serie en `numeraciones_fiscales` con
   `FOR UPDATE SKIP LOCKED` (ocupada → `serie_ocupada`, sin penalidad); si la serie tiene un
   espejo `pendiente` → `serie_bloqueada`; si no, lock de la venta, se verifica que siga
   emitida, se asigna el número y se inserta el espejo (`id_venta_origen`, resultado
   `pendiente`, sin turno, fecha de ahora, ítems copiados sin lote ni costo). Commit.
8. Transacción B: se retoma el lock de la serie. Espejo creado por esta llamada: se solicita
   el CAE. Espejo preexistente: se consulta primero (encontrado → se adopta; no encontrado →
   se refresca la fecha y se solicita con el mismo número). Se aplica el resultado y la
   decisión se actualiza con guarda `estado = 'pendiente'`.

Orden de locks: `numeraciones_fiscales` → `comprobantes_venta` → `facturaciones_de_venta` →
`acumulados_de_facturacion` (medio ascendente).

### Estados

| Evento | Espejo | Decisión |
|---|---|---|
| Aprobado, aprobado con observaciones, o la consulta lo encuentra | aprobado con CAE | `emitida`, con `id_comprobante_fiscal` |
| Rechazo definitivo | liberado (borrado, número devuelto) | `rechazada`, `ultimo_error` con los errores de ARCA, débito revertido |
| Sin respuesta definitiva | sigue `pendiente` con su número | `pendiente`, `intentos + 1`, espera de 30 s que se duplica, tope 10 min |
| Compuerta falla | no existe | `pendiente`, error, espera de 5 min que se duplica, tope 4 h |
| Venta anulada, sin espejo | no existe | `cancelada`, acumulado revertido |

Liberar un espejo: dentro de la transacción B y bajo el lock de la serie, se borran sus ítems
y su fila y se devuelve el número con guarda (`proximo_numero = N` solo si vale `N + 1`). Si
la guarda no afecta filas, no se borra: el espejo queda `rechazado` con su número y se
registra un error.

Acumulado: `cancelada` deshace todo lo que aplicó la decisión (aportes, `total_vendido`,
`total_facturado`); `rechazada` deshace solo el débito (`saldo += vendido`,
`total_facturado −= vendido`), así el cupo pasa a tickets posteriores.

### Procesador (T5b) y emisión en línea (T5c)

- `POST /api/fiscal/facturaciones/procesar`, política `OperacionDePos`, cuerpo
  `{ idPuntoVenta, maximo? }` (10 por defecto, tope 25). Presupuesto de 20 s en total y 8 s
  por ida y vuelta, con token propio; un corte por presupuesto no cuenta como fallo.
- `POST /api/fiscal/facturaciones/{id}/procesar` procesa una decisión e ignora la espera.
- La cabeza `pendiente` de una serie se procesa primero, salvo que esté en espera.
- En línea: tras decidir `pendiente`, la venta intenta emitir con 4 s de presupuesto antes
  del barrido. `EstadoFiscalDeVenta` suma `Error` y los datos del comprobante (tipo, letra,
  punto de venta fiscal, número, fecha, CAE, vencimiento, QR, observaciones).

## Reglas de T6 (dueño, 2026-10-14)

1. La caja espera la respuesta fiscal solo si la venta seguro se factura (forzada o con un
   medio al 100 %) y hay conexión, hasta 5 s. Aprobada: imprime la factura en lugar del
   ticket. Sin respuesta a tiempo: ticket común y la factura queda para después.
2. Porcentajes parciales: no espera, ticket común al instante; si termina facturada, la
   factura queda para reimprimir.
3. Botón "Facturar" en el panel de cobro, para todo cajero, apagado por defecto y que se apaga
   tras cada venta; viaja como `forzarFactura`, fijado al encolar. No aplica en presupuesto.
4. El POS dispara `POST /api/fiscal/facturaciones/procesar` solo, en cada sincronización y
   tras cada venta; sin botón en caja.
5. "Ventas del turno" muestra por venta facturada, pendiente o rechazada y permite reimprimir.
6. El QR de ARCA en la factura impresa va en T7.

## Supuestos pendientes de confirmación del dueño

- El forzado manual descuenta del cupo del medio.
- El acumulado es por empresa (no por caja).
- Un medio compartido entre empresas del tenant usa un solo porcentaje.
- Con porcentaje parcial y en línea, la caja espera unos segundos la respuesta fiscal (T6).
- Consumidor Final por defecto; ticket grande que exige identificar queda en "faltan datos".

## Incógnitas fiscales (validar con el contador)

- Monto desde el que hay que identificar al Consumidor Final.
- Ventana permitida de fecha del comprobante en una emisión diferida, y si un CAE emitido
  horas después de la entrega es válido.
- Leyenda de IVA al Consumidor Final (RG 5614).

## Progreso y evidencia

- 2026-10-12: exploración y diseño completos.
- 2026-10-12 — T1 hecha. Ruta: delegada (un escritor; más de dos archivos no triviales).
  Commits `fc32d4c5` y el de corrección que le sigue. RED observado (compilación y 9 tests
  de Vitest) y GREEN. Checks: `dotnet build` sin errores; `Ways.Application.Tests` 858/858;
  `Ways.IntegrationTests` completa 3781/3781 sobre `fc32d4c5`; Vitest 5896/5896; `tsc` limpio.
  Judgment-day ronda 1: ningún hallazgo severo; una advertencia confirmada por los dos jueces
  (un PUT sin el campo pisaba el porcentaje con 0), corregida con su test (RED y GREEN): el
  campo ausente conserva lo guardado. Tras la corrección se re-corrieron solo las pruebas de
  medios de pago e instantánea (29/29) y las de aplicación; la suite completa queda al CI.
  Rebase sobre main (38 commits): migración regenerada (`20261012153344`, mismo contenido),
  un fixture nuevo de main ajustado; integración filtrada 486/486, aplicación 871/871,
  `tsc` limpio, Vitest 5973/5973.
  Re-juicio del arreglo (`35bda509`): resuelto, sin hallazgos en ninguno de los dos jueces.
  JUDGMENT: APPROVED. Sin prueba de mutación de la cláusula de rango. RDD apagado en el clon: sin revisión nativa.
  Decisiones: sin subir la versión de la instantánea (una guardada sin el campo lee 0).
  PR: pablo-nader/ways2026#411, con auto-merge y Auto-fix.
- 2026-10-12 — T2 hecha. Ruta: delegada (un escritor). `PoliticaDeFacturacionPorMedio` en
  `Ways.Domain/Fiscal`, pura. RED por compilación y GREEN (28 casos); `Ways.Domain.Tests`
  1414/1414; `dotnet build` sin errores. Mutaciones observadas en rojo: regla del 100 %
  (4 tests), `>` por `>=` y mitad por tercio (1 cada una), débito de los medios al 0 % (1).
  Decisiones: sin tope de saldo por ahora; un medio con dos porcentajes en un ticket se
  rechaza; el cupo se mide contra el importe del medio en el ticket, no contra el total; los
  aportes son decimales exactos, así que la columna de saldo (T3) necesita escala suficiente.
  Judgment-day ronda 1 (`a544ed7e`): ningún hallazgo severo; advertencias confirmadas por los
  dos jueces y corregidas en `160ed928`: comentario que afirmaba de más, deriva del saldo de
  un medio facturado por arrastre sin documentar ni fijar con tests, guardas de nulos y
  prioridad de reglas sin test, cotas de convergencia flojas. Mutaciones tras la corrección,
  todas en rojo. Re-juicio: los seis puntos resueltos, sin hallazgos severos.
  JUDGMENT: APPROVED. `Ways.Domain.Tests` 1422/1422. La deriva por arrastre queda como
  decisión del dueño: cupo mensual, que se implementa en T3.
  PR: pablo-nader/ways2026#412. Las PR #411 y #412 están mergeadas en main.

- 2026-10-13 — T3 hecha. Ruta: delegada (un escritor). Commits: el del esquema y el de
  corrección que le sigue. Esquema: `comprobantes_venta.id_venta_origen` (FK compuesta a sí
  misma, UNIQUE parcial, CHECK de espejo fiscal y de espejo sin turno) y `factura_forzada`;
  `facturaciones_de_venta` (RLS estándar, idempotente por venta); `acumulados_de_facturacion`
  con PK `(id_empresa, id_medio_pago, periodo)` y `saldo_a_facturar numeric(18,6)`, al estilo
  de `numeraciones_fiscales` (filtro de tenant a mano, guarda de escritura en `SaveChanges`).
  Predicado único `SinEspejosFiscales` en reportes de ventas, serie temporal, artículos,
  rentabilidad y listado; el libro IVA ventas sigue viendo los espejos.
  Checks: RED (7 de 8 con totales duplicados) y GREEN; integración completa 4003/4003 antes
  del rebase y 4239/4239 después (sobre el commit del esquema); mutaciones del predicado en
  cada lector, todas en rojo.
  Judgment-day ronda 1: ningún hallazgo crítico; confirmados por los dos jueces y corregidos:
  lecturas por id que aceptaban un espejo como venta (devolución y anulación, ahora
  rechazadas con test), test estructural engañable (ahora cuenta lecturas y se prueba a sí
  mismo) y `docs/10` impreciso. Tras la corrección: dominio 1635/1635, aplicación 935/935,
  integración filtrada 485/485; la suite completa posterior queda al CI. Re-juicio: todo
  resuelto, sin advertencias. JUDGMENT: APPROVED.
  Aceptado sin corregir: la migración crea CHECKs, FK e índice único sobre
  `comprobantes_venta` sin `NOT VALID` ni `CONCURRENTLY` (recorre la tabla bloqueando
  escrituras); la base no ata el enlace a una venta que no sea espejo ni la decisión a su
  espejo; el UNIQUE del espejo incluye espejos anulados (a resolver en T5/T8). La bajada de
  la migración se revisó como script, sin ciclo subir-bajar-subir. Un medio de pago o una
  empresa con acumulado no se puede dar de baja.
  Para T4/T5: escribir `acumulados_de_facturacion` solo con SQL crudo; test de carrera de los
  tres 409 nuevos; el espejo va sin turno, pagos, stock ni cuenta corriente.

- 2026-10-13 — T4 hecha. Ruta: delegada (un escritor y tres rondas de corrección).
  `ServicioDeDecisionDeFacturacion`: tras el commit de la venta, en transacción propia, bloquea
  la venta, lee pagos (`importe − vuelto`) con el porcentaje vigente, deriva el mes de la fecha
  del comprobante en la zona del punto de venta, bloquea los acumulados en orden de medio con
  SQL crudo, aplica la política y deja la decisión `pendiente`. `ForzarFactura` viaja en la
  solicitud, se guarda y entra en `ExigirMismoContenido`; en una devolución se rechaza.
  Esquema agregado: `facturacion_decidida_en` (NULL = decisión adeudada; la migración deja lo
  existente como decidido sin reescribir la tabla), `facturacion_intentos`,
  `facturacion_proximo_intento_en` e índice parcial `ix_comprobantes_venta_decision_debida`.
  Autocuración: cada venta barre hasta 5 adeudadas de su punto de venta; un fallo suma intento
  y posterga (1 min que se duplica, tope 4 h); presupuesto de 3 s para la venta en curso y 3 s
  para el barrido.
  Decisiones del dueño (2026-10-13): sin certificado activo del ambiente configurado la venta
  queda como no aplicable, definitivo y sin facturación retroactiva (con certificado de otro
  ambiente, un aviso por empresa por hora en el log); el corte por presupuesto del barrido no
  cuenta como fallo.
  Checks sobre el código final: `dotnet build` sin errores; dominio 1689/1689; aplicación
  950/950; integración completa 4308/4308. Sin test-first estricto: la evidencia de cada
  cláusula son las mutaciones (certificado, idempotencia, locks, período, barrido, tope,
  backoff, presupuesto, aviso), todas en rojo. Sin test de comportamiento: que un corte del
  cliente no cancela la decisión (solo chequeos de texto) y que el presupuesto corta los
  reintentos de la estrategia.
  Judgment-day: ronda 1, un crítico confirmado (decisión sin camino de recuperación) y
  corregido; re-juicio 1, tres advertencias nuevas confirmadas y corregidas; re-juicio 2,
  ESCALATED por un crítico introducido en la ronda 2 (regla de certificado); el dueño eligió
  la regla y autorizó una ronda extra; re-juicio 3: todo resuelto, sin críticos.
  JUDGMENT: APPROVED tras la ronda extra autorizada.
  Limitaciones aceptadas: anular una venta decidida no devuelve el cupo ni cancela la decisión
  pendiente (T8; una decisión "no se factura" no guarda sus aportes, así que hoy no se puede
  revertir exacta); una candidata que agota el presupuesto del barrido en cada llamada no se
  posterga y demora la caja mientras dure la causa; el mes es el de ingestión en el servidor;
  cuenta corriente acumula como cualquier medio según su porcentaje; un ambiente mal
  configurado solo se nota por el log.
  Para T5: no emitir si la venta está anulada (cancelar la decisión); el estado fiscal en la
  respuesta ya prevé `aprobado`/`rechazado`.

- 2026-10-14 — T5a hecha. Ruta: delegada (un escritor y dos rondas de corrección).
  `ServicioDeFacturacionDeVentas.ProcesarDecisionAsync` y
  `POST /api/fiscal/facturaciones/{id}/procesar`. Pasos A (reserva la serie, bloquea la venta
  y la decisión, relee bajo lock, numera e inserta el espejo), C (consulta un espejo
  preexistente; si ARCA no lo tiene y las compuertas pasan, guarda la fecha nueva) y S
  (solicita el CAE), con una guarda de época (`xmin` del espejo) entre pasos.
  Rechazo definitivo: espejo borrado, número devuelto, decisión `rechazada`, débito revertido;
  la venta no se toca. Sin respuesta definitiva: espejo en trámite, intento y espera
  anotados (todavía nadie respeta la espera). Solo el código 602 de la consulta significa
  comprobante inexistente; un error de llamada se consulta antes de liberar; un 600 en la
  consulta renueva el ticket una vez. Anulación: 409
  `venta_facturada_requiere_nota_de_credito` si la venta tiene espejo aprobado o en trámite.
  Cambio fuera de la feature: `ReintentarAsync` (19a) excluye espejos y, ante un error de la
  consulta que no sea 602, corta con 503 en vez de pedir un CAE nuevo.
  Checks sobre el código final: `dotnet build` sin errores; dominio 1691/1691; aplicación
  963/963; integración completa 4390/4390. Sin test-first estricto: la evidencia por cláusula
  son las mutaciones de las tres rondas, todas en rojo y sin sobrevivientes.
  Judgment-day: ronda 1, un crítico confirmado (crear el espejo confiaba en lecturas sin
  lock) y dos advertencias (qué es "no encontrado", qué es rechazo definitivo), corregidos;
  re-juicio 1, dos advertencias nuevas confirmadas (ticket sin renovar en la consulta, 500
  por una carrera benigna), corregidas; re-juicio 2: todo resuelto, sin críticos.
  JUDGMENT: APPROVED. Queda una advertencia confirmada por los dos jueces, de un patrón
  que ya existía en 19a y que T5a usa en más lugares: invalidar el ticket descarta el que
  esté guardado, no el que falló; pasa a T5b.
  Limitaciones aceptadas: todo probado contra simuladores (602 y fecha de la consulta sin
  verificar contra ARCA real); sin tope de tiempo en WSFE, la serie queda retenida mientras
  dura una llamada; cualquier usuario de POS del tenant puede procesar cualquier decisión;
  un espejo `rechazado` por la guarda del número ocupa el lugar del espejo de la venta; una
  compuerta que lanza tras "no encontrado" da 500 sin anotar intento.

- 2026-10-14 — T5b hecha. Ruta: delegada (un escritor, dos rondas de corrección y una
  reversión). `ProcesadorDeFacturacionesPorLote` y `POST /api/fiscal/facturaciones/procesar`
  `{ idPuntoVenta, maximo? }` (10 por defecto, 1 a 25; fuera de rango se rechaza). Primero
  resuelve los espejos en trámite que encabezan cada serie y después las candidatas vencidas,
  por id; las cabezas no gastan `maximo`. La espera entre intentos ahora se respeta en el
  lote; el endpoint de una decisión la sigue ignorando.
  Presupuesto de 20 s sobre un `TimeProvider` inyectado: al vencer no empieza otra decisión;
  la cortada se posterga 1 min sin contar intento; un error de dominio que llega tarde se
  anota como intento; una decisión que lanza se anota con un código estable; un fallo del
  propio lote responde 5xx.
  ARCA: tope de 8 s por llamada a WSFE y un timeout no se reintenta en la misma llamada
  (ninguna operación); firma del ticket de acceso con tope propio de 30 s, que no se
  interrumpe en vuelo; invalidar el ticket solo descarta el que falló.
  Cambios de comportamiento en la emisión directa (19a): tope de 8 s en vez de 100 s, sin
  reintento tras un timeout, y la firma no se cancela con la solicitud.
  Checks: integración completa 4419/4419 sobre el commit de la ronda 2; después, la reversión
  del tope de la firma (una constante y textos) se verificó con `dotnet build`, aplicación
  994/994 y un filtro de integración fiscal 134/134; la suite completa final queda al CI.
  Sin test-first estricto; mutaciones de las tres rondas en rojo, con una sobreviviente
  equivalente (cortar el lote tras un error tardío) revisada por los jueces.
  Judgment-day: ronda 1, cuatro advertencias confirmadas (ARCA lento trababa el punto de
  venta, excepción sin postergar, fallo del lote tragado, tests atados al reloj real),
  corregidas; re-juicio 1, una advertencia nueva confirmada (firma colgada sin anotar),
  corregida; re-juicio 2: todo resuelto, sin críticos, pero los dos jueces señalaron que
  bajar el tope de la firma a 15 s era innecesario y dañino. Se revirtió a 30 s, el valor ya
  revisado, sin abrir otra ronda. JUDGMENT: APPROVED, con esa reversión posterior no juzgada.
  Limitaciones aceptadas: cualquier usuario de POS del tenant puede lanzar el lote de otro
  punto de venta del tenant; un pedido puede durar más que el presupuesto mientras hay una
  firma en vuelo (del orden del presupuesto más el tope de la firma); las lecturas a ARCA
  tampoco se reintentan tras un timeout; una cabeza cuya decisión no está pendiente no se
  puede postergar ni anotar; un fallo del lote pierde de la respuesta lo ya emitido; todo
  probado contra simuladores. Nada llama al lote todavía (lo hará el POS en T6).

- 2026-10-14 — T5c hecha. Ruta: delegada (un escritor y una ronda de corrección).
  `EmisionEnLineaAlCobrar.DecidirEmitirYSanearAsync`, llamado por POST /api/ventas tras el
  commit: decide (3 s), y solo si queda `pendiente` y hay un ticket de acceso vigente
  guardado hace un intento de emitir con min(4 s, lo que quede de 8 s); después el barrido
  con el resto. En la caja nunca se firma un ticket. La respuesta trae el estado fiscal (seis
  estados, incluido `cancelado`), un código de error estable y, si salió, los datos del
  comprobante; un reenvío informa el estado actual sin volver a emitir.
  Checks sobre el código final: `dotnet build` sin errores; dominio 1691/1691; aplicación
  1014/1014; integración completa 4439/4439 (una corrida anterior se descartó por errores de
  Docker compartido con otra sesión). Sin test-first estricto; mutaciones en rojo.
  Judgment-day: ronda 1, sin críticos; confirmadas por los dos jueces y corregidas: el error
  de la respuesta mostraba texto interno, la documentación omitía `cancelado` y el bloqueo de
  serie, tests faltantes; re-juicio: todo resuelto, sin críticos ni advertencias.
  JUDGMENT: APPROVED. Después, solo comentarios de documentación (el tipo compartido y las
  observaciones de ARCA).
  Limitaciones aceptadas: hasta que algo firme un ticket (el lote, que todavía nadie llama)
  el intento al cobrar no corre; el ticket vive en la memoria de cada proceso; un corte en
  la caja bloquea la emisión en línea de su serie hasta que corra un lote; si ARCA rechaza
  el ticket guardado, la caja puede esperar la firma completa; un rechazo de ARCA sin
  códigos llega al cajero como `error_inesperado` y uno de nivel de llamada como `0`; los
  endpoints de lote y de una decisión siguen devolviendo el texto completo del error.

- 2026-10-14 — T6a hecha. Ruta: delegada (un escritor y una ronda de corrección). Commits
  `0aa6ef5a` (feat) y `90e7221a` (fix). `ServicioDeFacturacionDeVentas.LeerEstadoDeVentaAsync`:
  con decisión reutiliza `LeerEstadoAsync`; sin decisión, espejo o `facturacion_decidida_en`
  ⇒ `no_corresponde`, si no `sin_decidir` (`EstadosFiscalesDeVenta.SinFila`). `GET /{id}` lo
  agrega en el endpoint (la anulación sigue con `fiscal` nulo); `por-turno` suma
  `EstadoFiscal` con una consulta en lote. Error como código estable (`CodigoDeError`).
  Checks: `dotnet build` sin errores; aplicación 1014/1014; dominio 1691/1691; integración
  filtrada 615/615 (antes de un renombre de variable) y 368/368 tras la corrección; la suite
  completa queda al CI. Sin RED previo: la evidencia son mutaciones en rojo (lectura sin
  `fiscal`, regla `sin_decidir`, conjunción del espejo, `CodigoDeError`, mapeo del lote,
  filtro de baja y respaldo ante fallo).
  Dos tests estructurales ajustados: la guarda del POST corta en su `.WithSummary(` y la
  lista permitida de lectores suma la lectura por id.
  Judgment-day: ronda 1, un crítico confirmado por los dos jueces (reimprimir una venta
  facturada daba 500 si el cliente, punto de venta, empresa o tipo estaba dado de baja, y 409
  si el CUIT dejó de ser numérico), corregido: el catálogo de la factura ignora solo la baja
  lógica y, si no se puede proyectar, el estado sale sin comprobante. Re-juicio: resuelto,
  sin hallazgos. JUDGMENT: APPROVED.
  Información sin corregir: el test de otro tenant no ejercita la consulta nueva (el 404 sale
  antes); ninguna guarda estructural fija que las lecturas no emitan; ningún test cuenta las
  consultas del lote.

  PR: pablo-nader/ways2026#443, mergeada.

- 2026-10-14 — T6b hecha. Ruta: delegada (un escritor y dos rondas de corrección). Commits
  `a9e26b89` (feat), `2a113775` y `18f3711d` (fix). Botón "Facturar" (apagado por defecto,
  se apaga tras cada venta, oculto en presupuesto) que viaja como `forzarFactura` en la venta
  encolada. `ventaSeguroSeFactura` (forzada o algún medio al 100 %) en
  `pos/facturacionAlCobrar.ts`. Si seguro se factura y hay conexión, la caja espera hasta 5 s
  la respuesta de esa venta (`esperarRespuesta` en `useSincronizacionOffline`); aprobada,
  imprime `facturaDeVenta` (letra, número, fecha, CAE y vencimiento, sin QR) con el
  comprobante del servidor; si no, el ticket común, que no cambió. Con porcentaje parcial
  nunca se imprime factura, en ningún camino.
  Checks: `tsc -b` limpio; lint sin errores; Vitest completo 7265/7265 sobre el código final.
  RED observado en helper, plantillas, ShellPos y cola; los tests de `Pos` nacieron verdes y
  su evidencia son mutaciones (umbral del 100 %, reset del botón, condición de conexión,
  factura sin aprobar, cuerpo del servidor), todas en rojo.
  Judgment-day: ronda 1, sin críticos; confirmadas por los dos jueces y corregidas: un fallo
  transitorio agotaba los 5 s, la factura salía con los montos locales y no con los del
  servidor, una respuesta sin espera quedaba en memoria, un JSDoc desplazado. Re-juicio 1:
  resueltas; los dos jueces confirmaron que el fallo transitorio solo liberaba a la cabeza de
  la cola, corregido en la ronda 2. Re-juicio 2: resuelto, sin hallazgos. JUDGMENT: APPROVED.
  Información sin corregir: un cambio de cobro durante el encolado puede dejar una respuesta
  en memoria (acotado); la liberación en la rama `sinComprobante` se prueba solo en el hook;
  el cartel de venta finalizada ya no dice "Guardada en este dispositivo" si el servidor
  respondió a tiempo. Sin release del POS hasta T7.

  PR: pablo-nader/ways2026#444.

- 2026-10-14 — T6c hecha. Ruta: delegada (un escritor). Commit `234e558c`, apilado sobre T6b.
  `crearDisparadorDeLote` (`pos/loteDeFacturacion.ts`): `POST /fiscal/facturaciones/procesar`
  solo con conexión y punto de venta, de a uno, con a lo sumo una vuelta pendiente, errores
  tragados y tope de 30 s en la caja; se dispara al terminar cada pasada de drenaje y tras
  cada venta directa. "Ventas del turno": Facturada / Pendiente (`pendiente` y
  `sin_decidir`) / Rechazada, y "Reimprimir factura" para las aprobadas y no anuladas, con
  `facturaDeVenta` de reimpresión desde la lectura por id (sin datos: error, nunca ticket).
  Checks: `tsc -b` limpio; lint sin errores; Vitest completo 7319/7319. Sin RED previo:
  mutaciones en rojo (vuelta pendiente, de a uno, sin conexión, re-chequeo de conexión,
  disparo tras el drenaje, venta directa, `sin_decidir`, botón para no aprobadas, estado de
  la lectura por id).
  Judgment-day ronda 1: ningún hallazgo severo confirmado por los dos jueces. JUDGMENT:
  APPROVED. Confirmada por los dos (sugerencia): el tope de 30 s libera la caja mientras el
  lote del servidor sigue y puede superponer otro. Sospechosos de un solo juez: el "de a
  uno" vive por montaje del POS (salir y volver a la pantalla dispara otro lote); el lote de
  la reconexión se saltea porque `enLinea` todavía es falso; un lote en curso puede ocupar
  la serie y la venta siguiente sale con ticket aunque se pidió factura; el botón de
  reimprimir no refresca la fila si la factura ya no está aprobada. Van a T6d.

- [x] **T6d — Seguimiento del lote.** Un lote de a uno por punto de venta en el proceso (no
  por montaje), tope de la caja por encima del peor caso del servidor, lote de la
  reconexión, refresco de la fila al reimprimir. Ruta: delegada.
  2026-10-14 — hecha. Commit `1943b85f`, apilado sobre T6c. Estado del lote en un mapa del
  módulo por punto de venta; peor caso del servidor 60 s con la configuración por defecto
  (20 s de lote + 30 s de firma WSAA + 5 s de cierre + 5 s de conteo) y tope de la caja
  75 s; una pasada con algún envío exitoso o una venta directa aceptada prueban que el
  servidor responde y piden el lote aunque `enLinea` siga falso; un ciclo con la cola vacía
  pide uno después de refrescar si estaba sin conexión; reimprimir una factura ya no aprobada
  actualiza la fila.
  Checks: `tsc -b` limpio; lint sin errores; Vitest completo 7331/7331; los archivos tocados
  re-corridos por el orquestador 223/223. Sin RED previo: nueve mutaciones en rojo.
  Judgment-day ronda 1: ningún hallazgo severo confirmado por los dos jueces. JUDGMENT:
  APPROVED. Confirmada por los dos (sugerencia): un comentario prometía "la instancia viva
  más reciente"; corregido después del juicio, solo comentarios. Sospechosos de un solo juez:
  ningún test mata la guarda que evita el doble lote en un ciclo normal con conexión; el
  mapa no se limpia al cerrar sesión (una vuelta pendiente puede salir con la sesión
  siguiente); el tope de 75 s queda corto si se sube `PresupuestoDeLoteSegundos` o
  `TimeoutDeWsaaSegundos`.
- [x] **T6e — Lote tras la venta solo si queda pendiente.** El lote tras cada venta podía
  ocupar la serie hasta 20 s y la venta siguiente con factura salía con ticket. Decisión
  (2026-10-14, delegada por el dueño: "lo que te parezca mejor"): después de una venta el
  lote sale solo si alguna venta enviada quedó con estado fiscal `pendiente` o `sin_decidir`
  (o sin estado); los ciclos periódicos, el arranque y la reconexión lo piden siempre. Ruta:
  delegada.
  2026-10-14 — hecha. Commit `e3ff22b8`, apilado sobre T6d. `ventaNecesitaElLote` en
  `pos/loteDeFacturacion.ts`; `drenarOutbox(siempreElLote)` con `loteSiempreRef` (un pedido
  que se suma a un drenaje en curso solo puede subirlo); `procesarFacturaciones(fiscal)` tras
  la venta directa. El drenaje del cierre de turno sigue la regla de tras-la-venta (aceptado:
  los ciclos lo cubren).
  Checks: `tsc -b` limpio; lint sin errores; Vitest completo 7358/7359 con un intermitente
  ajeno (`GrillaDeItemsDeCompra.test.tsx`, 124/124 solo); archivos tocados re-corridos por el
  orquestador 660/660. Sin RED previo: cinco mutaciones en rojo.
  Judgment-day ronda 1: un juez sin hallazgos y otro con una sugerencia (comentario de
  `procesarFacturaciones` desactualizado), corregida después, solo comentario. JUDGMENT:
  APPROVED.

## T7 (2026-10-14)

Decisión del dueño: la factura impresa lleva todo lo exigible ahora (QR, emisor, receptor, IVA
discriminado, ingresos brutos, inicio de actividades, leyenda de transparencia fiscal), con las
columnas nuevas que haga falta. `VistaDeTicket` no aplica: solo imprime el cierre web; las
facturas salen por ESC/POS en el POS de escritorio.

- [x] **T7a — Comando QR en ESC/POS.** `GS ( k` modelo 2 en `ConstructorDeTicket`. Ruta: delegada.
- [x] **T7b — Servidor: datos legales.** `ingresos_brutos` e `inicio_actividades` en
  `empresas` (migración, ABM y descriptor web) y `DatosDeFacturaFiscal` con emisor, receptor e
  IVA discriminado. Ruta: delegada.
- [x] **T7c — POS: factura legal.** `facturaDeVenta` con encabezado legal, receptor, IVA, leyenda
  y QR; reimpresión igual. Ruta: delegada.
- Release del POS después de T7c, avisando al dueño antes.

### Contenido legal verificado (fuentes primarias ARCA/AFIP, 2026-10-14)

- Formato ticket permitido: RG 4291 art. 15 (modelo Tique-Factura de la RG 3561, CAE al pie).
- Emisor (RG 1415 Anexo II A I a): razón social, domicilio comercial, CUIT, ingresos brutos,
  leyenda de condición frente al IVA, "INICIO DE ACTIVIDADES" con la fecha.
- Comprobante: letra, "Código Nº" del tipo, PV (5) y número (8), fecha; "C.A.E. N°" y
  "Fecha de Vto." al pie. Sin datos de imprenta ni ORIGINAL/DUPLICADO (RG 4291 art. 14 d).
- Receptor (A II): consumidor final → "A CONSUMIDOR FINAL"; identificación con DNI/CUIL/CDI
  desde $10.000.000 (RG 5700 y RG 5866/2026), nombre y domicilio pueden ir "NR"; resto →
  nombre, domicilio, CUIT y condición de IVA (RG 5616/2024 exige la condición del receptor).
- Condición de venta (A III / B e).
- Transparencia fiscal (RG 5614; RG 1415 B g; RG 4291 art. 15): en el ticket, bajo
  "OTRAS LEYENDAS", título "TRANSPARENCIA FISCAL", "IVA Contenido" y "Otros Impuestos
  Nacionales Indirectos". La norma de diseño no distingue letra.
- QR (RG 4892 y especificación vigente): base `https://www.arca.gob.ar/fe/qr/`; el host de
  afip sigue redirigiendo. Sin tamaño ni nivel de corrección fijados. No reemplaza datos.
- Fuentes: biblioteca.afip.gob.ar/dcp/REAG01001415_2003_01_07,
  biblioteca.afip.gob.ar/dcp/REAG01004291_2018_08_02, RG 4892 (REAG01004892_2020_12_22),
  arca.gob.ar/fe/qr/documentos/QRespecificaciones.pdf, Boletín Oficial RG 5614/5616/5700/5866.

Decisiones de T7c: la leyenda de transparencia va en todas las letras (A y B con el IVA de
ARCA, C con $0,00: la norma no fija el importe para monotributo y prohíbe discriminar IVA, a
confirmar con el contador); condición de venta "Cuenta corriente" si algún pago es a cuenta
corriente, si no "Contado"; el servidor pasa a `www.arca.gob.ar` en el QR.

### Evidencia de T7 (2026-10-14)

Ruta: delegada (tres escritores, dos de ellos en worktrees aislados, y tres arreglos).
Commits: `f358ebeb` (T7a), `13c27507` (T7b), `770c58ed` y `b95d8102` (correcciones de T7b),
`90a3229d` (T7c), `3ba76831` (corrección de T7c).
- T7a: `ConstructorDeTicket.qr` (GS ( k modelo 2, corrección M, módulo 5), rechaza contenido
  que no sea ASCII imprimible o exceda 2331 bytes antes de emitir bytes.
- T7b: `empresas.ingresos_brutos` (citext 30) e `inicio_actividades` (date), migración
  `DatosLegalesDeEmpresaParaFactura`; PUT de empresa sigue siendo reemplazo completo (como las
  alícuotas de percepción). `DatosDeFacturaFiscal` suma Emisor, Receptor y Totales
  (`ComposicionDeTotalesFiscales.Componer` sobre los ítems guardados).
- Corrección: `comprobantes_venta.datos_legales_fiscales` (jsonb, solo en espejos), migración
  `DatosLegalesCongeladosEnEspejoFiscal`; se escribe en la transacción A con emisor, receptor,
  documento y condición de IVA enviados a ARCA; el pedido, el reintento, el QR y la lectura
  salen de esa foto; NULL (espejos anteriores) lee las filas actuales.
- T7c: factura en modelo ticket con el contenido verificado, leyenda de transparencia en todas
  las letras, "C.A.E. N°" / "Fecha de Vto." y QR al pie; QR con base `www.arca.gob.ar`.
Checks: `dotnet build` limpio; aplicación 1026/1026; integración fiscal filtrada 276/276 sobre
el código final del servidor; Vitest completo 7384/7384 antes de la corrección de T7c e
impresión + ShellPos 197/197 después. Sin RED previo: mutaciones en rojo en cada tramo.
Judgment-day: T7a+T7b ronda 1, advertencia confirmada (datos legales leídos en vivo),
corregida; re-juicio, advertencia confirmada (el reintento mezclaba datos congelados y
vivos), corregida en la ronda 2; re-juicio final resuelto. T7c ronda 1 sin severos; la
sugerencia confirmada (spec con host viejo) y cuatro de un solo juez corregidas; re-juicio
final resuelto. JUDGMENT: APPROVED.
Información sin corregir: ningún test mata el congelado del CUIT del emisor; la consulta de un
espejo existente usa el CUIT actual; los totales usan código y nombre vivos de la alícuota;
"Codigo N°" se deduce de la letra (el DTO no trae el código de ARCA); `impTrib` no se imprime
(hoy siempre 0); la leyenda en la C de monotributo con $0,00 queda a confirmar con el contador.

## T8 (2026-10-14)

Decisiones del dueño: opción (a), "Anular" una venta con factura aprobada anula el ticket como
hoy y emite sola una NC fiscal (3/8/13, misma letra) por el total, con `CbtesAsoc` a la
factura, por el mismo circuito del espejo; mismos roles que la anulación; con la factura en
trámite, 409 `venta_con_factura_en_tramite`. La caja imprime la NC aprobada y se puede
reimprimir desde "Ventas del turno". NC parcial por devolución fuera de alcance.

Decisiones técnicas: segunda decisión en `facturaciones_de_venta` (`id_facturacion_origen`);
espejo NC con `id_venta_origen` = venta e `id_comprobante_asociado` = espejo factura; la
anulación solo inserta la decisión (nunca toca `numeraciones_fiscales`); cupo devuelto al
aprobarse la NC; NC rechazada queda para reintento manual (T9); se corrige que anular una venta
con factura rechazada no devolvía el cupo. `CbtesAsoc` según el WSDL de WSFEv1: después de
`CondicionIVAReceptorId`, antes de `Tributos`; Tipo/PtoVta/Nro obligatorios, Cuit/CbteFch
opcionales.

- [x] **T8a — Esquema, lectores y `CbtesAsoc`** (sin cambio de comportamiento). Ruta: delegada.
- [x] **T8b — Emisión de la NC** (proceso, lote, cupo). Ruta: delegada.
- [ ] **T8c — Anulación, intento en línea, lectura, POS e impresión de la NC.** Ruta: delegada.
  Obligatorio: la transacción de la anulación invoca `CancelarRechazadaDeVentaAnuladaAsync` cuando la
  factura de la venta está rechazada (desde T8b el lote ya no la liquida; hasta T8c queda a mano).

## Próximo paso

PR de T8a; después T8b (emisión de la NC) apilado.

### Evidencia de T8a (2026-10-15)

Commits `cf45be8c` (feat) y `ce1c1565` (fix) más el comentario de la clase. Migración
`20261015005752_NotaDeCreditoEsquema`: `facturaciones_de_venta.id_facturacion_origen` (FK
compuesta a sí misma, clave alternativa, únicos parciales factura/NC, CHECKs) y únicos
parciales del espejo factura/NC en `comprobantes_venta`, con índices comunes de soporte de las
FK (`ix_comprobantes_venta_venta_origen`, `ix_facturaciones_de_venta_comprobante`); 23505 de
los únicos nuevos → 409 `nota_de_credito_duplicada`. Lectores por venta eligen la factura
(lectura por id, por turno, proceso, decisión, lote, anulación). `CbtesAsoc` en `SolicitudDeCae`
y `MapeadorWsfe` en el orden del WSDL de WSFEv1. Sin cambio de comportamiento.
Checks: build limpio; aplicación 1030/1030; dominio 1691/1691; integración completa 4483/4483
sobre `cf45be8c`; tras la corrección, filtro de esquema, NC, migraciones y guardas 98/98.
Mutaciones en rojo (lectores por turno, por id, candidatos y conteo del lote; índice de soporte).
Judgment-day: ronda 1, advertencia confirmada (índices únicos parciales dejaban las FK sin
soporte), corregida; re-juicio: resuelta, quedaba un comentario de clase viejo, corregido (solo
comentario). JUDGMENT: APPROVED.
Información para T8b: varios filtros de tipo factura (búsqueda del espejo en `ProcesarAsync`,
`ExisteEspejoDeLaVentaAsync`, la decisión, `TieneFacturaVigenteAsync`, cabezas del lote) todavía
no tienen un test que los distinga; se cubren cuando haya NC reales.

### Evidencia de T8b (2026-10-15)

Commits `b5c9fa0b` (feat) y `7a88dc11` (fix). La decisión de NC se procesa como la factura:
exige venta `anulado` y factura aprobada (si no, pendiente con `nota_de_credito_venta_no_anulada`
/ `nota_de_credito_sin_factura_aprobada` y espera de compuerta); espejo NC{letra} con ítems,
total y datos legales congelados de la factura; `CbtesAsoc` (tipo, PV, número, CUIT congelado,
fecha fiscal); paso A con la serie NC → venta → decisión; aprobada devuelve todo el cupo de la
factura; rechazada devuelve el número y no toca el cupo; el lote procesa NC en su propia serie.
Una factura rechazada de una venta anulada pasa a `cancelada` y termina de devolver el cupo, por
la decisión suelta (el lote no la toma). La NC compone con alícuotas dadas de baja.
Checks: build limpio; aplicación 1040/1040; dominio 1695/1695; integración (facturación, NC,
anulación, guardas) 549/549 sobre `b5c9fa0b` y 550/550 tras la corrección. Sin RED previo: 31
mutaciones en rojo más la de las alícuotas.
Judgment-day: ronda 1, confirmado por los dos jueces el candidato del lote para facturas
rechazadas de ventas anuladas (sin índice, sin espera, ocupa el máximo), quitado; corregido
también el caso de alícuotas dadas de baja (un juez). Re-juicio: resueltos; los dos señalan que
hasta T8c esa liquidación queda a mano (anotado como obligatorio en T8c). JUDGMENT: APPROVED.
Información: una NC cuya factura nunca se aprueba quedaría pendiente (inalcanzable con la regla
de anulación de T8c).
