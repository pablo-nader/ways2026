> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 9. Dispositivos de escritorio (stage-desktop-pos)

### 9.3 Venta offline: instantánea de catálogo y precios congelados (stage-pos-venta-offline-backend)

Con el bloque de numeración (§9.2) el dispositivo puede identificar una venta offline; le
faltaba poder VENDER sin red: saber qué artículos existen, a qué precio (ya resuelto contra
ofertas), con qué IVA, y contra qué medios de pago — y, al sincronizar, que el servidor no le
recalculara el precio a espaldas del ticket que el cliente ya tiene en la mano. Decisión del
dueño (no una decisión de arquitectura libre): **la venta offline carga sus precios** — inversión
deliberada de la decisión 3 original de `SolicitudDeVenta` (§9.1/`Contratos.cs`: "sin ningún
campo de dinero"), que sigue exactamente igual para el camino online/web.

**`GET /api/pos/instantanea` — sin cambio de esquema, solo lectura.** Device-only (`Politicas.
RequiereDispositivo` + `PoliticaDeModoDePuntoVenta`, misma regla que el checkout), siempre para
el punto de venta PROPIO del dispositivo — sin parámetro `idPuntoVenta`, se deriva de
`dispositivos.id_punto_venta`. Contenido, derivado de lo que el checkout online REALMENTE lee
(`Pos.tsx`/`ServicioDeEscaneo`/`ServicioDeOfertas.ResolverAsync`/`ServicioDeVentas.
EmitirAsync`/`ServicioDePrecios`), nunca "por si acaso":

- Por artículo activo (`ArticuloDeInstantanea`): identidad (`idArticulo`, `codigoInterno`,
  `nombre`), todos sus `codigosBarra`, el precio YA RESUELTO por `ServicioDeOfertas.ResolverAsync`
  a `cantidad = 1` contra la lista de precio del Consumidor Final del tenant y la empresa del
  punto de venta (`precioOriginal`/`precioFinal`/`descuentoUnitario`/`aplicadas`, mismo shape que
  `ResultadoDeResolucion`), más `idAlicuotaIva`/`porcentajeIva` — el cliente nunca los había visto
  antes de esta etapa, y los necesita para imprimir el ticket. Un artículo SIN precio vigente hoy
  se omite (mismo motivo que el 400 `articulo_sin_precio_vigente` online: ofrecerlo sin poder
  cobrarlo no tiene sentido). `idArea`/`idListaPrecio`/`idOferta`/`esProducto`/`controlaLote`
  quedan afuera a propósito: el servidor los resuelve FRESCOS al sincronizar
  (`ServicioDeVentas.EmitirAsync` ya los lee de `articulos`/`alicuotas_iva` en el momento del
  emit, nunca del request), y el default FEFO ya cubre lotes sin que el dispositivo necesite
  saber nada de ellos.
- Medios de pago activos (recorte de flags: nombre, comportamiento, admite vuelto, requiere
  referencia) y `tolerancia_pago` resuelta con la MISMA precedencia (punto de venta > empresa >
  default) que el checkout.
- Venta con envío sin red (formato `?version=2`, ausentes en una instantánea guardada antes): `importeEnvio`, el parámetro
  `importe_envio` resuelto con la misma precedencia (punto de venta > empresa > default 0); `envioPorDefecto`, el flag
  `puntos_venta.envio_por_defecto`; y en cada cliente `domicilio`, `telefono` y `celular` (recortados, y la clave se omite si
  queda vacío) para la regla "cliente con domicilio" y el teléfono del ticket de entrega. Una instantánea sin `importeEnvio`
  no permite vender con envío sin red.
- `momento`: el instante en que el servidor resolvió TODO lo de arriba — el dispositivo lo
  muestra para que el operador sepa cuán vieja es su última instantánea.

**Sin paginar, sin refresco incremental — decisión explícita, no un olvido.** Paginar (aun
reusando el tope de `ServicioDeArticulos.TamanioMaximoDePagina` de la grilla interactiva)
exigiría N requests secuenciales para un catálogo de referencia de ~6000 artículos (legacy); un
precio que cambia entre la página 3 y la 4 volvería la instantánea INCONSISTENTE contra sí
misma — exactamente lo que este endpoint existe para evitar (el único invariante real de la
respuesta es que TODO se resolvió contra el mismo `momento`). Con los ~10 campos mínimos por
artículo, el JSON completo de 6000 artículos pesa un puñado de cientos de KB — un solo response,
aceptable para un pull en background ocasional sobre Wi-Fi/LAN de local. Tampoco hay
`If-Modified-Since`/ETag: no existe change-tracking entre `articulos`/`precios`/`ofertas`/
`codigos_barra` como para construir un delta correcto sin infraestructura nueva, y el propio
diseño de esta etapa encuadra la vejez como "acotada por el intervalo de refresco" (el
dispositivo vuelve a pedir la instantánea COMPLETA cada vez que tiene señal), no por el tamaño de
un delta — agregar eso ahora sería optimización especulativa sin un problema de tamaño medido.

**`POST /api/ventas` — precio de línea, solo junto a `NumeroPreasignado`.**
`LineaDeVenta.PrecioUnitario`/`DescuentoUnitario` (opcionales, `null` en el 100% del tráfico
online/web) son el precio que el DISPOSITIVO ya cobró offline, tomado de su última instantánea —
nunca lo que `ServicioDeOfertas.ResolverAsync` resolvería HOY al sincronizar. Reglas
(`ServicioDeVentas.ExigirPreciosOfflineValidos`):

- Con algún precio de línea presente y sin `NumeroPreasignado`: `400
  precio_offline_no_admitido` — el camino web/online sigue siendo la ÚNICA autoridad de precio,
  sin excepción.
- Todas las líneas con precio, o ninguna: `400 precio_offline_incompleto` en cualquier mezcla —
  no hay un tercer estado "algunas sí, algunas no", que dejaría media venta al precio de hoy y
  media al precio congelado sin ningún significado económico.
- `descuentoUnitario` sin `precioUnitario` tampoco tiene destino: mismo código
  (`dto-contract-honesty`).
- Precio o descuento negativo: `400 precio_offline_invalido`.

**El servidor RECORDS los precios recibidos** (`ServicioDeVentas.MaterializarItems`): con precio
offline, `precio_unitario`/`descuento`/`total` de `items_comprobante_venta` salen de la línea, no
de `ResolverAsync` — y por construcción, `ValidadorDePagos` (que sigue corriendo, sin excepción)
valida los pagos contra ESE total, nunca contra el que el servidor hubiera resuelto hoy. Sin
precio vigente hoy (el artículo pudo perder su precio entre el cobro offline y el sync), la venta
sincroniza igual — nunca el 400 `articulo_sin_precio_vigente` del camino online, porque el ticket
ya se le dio al cliente.

**Discrepancia visible, nunca silenciosa — auditoría, NO una columna nueva (DB CHANGE GATE
evitado a propósito).** El servidor sigue corriendo `ServicioDeOfertas.ResolverAsync` (como
siempre) y compara, por línea, lo que HUBIERA cobrado contra lo que el dispositivo cobró
(`NetoDeLinea`, mismo redondeo que `CalculadorDeTotales`, nunca una segunda autoridad de dinero:
la comparación es puramente diagnóstica, nunca alimenta `totales`/`ValidadorDePagos`/lo
persistido). Si difieren, dos señales, ninguna bloqueante (mismo criterio "warning, nunca
bloqueo" que `ItemComprobanteVenta.LoteVencido`):

1. `ItemEmitido.PrecioDiscrepante` (`true`/`false`) en la respuesta del emit — visible solo en el
   checkout fresco que detectó la discrepancia, como `LoteVencido`; una relectura/reprint no lo
   recalcula (siempre `false` ahí). Desde los combos también es `true` cuando SOLO difiere el combo: el dispositivo no
   detectó uno que el servidor sí (una versión vieja del POS o una instantánea vieja) o trajo una parte de combo distinta
   de la que el servidor calcula. Lo cobrado se graba igual (ver "Combos en el POS offline").
2. Una fila en `auditoria` (`AccionAuditada.VentaDiscrepancia = ("venta.discrepancia",
   "comprobante_venta")`, catálogo de 18 entradas) con el número/id del comprobante y, por línea
   discrepante, artículo/cantidad/precio y descuento cobrados vs. esperados — durable, consultable
   vía `GET /api/auditoria`. Se escribe UNA fila por comprobante (no una por línea), solo cuando
   hay al menos una línea discrepante.

**El ajuste manual de precio no es una discrepancia.** Una línea offline también puede traer
`ajusteManualPorcentaje` (§4, descuento o recargo que el cajero aplicó a mano sobre el neto de la
línea). El servidor lo valida (`400 ajuste_manual_invalido`), calcula el monto con
`CalculadorDeTotales` —nunca toma un importe del dispositivo— y la discrepancia se decide sobre el
neto ANTERIOR al ajuste, en las dos partes: `NetoDeLinea` (bruto − descuento con el precio y la
oferta que el servidor resuelve hoy) contra el total cobrado menos el monto del ajuste (bruto −
descuento con los valores del dispositivo). El porcentaje es el mismo dato de request de los dos
lados, así que no participa de la comparación: un ajuste manual por sí solo no marca
`PrecioDiscrepante` ni escribe la fila de auditoría, y una diferencia real de precio sigue
marcándose aunque haya ajuste, incluso con −100 % (total cobrado 0) o con un centavo de diferencia
que el redondeo del ajuste absorbería en el total. Cuando la fila de auditoría existe, lleva el
porcentaje y el monto del ajuste junto al total cobrado.

Se descartó agregar `precio_unitario_esperado`/`descuento_esperado`/un `precio_discrepante
GENERATED` a `items_comprobante_venta` (el patrón de `ArqueoTurno.Diferencia`, que hubiera sido la
alternativa más consultable) porque el DB CHANGE GATE de este proyecto exige presentar el modelo
y esperar aprobación explícita antes de aplicar cualquier migración — no pre-otorgada para un
modelo que el dueño todavía no vio. Auditoría cierra el requisito ("hacer visible, nunca
silenciosa") sin ese riesgo; si el volumen de discrepancias justifica más adelante un reporte
dedicado, ESE es el momento de proponer las columnas y pedir la aprobación.

**Combos (feature combos).** La instantánea trae `combos[]` y la línea offline puede traer las unidades
consumidas por un combo, la parte del combo y el combo; el servidor los graba tal cual y audita la diferencia contra su
propia detección. Contrato y decisiones en "Combos en el POS offline" (sección Combos).

**Ofertas "solo múltiplos" offline (feature ofertas-solo-multiplos, T2).** Una oferta "solo múltiplos" (§ ofertas,
`ofertas.solo_multiplos`) no tiene un precio unitario único: cobra la oferta en `N × floor(u / N)` unidades y el resto a
lista. La instantánea la representa sin cambiar el significado de los campos que leen los POS ya instalados:

- Cada precio plano, escalón y celda de familia en el que aplica una oferta "solo múltiplos" lleva `aplicadasCompletas`: la
  resolución entera del servidor, en su orden, con `multiploDe` (y `porFamilia`) en las ofertas estrictas. En ese caso
  `precioFinal`/`descuentoUnitario`/`aplicadas` llevan SOLO las ofertas comunes (con el tope del motor:
  `PrecioParaInstantanea.Separar`). Sin ninguna oferta estricta la clave falta y el JSON es byte por byte el anterior, así
  que la etiqueta de contenido de un tenant sin estas ofertas no cambia. El formato original (sin `?version=2`) usa los
  mismos campos de siempre. Sin cambio de versión: la clave es aditiva y opcional.
- **POS anterior (decisión explícita).** Un POS que no conoce `aplicadasCompletas` cobra las unidades de la oferta estricta
  a precio de lista: nunca menos de lo que cobra el servidor (el cliente paga de más en las unidades que completan un
  grupo hasta que la caja se actualice), y la auditoría de discrepancias lo registra. Se descartó plegar el beneficio en
  los campos de siempre: un POS anterior lo cobraría en todas las unidades, también en el resto, y cobraría de menos.
- El dispositivo no evalúa ofertas: elige el escalón o la celda como siempre, rearma la resolución entera con
  `aplicadasCompletas` y calcula el descuento de oferta de la línea con el puerto de `CoberturaDeOfertas`
  (`pos/coberturaDeOfertas.ts`, tabla de paridad con `CoberturaDeOfertasTests`): sobre las unidades que no consumió un combo,
  y con los grupos de una oferta de familia contados sobre todas las líneas del documento que la llevan, con el resto en las
  últimas. La detección de combos usa el descuento unitario de la resolución entera, como el servidor.
- `LineaDeVenta.DescuentoDeOfertaDeLinea` es ese descuento como importe de línea: va en lugar de `descuentoUnitario`
  (los dos juntos, en una línea por importe, negativo o mayor que el bruto de las unidades fuera de combo ⇒ 400
  `precio_offline_invalido`; sin precio offline ⇒ 400 `precio_offline_incompleto`). El servidor lo graba tal cual
  (`LineaParaCalcular.DescuentoDeOfertaDeLinea`, un solo redondeo al peso del neto) y la discrepancia compara contra el
  descuento de línea que resuelve hoy para las mismas unidades (cantidad menos las que consumió el combo que detecta el
  servidor). El ticket sintético y el payload salen de la misma resolución (`resolverDocumentoOffline`). La vista previa del carrito
  recibe `descuentoDeLinea` en esa misma resolución, pero `previaDeLinea` todavía calcula con el descuento unitario
  (pendiente, igual que en la vista previa online).

**Venta con envío offline (`SolicitudDeVenta.ImporteEnvio`).** Con `NumeroPreasignado` y `ConEnvio`, el dispositivo manda
el importe del envío que cobró (el de la instantánea, `importeEnvio`): el servidor lo graba tal cual en la línea "Envío" en vez
de leer `importe_envio` (con 0 no hay línea). Es otro caso del precio que ya cobró la caja: sin `NumeroPreasignado` o sin
`ConEnvio` se rechaza (400 `importe_envio_no_admitido`), y uno negativo, con más de 2 decimales o fuera de `numeric(14,2)` es
400 `importe_envio_invalido`. Sin el campo (un dispositivo anterior) sigue el parámetro. El reenvío de una venta ya emitida
devuelve su comprobante aunque el domicilio del cliente se haya borrado después: la búsqueda por número corre antes de la
validación de cliente con domicilio. Ver "Venta con envío" en §4.

**El camino online/web queda byte-idéntico.** Ningún actor sin `NumeroPreasignado` puede mandar
precio de línea (guard arriba); sin precio de línea, `MaterializarItems` sigue exactamente la
rama de siempre (`resultado.PrecioOriginal`/`DescuentoUnitario` de `ResolverAsync`), cero queries
nuevas, cero columnas nuevas escritas.

**Estado (stage-pos-venta-offline-backend): backend implementado — Parte A (instantánea) y Parte
B (precio offline + discrepancia auditada). Sin migración: ningún cambio de esquema en esta
etapa. Pendiente: el consumo desde el shell Tauri/React del dispositivo (outbox local, reintento
de sync, UI de discrepancia) — etapa separada.
