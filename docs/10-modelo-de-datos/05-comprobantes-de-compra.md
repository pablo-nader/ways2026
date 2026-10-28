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
    id_medio_pago integer NOT NULL,          -- con qué se pagó (efectivo de caja, transferencia…)
    numero_factura text NULL, importe,
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
