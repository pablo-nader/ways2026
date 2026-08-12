# 11 — Programa post-paridad

> Continúa la numeración de etapas del [doc 10](10-modelo-de-datos.md). La etapa 8 cerró el
> programa de paridad: el sistema nuevo hace todo lo que hacía el legacy y ya lo supera en
> compras, transferencias e inventario. Este documento define el **programa de crecimiento**:
> las etapas 9 a 19, que no persiguen paridad sino valor de negocio.

A partir de acá el legacy deja de ser la referencia de comportamiento. Se lo cita únicamente
donde una pantalla vieja quedó fuera del contrato de paridad y todavía tiene valor
(sección G del [doc 01](01-features-existentes.md) y la lista de reposición de
`alsina/imprimirArticulos.php`), o donde su ausencia explica una decisión de alcance. Todo lo
demás se diseña desde el modelo del doc 10 hacia adelante, sin heredar sus formas.

El criterio de ordenamiento del programa es, en este orden:

1. **Lo que destruye datos si se posterga.** Una etapa que solo se pospone cuesta tiempo; una
   etapa que captura información que hoy se pierde para siempre cuesta historia. Esas van
   primero, aunque sean chicas.
2. **Lo que permite medir.** No se puede mejorar lo que no se mide, y hoy el sistema no
   agrega nada del lado del servidor.
3. **Lo que el rubro exige.** El negocio es de alimentos y perecederos; el control de
   vencimientos no es una mejora opcional.
4. **Lo que amplía el ciclo documental y operativo.**
5. **Lo que tiene un plazo externo pero no un apuro interno.**

---

## Decisiones de alcance

Decisiones del dueño del producto tomadas el 2026-08-10, durante la ronda de definición de
este programa. Se registran acá para que las fases de proposal de cada etapa no las
reabran sin motivo nuevo.

| # | Decisión | Efecto en el programa |
|---|---|---|
| 1 | **Facturación fiscal ARCA: sin apuro.** El negocio hoy no factura fiscalmente y el legacy nunca lo hizo. | Va última (Etapa 19), a pesar de ser la de mayor tamaño. |
| 2 | **El rubro es alimentos y perecederos.** | Lotes con vencimiento se diseña con control real (FEFO, alertas), no como un campo informativo, y sube de prioridad (Etapa 12). |
| 3 | **Caja Virtual (G4) muere con el legacy.** El negocio de recargas y servicios no se porta. | Queda excluida. No se diseña, no se migra, no se reserva schema. |
| 4 | **Entra el ciclo documental completo: órdenes de compra, presupuestos a clientes y remitos.** | Etapas 16 y 17. |
| 5 | **Números de serie: excluidos.** El rubro no los necesita. | Fuera del programa. La trazabilidad unitaria no se diseña ni se deja reservada. |

### Exclusiones explícitas

- **Caja Virtual y sus cuatro canales** (Virtual, Claro, SUBE, Efectivo) — decisión 3.
- **Números de serie / trazabilidad unitaria** — decisión 5. La trazabilidad por lote
  (Etapa 12) es el nivel de granularidad elegido.
- **Combos** — el legacy tiene el feature muerto (`doc 01` §B3: la línea `COMBO...` solo
  descuenta del acumulado de descuentos, y `combos.php` está roto). No se porta un feature
  que nunca funcionó. Si en algún momento se quiere combos de verdad, es una etapa nueva con
  su propio diseño, no una migración.
- **Impresión de comanda por área hardcodeada** (`id_area == 8` en el legacy, con `areas`
  llegando solo hasta el 6). Si se quiere comanda, se diseña sobre el padrón `areas` real.

---

## Tabla de etapas

| Etapa | Alcance | Desbloquea |
|---|---|---|
| 9 | Costo congelado en la línea de venta (implementada — `2026-08-11-stage-9-costo-congelado`, PR #75) | Margen real y rentabilidad histórica |
| 10 | Capa de agregación + dashboard (implementada — `2026-08-12-stage-10-agregacion-dashboard`, PRs #76-#86) | Medir el negocio (supera G1) |
| 11 | Infraestructura de exportación + reportes descargables (implementada — `2026-08-12-stage-11-exportacion-reportes`, PRs #87-#98: XLSX con ClosedXML auditada, 16 exports, G2/G3, print views) | Todo lo imprimible y descargable posterior |
| 12 | Lotes y vencimientos con control FEFO: stock por lote, sugerencia al vender, alertas de próximos a vencer, decomiso | Operar el rubro perecedero sin pérdida ciega |
| 13 | Stock inteligente: mínimos y punto de pedido por artículo + PV, alertas de bajo stock, lista de reposición por proveedor, sugerencia de compra | Comprar por dato y no por memoria |
| 14 | Auditoría y trazabilidad de operaciones sensibles (precios, anulaciones, ajustes de stock, roles, reliquidaciones) | Responder "quién hizo esto y cuándo" |
| 15 | Cuenta corriente de proveedores con ledger propio: movimientos inmutables, pagos parciales, historial | Simetría con la CC de clientes (Etapa 7) |
| 16 | Órdenes de compra: OC → recepción → conversión a comprobante de compra, con estados | Circuito de compra completo, no solo el registro |
| 17 | Presupuestos y remitos: presupuesto convertible en venta, remito con salida de stock y facturación posterior consolidada | Ciclo documental de venta completo |
| 18 | Etiquetas de góndola, carteles imprimibles y consulta de precios para el salón | Operación de piso |
| 19 | Facturación electrónica ARCA: schema fiscal completo, WSAA/WSFE con certificado por empresa, CAE/CAEA, comprobante con QR, homologación | Vender con factura fiscal |

---

## Detalle por etapa

### Etapa 9 — Costo congelado en la línea de venta

**Alcance.** Agregar `costo_unitario` a `items_comprobante_venta` y capturarlo en el momento
de emitir, junto al resto del snapshot que la línea ya congela (precio, descuento, alícuota,
lista, oferta). Backfill best-effort de las ventas ya emitidas usando el
`articulos.costo_nominal` actual, marcado explícitamente como aproximado para que ningún
reporte lo presente como dato histórico real.

**Por qué va primera.** Es la única etapa del programa cuya postergación **destruye datos**.
Hoy el único costo del sistema es `articulos.costo_nominal`: mutable, sin historia, pisado
por cada compra confirmada. Cada venta que se emite sin costo congelado pierde su margen
para siempre — no es recuperable después con ninguna etapa posterior. Todo lo demás en este
programa se puede hacer más tarde sin costo adicional; esto no.

**Dependencias.** Ninguna. **Tamaño:** chica — una columna, una línea en `ServicioDeVentas`,
una migración y el script de backfill.

**Decisiones abiertas para el proposal.** Qué costo congelar exactamente cuando el artículo
tiene costo en moneda distinta o con impuestos incluidos; si el backfill se marca con una
columna `costo_es_estimado` o se infiere por fecha de corte; si se congela también el costo
en las líneas de nota de crédito.

### Etapa 10 — Capa de agregación + dashboard

**Alcance.** Endpoints de agregación server-side: ventas por período, por vendedor
(`id_empleado`, que ya viaja en cada comprobante), por punto de venta y por medio de pago;
ticket promedio; top de artículos vendidos; compras por proveedor; gastos; y margen real
apoyado en la Etapa 9. Dashboard web con gráficos que cubre y supera la pantalla G1 del
legacy (ventas y gastos de los últimos 7 días). Incluye comisiones por vendedor como reporte
calculado, no como liquidación registrada.

**Por qué acá.** Hoy el sistema no agrega nada: todos los endpoints son CRUD o listados
paginados, y el único cálculo derivado es el saldo de proveedor. El schema ya soporta todas
estas preguntas; falta la capa que las responda. Es la etapa que convierte los datos
capturados en decisiones, y todo lo que sigue se justifica o se descarta con lo que muestre.

**Dependencias.** Etapa 9 para la dimensión margen (el resto de los agregados no la
necesita). **Tamaño:** media — grande del lado web, moderada del lado API.

**Decisiones abiertas.** Si los agregados se resuelven con consultas directas o con vistas
materializadas y cada cuánto se refrescan; qué granularidad temporal se expone (día, semana,
mes) y cómo se maneja la zona horaria en los cortes; si el dashboard es único o configurable
por rol; regla de comisión (porcentaje plano, por artículo, por margen) — la fórmula es una
decisión de negocio, no técnica.

### Etapa 11 — Infraestructura de exportación + reportes descargables

**Alcance.** Elegir **una sola vez** la librería de generación de archivos (Excel/CSV/PDF) y
el patrón de endpoint de descarga, y estrenarlo con los reportes que ya se necesitan: Ver
Cajas con su detalle (G2), Caja General / Caja Z (G3), ventas y compras exportables, stock
exportable y estado de cuenta imprimible.

**Por qué acá.** Hoy no hay nada: ningún `.csproj` referencia una librería de Excel, CSV o
PDF, ningún endpoint devuelve un archivo y el front no tiene `xlsx` ni `jspdf`. Es una pieza
transversal, y el momento de decidirla es cuando aparece la primera necesidad real —
inmediatamente después del dashboard, que genera la pregunta "¿y esto lo puedo bajar?". Una
vez fijado el patrón, las etapas 12, 13 y 18 lo consumen sin volver a decidir.

**Dependencias.** Etapa 10 para los reportes que exportan agregados. **Tamaño:** media.

**Decisiones abiertas.** Qué librería y con qué licencia (varias de las conocidas del
ecosistema .NET tienen licencia comercial); generación sincrónica en el request o job
asincrónico con descarga diferida para volúmenes grandes; si el PDF se arma server-side o el
navegador imprime una vista; nombres, encabezados y branding por empresa en los archivos
generados.

### Etapa 12 — Lotes y vencimientos (FEFO)

**Alcance.** Lote con fecha de vencimiento, stock por lote, sugerencia FEFO al vender
(primero el que vence antes), alertas de mercadería próxima a vencer y circuito de
ajuste/decomiso por vencimiento con su movimiento de stock correspondiente. Módulo
**activable por empresa**: no todos los tenants venden perecederos y el POS no puede pagar el
costo de una dimensión extra que no usa.

**Por qué acá.** Decisión 2: el rubro es alimentos. La mercadería vencida es pérdida directa
y hoy el sistema no tiene forma de anticiparla. Se prioriza detrás de las etapas de medición
porque necesita que exista la infraestructura de reportes y alertas para ser útil, no solo la
tabla.

**Dependencias.** Etapa 11 para lo imprimible (planilla de vencimientos, control de góndola).
**Tamaño:** grande — toca el modelo de stock, la emisión de venta, la recepción de compra y
las transferencias.

**Decisiones abiertas.** Si el lote es obligatorio, opcional o configurable por artículo; qué
pasa con el stock existente al activar el módulo (lote "sin identificar" inicial); si FEFO es
sugerencia o imposición; cómo interactúa el lote con las transferencias entre puntos de venta
y con las notas de crédito; qué relación tiene con `movimientos_stock`, que hoy es un ledger
completo sin dimensión de lote.

### Etapa 13 — Stock inteligente: mínimos, alertas y reposición

**Alcance.** Stock mínimo y punto de pedido por artículo **y** punto de venta, alertas de bajo
stock, lista de reposición por proveedor (descargable con la infraestructura de la Etapa 11) y
sugerencia de compra a partir de mínimos, rotación y stock en tránsito.

La lista de reposición es una **idea tomada del legacy, no una paridad**:
`alsina/imprimirArticulos.php` abre su propia conexión hardcodeada
(`mysqli_connect('127.0.0.1','root','','ways')`) en vez de usar `conexion.php`, por lo que
casi seguro nunca funcionó en el hosting de producción (doc 01:170). Se aprovecha el concepto
—qué reponer, agrupado por proveedor— y se diseña desde cero; no hay comportamiento observado
que replicar.

**Por qué acá.** Es la contraparte de la Etapa 12: una controla lo que se vence, la otra lo
que falta. Va después porque las alertas de vencimiento y las de faltante conviene diseñarlas
sobre un mismo mecanismo de notificación, y ese mecanismo lo estrena la 12.

**Dependencias.** Etapa 11 (lista descargable) y Etapa 12 para el canal de alertas
compartido. La sugerencia de compra se apoya en la rotación, que `movimientos_stock` ya
permite calcular. **Tamaño:** media.

**Decisiones abiertas.** Mínimo fijo por artículo o calculado por rotación; si la alerta es
pull (pantalla) o push (correo/notificación); si la sugerencia de compra genera directamente
una orden de compra cuando exista la Etapa 16, o queda como listado.

### Etapa 14 — Auditoría y trazabilidad

**Alcance.** Registro de quién, cuándo y qué en las operaciones sensibles: cambios de precio,
anulaciones de comprobante, ajustes de stock, cambios de rol y permisos, reliquidaciones de
cuenta corriente. Consulta filtrable y exportable.

**Por qué acá.** Comparte la lógica de la Etapa 9 — **lo que no se audita antes de esta etapa
no es reconstruible después** — pero se prioriza detrás de las etapas de medición por valor
de negocio: la Etapa 9 pierde el margen de *toda* venta emitida, mientras que acá se pierde
el rastro de un conjunto mucho más chico de operaciones excepcionales. La honestidad del
argumento importa: cuanto antes se haga, menos historia ciega queda.

**Dependencias.** Etapa 11 para la exportación del registro. **Tamaño:** media —
mecánicamente simple, pero toca muchos puntos del código.

**Decisiones abiertas.** Tabla única de auditoría genérica versus registro por dominio;
cuánto detalle se guarda (valor anterior y nuevo, o solo el hecho); política de retención y
tamaño de la tabla; si la auditoría se escribe en la misma transacción que la operación
(consistente pero acoplada) o de forma diferida; qué operaciones entran en la primera pasada.

### Etapa 15 — Cuenta corriente de proveedores (ledger)

**Alcance.** Promover el saldo derivado actual (`Σ compras confirmadas − Σ gastos ligados`,
sin tabla, deliberadamente simple según el doc 10) a un ledger propio con movimientos
inmutables, espejo de la cuenta corriente de clientes de la Etapa 7. Pagos parciales,
imputación a comprobantes y historial consultable.

**Por qué acá.** El patrón ya existe y está probado: la Etapa 7 dejó un ledger inmutable con
reliquidación funcionando. Esto es aplicar el mismo diseño del otro lado del mostrador, con
riesgo bajo. Se hace después de las etapas de medición porque el saldo derivado actual
alcanza para operar, aunque no para pagar parcialmente ni reconstruir historia.

**Dependencias.** Ninguna dura; la Etapa 7 es el precedente de diseño a copiar.
**Tamaño:** media.

**Decisiones abiertas.** Cómo se migra el saldo derivado actual al ledger (asiento de apertura
por proveedor versus reconstrucción desde el historial de compras); si el gasto ligado sigue
siendo el mecanismo de pago o se reemplaza por un movimiento de pago propio; retenciones y
notas de crédito de proveedor.

### Etapa 16 — Órdenes de compra

**Alcance.** El circuito completo de compra: orden de compra → recepción (total o parcial) →
conversión a comprobante de compra. Estados `borrador / enviada / recibida parcial / cerrada /
anulada`. Hoy el circuito arranca en "ya compré": el comprobante de compra registra un hecho
consumado.

**Por qué acá.** Es una ampliación del flujo, no un arreglo. Necesita que la parte registral
esté sólida (Etapa 8, hecha) y gana mucho si la sugerencia de compra de la Etapa 13 puede
alimentarla, pero no la bloquea.

**Dependencias.** Independiente de 15 y 17. Se integra con 13 si esta ya existe.
**Tamaño:** media a grande.

**Decisiones abiertas.** Si la recepción parcial genera varios comprobantes de compra o uno
solo al cerrar; qué pasa con las diferencias de precio entre la OC y la factura recibida; si
la recepción mueve stock por sí misma o solo lo hace la confirmación del comprobante; cómo
interactúa con los lotes de la Etapa 12 si esta ya está activa.

### Etapa 17 — Presupuestos y remitos

**Alcance.** El lado venta del ciclo documental. **Presupuesto:** no mueve stock ni caja, tiene
vencimiento, es convertible en venta y conserva el precio ofrecido. **Remito:** entrega sin
facturar, el stock sale al remitir, y la facturación posterior consolida uno o varios remitos
en un comprobante.

**Por qué acá.** Decisión 4. Es la etapa que convierte el POS en un sistema de ventas
completo, más allá del mostrador. Va después de las etapas de medición y control porque no
resuelve un problema actual sino que habilita una forma de vender que hoy no existe.

**Dependencias.** Independiente de 15 y 16. Reutiliza el motor de precios, ofertas y snapshot
de la Etapa 5. **Tamaño:** grande.

**Decisiones abiertas.** Si el presupuesto reserva stock o no; si respeta el precio ofrecido o
reprecia al convertir (la reliquidación de la Etapa 7 es el precedente conceptual); numeración
propia o compartida con los tipos de comprobante existentes; qué comprobante fiscal
corresponde a la facturación consolidada de remitos cuando llegue la Etapa 19.

### Etapa 18 — Etiquetas, carteles y consulta de precios

**Alcance.** Etiquetas de góndola y carteles de precio imprimibles (formatos configurables,
selección por artículo, categoría, marca u oferta activa) y una vista o app de consulta de
precios para el salón, pensada para lectura de código de barras desde un dispositivo del
local.

**Por qué acá.** Es operación de piso: mejora el día a día pero no habilita nada. Se hace
cuando la infraestructura de impresión ya está resuelta y no hay que decidirla para esto.
Ambos ítems vienen del roadmap del doc 06.

**Dependencias.** Etapa 11 (infraestructura de impresión y descarga). **Tamaño:** media.

**Decisiones abiertas.** Qué formatos de etiqueta se soportan y si son configurables por
empresa; si la consulta de precios es una vista responsive del sistema o una superficie
separada con autenticación propia; qué precio muestra cuando hay ofertas y listas
diferenciadas.

### Etapa 19 — Facturación electrónica ARCA

**Alcance.** La etapa más grande del programa. Bloques:

1. **Completar el schema fiscal.** `empresas.id_condicion_fiscal` (el doc 10 lo pide, la
   entidad no lo tiene), `puntos_venta.numero` como número fiscal real (el `PPPP` actual es el
   id interno — flagged en el archive de la Etapa 5) y campos de resultado en el comprobante:
   CAE, vencimiento de CAE, resultado, observaciones.
2. **Activar lo que ya está dormido.** `ResolvedorDeLetraComprobante` está completo y testeado
   pero sin usar, y `ServicioDeVentas` bloquea explícitamente los tipos fiscales (hoy solo
   emite TX/NCX). Hay que abrir el camino `es_fiscal` en la emisión.
3. **Cálculo fiscal real.** Neto gravado e IVA total por alícuota a partir del snapshot de IVA
   que la línea ya guarda.
4. **Cliente WSAA + WSFE** con certificado y clave privada **por empresa** — cada empresa del
   tenant tiene su propio CUIT y su propio circuito ARCA, con lo que la gestión y el
   almacenamiento seguro de credenciales es parte del alcance, no un detalle.
5. **Contingencia CAEA** para operar cuando el servicio de ARCA no responde.
6. **Impresión del comprobante** con QR ARCA, y la decisión de fondo: PDF propio versus
   controlador fiscal.
7. **Homologación** contra el entorno de pruebas antes de producción.

**Por qué va última.** Decisión 1: sin apuro. El legacy nunca facturó fiscalmente —verificado
exhaustivamente: `ticket.php` es un `window.print()`— así que no hay paridad que recuperar ni
operación que se degrade por esperar. Cuando el negocio necesite factura fiscal, esta etapa
arranca con la base ya preparada: los padrones (`condiciones_fiscales`, `alicuotas_iva`,
`tipos_comprobante` con `es_fiscal`, `letra`, `discrimina_iva`, `codigo_afip`), el snapshot de
IVA por línea y el resolvedor de letra ya existen.

**Dependencias.** Ninguna técnica: podría hacerse antes. Va última por decisión de producto.
Si la Etapa 17 ya está hecha, hay que resolver la facturación fiscal consolidada de remitos.
**Tamaño:** grande — la mayor del programa, candidata natural a dividirse en sub-etapas
durante su propio proposal.

**Decisiones abiertas.** Todas las del bloque de impresión; si se soporta también factura de
crédito electrónica; qué se hace con el histórico no fiscal al activar el modo fiscal; si la
homologación se hace por empresa o una sola vez a nivel plataforma.

---

## Grafo de dependencias

```
9 ──► 10 ──► 11 ──┬──► 12 ──► 13
                  ├──► 14
                  └──► 18

15   (independiente)
16   (independiente; se integra con 13 si existe)
17   (independiente)
19   (independiente; última por decisión de producto)
```

Lectura del grafo:

- **9 → 10**: el margen del dashboard necesita el costo congelado. El resto de los agregados
  de la 10 no depende de la 9.
- **10 → 11**: la infraestructura de exportación se decide cuando ya hay agregados que
  exportar.
- **11 → 12, 13, 18**: dependencia parcial, solo por lo imprimible y descargable (planillas de
  vencimiento, lista de reposición, etiquetas y carteles).
- **11 → 14**: solo para exportar el registro de auditoría.
- **12 → 13**: las alertas de vencimiento y las de bajo stock comparten mecanismo de
  notificación; la 12 lo estrena.
- **15, 16, 17**: independientes entre sí y del resto. Pueden reordenarse según necesidad del
  negocio sin romper nada.
- **19**: sin dependencias técnicas duras. Su posición al final es decisión de producto
  (decisión 1), no una restricción del grafo.

---

## Backlog absorbido

Ítems heredados que este programa se hace cargo de cerrar, para que no queden flotando en
notas de etapas ya archivadas.

### Del programa de paridad (etapas 1-8)

| Ítem | Etapa que lo absorbe |
|---|---|
| Las 4 sugerencias de cobertura del verify de la Etapa 8 | Se resuelven dentro de la etapa que toque el código correspondiente |
| Recargo por medio de pago (la columna existe, la lógica está dormida) | Etapa 10 lo expone en los agregados; su activación es un cambio menor previo |
| Conteo de inventario completo (la Etapa 8 entregó una versión mínima, sin workflow de snapshot/variance) | Etapa 13 |
| Cuenta corriente de proveedores con ledger propio | Etapa 15 |
| Órdenes de compra | Etapa 16 |
| Libro IVA compras | Etapa 19 |
| `puntos_venta.numero` real (el `PPPP` actual es el id interno) | Etapa 19 |
| `empresas.id_condicion_fiscal` (pedido por el doc 10, ausente en la entidad) | Etapa 19 |

### Del roadmap del doc 06 ("Después del cutover")

| Ítem del doc 06 | Estado |
|---|---|
| Compras con detalle y actualización de costos masiva | Hecho en la Etapa 8 (la actualización masiva de costos queda como mejora dentro de la 13) |
| Stock transferido entre locales | Hecho en la Etapa 8 |
| Reportes de rentabilidad por artículo / marca / proveedor | Etapas 9 + 10 |
| Facturación electrónica AFIP/ARCA | Etapa 19 |
| App de consulta de precios para el salón | Etapa 18 |
| Etiquetas y carteles de góndola | Etapa 18 |
| Multi-empresa (locales como tenants) | Hecho — doc 09, implementado desde la Etapa 1 |
| Notificaciones de bajo stock | Etapa 13 |
| Ver Cajas + detalle (G2) | Etapa 11 |
| Caja General / Z (G3) | Etapa 11 |
| Dashboard de 7 días (G1) | Etapa 10 (lo cubre y lo supera) |
| Caja Virtual con los 4 canales (G4) | **Excluido** — decisión 3 |

### Deuda técnica suelta (sin etapa asignada)

Dos ítems heredados que no pertenecen a ninguna etapa de este programa. Se corrigen como fix
aislado cuando algún trabajo toque ese código, sin esperar a que llegue una etapa.

- **Concurrencia del replace-set de `articulos_empresas`.** Problema de escritura concurrente
  en un endpoint existente; es un arreglo puntual, no una feature.
- **Detalle JSON en PascalCase.** Inconsistencia de serialización con el resto de los
  contratos; se normaliza al primer cambio que toque ese payload.

---

## Proceso

Cada etapa de este programa se ejecuta con el mismo flujo que las ocho anteriores:

1. **SDD completo**: `proposal → specs / design → tasks → apply → verify → archive`. Las
   decisiones abiertas listadas en cada etapa son el material de entrada de su proposal.
2. **Apply por slices**, cada slice con su ronda de **judgment-day** (review dual ciego) hasta
   una ronda limpia antes de abrir el PR.
3. **PRs encadenados stacked-to-main**, respetando el presupuesto de líneas por PR.
4. **Gate de base de datos obligatorio**: antes de cualquier migración se presenta el modelo
   propuesto —tablas, columnas clave, constraints, categoría de scoping— y se espera
   aprobación explícita. Ninguna etapa genera ni aplica una migración sin ese paso, incluidos
   los sub-agentes delegados.
5. **Toda etapa cierra con tests**: unitarios como mínimo, integración en los endpoints y e2e
   en los flujos críticos cuando sea viable.

Este documento no reemplaza al [doc 10](10-modelo-de-datos.md): las tablas nuevas que
introduzca cada etapa se documentan allá, en la sección que les corresponda, cuando se
implementen.
