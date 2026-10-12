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
- **Alcance del acumulado:** por `(id_empresa, id_medio_pago)`, perpetuo, con tope.
- **Dónde se decide:** en el servidor, al ingerir la venta, en una transacción corta propia.
  En la solicitud solo viaja `ForzarFactura`; nunca la decisión, el porcentaje ni el CAE.
- **Diferido:** fila de decisión en `pendiente`; la procesa un endpoint que el POS dispara en
  cada drenaje y rendición. Sin certificado activo no se decide ni se mueve el acumulado.

## Modelo (resumen)

- `medios_pago.porcentaje_facturacion numeric(5,2) NOT NULL DEFAULT 0`, CHECK 0..100.
- `comprobantes_venta.id_venta_origen integer NULL` (FK compuesta, UNIQUE parcial).
- `comprobantes_venta.factura_forzada boolean NOT NULL DEFAULT false`.
- `facturaciones_de_venta` (operativa, RLS): decisión, estado, aportes, comprobante fiscal.
- `acumulados_de_facturacion`: PK `(id_empresa, id_medio_pago)`, saldo y totales.

## Alcance autorizado

Las tareas T1–T9. Fuera de alcance: CAEA, alta del certificado real y homologación (19b).

## Entrega

Estrategia `ask-on-risk` con cadena `stacked-to-main` (ya elegida por el dueño). Pronóstico:
~3.000 líneas, una PR por tarea. Cada PR pasa judgment-day antes de abrirse.

## Tareas

- [x] **T1 — Porcentaje en el medio de pago.** Migración, `MedioPago`, contratos, servicio,
  descriptor web e instantánea del POS. Checks: integración del ABM (0..100, rechazo fuera de
  rango), Vitest del descriptor. Ruta: delegada (2+ archivos no triviales).
- [ ] **T2 — `PoliticaDeFacturacionPorMedio`.** Dominio puro. Checks: unitarios sin base
  (0 %, 100 %, mixto, forzado, convergencia sobre N tickets, reversión). Ruta: delegada.
- [ ] **T3 — Esquema y no doble conteo.** Enlace, `factura_forzada`, las dos tablas, RLS,
  predicado compartido en reportes y listado. Checks: integración con espejo sembrado,
  guardas estructurales, prueba de mutación del predicado. Ruta: delegada.
- [ ] **T4 — Decisión al ingerir.** Servicio de decisión, `ForzarFactura` en la solicitud y en
  `ExigirMismoContenido`, estado fiscal en la respuesta. Depende de T1–T3.
- [ ] **T5 — Emisión del espejo y procesador.** `EmitirDesdeVentaAsync` en dos transacciones,
  endpoint `procesar`, intento en línea. Antes: confirmar con un test que hoy un timeout de
  ARCA revierte la fila pendiente. Depende de T4.
- [ ] **T6 — POS.** Toggle de forzado, bandera en la cola, espera acotada, captura de la
  respuesta del drenaje. Depende de T4 y T5.
- [ ] **T7 — Impresión.** Factura con CAE y QR en `VistaDeTicket` y en el POS de escritorio,
  reimpresión. Depende de T5.
- [ ] **T8 — Anulación y nota de crédito de una venta facturada.** `CbtesAsoc`, espejo de NC,
  reversión del acumulado. Depende de T5.
- [ ] **T9 — Back office.** Pendientes y rechazadas, reporte vendido vs. facturado por medio.

## Supuestos pendientes de confirmación del dueño

- El forzado manual descuenta del cupo del medio.
- El acumulado es perpetuo y por empresa (no por caja ni mensual).
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
  Commits `1b6c657d` y el de corrección que le sigue. RED observado (compilación y 9 tests
  de Vitest) y GREEN. Checks: `dotnet build` sin errores; `Ways.Application.Tests` 858/858;
  `Ways.IntegrationTests` completa 3781/3781 sobre `1b6c657d`; Vitest 5896/5896; `tsc` limpio.
  Judgment-day ronda 1: ningún hallazgo severo; una advertencia confirmada por los dos jueces
  (un PUT sin el campo pisaba el porcentaje con 0), corregida con su test (RED y GREEN): el
  campo ausente conserva lo guardado. Tras la corrección se re-corrieron solo las pruebas de
  medios de pago e instantánea (29/29) y las de aplicación; la suite completa queda al CI.
  Sin prueba de mutación de la cláusula de rango. RDD apagado en el clon: sin revisión nativa.
  Decisiones: sin subir la versión de la instantánea (una guardada sin el campo lee 0).

## Próximo paso

T1.
