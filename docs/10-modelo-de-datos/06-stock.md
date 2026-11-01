> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 6. Stock

## 6. Stock

```sql
stock (                       -- [operativa] (por sucursal desde sucursales T2)
    id_articulo, id_sucursal,                -- FK (id_sucursal, id_tenant) → sucursales
    cantidad numeric(12,3) NOT NULL DEFAULT 0,          -- cache del libro
    minimo numeric(12,3) NULL, reposicion numeric(12,3) NULL,
    PRIMARY KEY (id_articulo, id_sucursal)
);

movimientos_stock (           -- [operativa]
    id_movimiento, id_articulo,
    id_sucursal,                             -- la clave de stock que movió (sucursales T2)
    id_punto_venta,                          -- la caja que originó el movimiento (trazabilidad)
    cantidad numeric(12,3),                  -- con signo: venta −, compra +, anulación inversa
    motivo   motivo_stock,                   -- enum: venta | compra | anulacion | ajuste
                                             --     | transferencia | inventario
                                             --     | decomiso | reclasificacion (Etapa 12)
                                             --     | remito (Etapa 17, noveno valor)
    id_comprobante_venta NULL, id_comprobante_compra NULL,
    id_punto_venta_destino NULL,             -- transferencias entre locales (nuevo)
    id_sucursal_destino NULL,                -- sucursal de destino de la transferencia (T2)
    id_lote NULL,                            -- dimensión de lote (Etapa 12)
    id_remito NULL,                          -- documento del cuarto write site (Etapa 17)
    id_empleado, observaciones, creado_el
);

lotes (                        -- [catálogo — tenant-wide, sin id_empresa, como articulos] (Etapa 12)
    id_lote, id_tenant, id_articulo,
    codigo text NOT NULL,                    -- server-derivado del vencimiento ISO si se omite
    fecha_vencimiento date NULL,             -- NULL si y solo si es_sin_identificar
    es_sin_identificar boolean NOT NULL DEFAULT false,
    created_at, updated_at, deleted_at
);
-- Clave natural: UNIQUE (id_tenant, id_articulo, codigo) WHERE deleted_at IS NULL
--   (ux_lotes_articulo_codigo) — target del get-or-create.
-- A lo sumo un lote sin identificar por artículo: UNIQUE (id_tenant, id_articulo)
--   WHERE es_sin_identificar AND deleted_at IS NULL (ux_lotes_sin_identificar).
-- fecha_vencimiento es inmutable una vez creado el lote — una segunda recepción con
-- fecha distinta se rechaza (409 lote_vencimiento_incompatible) en vez de sobrescribir.

stock_lotes (                  -- [operativa] (Etapa 12)
    id_articulo, id_sucursal, id_lote, id_tenant,
    cantidad numeric(12,3) NOT NULL DEFAULT 0,          -- cache del libro, por lote
    PRIMARY KEY (id_articulo, id_sucursal, id_lote)
);
-- PK-only, sin auditoría — mismo criterio que `stock`. Sin CHECK sobre cantidad: un saldo
-- de lote negativo está permitido en el mostrador (legacy parity), refusado solo en
-- caminos de back-office (transferencia, decomiso).
```

> **Estado (Etapa 5, Slice 3):** `stock` y `movimientos_stock` implementadas — `motivo` solo
> tiene camino de escritura para `venta`/`anulacion`/`ajuste` en esta etapa; `compra`/
> `transferencia`/`inventario` quedan como valores reservados del enum, sin escritor.
> `id_punto_venta_destino` sí se crea en esta etapa (columna lista para transferencias), pero
> ningún camino de escritura la usa todavía.
>
> **Estado (Etapa 8, Slice 1 — RESUELTO):** `movimientos_stock.id_comprobante_compra` aterriza
> en esta slice, junto con su FK compuesta `(id_comprobante_compra, id_tenant)` RESTRICT y su
> índice de soporte — la deuda declarada arriba (columna sin FK) queda pagada. `motivo = compra`
> abre camino de escritura recién en Slice 2 (`ServicioDeCompras.ConfirmarAsync`/`AnularAsync`);
> `transferencia`/`inventario` lo abren en Slice 3 (`ServicioDeStock.TransferirAsync`/
> `ContarAsync`) — ningún escritor nuevo se agrega en esta slice, solo el esquema.
> Nota de implementación: los FK de `id_empleado` (comprobantes_venta, movimientos_stock,
> movimientos_cuenta_corriente) son simples hacia `usuarios.id_usuario` — no compuestos con
> `id_tenant` — porque la clave alterna requerida forzaría `id_tenant NOT NULL` en `usuarios`,
> rompiendo el centinela NULL del staff de plataforma. `id_empleado` se deriva siempre del
> contexto autenticado del servidor, nunca del cliente.
>
> **Estado (Etapa 8, stage-8-compras-transferencias-inventario): implementada — los tres
> motivos reservados ya tienen escritor.** `motivo = compra` abre en `ServicioDeCompras`
> (Slice 2, confirmar/anular). `motivo = transferencia` abre en
> `ServicioDeStock.TransferirAsync` (Slice 3): una transacción, dos filas espejadas por
> artículo (origen negativo, destino positivo), ordenadas en un único orden ascendente sobre
> las `2N` claves `(id_articulo, id_punto_venta)` — el orden total que evita el deadlock
> contra una transferencia inversa simultánea o un checkout concurrente. `motivo = inventario`
> abre en `ServicioDeStock.ContarAsync` (Slice 3): el cliente manda el TOTAL contado, nunca un
> delta — el servidor lo deriva bajo el mismo row lock que `AjustarAsync` usa, y un conteo sin
> diferencia no escribe ninguna fila (evita `ck_movimientos_stock_cantidad_no_cero` en vez de
> chocar contra ella). Ningún enum, columna ni invariante del esquema de arriba cambió de
> forma — el trabajo de esta etapa fue enteramente de escritores nuevos, nunca de esquema.
>
> **Estado (Etapa 12 — COMPLETA):** `lotes`/`stock_lotes` nuevas, con RLS propia; seis columnas
> aditivas (`movimientos_stock.id_lote`, `articulos.controla_lote`,
> `items_comprobante_venta.id_lote`, `items_comprobante_compra.codigo_lote`/
> `fecha_vencimiento`/`id_lote`) y dos valores nuevos de `motivo_stock` (`decomiso`,
> `reclasificacion`) — ambos agregados vía `ALTER TYPE ... ADD VALUE` (ningún `Sql()` de la
> migración que los agrega puede nombrarlos, PG lo prohíbe dentro de la misma transacción).
> Los OCHO motivos tienen escritor: `reclasificacion` lo escribe solo la reconciliación de
> activación (pares neto cero de `ServicioDeLotes.ReconciliarAsync`) y `decomiso` solo
> `ServicioDeStock.DecomisarAsync` (Admin, cantidad positiva negada server-side, nunca
> negativo). `stock.cantidad = SUM(movimientos)` sigue intacto sobre los ocho motivos; el
> segundo invariante `stock_lotes.cantidad = SUM(movimientos con ese id_lote)` y el tercero
> (`SUM(stock_lotes) = stock.cantidad` para par reconciliado) están probados end-to-end.
> Ningún dato existente se reescribió: `controla_lote` default `false`, todo lo demás nullable.
> Control efectivo = `articulos.controla_lote` AND parámetro `lotes_habilitado` (empresa);
> selección FEFO particionada no-vencidos-primero (`ReglaDeLotes.ElegirFefo`); vencido =
> `fecha_vencimiento < hoy` estricto, con "hoy" en la zona horaria del punto de venta.
>
> **Estado (Etapa 17, DB CHANGE GATE ejercido y aprobado en slice 4):
> `movimientos_stock.id_remito` implementada — etapa completa (PRs #1-#8); el cuarto write
> site (`ServicioDeRemitos.EmitirAsync`, slice 5) ya escribe esta columna — este header quedó
> abierto entre las slices 4 y 5 y se cierra recién acá, siguiendo la regla 19 del programa.**
> `ALTER TABLE movimientos_stock ADD COLUMN id_remito integer NULL` + `fk_movimientos_stock_remito`
> compuesta MATCH SIMPLE + `ix_movimientos_stock_remito` — mismo shape exacto que
> `fk_movimientos_stock_comprobante_venta`/`..._comprobante_compra`. Metadata-only en PG 11+
> (columna nullable sin default), sin table rewrite. Sin esta columna, una fila de `motivo =
> remito` sería la única inatribuible de un ledger append-only cuyo propósito es la
> reconstrucción. `motivo_stock` gana su **noveno** valor, `'remito'`, vía `ALTER TYPE ... ADD
> VALUE` en `RemitosEtapa17` — **el único artefacto IRREVERSIBLE de la etapa** (Postgres no
> soporta `DROP VALUE`; mismo mecanismo y misma aceptación que `decomiso`/`reclasificacion` en
> la Etapa 12, precedente citado por el proposal §B) — ningún `Sql()` de esa misma migración
> puede nombrarlo. `motivo = remito` abre camino de escritura recién en slice 5
> (`ServicioDeRemitos.EmitirAsync`, el cuarto write site — la garantía de "tres write sites" del
> spec de stock se enmienda explícitamente a cuatro, mismo contrato/lock order duplicado
> intencional).

> **Edición de una compra confirmada (E6):** `motivo = compra` también lo escribe `ServicioDeCompras.ActualizarConfirmadaAsync`: un
> movimiento por clave `(artículo, sucursal, punto de venta, lote)` con la DIFERENCIA con signo (positivo o negativo, nunca cero) contra lo que el
> libro de esa compra ya suma, con `observaciones = 'Edición de la compra #id'`. Por eso el libro de una compra puede tener varios
> movimientos `compra` de la misma clave; la anulación revierte la suma neta por clave, no cada uno.

> **Estado (sucursales, T2): el stock es de la sucursal.** `stock` y `stock_lotes` se claven por
> `(id_articulo, id_sucursal[, id_lote])` con FK `(id_sucursal, id_tenant)` → `sucursales`; las cajas de una
> sucursal comparten la fila, y `minimo`/`reposicion` son por sucursal. `movimientos_stock` gana
> `id_sucursal NOT NULL` (y `id_sucursal_destino` en las transferencias) y CONSERVA `id_punto_venta` como la
> caja que originó el movimiento. Los invariantes valen por sucursal: `stock.cantidad = Σ cantidad` de
> `movimientos_stock` por `(id_articulo, id_sucursal)`, y `Σ stock_lotes = stock` para un artículo
> lote-efectivo. Un solo escritor (`EscritorDeStock`) para ventas, remitos, compras, ajustes, decomisos,
> transferencias, conteos y lotes, con un solo orden de locks `(id_sucursal, id_articulo, id_lote NULLS
> FIRST)`. Cada escritor resuelve la sucursal desde la caja dentro de su transacción; las anulaciones y la
> edición de una compra revierten sobre la sucursal guardada en los movimientos originales, nunca la que la
> caja tenga después. Una transferencia entre dos cajas de la misma sucursal se rechaza con `409
> transferencia_misma_sucursal`. Migración `StockPorSucursal`: backfill desde `puntos_venta.id_sucursal` que
> aborta si dos filas cayeran sobre la misma clave (no fusiona saldos en silencio). Los contratos HTTP de
> escritura siguen hablando de punto de venta: un punto de venta escribe sobre el stock de su sucursal.
>
> **Fusión de sucursales (T5).** Pasa todo el stock de una sucursal a otra con pares de movimientos `transferencia`
> (egreso en el origen, ingreso en el destino, `id_sucursal_destino` = destino), uno por saldo de agregado y de lote,
> negativos incluidos; el origen queda en cero. Toma las filas de las dos sucursales en orden de sucursal y, dentro de
> cada una, todos los agregados antes que los lotes: un escritor que tiene el lote de un artículo ya tiene su agregado,
> así que no se cruza. Antes tomó con `FOR UPDATE` las cajas de las dos sucursales (ver doc 01, Sucursales).

> **Estado (sucursales, T3): las lecturas hablan de sucursal.** Los reportes de stock (`GET /api/reportes/stock/`
> `existencias|vencimientos|vencimientos/resumen|reposicion|reposicion/resumen|rotacion` y los `/export`) piden
> `idSucursal` o, por compatibilidad con la web y el POS empaquetado, `idPuntoVenta`, que se resuelve a la sucursal de
> esa caja; exactamente uno de los dos (`400 alcance_de_stock_requerido` / `alcance_de_stock_ambiguo`). Las
> respuestas devuelven `idSucursal` y `nombreSucursal` y ya no `idPuntoVenta`: dos cajas de una sucursal reciben la
> misma respuesta, con una sola fila por artículo (o lote). La zona horaria y los días de alerta/rotación siguen
> siendo parámetros por caja: con `idSucursal` se toman de la caja de menor id de la sucursal, hasta que esos
> parámetros pasen a ser de la sucursal. Una sucursal dada de baja se trata como inexistente (`404`), también por la
> caja que todavía la referencia. La ficha del artículo (`articulo_info`) lista el stock por sucursal; si una sucursal
> dada de baja conserva saldo, la ficha lo sigue mostrando como `(sucursal id N)` en lugar de ocultarlo. Los
> nombres de archivo de los export son `existencias_sucursal{id}_…`. Las ventas se pueden agrupar por sucursal
> (`ventas_agrupadas` con `agruparPor = sucursal`, vía `ventas.id_punto_venta → puntos_venta.id_sucursal`); una
> sucursal dada de baja con ventas conserva su fila como `(sucursal id N)`, para que el total no pierda importe.

`stock.cantidad` es un cache mantenido en la misma transacción del movimiento.
Transferencia entre locales: dos movimientos espejados — feature nueva que el legacy
resolvía "a mano y que Dios ayude".
