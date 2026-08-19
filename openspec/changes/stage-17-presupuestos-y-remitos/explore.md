# Explore — Stage 17: Presupuestos y remitos

Fecha: 2026-08-19. Fase ejecutada por sdd-explore (sonnet) bajo mandato autónomo; contenido
persistido verbatim por el orquestador (el agente de fase no tenía Write en su toolset).

## 1. Qué dice el doc 11 textual de la 17 y qué tenía el legacy

**Doc 11, Etapa 17** (`docs/11-programa-post-paridad.md:307-324`), cita textual completa:

> **Alcance.** El lado venta del ciclo documental. **Presupuesto:** no mueve stock ni caja, tiene vencimiento, es convertible en venta y conserva el precio ofrecido. **Remito:** entrega sin facturar, el stock sale al remitir, y la facturación posterior consolida uno o varios remitos en un comprobante.
>
> **Por qué acá.** Decisión 4. Es la etapa que convierte el POS en un sistema de ventas completo, más allá del mostrador. Va después de las etapas de medición y control porque no resuelve un problema actual sino que habilita una forma de vender que hoy no existe.
>
> **Dependencias.** Independiente de 15 y 16. Reutiliza el motor de precios, ofertas y snapshot de la Etapa 5. **Tamaño:** grande.
>
> **Decisiones abiertas.** Si el presupuesto reserva stock o no; si respeta el precio ofrecido o reprecia al convertir (la reliquidación de la Etapa 7 es el precedente conceptual); numeración propia o compartida con los tipos de comprobante existentes; qué comprobante fiscal corresponde a la facturación consolidada de remitos cuando llegue la Etapa 19.

Y en la Etapa 19 (`docs/11-programa-post-paridad.md:373`), como dependencia inversa registrada:

> Si la Etapa 17 ya está hecha, hay que resolver la facturación fiscal consolidada de remitos.

**Legacy alsina: cero paridad, confirmado por búsqueda exhaustiva.** `presupuesto|remito` sobre `docs/01-features-existentes.md` → 0 coincidencias; sobre todo `alsina/` → 0 coincidencias. Es **greenfield puro**, mismo perfil que la Etapa 16 (su spec: *"Greenfield: the legacy never had an order document... every rule here is a decision, not a port"*, `openspec/specs/ordenes-de-compra/spec.md:12`). Lo único adyacente en el legacy es el modal F3 de dirección de entrega, que ya vive como `comprobantes_venta.direccion_entrega` (etapa 5).

## 2. Mapa del circuito de venta actual — y el hallazgo del tipo `PRE`

### 2.1 El checkout intocable

`ServicioDeVentas.EmitirAsync` (`src/Ways.Application/Ventas/ServicioDeVentas.cs:50-300`) es "decide, then commit" (doc-comment :23-45): todo lo que decide corre FUERA de la transacción armando un `PlanDeVenta` inmutable. La transacción (`EjecutarTransaccionAsync`, :762+) escribe en orden pineado: (0) turno FOR SHARE :773; (1) numeración ya comprometida en su PROPIA transacción previa vía `AsignadorDeNumeroComprobante.AsignarComprometidoAsync` :278-280; (2) comprobante :781-801; (3+4) items y pagos :803-850; (5) stock en orden ascendente `(id_articulo, id_lote NULLS FIRST)`, se salta ENTERO si `EsProducto=false` :855-885; (6) cuenta corriente :887-914. Cada fila mutable con un único statement atómico con su propio row lock.

### 2.2 El hallazgo crítico: `PRE` ya está sembrado y hoy no está realmente bloqueado

`docs/10-modelo-de-datos.md:80-92` define `tipos_comprobante` con `afecta_stock boolean -- presupuesto: no`, y el seed `src/Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs:79` siembra:
```csharp
(ClaseComprobante.Venta, "PRE", "Presupuesto", null, 1, false, false, false),
```
(`Activo=true, Clase=Venta, EsFiscal=false, AfectaStock=false`).

El gate real, `ServicioDeVentas.ResolverTipoComprobanteAsync` (`ServicioDeVentas.cs:923-937`), evalúa solo `!Activo`, `Clase != Venta`, `EsFiscal` → **`PRE` pasa las tres**. No hay whitelist adicional en DTO ni endpoint (`VentasEndpoints.cs:23`). Y `AfectaStock` **no se lee en ningún punto de Ways.Application** fuera de proyecciones de catálogo (grep exhaustivo: un comentario en `ServicioDeCuentaCorriente.cs:287` y dos proyecciones de lectura en Catalogos/). El precedente `RC` (también `AfectaStock=false`) logra "no toca stock" ESTRUCTURALMENTE: no pasa por `EmitirAsync` — tiene su propio servicio que jamás agrega items, y el loop de stock itera `plan.Items.Where(i => i.EsProducto)`.

**Consecuencia verificada:** un `POST /api/ventas` con `codigoTipoComprobante = "PRE"` y líneas de producto reales HOY pasaría el resolver y `EjecutarTransaccionAsync` decrementaría stock y consumiría CC exactamente como un TX — contradiciendo el comentario del seed y el doc 11. Riesgo latente real en main, no hipotético.

## 3. Modelo tentativo para el gate

### 3.1 Precedente OC (Etapa 16, recién archivada)

Documento de intención con ciclo de vida propio (5 estados), numeración con serie propia vía el mecanismo genérico (`numeraciones_comprobante.tipo_comprobante` es `varchar(30)` LIBRE sin FK — el doc-comment de `AsignadorDeNumeroComprobante.cs:41-44` lo dice explícito; TX/NCX, RC y 'OC' ya lo reusan; una serie `'PRES'` o `'REM'` cuesta cero schema), FK opcional hacia el hecho real (1 OC → N comprobantes), `pendiente` siempre derivado, estado = proyección + decisiones humanas, EntidadBase SÍ (documento mutable).

### 3.2 Dos opciones reales para presupuesto

**Opción A — tabla propia (`presupuestos`/`items_presupuesto`), espejo de OC.**
Pros: preserva `EmitirAsync` byte-idéntico sin gate nuevo — el bug del punto 2 desaparece POR CONSTRUCCIÓN; vencimiento y estados propios (`borrador|enviado|vencido|convertido|anulado`) sin forzar `estado_comprobante` (binario `emitido|anulado`, doc 10 §4); serie `'PRES'` gratis.
Contras: el `PRE` sembrado queda huérfano (decisión de qué hacer con él); segunda materialización de items con snapshot de precio/IVA (o extracción compartida — decisión de design).

**Opción B — `comprobante_venta` de tipo `PRE`.**
Pros: cero tabla nueva; reusa el snapshot de items.
Contras: `comprobantes_venta` no tiene vencimiento ni estado "convertido" — requiere ALTER de la tabla más caliente del sistema con columnas de un tipo entre once; y sobre todo requiere que `EmitirAsync` sepa NO mover stock/CC para PRE — tocar la lambda intocable, violando el criterio cero-statements-extra que la 16 defendió como vinculante.

**Lectura del explore:** el precedente OC + el hallazgo del punto 2 empujan con fuerza hacia la **Opción A**.

### 3.3 Scoping

Ambos documentos: **operativa** (`id_tenant` + `id_punto_venta`, doc-09:19-21) — igual que ordenes_compra/comprobantes_venta/comprobantes_compra/movimientos_stock. Doble capa: filtro EF + RLS.

### 3.4 Índices — no contados en el explore

Tarea del proposal, una vez elegido el shape (lección de la enmienda 1 de la 14: contarlos desde el arranque EN EL PROPOSAL).

## 4. El remito y el stock

### 4.1 La garantía de "tres write sites"

`openspec/specs/stock/spec.md:178-189`, Requirement "Lock Order Extends To The Lot Dimension, Identical At All Three Write Sites":

> Every transaction that touches stock MUST build one total ascending order over the keys it will lock, in the exact form `ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST` [...] This rule MUST be implemented identically and independently at all three write sites (`ServicioDeVentas`, `ServicioDeCompras`, `ServicioDeStock`), each with its own concurrency test — **the duplication is not refactored away**.

La Etapa 16, pudiendo agregar un cuarto write site, lo evitó deliberadamente (la recepción ES un comprobante — el motor existente mueve el stock).

### 4.2 Opciones para remito (doc 11: "el stock sale al remitir")

**(a) Tabla propia `remitos`/`items_remito` con motor de stock propio** — un CUARTO write site: rompe/extiende literalmente el enunciado "all three" y exige enmendar esa garantía explícitamente en el spec (cuarto sitio con su propio test de concurrencia y la misma duplicación intencional del lock order).
**(b) `comprobante_venta` de tipo nuevo `REM`** (hoy NO existe en el seed) con un servicio hermano estilo RC que reuse el patrón — pero el remito SÍ lleva items y SÍ mueve stock, así que no es el caso RC (cero items): terminaría duplicando el loop de stock igual, solo que dentro del territorio de comprobantes_venta, con las mismas incomodidades de estado binario del punto 3.2-B.
Motivo de stock: el enum `motivo_stock` tiene 8 valores, ninguno "remito" (`docs/10-modelo-de-datos.md:584-586,651-654`); agregar un noveno es `ALTER TYPE ... ADD VALUE` — aditivo puro, precedente etapa 12 (decomiso/reclasificacion), mecánicamente barato pero IRREVERSIBLE (criterio conocido del programa).

## 5. Integraciones

**Presupuesto→venta pre-carga el POS**: un `GET /api/presupuestos/{id}/para-venta` que devuelve el shape de `SolicitudDeVenta` con los precios congelados del presupuesto, y el POS lo manda tal cual al `POST /api/ventas` existente. **Cero cambio al checkout** — la integración vive del lado del presupuesto.
**Remito→factura**: consolidación N:1 (varios remitos → una factura), FK del lado "muchos": `remitos.id_comprobante_venta NULL`. El tipo fiscal de la consolidación queda diferido a la Etapa 19 (doc 11:373) — la 17 solo deja la relación lista.
**La conversión como FK** (precedente fresquísimo de la 16): `comprobantes_venta.id_presupuesto_origen NULL`; nunca copia desnormalizada.

## 6. Qué NO toca, riesgos, superficies, slices

**NO toca**: `EmitirAsync`/`EjecutarTransaccionAsync` byte-idénticos (el criterio vinculante de la 16 aplicado a ventas — con la única excepción quirúrgica que la OD2 de abajo autoriza en el RESOLVER, fase decide, jamás la transacción); `AsignadorDeNumeroComprobante` (tercera reutilización sin tocar); ledgers existentes; facturación fiscal consolidada (Etapa 19).

**Riesgos/decisiones para el proposal**: (1) el hallazgo del PRE — cerrar antes de construir; (2) reserva de stock sin soporte de modelo (ni enum ni columna — grep confirmado); (3) reprecio-al-convertir vs congelado (la reliquidación es el precedente conceptual pero es la pieza más compleja de la 7); (4) el cuarto write site; (5) `estado_comprobante` binario; (6) el PRE sembrado huérfano.

**Superficies**: API `POST/GET/PUT /api/presupuestos` + `/{id}/enviar` + `/{id}/para-venta` + `/{id}/anular`; `/api/remitos` + `/{id}/facturar`. Web: pantallas de ambos documentos + integración con el POS.

**Slices**: 6-8 tentativos — (1) schema+enums, (2) ABM+numeración presupuestos, (3) conversión→venta, (4) emisión de remito con stock, (5) consolidación remito→factura, (6-8) web. Tamaño "grande" del doc 11, comparable o mayor a los 6 PRs de la 16.

**Rutas citadas**: docs/11:39,268-324,365-373 · docs/10:80-92,318-360,581-654 · docs/09:19-21,126-137 · ServicioDeVentas.cs:23-45,50-300,762-937,1007+,1276 · AsignadorDeNumeroComprobante.cs:9-44 · ServicioDeCuentaCorriente.cs:275-320 · InicializadorDeBaseDeDatos.cs:68-88 · VentasEndpoints.cs:16-30 · specs de comprobantes-venta:1-30,405-416 · ordenes-de-compra:1-30,62-71,192-207 · stock:178-189.

## Orchestrator Decisions (mandato autónomo, 2026-08-19 — a formalizar por el proposal)

Ninguna está en la lista de pendientes reservados del dueño; el proposal las formaliza con
opciones/tradeoffs/costo de revertir o las refuta con evidencia:

1. **Presupuesto = TABLA PROPIA (opción A), espejo estructural de la OC de la 16**: el checkout
   queda byte-idéntico POR CONSTRUCCIÓN, estados propios con vencimiento, serie `'PRES'` vía el
   mecanismo genérico (cero schema). El proposal fija el enum exacto de estados.
2. **El hallazgo del PRE se cierra EN ESTA ETAPA como parte del gate, con DOS redes**: (a) el
   tipo `PRE` sembrado se DESACTIVA (`activo=false` vía data statement idempotente — es
   catálogo global sembrado, no se borra) porque jamás tuvo escritor y su existencia activa es
   una venta fantasma latente; (b) defensa en profundidad QUIRÚRGICA en
   `ResolverTipoComprobanteAsync` (la fase DECIDE, pre-transacción — no la lambda):
   `!tipo.AfectaStock` con items de producto = 400, para que ningún tipo futuro sin-efectos
   pueda colarse por el checkout. La lambda de transacción NO se toca (criterio
   cero-statements-extra); el test de mutación de ambas redes es obligatorio.
3. **El presupuesto NO reserva stock**: es cotización, no apartado. No hay soporte de modelo y
   agregarlo sería un write site nuevo sin caso de negocio confirmado — diferido con registro.
4. **Precio CONGELADO al convertir** (el doc 11 lo dice textual: "conserva el precio
   ofrecido"); el reprecio queda descartado — el mecanismo de gobierno es el VENCIMIENTO: un
   presupuesto vencido no es convertible (el precio viejo muere con él). La reliquidación de
   la 7 NO se replica.
5. **Remito = TABLA PROPIA con servicio propio (`ServicioDeRemitos`) como CUARTO write site
   FORMAL de stock**: la garantía de "tres write sites" del spec de stock se ENMIENDA
   explícitamente a cuatro — con el mismo contrato que hizo honesta a la regla: lock order
   idéntico e implementado independientemente, su propio test de concurrencia, y la
   duplicación intencional documentada. Reusar el loop de ventas acoplaría el servicio
   intocable (peor). Motivo de stock nuevo `remito` vía `ALTER TYPE ... ADD VALUE` (aditivo
   puro, precedente decomiso/reclasificacion de la 12; irreversible ACEPTADO con registro —
   el valor nace con escritor). La anulación de remito revierte con motivo `anulacion` (el
   precedente de ventas).
6. **Conversión por FK, sin tablas puente** (precedente 16): `comprobantes_venta.
   id_presupuesto_origen NULL` (1 presupuesto → a lo sumo 1 venta: el estado `convertido` lo
   garantiza) y `remitos.id_comprobante_venta NULL` (N remitos → 1 factura). La consolidación
   fiscal es de la 19; la 17 deja la FK lista.
7. **La conversión pre-carga el POS del lado del presupuesto** (`/{id}/para-venta` con el
   shape de SolicitudDeVenta y precios congelados) — cero cambio al checkout; el POST de venta
   resultante lleva `id_presupuesto_origen` y marca `convertido` en la MISMA transacción del
   lado del presupuesto (el proposal define el acople mínimo — precedente: la proyección de la
   16 en posición de lock segura).
