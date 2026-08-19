# Explore — Stage 16: Órdenes de compra

Fecha: 2026-08-18. Fase ejecutada por sdd-explore (sonnet) bajo mandato autónomo; contenido
persistido verbatim por el orquestador (el agente de fase no tenía Write en su toolset).

## Current State

**Circuito de compra hoy** (`src/Ways.Application/Compras/ServicioDeCompras.cs`, doc-10 §5): arranca directamente en `comprobantes_compra` — sin ningún antecedente de intención de compra. Ciclo `borrador → confirmada → anulada` (`Ways.Domain.Compras.EstadoCompra`, **enum nativo Postgres** `estado_compra` vía `npgsql.MapEnum`, nunca `HasPostgresEnum`). `borrador` es el único estado mutable — replace-set completo bajo `SELECT ... FOR UPDATE ... WHERE estado='borrador'`. `ConfirmarAsync` es UNA transacción ADO cruda (no EF) con orden fijo de locks: (1) `UPDATE ... RETURNING` como única autoridad de transición, (2) congela el read-set de items, (2.b) resuelve/crea lotes (`ServicioDeLotes.ResolverOCrearAsync`, get-or-create serializado vía `ON CONFLICT` sobre `ux_lotes_articulo_codigo`), (3) UN `movimientos_stock` + upsert `stock`/`stock_lotes` por item, orden ascendente `(id_articulo, id_lote)`, (4) `costo_nominal` (solo `actualiza_costo`), (5) lock de `proveedores` como **último** row-lock de la transacción (etapa 15 — invertir el orden reintroduce deadlock), (6) UN movimiento `compra` en `movimientos_cuenta_corriente_proveedor`. `AnularAsync` revierte por contramovimientos, rechaza si dejaría stock negativo (agregado y por lote), audita y escribe un `ajuste` reversor — nunca toca `gastos` ligados ni `costo_nominal`.

**Quién escribe qué, y cuándo:**
- `movimientos_stock` (`motivo=compra`/`anulacion`), `costo_nominal`, `movimientos_cuenta_corriente_proveedor` — los tres **solo** en `ConfirmarAsync`/`AnularAsync`. El `borrador` de una compra no toca ninguno de los tres.
- `lotes`/`stock_lotes` — resueltos dentro de `ConfirmarAsync` únicamente.

**Precedente de paridad:** confirmado — el legacy `alsina/facturacion.php?accion=nuevo` (C3, doc-01:203-208) **nunca persistía**: acumulaba en `$_SESSION['compra']` sin INSERT y referenciaba una columna `proveedor` ya inexistente en `articulos`. Cero órdenes de compra en el legacy; todo el diseño de la etapa 16 es interno (la propia spec `comprobantes-compra/spec.md` ya lo documenta: "Greenfield: legacy C3 never persisted").

**Sugerencia de compra (Etapa 13, archivada, cero cambios de schema):** `GET /api/reportes/stock/reposicion?idPuntoVenta=&dias=` → `Reposicion(IdPuntoVenta, Hoy, DiasDeRotacion, ZonaHoraria, Filas)` con `FilaDeReposicion(IdArticulo, Articulo, Cantidad, Minimo, Reposicion?, Sugerido?, IdProveedor?, Proveedor?, ConsumoDiarioPromedio?, DiasDeCobertura?)`. El agrupamiento por proveedor (bucket "Sin proveedor" incluido, nunca omitido) lo hace el front en `Ways.Web/src/paginas/agruparPorProveedor.ts` + `Reposicion.tsx`. `sugerido = reposicion IS NULL ? null : max(0, reposicion − cantidad)`, nunca `0`. `GET /api/reportes/stock/rotacion?idPuntoVenta=&dias=` → `Rotacion(..., Filas: FilaDeRotacion[])` con `FilaDeRotacion(IdArticulo, Articulo, ConsumoEnVentana, ConsumoDiarioPromedio, MinimoSugerido)` — solo artículos con historia calificada. La spec `reposicion-de-stock/spec.md` documenta **explícitamente** que "stock en tránsito" queda omitido de la fórmula "hasta que la Etapa 16 le dé a las órdenes un estado y una llegada esperada" — la 16 es la consumidora prevista de esa nota, no al revés.

**Ledger de proveedores (Etapa 15, archivada):** `movimientos_cuenta_corriente_proveedor` (append-only, sin `EntidadBase`) con exactamente 4 escritores: `apertura` (solo migración), `compra` (`ConfirmarAsync`), `pago` (`ServicioDeGastos`), `ajuste` (anulación + ajuste manual). La OC solo puede interactuar con este ledger **en el punto de conversión** (reusando `ConfirmarAsync`), nunca antes: mientras es OC pura (borrador/enviada/recibida parcial) no hay deuda.

**Precedente enum vs texto+CHECK (Etapa 14, decisión 8 del proposal archivado):** doc-10 principio 4 — "enum nativo de Postgres solo para estados de máquina de estados; los padrones son datos, no enums". `auditoria.accion` quedó como `text + CHECK` justamente por ser un catálogo abierto y creciente. `estado_compra` (3 valores, máquina de estados real) es enum nativo. El ciclo de la OC (`borrador/enviada/recibida_parcial/cerrada/anulada`, 5 valores fijos) cae del lado del enum nativo por el mismo criterio.

## Affected Areas

- `docs/10-modelo-de-datos.md` §5 — extensión con las tablas de OC, siguiendo la convención "Estado (Etapa N...)".
- `docs/09-multi-tenancy.md` — categoría de scoping (candidata: operativa, igual que `comprobantes_compra`).
- `src/Ways.Domain/Compras/` — nuevo agregado `OrdenCompra`/`ItemOrdenCompra`, enum `EstadoOrdenCompra`.
- `src/Ways.Application/Compras/ServicioDeCompras.cs` o un `ServicioDeOrdenesDeCompra` dedicado (precedente del proyecto: cada ciclo registral tiene su propio servicio — `ServicioDeVentas`/`ServicioDeCompras`/`ServicioDeStock` no se comparten).
- `src/Ways.Application/Stock/` — si la recepción mueve stock por sí misma, nuevo motivo o reutilización de `motivo=compra` recién en la conversión.
- `openspec/specs/comprobantes-compra/spec.md` — probable nueva Requirement si la conversión pobla un FK.
- `openspec/specs/reposicion-de-stock/spec.md` — su nota "stock en tránsito omitido" queda potencialmente resuelta.
- `src/Ways.Web/src/paginas/Reposicion.tsx` + `agruparPorProveedor.ts` — candidatos naturales para un botón "generar OC" por proveedor.

## Modelo tentativo para el gate de DB (a validar en proposal/design — no se aplica acá)

```sql
ordenes_compra (            -- [operativa]
    id_orden_compra, id_tenant, id_punto_venta,
    id_proveedor, id_empleado,
    fecha_emision timestamptz, fecha_esperada date NULL,   -- ETA, insumo de "stock en tránsito"
    observaciones,
    estado estado_orden_compra NOT NULL   -- enum nativo: borrador|enviada|recibida_parcial|cerrada|anulada
);

items_orden_compra (
    id_item, id_orden_compra, orden,
    id_articulo NOT NULL,     -- mismo criterio que items_comprobante_compra
    cantidad_pedida numeric(12,3),
    costo_unitario_estimado numeric(14,4) NULL,
    cantidad_recibida numeric(12,3) NOT NULL DEFAULT 0   -- acumulación por item (ver Recepción parcial)
);
```

FKs candidatas con índice de soporte contado cada una: `ordenes_compra.id_proveedor` compuesta con `id_tenant` (como `comprobantes_compra`); `ordenes_compra.id_punto_venta` compuesta; `items_orden_compra.id_orden_compra`; `items_orden_compra.id_articulo`; y, con el vínculo FK directo elegido, `comprobantes_compra.id_orden_compra NULL` compuesta.

**Enum de estados:** por el precedente de la Etapa 14 (decisión 8) y `estado_compra` existente, `estado_orden_compra` debería ser enum nativo Postgres (`npgsql.MapEnum`, nunca `HasPostgresEnum`).

**Cómo se liga la conversión OC→comprobante:**
1. **FK directa `comprobantes_compra.id_orden_compra NULL`** — espejo exacto de `gastos.id_comprobante_compra NULL` (etapa 8/15). Pros: historia trivial, sigue un precedente ya usado dos veces, soporta naturalmente 1 OC → N comprobantes. Cons: ninguno estructural si se acepta cardinalidad 1:N.
2. **Tabla puente `recepciones_orden_compra`** — modela recepción física desacoplada de la factura. Pros: más fiel si mercadería y factura llegan en momentos distintos. Cons: tabla adicional, rompe la invariante "solo confirmar mueve stock/costo/CC", mayor superficie de gate de DB.

Recomendación tentativa: opción 1 (FK simple) por consistencia de precedente y menor ruptura de invariantes.

## La recepción parcial — opciones

**Opción A — un comprobante de compra por cada recepción**, con `id_orden_compra` poblado; `items_orden_compra.cantidad_recibida` se acumula sumando lo confirmado. Reutiliza `ConfirmarAsync` sin tocarlo — cero riesgo sobre el motor de stock/costo/lote/CC ya probado. Realista si cada entrega física trae su propio remito/factura parcial (patrón típico argentino).

**Opción B — un solo comprobante al cerrar la OC.** La recepción anota cantidades sin generar comprobante; solo al cerrar se emite UN comprobante consolidado. Exige que la recepción mueva stock antes de que exista comprobante — rompe la invariante actual "solo `ConfirmarAsync` mueve stock/costo/CC", necesita nuevo motivo de movimiento o una zona "recibido no facturado", y tensiona la CHECK `ck_comprobantes_compra_confirmada_completa`.

Esta decisión está acoplada 1:1 con la tercera decisión abierta de doc-11 ("si la recepción mueve stock por sí misma o solo lo hace la confirmación del comprobante").

## Integración con la Etapa 13

`GET /api/reportes/stock/reposicion` ya trae todo lo necesario (artículo, `Sugerido`, `IdProveedor`) para pre-cargar un borrador de OC: `FilaDeReposicion.{IdArticulo, Sugerido} → ItemOrdenCompra.{IdArticulo, CantidadPedida}` filtrado por proveedor. No requiere tocar la Etapa 13 (archivada con "cero cambios de schema" ratificado) — la integración es de lectura unidireccional, Etapa 16 consume, nunca al revés.

## Qué NO toca la etapa

Checkout de venta; el ledger de proveedores salvo en el punto de conversión (reusando `ConfirmarAsync`); `articulos.costo_nominal` (la OC solo tiene un `costo_unitario_estimado`, no un hecho); lotes de la Etapa 12 salvo en la conversión (dentro de `ConfirmarAsync` como siempre); la sugerencia de compra de la Etapa 13 en sí (solo se lee); reversión de gastos (sigue sin existir).

## Riesgos / decisiones abiertas para el proposal

- Recepción-mueve-stock-por-sí-misma sí/no (doc-11 decisión abierta 3) — bisagra de todo el diseño. **Resuelta por OD1 abajo.**
- Diferencias de precio OC vs factura real (doc-11 decisión abierta 2) — informativo o bloqueante, sin mecanismo hoy. **Resuelta por OD3 abajo.**
- Cardinalidad real OC↔comprobante (1:1 vs 1:N). **Resuelta por OD2 abajo.**
- Interacción con lotes si se recibe antes de facturar — moot bajo OD1 (la recepción ES un comprobante y los lotes se resuelven donde siempre).

## Superficies API/web (a definir en proposal, orientativas)

- API: `POST/PUT /api/ordenes-compra` (borrador CRUD, mismo patrón replace-set que compras), `POST /api/ordenes-compra/{id}/enviar`, recepción vía comprobante ligado (OD1), `POST /api/ordenes-compra/{id}/cerrar`, `POST /api/ordenes-compra/{id}/anular`.
- Web: pantalla de listado/detalle de OC, botón "generar OC" desde `Reposicion.tsx` agrupado por proveedor, flujo de recepción.

## Estimación gruesa de slices

Del orden de 5-7 slices, comparable a la Etapa 8/15 (schema → CRUD borrador → enviar/recibir → conversión → anular/cerrar → integración con reposición → web), tamaño "media a grande" como ya lo caracteriza doc-11.

## Orchestrator Decisions (mandato autónomo, 2026-08-18 — a formalizar por el proposal)

Ninguna de estas decisiones está en la lista de pendientes reservados del dueño; el orquestador
las resuelve bajo el mandato autónomo y el proposal debe formalizarlas con
opciones/tradeoffs/costo de revertir (o refutarlas con evidencia):

1. **La recepción NO mueve stock por sí misma — opción A**: cada recepción física se registra
   como un comprobante de compra (borrador → confirmada) ligado a la OC. La invariante "solo
   `ConfirmarAsync` mueve stock/costo/lotes/CC" — el motor probado de las etapas 8/12/15 — se
   preserva INTACTA. La OC acumula `cantidad_recibida` por item a partir de los comprobantes
   confirmados y transiciona sola a `recibida_parcial`/`cerrada` (con cierre manual permitido
   para cortar una OC que no se completará).
2. **Cardinalidad 1 OC → N comprobantes** vía FK directa `comprobantes_compra.id_orden_compra
   NULL` (espejo del precedente `gastos.id_comprobante_compra`, ya usado dos veces). Sin tabla
   puente: la recepción ES el comprobante.
3. **Diferencias de precio OC vs factura: INFORMATIVAS, jamás bloqueantes.** El comprobante es
   el hecho; la OC es la intención. La superficie de lectura muestra el desvío
   (estimado vs real) sin impedir la confirmación. Un control bloqueante es política de compras
   del dueño — diferido con registro.
4. **`estado_orden_compra` = enum nativo Postgres** (5 valores, máquina de estados real —
   precedente de `estado_compra` y de la decisión 8 de la 14).
5. **La fórmula de reposición NO se toca en esta etapa**: el "stock en tránsito" que la spec de
   la 13 dejó anotado queda como EXTENSIÓN DIFERIDA con registro explícito (tocaría una
   capability archivada con cero-schema ratificado; la 16 ya provee `fecha_esperada` y
   cantidades pendientes — el insumo queda listo). La integración de esta etapa es
   unidireccional: pre-cargar el borrador de OC desde la lista de reposición por proveedor.
6. **Anulación de OC**: solo desde `borrador`/`enviada` sin comprobantes ligados confirmados;
   una OC con recepciones confirmadas se CIERRA (no se anula) — la historia de los comprobantes
   es inmutable y sus efectos ya ocurrieron. El proposal valida esta regla contra los estados.
