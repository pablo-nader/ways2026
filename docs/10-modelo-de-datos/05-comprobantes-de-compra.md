> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 5. Comprobantes de compra

## 5. Comprobantes de compra

Hoy (en el sistema legacy) las compras se cargan como gastos sin detalle; esta es la pieza nueva completa.

> **Vocabulario (etapa E1):** lo que antes se llamaba *gasto* es un **pago** (grupos del dueño: Compras, Ventas,
> Pagos, Cobros). La tabla es `pagos` (`id_pago`, enums `categoria_pago` y `origen_fondos_pago`) y el código usa
> `Pago` (`Ways.Domain.Pagos`, `ServicioDePagos`). Solo el contrato HTTP conserva el nombre viejo hasta la etapa
> E2: las rutas `/api/gastos…`, los nombres JSON (`idGasto`, `gastos`, …) y los códigos de error `gasto_*`.

```sql
comprobantes_compra (         -- [operativa]
    id_comprobante_compra,
    id_proveedor,
    id_tipo_comprobante integer NOT NULL,    -- clase = compra (FA del proveedor, etc.)
    numero_externo      citext,              -- el número DEL PROVEEDOR ("00003-00012345");
                                             -- acá no hay correlativo propio; forma canónica abajo
    fecha_comprobante   date,                -- la fecha de la factura
    fecha_recepcion     timestamptz,         -- cuándo entró la mercadería
    id_punto_venta,                          -- a qué local ingresa el stock
    id_empleado,
    subtotal, descuento_total, iva_total NULL, total,
    impuestos_internos numeric(14,2) NOT NULL DEFAULT 0, -- suma de los items; integra total, fuera de la base del IVA
    discrimina_iva boolean NOT NULL,         -- snapshot por comprobante; ver más abajo
    precios_incluyen_iva boolean NOT NULL DEFAULT false,  -- snapshot; solo si discrimina_iva
    observaciones,
    estado estado_compra NOT NULL,           -- enum: borrador | confirmada | anulada
    origen origen_compra NOT NULL DEFAULT 'manual'  -- enum: manual | asistente; quién cargó la compra
);
-- UNIQUE (id_proveedor, id_tipo_comprobante, numero_externo): la misma factura no entra dos veces.
--
-- Número canónico (tipos con registra_libro_iva: C-FA, C-FB, C-FC): el servicio guarda
-- `PPPPP-NNNNNNNN` (punto de venta de 5 dígitos, número de 8, la forma vigente de ARCA que imprime la
-- factura legal). Acepta `3-12345`, `0003-00012345`, `00003-00012345` o 12/13 dígitos pegados, y responde
-- 400 `numero_comprobante_invalido` si no puede interpretarlo (letras, más de un guion, más de 5/8
-- dígitos significativos, punto de venta o número en cero). Los demás tipos (remito) conservan texto libre.
-- El índice único compara texto, así que no es la defensa contra formatos distintos: el chequeo de duplicado
-- (`GET /api/compras/duplicados`) compara el VALOR NUMÉRICO de cada segmento, partiendo en el último
-- guion, y por eso también encuentra filas anteriores a la forma canónica ("0003-00012345").
-- Seguimiento: las filas viejas no se normalizaron. Una migración de datos podría chocar con el índice
-- único (dos formatos de la misma factura) y exige resolver esos pares antes; mientras tanto el
-- chequeo numérico las detecta.

alicuotas_comprobante_compra (            -- [operativa, child: solo id_tenant, como los items]
    id_alicuota_comprobante_compra, id_tenant,
    id_comprobante_compra,                   -- FK compuesta (id, id_tenant)
    id_alicuota_iva integer NOT NULL,        -- FK a alicuotas_iva [global]
    porcentaje      numeric(5,2),            -- snapshot del porcentaje
    neto            numeric(14,2),           -- suma de los totales de línea con esa alícuota
    iva             numeric(14,2)            -- el calculado, o el impreso por el proveedor
);
-- UNIQUE (id_comprobante_compra, id_alicuota_iva); CHECK (neto >= 0 AND iva >= 0).

percepciones_comprobante_compra (         -- [operativa, child: solo id_tenant, como los items]
    id_percepcion_comprobante_compra, id_tenant,
    id_comprobante_compra,                   -- FK compuesta (id, id_tenant)
    tipo            text NOT NULL,           -- 'iibb' | 'iva'
    base_imponible  numeric(14,2),           -- informativa
    alicuota        numeric(6,3),            -- informativa
    importe         numeric(14,2)            -- lo que dice la factura; suma al total
);
-- UNIQUE (id_comprobante_compra, tipo) (la jurisdicción de IIBB agregará una columna);
-- CHECK (tipo IN ('iibb', 'iva')); CHECK (base_imponible >= 0 AND importe >= 0);
-- CHECK (alicuota >= 0 AND alicuota <= 100).

items_comprobante_compra (
    id_item, id_comprobante_compra, orden,
    id_articulo NULL,                        -- NULL = línea por concepto (ver más abajo)
    descripcion text,                        -- snapshot; en un concepto es el texto libre
    cantidad        numeric(12,3),           -- unidades totales
    bultos          numeric(10,2) NULL, unidades_por_bulto numeric(10,2) NULL,
    costo_unitario  numeric(14,4),           -- 4 decimales: costos con más precisión
    descuento       numeric(14,2) DEFAULT 0,
    id_alicuota_iva integer, porcentaje_iva numeric(5,2),
    total           numeric(14,2),
    impuesto_interno numeric(14,2) NOT NULL DEFAULT 0, -- importe de la línea entera, como lo imprime el proveedor;
                                             -- no integra `total` de la línea ni la base del IVA
    actualiza_costo boolean DEFAULT true,    -- al confirmar, pisa articulos.costo_nominal
    precio_sugerido numeric(14,2) NULL,      -- propuesta de nuevo precio de venta (margen)
    codigo_proveedor citext NULL             -- el código impreso por el proveedor para la línea
);
-- CHECK ck_items_comprobante_compra_codigo_proveedor_normalizado:
--   codigo_proveedor IS NULL OR (codigo_proveedor = btrim(codigo_proveedor) AND codigo_proveedor <> '').
-- Máximo 50 caracteres (el mismo que codigos_proveedor.codigo). Sin FK ni índice propios.
```

**Código de proveedor de la línea.** `codigo_proveedor` guarda el código tal como el proveedor lo
imprimió, normalizado (recortado; vacío equivale a `NULL`), en toda línea que lo trae, con o sin
artículo y haya quedado asociado o no. El guardado del borrador solo lo persiste y nunca toca
`codigos_proveedor`. Al confirmar, cada línea con artículo y con código lo asocia a ese
artículo y al proveedor de la compra dentro de la misma transacción: si el proveedor ya lo
tiene asignado a ese artículo no hay nada que hacer, y si lo tiene asignado a otro artículo (o
si otra línea de la misma compra, anterior en el orden, ya lo reclamó para otro artículo) la
compra se confirma igual y el código queda solo en la línea. Un concepto nunca asocia. La
carrera con una asociación concurrente del mismo código se resuelve con
`INSERT … ON CONFLICT DO NOTHING` sobre `ux_codigos_proveedor_proveedor_codigo`, sin abortar la
transacción de la confirmación. Una línea cuyo artículo está dado de baja al
confirmar no se asocia: la compra se confirma y el código queda solo en la línea.

**Líneas por concepto.** Una línea con `id_articulo NULL` es un concepto (un flete, una
factura de ferretería cargada por total): descripción libre, cantidad, costo, descuento y
alícuota, sin artículo detrás. Suma al subtotal, al IVA y al `total` del comprobante, y por lo
tanto a la cuenta corriente del proveedor, pero no tiene efectos de inventario: no genera
`movimientos_stock`, no actualiza `articulos.costo_nominal`, no resuelve lote ni calcula
`precio_sugerido`, y no cuenta para la cobertura de una orden de compra. Una compra solo con
conceptos es válida. La CHECK `ck_items_comprobante_compra_concepto_sin_efectos` lo exige en la
base: con `id_articulo IS NULL`, `actualiza_costo = false` y `codigo_lote`, `fecha_vencimiento`,
`id_lote`, `bultos`, `unidades_por_bulto` y `precio_sugerido` son `NULL`. El servicio rechaza con
400 (`concepto_sin_descripcion`, `concepto_con_lote`, `concepto_con_bultos`,
`concepto_actualiza_costo`) lo que la CHECK prohíbe, en vez de aceptarlo y descartarlo.

**IVA por comprobante y desglose por alícuota.** `discrimina_iva` es un snapshot por
comprobante, no un dato del tipo: es lo que usan el cálculo y el costo efectivo (en el guardado
y en la confirmación, que lo lee del `RETURNING` bajo el lock del encabezado) y la cobertura de
una orden de compra. Lo resuelve `ReglaDeDiscriminacionDeIva` según
`tipos_comprobante.registra_libro_iva`:

| Tipo | `registra_libro_iva` | `discrimina_iva` del comprobante |
|---|---|---|
| `C-FA` | true | siempre true |
| `C-FB`, `C-FC` | true | siempre false |
| `C-RM` | false | lo elige quien carga (`discriminaIva` del request; sin pedido, false) |

Pedir un valor que contradice a una factura es 400 `discrimina_iva_incompatible`: nunca se acepta
y se corrige en silencio. `alicuotas_comprobante_compra` tiene filas solo cuando el comprobante
discrimina IVA y se reemplaza entero, junto con los ítems, en cada guardado del borrador. Cada
fila es el neto de una alícuota (la suma de los totales de línea que la usan) y su IVA, calculado
**una sola vez sobre ese neto** — la convención de una factura, no el redondeo por línea.
`iva_total` y `total` salen de las filas guardadas: `iva_total = sum(iva)` y `total = subtotal −
descuento_total + iva_total`. Con `discrimina_iva = false` no hay filas e `iva_total` es `NULL`.

Exento y no gravado son filas de `alicuotas_iva` como cualquier otra (porcentaje 0, sin
`codigo_afip`), así que aparecen en el desglose con IVA cero; el libro IVA los clasifica por nombre
con `ComposicionDeTotalesFiscales.Clasificar`, la misma función que usa la emisión fiscal.

**IVA impreso (override de redondeo).** El request puede traer, por alícuota, el IVA que el
proveedor imprimió (`ivaImpreso`). Se acepta cuando `|impreso − calculado| ≤ 1,00` y se guarda en
`iva`; fuera de esa tolerancia, o negativo, es 400 `iva_impreso_fuera_de_tolerancia`. También es
400 informarlo en un comprobante que no discrimina (`iva_impreso_sin_discriminar`), para una
alícuota que no está en las líneas (`iva_impreso_alicuota_desconocida`) o repetida
(`iva_impreso_duplicado`). El override no cambia el costo efectivo de los artículos, que sale del
importe de la línea.

**Precios con IVA incluido.** `precios_incluyen_iva` es un snapshot por comprobante (se pre-carga
del proveedor y se edita) que dice que el `costo_unitario` tipeado ya trae el IVA. Solo vale si el
comprobante discrimina IVA: pedirlo en uno que no discrimina es 400
`precios_incluyen_iva_sin_discriminar`, nunca se descarta (un comprobante que no discrimina ya es
un precio final). En ese modo el `total` de cada línea es el precio final, `round(cantidad ×
costo_unitario − descuento, 2)`, y el desglose se arma **una sola vez por alícuota** a partir de
la suma de finales de esa alícuota: `iva = round(final × p / (100 + p), 2)` y `neto = final −
iva`, de modo que `neto + iva` suma exactamente lo tipeado. Un IVA impreso se valida contra ese
`iva` calculado (tolerancia 1,00) y mueve el neto en sentido contrario, así que el total tipeado
no cambia; uno que dejaría el neto negativo es 400. El costo efectivo de un artículo es `total /
cantidad` (con precios netos es `total × (1 + p/100) / cantidad`) y la confirmación lo deriva con
el flag del `RETURNING` bajo el lock del encabezado, igual que `discrimina_iva`; la cobertura de
una orden de compra usa el mismo flag. En este modo `subtotal` y `descuento_total` ya incluyen el
IVA y `total = subtotal − descuento_total + percepciones` (sumar `iva_total` contaría el IVA dos
veces); con precios netos sigue siendo `subtotal − descuento_total + iva_total + percepciones`.

**Percepciones.** Un comprobante puede traer una percepción de IIBB y una de IVA
(`percepciones_comprobante_compra`, reemplazadas junto con los ítems en cada guardado del
borrador). Solo las admite un tipo con `registra_libro_iva` (`percepciones_sin_libro_iva`) y la de
IVA además exige que el comprobante discrimine IVA (`percepcion_iva_sin_discriminar`); también son
400 el tipo desconocido, el tipo repetido, los importes negativos, la alícuota fuera de 0 a 100 y
los decimales de más (más de 2 en base e importe, más de 3 en la alícuota). `importe` es lo que
imprimió el proveedor y es lo que suma al `total` del comprobante —y, al confirmar, a la cuenta
corriente del proveedor—; `base_imponible` y `alicuota` son informativas y no se recalculan. Un
vendedor ve el tipo y la alícuota pero no la base ni el importe, igual que el desglose de IVA.
El editor pre-carga la sugerencia cuando el proveedor tiene el flag, la empresa del punto de venta
de la compra tiene la alícuota y el tipo corresponde: base = neto gravado (suma del neto de las
alícuotas mayores a 0%; en un comprobante que no discrimina, subtotal menos descuento) e importe =
`round(base × alícuota / 100, 2)`, que se recalcula mientras la fila no se toque.

La migración `ComprasPercepcionesYPrecioFinal` agrega las columnas y la tabla con sus `DEFAULT`
(todo lo existente queda en precios netos y sin percepciones), así que no hace backfill.

**Impuestos internos.** Migración `ImpuestosInternos` (solo columnas con `DEFAULT 0`, sin backfill;
sin CHECK: el servicio rechaza lo inválido). El importe es **por línea** y va aparte de la base del IVA:
`total = subtotal − descuento_total + iva_total + impuestos_internos + percepciones` (con precios
finales, `subtotal − descuento_total + impuestos_internos + percepciones`). El costo efectivo con IVA
de una línea es `(importe de la línea con IVA + impuesto_interno) / cantidad`, y lo usan la
confirmación, la ficha del artículo y la cobertura de la orden de compra. Se rechazan con 400 un
importe negativo (`impuesto_interno_invalido`), uno distinto de cero en un comprobante que no discrimina
IVA (`impuesto_interno_sin_discriminar`) y uno en una línea por concepto
(`concepto_con_impuesto_interno`). El artículo guarda `porcentaje_impuesto_interno` (§3): con un
`costo_lista` sin IVA el costo nominal es `neto × (1 + %II/100 + %IVA/100)` (el IVA no grava al impuesto
interno) y el descuento del proveedor se aplica sobre ese total. Las ventas y la facturación ARCA no
cambian (el comercio revende: el impuesto interno es costo, no crédito fiscal). El Libro IVA de compras
tiene una columna `impuestos_internos` por fila y en totales, que entra en la `diferencia`.

**Libro IVA (compras y ventas).** Implementado sin tablas ni columnas nuevas: es una lectura de
`comprobantes_compra` y `comprobantes_venta` bajo `LecturaDeReportes` (supervisor y admin; el
de compras expone costos, así que el vendedor nunca lo ve). `GET /api/reportes/libro-iva-compras`
y `/libro-iva-ventas` (`desde`, `hasta`, `idEmpresa` opcional: sin él abarca todas las
empresas del tenant), cada uno con su `/export` XLSX; el período máximo es el de los demás
reportes (366 días) y el tope de filas el de las exportaciones.

- *Compras:* comprobantes `confirmada` de tipos con `registra_libro_iva`, por
  `fecha_comprobante` (inclusive en ambos extremos); borrador, anulada y `C-RM` quedan afuera. El
  desglose sale de `alicuotas_comprobante_compra`: cada alícuota gravada aporta neto e IVA a su
  porcentaje, Exento va a la columna *exento* y No gravado a *no gravado* (por nombre, nunca por
  porcentaje 0: el 0% real tiene `codigo_afip` y es una alícuota más). Una factura sin
  discriminar (`C-FB`, `C-FC`) no tiene crédito fiscal: su importe, menos las percepciones, va
  entero a *no gravado*. Las percepciones de IVA e IIBB salen de `percepciones_comprobante_compra`.
- *Ventas:* comprobantes de tipos `es_fiscal` con `resultado_fiscal` `aprobado` o
  `aprobado_con_observaciones` (es decir, con CAE); pendiente, rechazado y no fiscales (TX/NCX)
  quedan afuera. Un comprobante con CAE que se anuló localmente **sigue en el libro**: ante ARCA
  solo lo revierte una nota de crédito. Entra con la advertencia `anulado_sin_nc` ("Anulado sin
  NC" en la web y en la columna *Observaciones* del export). `AnularAsync` todavía no impide
  anular un fiscal con CAE; ese guard queda como seguimiento. Se filtra y se muestra por el día
  local de la empresa (parámetro `zona_horaria`), que es también la fecha (`CbteFch`) que la
  emisión envía a ARCA. La fecha fiscal no se persiste (no hay columna): se deriva del instante
  `comprobantes_venta.fecha`. Los comprobantes emitidos antes de este cambio, entre las 21:00 y las
  24:00 (hora de Argentina), se enviaron a ARCA con el día UTC y pueden figurar un día después en
  ARCA que en el libro.
  El desglose se recompone desde `items_comprobante_venta` con `ComposicionDeTotalesFiscales.Componer`
  —la función de la emisión—, así que no hay tabla de alícuotas de venta. Las notas de crédito
  fiscales (`NCA`/`NCB`/`NCC`) restan: la emisión guarda sus importes en positivo y el libro
  aplica `tipos_comprobante.signo`. Las percepciones de ventas no existen y salen en cero. El
  número es `puntos_venta.numero_fiscal` + el número fiscal del comprobante; si el punto de venta no
  tiene número fiscal se muestra `s/PV-NNNNNNNN` con la advertencia `sin_numero_fiscal`, nunca el id
  interno.
- *Alícuotas sin mapeo:* en la emisión una alícuota sin código AFIP que no es Exento ni No gravado
  falla (409 `alicuota_sin_mapeo_afip`). El libro es de solo lectura y no se cae: muestra el importe en
  su porcentaje, junto a las otras alícuotas, con la advertencia `alicuota_sin_clasificar`.
- *Consistencia:* cada fila lleva `diferencia = total − (neto gravado + IVA + no gravado + exento +
  percepciones + impuestos internos)`. En un dato bien formado es cero; si no lo es, la fila se informa con su
  diferencia en vez de ocultarse o corregirse.
- Los catálogos globales (tipos, alícuotas) se leen sin el filtro de baja lógica, para que dar de
  baja uno no saque del libro un comprobante ya declarado; proveedor o cliente dados de baja dejan
  la fila con "(no disponible)".

La migración `ComprasRemitoYAlicuotas` backfillea `discrimina_iva` desde el tipo de cada
comprobante existente y reconstruye el desglose de los que ya discriminan a partir de sus ítems,
con el mismo redondeo por línea con el que se calculó su `iva_total`, de modo que la suma de las
filas coincide con el encabezado.

Ciclo: se carga en `borrador` (se puede ir armando con el remito en la mano), y al
**confirmar** — en una sola transacción — genera los `movimientos_stock` de entrada,
actualiza `costo_nominal` donde `actualiza_costo`, y ofrece actualizar precios de venta
según margen del grupo/proveedor. `anulada` revierte con contramovimientos.

> **Estado (Etapa 8, Slice 1 — schema + seed gate):** `comprobantes_compra`/
> `items_comprobante_compra` creadas por la migración `ComprasYTransferenciasEtapa8`, con dos
> ajustes de conformidad sobre el esquema de arriba (DB CHANGE GATE, autonomous mode
> 2026-08-05): (1) la unicidad de `numero_externo` gana `id_tenant` en la clave —
> `(id_tenant, id_proveedor, id_tipo_comprobante, numero_externo)` — porque toda tabla
> `[operativa]` está tenant-scoped y el esquema de arriba omite `id_tenant` por convención;
> (2) es una **unicidad PARCIAL** (`WHERE estado <> 'anulada' AND numero_externo IS NOT
> NULL`), no una UNIQUE llana: excluye `numero_externo NULL` mientras es borrador y permite
> reingresar el número de una factura anulada por error de carga. `id_articulo` en
> `items_comprobante_compra` nació **`NOT NULL`**, a diferencia de `items_comprobante_venta`
> (§4); la migración `ComprasLineasPorConcepto` lo volvió nullable (ver "Líneas por concepto"
> arriba). Nueva CHECK
> `ck_comprobantes_compra_confirmada_completa` (`estado = 'borrador' OR (numero_externo,
> fecha_comprobante, fecha_recepcion todos NOT NULL)`), no especificada arriba, cierra a nivel
> esquema la regla de negocio "no se confirma sin identidad de factura". `estado_compra` se
> registra únicamente vía `npgsql.MapEnum<EstadoCompra>` en `DependencyInjection.cs` y
> `WaysDbContextFactory.cs` — nunca también con `HasPostgresEnum`, mismo criterio que el
> resto de los enums nativos del proyecto. La aritmética de la compra (`CalculadorDeCompra`),
> el ciclo `borrador → confirmada → anulada` y la actualización de `costo_nominal` son Slice 2.
>
> **Estado (Etapa 8, stage-8-compras-transferencias-inventario): implementada — cierre del
> ciclo completo.** `CalculadorDeCompra` (Slice 2) es la única aritmética de la compra —
> `cantidad = unidades + bultos × unidades_por_bulto`, `costo_nominal` recibe el costo
> efectivo **IVA-incluido** redondeado `AwayFromZero`, deduplicado por el `orden` más alto
> cuando dos líneas repiten artículo o pertenecen a la misma familia (ver "Compras y familias", en §3).
> El ciclo `borrador → confirmada → anulada` corre en
> `ServicioDeCompras`: el borrador es un replace-set completo bajo `FOR UPDATE`, confirmar es
> una `UPDATE … RETURNING` estado-guardada como única autoridad de transición (el mismo listón
> que `MarcarAnuladoAsync` en etapa 5), y anular revierte por contramovimientos, rechazado con
> `409` si dejaría stock negativo. `precio_sugerido` se calcula con el `SugeridorDePrecio`
> existente —sobre el costo efectivo de la línea y redondeado al `redondeo_sugerencia_precio` de la empresa del
> punto de venta de la compra, resuelto una vez por compra— y se aplica por una acción explícita separada — nunca dentro del confirm (decisión
> 3 de la propuesta). Anular una compra **no** revierte los pagos ya ligados: quedan como
> historial de un pago real (decisión 6, sin motor de reversión de pagos en el proyecto).
>
> **Estado (Etapa 16): implementada — etapa completa (PRs #140-#145).** `comprobantes_compra` gana `id_orden_compra integer NULL` —
> ALTER aditivo, metadata-only en PG 11+, sin rewrite de tabla. FK 9
> `fk_comprobantes_compra_orden_compra` compuesta `(id_orden_compra, id_tenant)` contra
> `ordenes_compra`, RESTRICT, MATCH SIMPLE (el default): con `id_orden_compra` NULL la
> constraint no se chequea, mismo precedente que `pagos.id_comprobante_compra`. Ninguna CHECK
> ata `id_orden_compra` a `estado`: el link se setea mientras `borrador` y se congela después
> por el guard de servicio (`ExigirOrdenLigableAsync`, slice 3), no por esquema. `NULL` sigue
> siendo el 100% del tráfico anterior a esta etapa, un estado permanentemente legítimo: no toda
> compra viene de una orden.
>
> **Origen (carga asistida): implementada.** `comprobantes_compra` gana `origen origen_compra NOT NULL
> DEFAULT 'manual'` — enum nativo (`manual`, `asistente`), registrado con `MapEnum` como el resto. El
> `ALTER` rellena las filas existentes con `manual` (el 100% del tráfico anterior), así que la migración no
> escribe datos. Lo fija solo la capa de aplicación (`ServicioDeCompras.CrearBorradorAsync(solicitud,
> origen)`): la API REST nunca lo acepta y siempre crea `manual`, y el PUT de un borrador no lo cambia.
> `asistente` marca un borrador cargado desde una factura por el conector; la confirmación sigue siendo
> humana. Se expone en el detalle y el listado de compras, con filtro `origen` en el listado y su export.

**Relación con pagos:** la compra registra la mercadería; el pago registra la plata.
`pagos` gana `id_comprobante_compra NULL`: pagarle al proveedor referencia la factura.
Una compra puede estar impaga (sin pago asociado) — eso ya da una cuenta corriente de
proveedores embrionaria sin tabla extra.

```sql
pagos (                      -- [operativa]
    id_pago, fecha, id_empresa, id_punto_venta,
    id_turno_caja NULL, id_empleado,
    categoria  categoria_pago,              -- enum: proveedor | sueldos | viaticos
                                             --     | impuestos | servicios | otros
    origen_fondos  origen_fondos_pago,      -- enum: caja_turno | tesoreria
    id_proveedor NULL, id_comprobante_compra NULL,
    id_area NULL, concepto, detalle,
    numero_factura text NULL, importe,        -- el medio vive en items_pago (abajo), no en la cabecera
    id_movimiento_personal integer NULL,     -- pago de personal que lo generó (§8c)

    CONSTRAINT ck_pagos_caja_turno_requiere_turno
        CHECK (origen_fondos <> 'caja_turno' OR id_turno_caja IS NOT NULL),
    CONSTRAINT ck_pagos_movimiento_personal_es_sueldo
        CHECK (id_movimiento_personal IS NULL OR categoria = 'sueldos')
);
```

Los retiros de efectivo **dejan de ser pagos** (`tipo=95` era otro número mágico):
van a `movimientos_caja` (§7).

> **Estado (Etapa 6, Slice 1):** `pagos` se crea en esta etapa, pero **sin**
> `id_comprobante_compra`: `comprobantes_compra` no existe todavía (etapa 8), así que un
> `int NULL` sin FK sería una referencia sin garantía — mismo criterio que
> `movimientos_stock.id_comprobante_compra` (§6). `id_turno_caja` se crea **NOT NULL** en esta
> etapa: todo pago se registra contra un turno abierto, resuelto server-side, nunca input de
> cliente.
>
> **Estado (Etapa 8, Slice 1 — RESUELTO):** `pagos.id_comprobante_compra` aterriza en esta
> slice, junto con su FK compuesta `(id_comprobante_compra, id_tenant)` RESTRICT y su índice
> de soporte — la deuda declarada arriba queda pagada, sin cambio de forma en ninguna otra
> columna. Poblado por `ServicioDePagos` (etapa 8, Slice 4) bajo un `SELECT ... FOR SHARE`
> sobre el header de la compra (TOCTOU cerrado contra una anulación concurrente); solo válido
> cuando `categoria = proveedor` y la compra referenciada está `confirmada`. El vínculo es
> historia, no un bloqueo: anular la compra vinculada sigue permitido (design decisión 6 —
> ningún camino de reversión de pago existe todavía).
>
> **Estado (admin-panel-expense-upload, PR1 — RESUELTO):** `pagos.id_turno_caja` pasa a ser
> **NULLABLE** — deja de ser el único origen de fondos posible. `id_empresa` aterriza NOT NULL
> (FK compuesta `(id_empresa, id_tenant)` a `empresas`, RESTRICT), resuelto server-side desde
> `puntos_venta.id_empresa` del punto de venta del pago — backfill de una sola vez para las
> filas preexistentes. `origen_fondos` aterriza NOT NULL (enum nativo `origen_fondos_pago`).
> `ck_pagos_caja_turno_requiere_turno` es el backstop de esquema: solo el origen `caja_turno`
> exige un turno.
>
> **Estado (stage-gastos-origen-fondos-pos, PR2 — RESUELTO):** `ServicioDePagos.RegistrarAsync`
> ofrece los dos orígenes de `SolicitudDePago.OrigenFondos` (default `caja_turno`, retrocompat).
> Un pago `tesoreria` sigue atado al turno abierto (`id_turno_caja` poblado, trazabilidad) pero
> se paga directo del fondo de tesorería de la empresa: en la MISMA transacción del insert,
> `EscriturasDeTesoreria.ApendearAsync` agrega una fila `movimientos_tesoreria` con
> `tipo = 'pago'`, `id_pago` = el pago recién insertado, `ingreso = 0`, `egreso = importe`.
> Un pago `caja_turno` sigue descontando el efectivo del cajón como antes. Los totales del turno
> que alimentan el arqueo/cierre (`LectorDeMovimientosDelTurno`, `LectorDeContenidoDeResumen`)
> filtran `origen_fondos = 'caja_turno'`: un pago `tesoreria` nunca resta de ningún medio ni lo
> vuelve arqueable — ya se descontó de tesorería en el momento en que se registró. El Z-report/
> historial (`LectorDeLineasDelTurno.LeerPagosAsync`, `GET /api/gastos`) sigue listando los dos
> orígenes sin filtrar (informativo, expone `OrigenFondos` para que la UI etiquete "Caja
> general").
>
> **Estado (stage-gasto-a-compra, PR4 — RESUELTO):** `pagos.id_comprobante_compra` deja de
> poblarse solo al crear el pago — `POST /api/gastos/{id}/vincular-compra`
> (`ServicioDePagos.VincularCompraAsync`) liga un pago YA EXISTENTE (POS o administración) a una
> compra confirmada, sin cambio de esquema. Si el pago ya es `categoria = proveedor` con
> `id_proveedor` coincidente, el vínculo es solo trazabilidad: se imputa la compra en el movimiento
> `pago` que ese pago ya escribió al crearse (`EscriturasDeCuentaCorrienteProveedor.
> ImputarMovimientoDePagoAsync`, el único `UPDATE` de esa tabla), el saldo del proveedor no cambia
> (el pago ya estaba contado). Si el pago no tenía proveedor (`categoria` distinta o
> `id_proveedor` nulo), el vínculo lo CONVIERTE: pasa a `categoria = proveedor` con el proveedor de
> la compra y recién ahí se escribe el `pago` (nunca se escribió antes), reduciendo el saldo por
> primera vez, con el punto de venta del propio pago o, si no tiene uno, el de la compra
> (`ValidarFormaPorTipo` exige punto de venta en cualquier movimiento que no sea `apertura`). Mismo
> orden de locks que el alta (compra `FOR SHARE` primero — nunca abre un ciclo contra la anulación,
> que toma el header EXCLUSIVO como su único lock) seguido del pago `FOR UPDATE`.

### Líneas por medio de pago: `items_pago` (E2-S1)

Un pago se puede cancelar con más de un medio (efectivo + transferencia). La hija `items_pago` lleva una
fila por medio. `pagos` ya no guarda el medio (E2-S3b eliminó `pagos.id_medio_pago`): el medio de cada
entrega vive solo acá, e `importe` de la cabecera es la suma de los ítems vivos.

```sql
items_pago (                 -- [operativa, hija de pagos: scope id_tenant únicamente]
    id_item_pago, id_tenant,
    id_pago integer NOT NULL,                -- FK compuesta (id_pago, id_tenant) → pagos (ak_pagos_id_pago_id_tenant), RESTRICT
    id_medio_pago integer NOT NULL,          -- FK compuesta (id_medio_pago, id_tenant) → medios_pago, RESTRICT
    importe numeric(14,2) NOT NULL,
    referencia text NULL,                    -- cupón / número de operación
    created_at, updated_at, deleted_at,

    CONSTRAINT ck_items_pago_importe_positivo CHECK (importe > 0),
    CONSTRAINT ck_items_pago_referencia_longitud
        CHECK (referencia IS NULL OR char_length(referencia) <= 100)
);
-- índices: ix_items_pago_tenant, ix_items_pago_pago (id_pago, id_tenant), ix_items_pago_medio_pago
```

> **Invariante de cabecera.** Si un pago tiene al menos un ítem vivo (`deleted_at IS NULL`), la suma
> de esos ítems es igual a `pagos.importe`. Lo sostiene el constraint trigger
> `tg_items_pago_suma_igual_importe` (`DEFERRABLE INITIALLY DEFERRED`, sobre `items_pago` por
> insert/update/delete) y `tg_pagos_importe_igual_suma_items` (sobre `pagos` cuando cambia
> `importe`). Es diferido para que la cabecera y sus ítems se escriban en cualquier orden dentro de
> una transacción; al comitear, una suma distinta sale como `23514` con el nombre del trigger como
> constraint. Un pago sin ítems vivos no se controla. La baja lógica de un pago da de baja sus ítems (el servicio
> marca el mismo `deleted_at` en la cabecera y en sus ítems vivos, en un solo guardado); el backfill copia
> el `deleted_at` del pago al ítem.
>
> **Escritura.** `ServicioDePagos` escribe los ítems del pago (uno por línea; el pago de personal
> también: UN pago con un ítem por línea de dinero, cuya `referencia` vive en el ítem) en la misma
> transacción que la cabecera de cada pago nuevo (alta del POS, alta administrativa, pagar una compra
> y pago de personal), y la edición lleva los ítems consigo en el mismo guardado. La migración `ItemsDePago` hizo el backfill de un ítem por pago existente, también
> los dados de baja (copian su `deleted_at`). Los dos `CHECK` y las FK son backstop: el servicio
> nunca los dispara.
>
> **Líneas (E2-S3a).** Desde esta rebanada el servicio recibe de 1 a 20 líneas `(medio, importe,
> referencia)` por pago (`ReglaDeLineasDePago`; un medio puede repetirse) y escribe un ítem por línea, en
> orden. `importe` es la suma de las líneas. La tesorería tiene un único movimiento por pago, por el total
> (también el pago de personal con varias líneas). Editar reemplaza el conjunto de líneas: los ítems vivos se reutilizan
> por posición (orden de id), los sobrantes se dan de baja lógica y las líneas nuevas se agregan, todo en
> el mismo guardado que la cabecera y bajo el lock `FOR UPDATE` del pago.
>
> **Compatibilidad de las rutas actuales.** `/api/gastos*` y `POST /api/compras/{id}/pagos` conservan su
> forma `{idMedioPago, importe}` y la mapean a UNA línea. Editar por ellas un pago con más de un ítem vivo
> responde `409 gasto_con_varios_medios` y no cambia nada; con un solo ítem conserva su `referencia` (salvo que la edición cambie el medio: entonces la
> referencia se limpia). Estas rutas redondean el importe a 2 decimales con el mismo criterio que
> aplicaba Postgres al persistir (`numeric(14,2)`, mitad lejos de cero): `10.005` se guarda como
> `10.01` y no responde `pago_linea_importe_invalido`, que es solo del contrato con líneas. Un importe
> que redondea a 0 o menos responde `400 gasto_importe_invalido`, y uno mayor que `999999999999.99`
> responde `400 valor_fuera_de_rango` (la traducción de `22003`), como antes. Un id inexistente
> responde `404` antes de validar el cuerpo.
>
> **Medio de compatibilidad (E2-S3b).** Los listados y respuestas que todavía muestran UN medio por pago
> (`idMedioPago` del historial, del detalle del turno y de las respuestas de alta, el nombre en el historial de
> administración, la clave `id_medio_pago` del payload de auditoría) lo derivan del **ítem vivo de menor id**
> del pago (el de la primera línea). Los listados `PagoListado` agregan además `lineas`
> (`[{idItem, idMedioPago, nombreMedioPago, importe, referencia}]`, en orden de id). La migración
> `QuitarMedioDeLaCabeceraDelPago` eliminó `pagos.id_medio_pago`, `fk_pagos_medio_pago` e `ix_pagos_medio_pago`; su
> `Down` los recrea y rellena la columna con ese mismo medio (o el del ítem de menor id si no quedan ítems vivos).
> Dar de baja un medio usado por un pago sigue bloqueado, ahora solo por la FK `fk_items_pago_medio_pago`
> (rótulo "gastos"). El pago de personal con varias líneas sigue siendo **un pago por línea de dinero**, cada uno con
> su único ítem (la referencia se repite en `pagos.detalle` y en el ítem) y su propio movimiento de tesorería: el POS
> instalado no lee `lineas`, y fusionarlas en un solo pago lo mostraría como una sola fila bajo el primer medio. La
> fusión (un pago con un ítem por línea y un egreso por el total) queda para después del release del POS (E2-P).
> El desglose lista una línea por ítem, con `idGasto` por pago e `idItem` por ítem. El historial de administración
> (`/api/gastos/administracion` y `/{id}`) agrega `lineas` con la misma forma que `PagoListado`.

### Aplicaciones de pagos a compras: `aplicaciones_pago` (E3-S1a)

Un pago se aplica por partes a una o más compras, y una compra recibe partes de varios pagos; la parte de un pago
que no está aplicada es un crédito a favor del proveedor. La hija `aplicaciones_pago` lleva una fila por par
pago-compra. S1a trajo el esquema y el backfill; desde S1b los escritores de vínculos la escriben (ver «Doble escritura
y liberación») y los lectores siguen derivando lo pagado de `pagos.id_comprobante_compra`.

```sql
aplicaciones_pago (          -- [operativa, hija de pagos: scope id_tenant únicamente]
    id_aplicacion_pago, id_tenant,
    id_pago integer NOT NULL,                -- FK compuesta (id_pago, id_tenant) → pagos (ak_pagos_id_pago_id_tenant), RESTRICT
    id_comprobante_compra integer NOT NULL,  -- FK compuesta (id_comprobante_compra, id_tenant) → comprobantes_compra, RESTRICT
    importe numeric(14,2) NOT NULL,
    origen origen_aplicacion_pago NOT NULL,  -- enum: pago_de_compra | aplicacion | migracion
    id_empleado integer NOT NULL,            -- FK simple a usuarios (quién aplicó), igual que pagos.id_empleado
    fecha timestamptz NOT NULL,
    created_at, updated_at, deleted_at,

    CONSTRAINT ck_aplicaciones_pago_importe_positivo CHECK (importe > 0)
);
-- único parcial: ux_aplicaciones_pago_pago_compra (id_pago, id_comprobante_compra, id_tenant) WHERE deleted_at IS NULL
-- índices: ix_aplicaciones_pago_tenant, ix_aplicaciones_pago_pago (id_pago, id_tenant),
--          ix_aplicaciones_pago_compra (id_comprobante_compra, id_tenant), ix_aplicaciones_pago_empleado
```

> **Invariantes** (constraint triggers `DEFERRABLE INITIALLY DEFERRED`; fallan al comitear como `23514` con el nombre
> del trigger como constraint, que `ManejadorDeErrores` traduce a `409`). Solo cuentan las aplicaciones vivas:
>
> - `tg_aplicaciones_pago_no_excede_pago` (`409 aplicacion_excede_pago`): la suma de las aplicaciones de un pago no
>   supera `pagos.importe`, y un pago dado de baja no tiene aplicaciones vivas. Dispara en insert/update/delete de
>   `aplicaciones_pago` y en el update de `importe`/`deleted_at` de `pagos` (`tg_pagos_cubren_aplicaciones`).
> - `tg_aplicaciones_pago_no_excede_compra` (`409 aplicacion_excede_compra`): la suma de las aplicaciones de una compra
>   no supera `comprobantes_compra.total`, la compra está `confirmada`, y cada pago aplicado es de categoría `proveedor`
>   y del proveedor de la compra. La empresa NO es una condición desde la migración `AplicacionesEntreEmpresas` (la
>   creación original la exigía): un pago puede aplicarse a una compra de otra empresa del mismo tenant, porque antes
>   de las aplicaciones ese vínculo ya contaba como pagado. Dispara además en el update de `total`/`estado` de la compra
>   (`tg_comprobantes_compra_admiten_aplicaciones`) y en el de `id_proveedor`/`categoria`/`id_empresa` del pago
>   (`tg_pagos_coherentes_con_aplicaciones`).
> - El `23505` de `ux_aplicaciones_pago_pago_compra` sale como `409 aplicacion_duplicada` y el `23514` de la CHECK como
>   `400 aplicacion_importe_invalido`.
>
> Los triggers leen el estado de la transacción que comitea: no serializan dos transacciones concurrentes. Los escritores
> de S1b tienen que tomar los locks de la compra y del pago antes de aplicar; el trigger es solo el backstop.
>
> **Backfill** (migración `AplicacionesDePago`, bajo `SET LOCAL app.acceso = 'plataforma'`). Para los pagos vivos de
> categoría `proveedor` con `id_comprobante_compra`, a una compra `confirmada` del mismo proveedor y la misma empresa:
> por compra, en orden `(pagos.fecha, id_pago)`, cada pago aplica `greatest(0, least(importe, total - suma de los importes
> anteriores))`; las filas con cero no se crean, el `origen` es `migracion`, y `fecha` e `id_empleado` son los del pago.
> Lo que excede el total de la compra, los pagos ligados a compras anuladas y los pagos que no cumplen las condiciones
> de arriba quedan sin aplicar (crédito). Es idempotente y los triggers se crean después.
>
> **Vínculos entre empresas** (migración `AplicacionesEntreEmpresas`). El backfill de arriba salteaba los pagos ligados a
> una compra de otra empresa, que antes contaban como pagados. La migración reemplaza
> `fn_aplicaciones_pago_exigir_compra` sin la condición de empresa (`Down` la restituye y da de baja las aplicaciones
> entre empresas) y completa esas aplicaciones: por compra, en orden `(pagos.fecha, id_pago)`, cada pago aplica
> `greatest(0, least(lo que le queda disponible al pago, total - aplicaciones vivas de la compra - suma de los importes
> de los candidatos anteriores))`, con `origen = 'migracion'`, bajo `SET LOCAL app.acceso = 'plataforma'` e idempotente
> (un par pago-compra con aplicación viva no es candidato). Los vínculos NUEVOS siguen exigiendo la misma empresa:
> el alta del POS, el alta administrativa y vincular responden `400 compra_de_otra_empresa`.
>
> **Doble escritura y liberación (E3-S1b).** Los lectores derivaban lo pagado de `pagos.id_comprobante_compra` hasta S2
> (ver «Lectores» abajo), así que cada escritor que fija ese vínculo escribe además la aplicación, y la anulación y la baja la liberan:
>
> - Alta del POS, alta administrativa y pagar compra (origen `pago_de_compra`), y vincular (origen `aplicacion`): el
>   `importe` es `min(pagos.importe, comprobantes_compra.total - suma de las aplicaciones vivas de la compra)`, leído bajo
>   el lock `FOR UPDATE` de la compra (antes `FOR SHARE` en el alta y en vincular). Si el tope es 0 no se escribe nada:
>   el vínculo queda y el excedente del pago queda disponible; si la compra libera lugar después, se aplica de forma
>   explícita (ver «Sin reaplicación automática»).
> - Un pago no elegible para el trigger de la compra (otro proveedor o no `proveedor`) conserva el vínculo heredado sin
>   aplicación. La empresa no es una condición: un vínculo heredado entre empresas se aplica igual (alta nueva entre
>   empresas: `400 compra_de_otra_empresa`).
> - Orden de locks de los escritores de pagos: turno → compras `FOR UPDATE` en id ascendente → pago → proveedor → tesorería.
> - La anulación de la compra da de baja (`deleted_at`) todas sus aplicaciones vivas en la misma transacción, bajo el
>   header exclusivo; la baja del pago hace lo mismo con las suyas, tomando antes TODAS sus compras (las aplicadas y la
>   del vínculo) `FOR UPDATE` en id ascendente y después el pago. El pago y sus movimientos no se tocan: lo liberado queda
>   como crédito.
> - **Sin reaplicación automática.** Lo que libera la baja de un pago, su edición a la baja o una desaplicación vuelve al
>   saldo de la compra y nada se aplica solo a otros pagos: los demás pagos conservan su disponible y el administrador lo
>   aplica de forma explícita con `POST /api/pagos/{id}/aplicaciones` (ver E3-S3). Así ninguna transacción lockea el pago
>   de otro y una desaplicación hecha a propósito no se deshace sola.
> - Editar un pago con aplicaciones no se restringe en el importe. Si el importe nuevo queda por debajo de lo aplicado, las
>   aplicaciones vivas se recortan automáticamente de la más nueva a la más vieja (`fecha` y `id` descendentes): se dan de
>   baja las que entran enteras y se achica la última. Si el importe sube y el pago es elegible (categoría `proveedor`,
>   proveedor de la compra del vínculo, compra confirmada), la aplicación al vínculo crece hasta el aumento, el disponible y
>   el lugar de la compra, y solo si esa aplicación sigue viva: la edición nunca crea una aplicación (no deshace una
>   desaplicación); el aumento queda disponible para la ruta explícita. La edición toma todas las compras del pago (aplicadas y vínculo) `FOR UPDATE` en id ascendente antes
>   que el pago (los conjuntos se pre-leen sin lock y se revalidan bajo el lock del pago: si cambiaron, `409
>   pago_cambio_concurrente`, sin reintento). Si la compra está anulada la edición sigue siendo válida y no toca
>   aplicaciones. Cambiar el proveedor o la categoría de un pago con CUALQUIER aplicación viva (con o sin vínculo) es
>   `400 proveedor_no_coincide_con_la_compra` / `gasto_de_compra_debe_ser_de_proveedor`; la empresa no es editable.
> - Los escritores de S1b no agregan códigos de auditoría: el alta, la edición y la baja del pago ya se auditan y la aplicación es un efecto
>   interno de esas operaciones.
>
> **Lectores (E3-S2).** Lo pagado de una compra (`LectorDePagadoPorCompra`) es la suma de sus aplicaciones vivas; ya no
> suma `pagos.id_comprobante_compra` ni los ajustes imputados (el único ajuste imputado a una compra es el
> contramovimiento de su anulación, y la anulación libera las aplicaciones). Lo aplicado de un pago es la suma de las
> suyas y su disponible es `pagos.importe - aplicado - redondeo` (`LectorDeDisponiblePorPago`; el redondeo es la suma de los
> movimientos `redondeo` con `id_pago`, ver E4 abajo).
> Se exponen, solo como campos nuevos del JSON: `aplicado`, `disponible` (null si el pago no es de proveedor) y
> `aplicaciones[]` en `PagoDeAdministracionListado`; `disponibleDelPago` en las filas `pago` del estado de cuenta del
> proveedor, y `saldoAFavor` (`max(0, -saldo)`) y `disponibleEnPagos` en su encabezado. `GET /api/proveedores/{id}/saldo`
> y los listados del POS no cambian de forma.
>
> Cambios de comportamiento: (1) una compra con pagos sobre-imputados de forma heredada (un pago editado hacia arriba, o
> dos pagos que superan el total) muestra `pagado` = total y no la suma cruda de los pagos; el saldo pendiente no cambia
> porque ya se acotaba en 0. (2) El detalle de una compra anulada muestra `pagado` 0, porque sus aplicaciones están
> liberadas. Los vínculos heredados entre empresas siguen contando como pagados (migración `AplicacionesEntreEmpresas`).
>
> **Aplicar, desaplicar y adopción (E3-S3).** Sin cambios de esquema.
>
> - `POST /api/pagos/{id}/aplicaciones` aplica el disponible de un pago a 1..20 compras confirmadas de UN mismo proveedor
>   (origen `aplicacion`, usuario y fecha de ahora); aplicar de nuevo al mismo par agranda la única aplicación viva.
>   `DELETE /api/pagos/{id}/aplicaciones/{idAplicacion}` da de baja la aplicación: el lugar vuelve al saldo de la compra y
>   no se reaplica a ningún otro pago. Ambos apilan `GestionDeCatalogo` y auditan `pago.aplicacion` /
>   `pago.desaplicacion` (entidad `pago`; el payload lleva categoría, proveedor, vínculo, aplicado, disponible y las
>   aplicaciones vivas, antes y después).
> - Orden de locks: compras `FOR UPDATE` en id ascendente → pago `FOR UPDATE` → fila del proveedor (solo en la
>   adopción). No toca la tesorería: la plata del pago ya salió. Las lecturas del total, del estado, del disponible y de lo
>   pagado nacen después de los locks; el escritor no reintenta (`FabricaDeEstrategiaSinReintento`).
> - Códigos: `404` pago, aplicación (que debe ser de ese pago y estar viva) o compra inexistentes, antes de validar el
>   cuerpo; `400 aplicacion_sin_compras`, `aplicacion_demasiadas_compras`, `aplicacion_compra_repetida`,
>   `aplicacion_importe_invalido` (positivo, hasta 2 decimales), `compras_de_distintos_proveedores`; `409
>   compra_no_confirmada` / `compra_anulada`, `compra_de_otro_proveedor`, `pago_no_aplicable` (sueldos),
>   `gasto_de_pago_de_personal`, `aplicacion_excede_disponible` (la suma supera el disponible del pago) y
>   `aplicacion_excede_saldo` (el importe supera el saldo pendiente de su compra). Toda compra de una aplicación NUEVA
>   tiene que ser de la empresa del pago: `400 compra_de_otra_empresa` (el mismo código que vincular).
> - **Adopción.** Un pago sin proveedor (categoría distinta de `proveedor`, o `proveedor` sin proveedor asignado) pasa,
>   en su primera aplicación, a categoría `proveedor` con el proveedor de las compras, y escribe el movimiento `pago` de la
>   cuenta corriente del proveedor por su importe COMPLETO con la fecha de ahora (aunque se aplique solo una parte; el
>   resto queda como crédito a favor). `pagos.id_comprobante_compra` pasa SIEMPRE a la primera compra del pedido («compra de
>   origen») y el movimiento `pago` se imputa a ella: el pago no tenía proveedor, así que un vínculo anterior era a una
>   compra que no le corresponde. Un pago con otro proveedor es `409 compra_de_otro_proveedor`. El proveedor adoptado se conserva
>   al desaplicar.
> - Desaplicar no toca `pagos.id_comprobante_compra`: es la «compra de origen» (historia) y la imputación del movimiento
>   `pago` de la cuenta corriente; los ajustes de un pago sin punto de venta propio toman el de esa compra. El vínculo no
>   genera aplicación por sí mismo, así que la desaplicación libera de verdad.
> - `vincular-compra` (`/api/gastos/{id}` y `/api/pagos/{id}`) es una aplicación de `min(disponible, saldo)` a esa compra:
>   conserva `gasto_ya_vinculado` y `compra_de_otra_empresa` (los vínculos NUEVOS siguen exigiendo la misma empresa) y suma
>   `409 pago_sin_disponible` cuando el pago no tiene nada sin aplicar. Su conversión de un pago sin proveedor es la misma
>   adopción.
> - `GET /api/compras/{id}/pagos-aplicables` y `GET /api/proveedores/{id}/pagos-disponibles` devuelven
>   `{pagos: [{idPago, fecha, concepto, importe, disponible, idProveedor}]}` ordenados por fecha: los del proveedor con
>   disponible mayor a 0 (filtrado en SQL) y, solo en la de la compra, los pagos sin proveedor (sin sueldos ni pagos de
>   personal), que `idProveedor: null` marca como adoptables. La lectura de la compra lista solo pagos de la empresa de la
>   compra, porque aplicar lo exige; la del proveedor lista los de todas sus empresas. Ambas devuelven como máximo 200
>   pagos, los más antiguos primero: el resto se alcanza aplicando y volviendo a consultar.
>
> **Pagar una compra con pagos a cuenta y con más de lo que se debe (E3-S4).** `POST /api/pagos/de-compra/{idCompra}` (contrato
> nuevo; `POST /api/compras/{id}/pagos` y `/api/gastos` conservan `409 pago_excede_saldo_pendiente`):
> - Cuerpo `{fecha, concepto?, lineas: [..0..20], aplicaciones?: [{idPago, importe}..0..20]}`; al menos una de las dos listas
>   no vacía (`400 pago_sin_lineas`). Primero se aplica lo existente y después se crea el pago nuevo por el TOTAL de sus líneas.
> - Cada `aplicaciones[i].importe` ≤ disponible de ese pago (`409 aplicacion_excede_disponible`) y la suma ≤ saldo de la compra
>   (`409 aplicacion_excede_saldo`); un `idPago` repetido es `400 aplicacion_pago_repetido`, un importe no positivo o con más de
>   dos decimales `400 aplicacion_importe_invalido`, más de 20 `400 aplicacion_demasiados_pagos`. Valen las reglas de adopción y
>   de empresa de aplicar (`pago_no_aplicable`, `compra_de_otro_proveedor`, `compra_de_otra_empresa`). Una compra sin saldo
>   pendiente sigue siendo `409 compra_sin_saldo_pendiente`.
> - La aplicación del pago nuevo es `min(total de las líneas, saldo que quede después de las aplicaciones)`; el resto queda
>   como `disponible` del propio pago (crédito a favor). El movimiento de cuenta corriente y el egreso de tesorería son los del
>   pago completo, así que el saldo del proveedor puede quedar negativo (a favor).
> - Respuesta `{pago | null, pagado, saldoPendiente, aplicaciones: [{idAplicacion, idPago, importe, fecha}], disponibleDelPago | null}`:
>   `pago` es `null` (y el status 200 en vez de 201) cuando no hubo líneas; `aplicaciones` son todas las vivas de la compra.
> - `fecha` se valida siempre (no futura) y es la fecha de negocio del pago nuevo y de las aplicaciones nuevas del pedido;
>   `concepto` sin `lineas` es `400 concepto_sin_pago_nuevo`. Si lo existente salda la compra y vienen líneas, `409
>   compra_sin_saldo_pendiente` sin escribir. Compra y cada `idPago` inexistentes son 404 antes de validar el cuerpo.
> - `cerrarEnCero` (booleano, por defecto `false`): ver «Redondeo» abajo.
> - Orden de locks: compra `FOR UPDATE` → pagos existentes `FOR UPDATE` en id ascendente → por cada pago existente adoptado
>   (sin proveedor), la fila del proveedor, dentro de ese paso y antes del insert del pago nuevo → insert del pago nuevo (sin
>   lock) → fila del proveedor del pago nuevo (ya tomada si hubo adopción) → advisory de tesorería, último. Forma (b) de
>   `ef-retry-safe-writes`.

> **Redondeo: cerrar en cero (E4).** Pagar 1500 una compra de 1503,50 o de 1430,25 puede dejar la cuenta sin saldo, a mano y hasta
> una tolerancia. Parámetro `tolerancia_redondeo_pago_proveedor` (§9; decimal, default `500`, de 0 a 999999999999.99 con hasta 2
> decimales, `400 parametro_tolerancia_redondeo_invalida`), resuelto punto de venta de la compra > empresa > default; no es la
> `tolerancia_pago` de ventas. La diferencia vive en movimientos `redondeo` de la cuenta del proveedor (§8b), no en columnas:
> - **Pagado de una compra** = Σ aplicaciones vivas **− Σ importe de sus `redondeo`** (negativos, así que suman; su reversa resta).
>   Una compra con redondeo vigente queda Pagada (saldo 0).
> - **Disponible de un pago** = `importe − aplicado` − Σ importe de sus movimientos `redondeo` (positivos). Un pago con redondeo vigente queda
>   con disponible 0. Cuenta también en la lista de pagos aplicables, aplicar, vincular y el crédito del proveedor.
> - `cerrarEnCero: true` en `POST /api/pagos/de-compra/{idCompra}`: `dif = saldo − (crédito aplicado + total de las líneas)`. `dif = 0`
>   no escribe nada; `0 < dif ≤ tolerancia` escribe un redondeo del lado de la compra por `dif` (queda Pagada); `−tolerancia ≤ dif < 0`
>   (pagó de más) convierte el excedente del pago nuevo en un redondeo del lado del pago (disponible 0, sin crédito a favor);
>   `|dif| > tolerancia` es `409 redondeo_excede_tolerancia` sin escribir nada. La respuesta gana `redondeo` (= `dif` con signo, `null`
>   sin cierre). Con `false` el comportamiento de E3-S4 no cambia.
> - `POST /api/compras/{id}/ajuste-de-redondeo` (sin cuerpo): compra confirmada con `0 < saldo ≤ tolerancia` → redondeo del lado de la
>   compra por todo el saldo (`{idCompra, redondeo, pagado, saldoPendiente}`); sin saldo `409 compra_sin_saldo_pendiente`, más que
>   la tolerancia `409 redondeo_excede_tolerancia`, anulada o borrador el 409 de siempre; compra inexistente 404. Locks: compra
>   `FOR UPDATE` → fila del proveedor.
> - `POST /api/pagos/{id}/ajuste-de-redondeo` (sin cuerpo): pago de proveedor con `0 < disponible ≤ tolerancia` → redondeo del lado
>   del pago por todo el disponible (`{idPago, redondeo, disponible: 0}`); `409 pago_sin_proveedor`, `409 pago_sin_disponible`, `409
>   redondeo_excede_tolerancia`; pago inexistente 404. Locks: pago `FOR UPDATE` → fila del proveedor.
> - `GET /api/compras/{id}/pagos-aplicables` gana `toleranciaRedondeo` (la vigente para esa compra) para que la UI sepa si ofrece
>   cerrar en cero; la lista de un proveedor lo deja `null`.
> - Los tres caminos apilan `GestionDeCatalogo`, usan `FabricaDeEstrategiaSinReintento` y auditan `compra.redondeo` (entidad
>   `comprobante_compra`) o `pago.redondeo` (entidad `pago`), 25 → 27 códigos del catálogo.
> - **Compensaciones** (el redondeo perdona algo que depende de lo aplicado, así que se revierte con un `redondeo` de signo opuesto
>   ANTES de que esa dependencia cambie): anular la compra revierte su redondeo (si no, el contramovimiento dejaría el saldo del
>   proveedor a favor por lo perdonado); desaplicar, dar de baja un pago o recortarle una aplicación (edición que baja el importe)
>   revierte el redondeo de cada compra a la que ese pago aplica menos que antes (la compra vuelve a deber lo que le falta); la baja o
>   un cambio de importe de un pago revierte su redondeo del lado del pago (el disponible vuelve a ser `importe − aplicado`); editar
>   solo concepto, categoría o medios sin cambiar el importe no revierte nada. Aplicar o vincular sobre una compra con redondeo
>   vigente no tiene lugar (su saldo es 0), y crecer una aplicación por edición descuenta lo perdonado.

### Edición de una compra confirmada (E6)

> **Sin cambio de esquema.** `PUT /api/compras/{id}` (`GestionDeCatalogo`) despacha por estado: `borrador` reemplaza su set completo (sin
> cambios); `confirmada` se edita por DIFERENCIA (`ServicioDeCompras.ActualizarConfirmadaAsync`, `FabricaDeEstrategiaSinReintento`);
> `anulada` es `409 compra_no_editable`; inexistente `404`, antes de validar el cuerpo. El cuerpo es el mismo de siempre; encabezado,
> líneas, alícuotas y percepciones se reemplazan por EF (`RemoveRange`/`AddRange`, como el borrador) y `fecha_recepcion`, `id_empleado`
> y `origen` conservan su valor. Una confirmada exige número y fecha de comprobante y al menos una línea (`400
> compra_numero_externo_requerido` / `compra_incompleta_para_confirmar` / `compra_sin_items`). La unicidad del número sigue siendo
> `ux_comprobantes_compra_numero_externo` (`409 compra_duplicada`).
> - **Stock:** por clave `(artículo, punto de venta, lote)` se compara lo que el libro de la compra ya suma (confirmación y ediciones
>   previas, neteado por clave) contra lo que la compra editada tendrá; cada clave con diferencia ≠ 0 escribe UN movimiento
>   `motivo = compra` con la diferencia con signo (`observaciones = 'Edición de la compra #id'`) y el upsert de `stock` y `stock_lotes`.
>   Cambiar el punto de venta saca del viejo y pone en el nuevo. Si una diferencia negativa deja un lote o el agregado en negativo:
>   `409 compra_edicion_stock_negativo` (nombra artículo y lote) y la transacción entera se deshace.
> - **Lotes:** la línea que conserva artículo, código de lote y vencimiento conserva su `id_lote` y no se vuelve a verificar contra «hoy» SOLO por lo ya recibido (si la suma de cantidades de esa clave crece, el vencimiento se verifica como en la confirmación); la línea que no cambia conserva su `precio_sugerido` guardado; los lotes se bloquean ascendentes por (artículo, código resuelto, ordinal), igual que la confirmación;
>   las nuevas o cambiadas se resuelven con las exigencias de la confirmación (`lote_requerido`, `lote_vencido_en_recepcion`).
> - **Cuenta del proveedor:** la diferencia contra la suma de los asientos `compra` de esa compra (por proveedor) escribe un asiento
>   `compra` por la diferencia (`detalle = 'Edición de la compra #id: $A → $B'`) y actualiza `proveedores.saldo`. Si cambia el proveedor,
>   el anterior recibe la reversa de lo suyo y el nuevo el total. La suma de los asientos `compra` de una compra sigue siendo su total,
>   así que la anulación (que revierte esa suma) no cambia. Una compra anterior al libro de proveedores (sin asiento `compra`) no puede
>   cambiar total ni proveedor: `409 compra_edicion_sin_asiento_de_cuenta`. El estado de cuenta muestra `saldoPendienteDeLaCompra` solo en
>   el asiento `compra` más nuevo de cada compra, del proveedor actual de la compra.
> - **Órdenes de compra:** se bloquean `FOR UPDATE`, ascendentes por id, la que la compra deja y la que toma, y las dos se re-proyectan con
>   las líneas ya escritas. La ligadura de la orden nueva (existe, no anulada, mismo proveedor y punto de venta) solo se verifica si cambia
>   la orden, el proveedor o el punto de venta.
> - **Orden de locks:** membresía de familias (compartido) → encabezado `FOR UPDATE WHERE estado='confirmada'` (0 filas: `404` o `409
>   compra_cambio_concurrente`) → órdenes → lotes → stock (agregado y después lotes) → escritura EF → re-proyección → proveedores (último).
> - **Auditoría:** `compra.edicion` (entidad `comprobante_compra`, catálogo 27 → 28): antes y después del encabezado y las líneas, y en el
>   valor nuevo las diferencias de stock y de cuenta asentadas.
> - **Anulación:** revierte el stock NETO por `(artículo, punto de venta, lote)` (una clave que ya netea a cero no se revierte), porque
>   revertir cada asiento de una compra editada rechazaría con un falso `compra_anulacion_stock_negativo`.
> - **TEMPORAL (reemplazado por E6-S3):** una edición que baja el total por debajo de lo pagado, que cambia el total de una compra con
>   redondeo perdonado o que cambia proveedor, punto de venta o empresa de una compra con pagos aplicados, redondeo o algún pago vivo vinculado (`pagos.id_comprobante_compra`) responde `409
>   compra_edicion_con_aplicaciones`. Los costos del artículo (`costo_nominal`) y los códigos de proveedor todavía NO se vuelven a escribir
>   al editar (E6-S4).

### Órdenes de compra (Etapa 16)

La intención colocada sobre un proveedor ANTES de que exista un `comprobantes_compra`
(doc-11:268-285). Greenfield: el legacy nunca tuvo este documento (doc-01:203-208).

```sql
ordenes_compra (               -- [operativa]
    id_orden_compra, id_tenant, id_punto_venta,  -- a qué local llega la mercadería
    id_proveedor,
    id_empleado,                                 -- quién la creó
    numero                bigint      NULL,       -- correlativo propio por PV, serie 'OC';
                                                  -- se asigna únicamente al ENVIAR
    fecha_emision          timestamptz NOT NULL,   -- IRelojDelSistema, sin DEFAULT now()
    fecha_envio            timestamptz NULL,       -- par de `numero`
    fecha_esperada         date        NULL,       -- ETA declarada
    fecha_cierre           timestamptz NULL,       -- solo con estado = 'cerrada'
    id_empleado_cierre     integer     NULL,       -- NOT NULL ⇒ cierre MANUAL, nunca revertido
    observaciones,
    estado estado_orden_compra NOT NULL            -- enum: borrador | enviada | recibida_parcial
                                                    --     | cerrada | anulada
);

items_orden_compra (
    id_item, id_orden_compra, orden,
    id_articulo,                       -- NOT NULL: sin artículo no puede recibirse contra stock
    descripcion text,                  -- snapshot
    cantidad_pedida         numeric(12,3),
    costo_unitario_estimado numeric(14,4) NULL     -- intención, jamás un hecho; NULL = no cotizado
    -- SIN cantidad_recibida: siempre se deriva del libro de recepción, nunca se cachea.
);
```

**Estado (Etapa 16, DB CHANGE GATE ejercido y aprobado 2026-08-18): implementada — etapa
completa (PRs #140-#145).** Creadas por la migración `OrdenesDeCompraEtapa16`, aditiva pura, cero
data statements. El circuito completo vive: borrador/envío con serie propia 'OC' (slice 2),
ligadura y proyección de estado desde el libro (slice 3), cierre manual y anulación gobernada
por el libro (slice 4), read model con cobertura por artículo y desvío informativo (slice 5),
y las pantallas web con el botón de generar OC desde Reposición (slice 6). `EntidadBase`: **SÍ** en las dos tablas — a diferencia de
`movimientos_cuenta_corriente_proveedor`/`auditoria` (ledgers append-only, etapas 14/15), una OC
es mutable durante `borrador` (replace-set completo bajo `SELECT … FOR UPDATE`) y se edita de
nuevo en `enviar`/`cerrar`/`anular` — hereda el filtro de tenant estándar y `EstamparTenant()`
como `ComprobanteCompra`, sin filtro clonado. `ux_ordenes_compra_numero` es **UNIQUE PARCIAL**
`WHERE numero IS NOT NULL` — a diferencia de `ux_comprobantes_compra_numero_externo`, NO excluye
`anulada`: una serie propia jamás reusa números (los huecos son legítimos), el `numero_externo`
es del proveedor y sí puede repetirse tras anular. `estado_orden_compra` se registra únicamente
vía `npgsql.MapEnum<EstadoOrdenCompra>` en `DependencyInjection.cs`/`WaysDbContextFactory.cs` —
nunca también con `HasPostgresEnum`. `ManejadorDeErrores.cs` gana 6 ramas exactas: 2 `23505`
(`ux_ordenes_compra_numero` → `numero_de_orden_duplicado`, **tercera ocurrencia** del ordering
trap, resuelta por encima de `ClasificarUnicidad`; `ux_items_orden_compra_orden` →
`orden_de_item_duplicado`) + 4 `23514` (las dos CHECKs de cada tabla). El proyector puro
(`ProyectorDeEstadoDeOrden`, `Ways.Domain.Compras`) y el motor de escritura
(`EscriturasDeOrdenDeCompra`) son slice 3; `ServicioDeOrdenesDeCompra` (draft CRUD, `enviar`,
`cerrar`, `anular`, lectura) es slices 2-5.
