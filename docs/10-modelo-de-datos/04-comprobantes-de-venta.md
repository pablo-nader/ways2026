> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 4. Comprobantes de venta

## 4. Comprobantes de venta

```sql
comprobantes_venta (          -- [operativa]
    id_comprobante_venta,
    id_tipo_comprobante integer NOT NULL,    -- FB, TX, NCX…
    numero              bigint  NOT NULL,    -- correlativo de numeraciones_comprobante (doc 09)
    -- visible: "PPPP-NNNNNNNN" (punto de venta + numero)
    fecha               timestamptz NOT NULL,
    id_punto_venta, id_turno_caja integer NULL,
    id_empleado, id_cliente,
    id_comprobante_asociado integer NULL,    -- NC/ND → el comprobante que corrigen
    -- totales (redundantes con items, verificados por dominio)
    subtotal, descuento_total,
    descuento_manual_total numeric(14,2) NOT NULL DEFAULT 0,  -- Σ de los descuentos manuales de línea
    recargo_manual_total   numeric(14,2) NOT NULL DEFAULT 0,  -- Σ de los recargos manuales de línea
    total,                                   -- subtotal − descuento_total − descuento_manual_total + recargo_manual_total
    neto_gravado NULL, iva_total NULL,       -- solo si discrimina_iva
    -- entrega
    direccion_entrega text NULL, observaciones text NULL,
    estado  estado_comprobante NOT NULL,     -- enum: emitido | anulado
    -- Etapa 19a: aditivas, las cuatro NULL ⇒ "no es un comprobante fiscal" (TX/NCX/TXR/RC)
    cae                    varchar(14) NULL,
    cae_vencimiento        date        NULL,
    resultado_fiscal       resultado_fiscal NULL,  -- enum: pendiente|aprobado|aprobado_con_observaciones|rechazado
    observaciones_fiscales jsonb       NULL,        -- [{ "codigo": int, "mensaje": string }]
    datos_legales_fiscales jsonb       NULL,        -- solo espejo fiscal: emisor + receptor congelados al emitir
    -- Facturación por medio de pago: espejo fiscal y bandera del cajero
    id_venta_origen        integer     NULL,        -- espejo fiscal → la venta (TX) que factura
    factura_forzada        boolean     NOT NULL DEFAULT false, -- el cajero pidió facturar este ticket
    facturacion_decidida_en timestamptz NULL,      -- NULL = la decisión está debida; con valor = ya decidida (marca de idempotencia)
    facturacion_intentos    integer     NOT NULL DEFAULT 0, -- intentos fallidos de decidir esta venta (solo crece)
    facturacion_proximo_intento_en timestamptz NULL         -- el barrido no la reintenta antes de este instante
);
-- INDEX  (id_punto_venta, id_comprobante_venta) WHERE facturacion_decidida_en IS NULL AND id_venta_origen IS NULL
--                                                                            ix_comprobantes_venta_decision_debida
-- UNIQUE (id_punto_venta, id_tipo_comprobante, numero)
-- FK (id_venta_origen, id_tenant) → comprobantes_venta ON DELETE RESTRICT
-- INDEX  (id_venta_origen, id_tenant) WHERE id_venta_origen IS NOT NULL         ix_comprobantes_venta_venta_origen
--                                                                            ⇒ soporte de la FK (el único de abajo es parcial)
-- UNIQUE (id_venta_origen, id_tenant) WHERE id_venta_origen IS NOT NULL AND id_comprobante_asociado IS NULL
--                                                                            ⇒ a lo sumo un espejo de factura por venta
-- UNIQUE (id_comprobante_asociado, id_tenant) WHERE id_venta_origen IS NOT NULL AND id_comprobante_asociado IS NOT NULL
--                                                                            ux_comprobantes_venta_nota_credito_espejo
--       ⇒ el espejo de una nota de crédito (NC A/B/C) lleva id_venta_origen = la venta e id_comprobante_asociado = el
--         espejo de la factura que anula; una factura espejo tiene a lo sumo una
-- CHECK id_venta_origen IS NULL OR resultado_fiscal IS NOT NULL  ⇒ un espejo es siempre fiscal
-- CHECK id_venta_origen IS NULL OR id_turno_caja IS NULL         ⇒ un espejo no pertenece a ningún turno

items_comprobante_venta (
    id_item, id_comprobante_venta, orden,
    id_articulo integer NULL,                -- NULL solo en líneas de concepto libre (hoy, el "Envío": ver abajo)
    -- snapshot: el ticket se reimprime igual aunque el catálogo cambie
    descripcion text, codigo_barra text NULL,
    id_area integer NULL,                    -- el del artículo; NULL solo en líneas de concepto libre sin artículo (Envío)
    cantidad        numeric(12,3),
    precio_unitario numeric(14,2),           -- de la lista aplicada, ya resuelto
    id_lista_precio integer,                 -- con qué lista se vendió
    id_oferta       integer NULL,            -- si una oferta tocó esta línea
    id_combo        integer NULL,            -- combo que consumió unidades de la línea (FK compuesta, Restrict)
    descuento       numeric(14,2) NOT NULL DEFAULT 0,   -- importe descontado en la línea (oferta + parte del combo)
    ajuste_manual_porcentaje numeric(5,2) NULL,         -- con signo: negativo = descuento, positivo = recargo
    ajuste_manual   numeric(14,2) NOT NULL DEFAULT 0,   -- monto con signo del ajuste, sobre el neto de la línea
    id_alicuota_iva integer, porcentaje_iva numeric(5,2),  -- snapshot
    total           numeric(14,2)            -- cantidad × precio − descuento + ajuste_manual
);

cobros (                              -- antes pagos_comprobante (E7-S1)
    id_cobro, id_comprobante_venta,
    id_medio_pago, importe numeric(14,2),
    referencia text NULL,                    -- cupón, nro de operación
    vuelto numeric(14,2) NOT NULL DEFAULT 0, -- solo medios que admiten vuelto
    id_cobro_mercadopago integer NULL        -- el cobro aprobado de Mercado Pago que pagó este medio (§7); FK compuesta (id, id_tenant)
);
```

**Pago cobrado con Mercado Pago (E3-T2).** Un medio con `proveedor` (§1) se cobra con una orden del proveedor: su pago en la venta
tiene que traer `id_cobro_mercadopago` (400 `cobro_mercadopago_requerido`) y un medio sin proveedor no lo admite (400
`cobro_mercadopago_en_medio_sin_proveedor`). Solo se admite en una venta con número preasignado (400
`cobro_mercadopago_sin_numero_preasignado`), sin `referencia` propia (400 `cobro_mercadopago_referencia_no_admitida`: la fija el
servidor con el id del pago de Mercado Pago) y sin repetir el cobro en dos pagos (400 `cobro_mercadopago_repetido`). En una
devolución (nota de crédito) el medio con proveedor es solo el medio de devolución y no lleva cobro (400
`cobro_mercadopago_no_admitido_en_devolucion` si lo trae). La emisión toma `FOR UPDATE` del cobro después del turno y antes del
comprobante, lo lee una vez bajo ese lock y exige que sea de la misma venta (punto de venta, número y medio; si no, 409
`cobro_mercadopago_de_otra_venta`), esté `aprobado` (409 `cobro_mercadopago_no_aprobado`), no esté ligado a otra venta (409
`cobro_mercadopago_ya_usado`) y haya cobrado exactamente el importe del pago (409 `cobro_mercadopago_importe_distinto`); un cobro
inexistente es 400 `cobro_mercadopago_inexistente`. Después liga `cobros_mercadopago.id_comprobante_venta` al comprobante y deja
la `referencia` del pago con el id del pago de Mercado Pago, todo en la transacción de la venta. El reenvío de la misma venta
(mismo número y contenido) devuelve el comprobante ya emitido; el cobro entra en la identidad que se compara, y la referencia de
un pago con cobro no. Anular la venta deja el cobro ligado (el dinero entró; su devolución es de la conciliación, E6). Ni el cambio
de medios (409 `pago_mercadopago_no_editable` sobre una venta con un pago cobrado con Mercado Pago; un medio con proveedor entre los
medios nuevos se rechaza igual que en la venta, sin número preasignado) ni la facturación de remitos ni los recibos pueden agregar
un medio con proveedor sin cobro.

Cambio de medios de una venta ya emitida (sin CAE, sin cuenta corriente, mismo total cobrado): los cobros vigentes se dan de
baja lógica (`deleted_at`) y se insertan los nuevos; no hay columna nueva. Todo lector de `cobros` ve solo los vivos. Los
cobros con los que la venta se ingirió se reconocen porque nacieron con el comprobante (`created_at` igual al del
comprobante) y están dados de baja: el reenvío offline de esa venta los compara para responder la misma venta. El cambio
mueve el aporte de la venta en `acumulados_de_facturacion` (y reescribe `facturaciones_de_venta.aportes` si tiene decisión),
recalcula el arqueo de un turno cerrado con su marca (§7) y compensa `movimientos_cuenta` con un `ajuste` por medio (o las
filas de cierre de un medio que el cierre no acreditó). Queda auditado como `venta.medios`.

Cambios de fondo respecto del legacy:

| Legacy | Nuevo |
|---|---|
| `ventas.articulos` string serializado | `items_comprobante_venta` |
| Descuento como línea fantasma `OF...` | `descuento` + `id_oferta` **en el item** |
| Precio cambiado a mano en caja, sin rastro | `ajuste_manual_porcentaje` + `ajuste_manual` **en el item** |
| Columnas `efectivo/tarjetas/c_corriente/vuelto` | N filas en `cobros` |
| `tipo` 1/2 (venta/devolución) | `tipos_comprobante` con `signo` (ticket X / NC X) |
| `tipo` 3/4/5 metidos en `ventas` | `movimientos_cuenta_corriente` (§8) |
| `eliminado = 1` | `estado = anulado` + contramovimientos de stock auditados |
| Tolerancia $10 / vuelto máx $20 hardcodeados | parámetros por punto de venta (§9) |

**Anular** un comprobante no lo borra ni edita: lo marca `anulado`, genera los
`movimientos_stock` inversos y el contramovimiento de cuenta corriente si lo hubo.
El bug de "restaurar suma stock" muere por diseño: el stock solo se mueve por
movimientos, y los movimientos no se editan.

**Venta con envío (línea de concepto libre).** Con `ConEnvio` en el pedido de venta, el servidor agrega UNA línea
`items_comprobante_venta` sin artículo (`id_articulo` NULL) ya construida hoy: `descripcion` "Envío", `cantidad` 1,
`precio_unitario` = el parámetro `importe_envio` del punto de venta (§9; con importe 0 no hay línea), sin descuento,
oferta, combo, ajuste manual ni lote, con `costo_unitario` 0 (no hay mercadería que costear; así la cobertura de costo de
rentabilidad no la cuenta como "sin costo" y su importe suma al margen), sin área (`id_area` NULL), y `id_alicuota_iva`/`porcentaje_iva` de la alícuota
del 21 % (la fila con `codigo_afip` = 5), no la de ningún artículo. Entra al mismo cálculo de totales que el resto, así que
`subtotal`, `total` y el desglose fiscal (neto gravado, IVA) la incluyen, y queda siempre AL FINAL de la venta. No genera
`movimientos_stock`. El área es un atributo del artículo, así que `id_area` es NULL (migración `IdAreaNulableEnItemsDeVenta`, solo
DDL, sin backfill, FK compuesta intacta): el envío nunca figura en un desglose por área, y sí en los totales de la venta y del
turno (el desglose "ingresos por área" del resumen de turno queda por debajo del total por el monto de los envíos). Reglas del
servidor:
- exige un cliente que no sea Consumidor Final (400 `envio_requiere_cliente`) con `domicilio` no vacío (400
  `envio_requiere_domicilio`);
- sin `direccion_entrega` en el pedido, el comprobante guarda el domicilio del cliente;
- el importe lo resuelve el servidor; la única excepción es una venta con número pre-asignado (offline), donde el pedido
  puede traer `ImporteEnvio` (el que cobró la caja): se graba tal cual en la línea (con 0 no hay línea) y así un cambio de
  `importe_envio` entre dos reenvíos no altera el total. Sin `ImporteEnvio` (un dispositivo anterior) sigue el parámetro. En
  cualquier otra venta, o sin `ConEnvio`, `ImporteEnvio` se rechaza (400 `importe_envio_no_admitido`), y uno negativo, con más
  de 2 decimales o que no entra en `numeric(14,2)` es 400 `importe_envio_invalido`;
- el reenvío de una venta con número pre-asignado que ya tiene comprobante se resuelve antes de validar al cliente: aunque el
  domicilio se haya borrado después, devuelve el comprobante existente (la identidad que compara la guarda no incluye el
  importe del envío aparte: lo cubre el total que reciben los pagos);
- una nota de crédito o la conversión de un presupuesto con `ConEnvio` se rechaza (400 `envio_no_admitido`); la nota de crédito
  de una venta con envío devuelve las líneas de artículo que se piden y NO devuelve el envío (no tiene artículo que elegir),
  y la anulación revierte el stock por los movimientos, así que la línea sin artículo no estorba;
- los reportes por artículo (top, ventas por artículo, consultas del conector) no ven la línea; rentabilidad la cuenta con costo 0
  (suma al margen y arma una fila sin artículo "Envío"); el total de ventas y el total del resumen de turno la incluyen, el
  desglose por área no. El
  espejo fiscal y el libro IVA leen los items por alícuota, sin mirar el artículo, así que el envío entra a su 21 %.

> **Estado (Etapa 9, stage-9-costo-congelado): implementada.** `items_comprobante_venta`
> gana dos columnas adicionales al esquema de arriba, agregadas por la migración
> `CostoCongeladoEnVentaEtapa9`: `costo_unitario numeric(14,2) NULL` — snapshot de
> `articulos.costo_nominal` al emitir, por unidad, **sin signo** (igual que
> `precio_unitario`: el signo vive en `cantidad`) y con IVA incluido; `NULL` = costo
> desconocido, nunca se colapsa a `0` — y `costo_es_estimado boolean NOT NULL DEFAULT
> false`, en `true` únicamente en filas completadas por el backfill de la propia migración.
> Dos CHECKs nuevas: `ck_items_comprobante_venta_costo_no_negativo` (`costo_unitario IS
> NULL OR costo_unitario >= 0`) y `ck_items_comprobante_venta_estimado_con_costo` (`NOT
> costo_es_estimado OR costo_unitario IS NOT NULL`). El backfill de filas preexistentes
> corre en modo plataforma (`SET LOCAL app.acceso = 'plataforma'`, dentro de la misma
> migración) porque la tabla está bajo `FORCE ROW LEVEL SECURITY` y el rol de aplicación no
> tiene `BYPASSRLS`; es idempotente por construcción. El costo **nunca** se expone en
> `ItemEmitido`/`ComprobanteEmitido` ni en ningún payload del POS — es un dato server-side
> para el margen de la etapa 10.
>
> **Estado (Etapa 17, slice 1 — schema + backstops):** `comprobantes_venta` gana
> `id_presupuesto_origen integer NULL` — ALTER aditivo, metadata-only en PG 11+, sin rewrite
> de tabla. FK 23 `fk_comprobantes_venta_presupuesto_origen` compuesta
> `(id_presupuesto_origen, id_tenant)` contra `presupuestos`, RESTRICT, MATCH SIMPLE (el
> default): con `id_presupuesto_origen` NULL la constraint no se chequea — el 100% del
> tráfico anterior a esta etapa, permanentemente legítimo. Índice 29
> `ux_comprobantes_venta_presupuesto_origen` — **UNIQUE PARCIAL**
> `WHERE id_presupuesto_origen IS NOT NULL` — es a la vez el soporte de FK 23 y la garantía
> de base de que a lo sumo un comprobante liga a cada presupuesto (1:1 de conversión). Ninguna
> CHECK ata esta columna a nada: el acuerdo presupuesto↔venta (mismo tenant/cliente, no
> vencido, todavía `enviado`) es una regla cross-table que el esquema no puede expresar — la
> aplica el `UPDATE` guardado de `EscriturasDePresupuesto.MarcarConvertidoAsync` (slice 3, ver
> "Presupuestos" más abajo). `ComprobanteEmitido` gana `IdPresupuestoOrigen` en el
> round-trip (`dto-contract-honesty` regla 2).
>
> **Estado (Etapa 19a — CERRADA, regla 19):** `comprobantes_venta` gana las cuatro columnas de
> arriba (migración `FiscalArcaEtapa19a`), aditivas puras — CERO statement extra en una venta no
> fiscal.
> `ck_comprobantes_venta_fiscal_coherente` (4 conjuntos): o las cuatro son `NULL`, o
> `resultado_fiscal` está seteado con `cae`/`cae_vencimiento` llegando juntos y presentes SII
> `resultado_fiscal` es una de las dos aprobaciones (`aprobado`/`aprobado_con_observaciones` —
> una aprobación CON observaciones **es** una factura válida, I3 de la máquina CAE la trata
> como terminal). `ck_comprobantes_venta_cae_digitos` exige 14 dígitos. Índice PARCIAL
> `ix_comprobantes_venta_fiscal_pendientes` `WHERE resultado_fiscal = 'pendiente'` — su
> consumidor (resolución de pendientes/reintento, invariante I2) llega en slice 5. Sin columna
> nueva para el número fiscal: `numero` ya lo lleva, alimentado por la serie fiscal
> (`numeraciones_fiscales`, ver "Fiscal ARCA" más abajo) en vez de la interna — disjuntas por
> tipo de comprobante. `observaciones_fiscales` sigue el precedente `jsonb` de `Auditoria` —
> nunca la respuesta cruda de ARCA (persistiría `Token`/`Sign`, una credencial portadora, sin
> cifrado). El camino de escritura fiscal es `ServicioDeFacturacionFiscal` (slice 5) — el único
> escritor de estas cuatro columnas, vía la guarded `UPDATE` U2 (`WHERE … AND resultado_fiscal =
> 'pendiente'`).
>
> **Estado (ajuste manual de precio en ventas): implementada**, migración `AjusteManualEnVentas`
> (solo DDL, sin backfill: los valores por defecto cubren las filas existentes).
> `items_comprobante_venta` gana `ajuste_manual_porcentaje numeric(5,2) NULL` — porcentaje con
> signo que el operador aplica a mano sobre el **neto de la línea** (bruto menos `descuento` de
> oferta): negativo = descuento, positivo = recargo, `NULL` = sin ajuste — y `ajuste_manual
> numeric(14,2) NOT NULL DEFAULT 0`, el monto resultante con el signo del porcentaje. Dos CHECKs:
> `ck_items_comprobante_venta_ajuste_manual_porcentaje_valido` (`NULL`, o distinto de cero y
> entre −100 y 100) y `ck_items_comprobante_venta_ajuste_manual_con_porcentaje` (sin porcentaje
> el monto es 0). El total de línea pasa a ser `round(cantidad × precio) − descuento +
> ajuste_manual`. `comprobantes_venta` gana `descuento_manual_total` y `recargo_manual_total`
> (ambos `numeric(14,2) NOT NULL DEFAULT 0`): la suma de los ajustes de las líneas de
> porcentaje negativo y positivo respectivamente, separados a propósito para que un recargo en
> una línea no pueda ocultar un descuento en otra; el total del encabezado es `subtotal −
> descuento_total − descuento_manual_total + recargo_manual_total`. La clasificación depende del
> signo del **porcentaje**, no del monto, de modo que una línea de NCX (cantidad negativa) sigue
> la misma convención de signo que `descuento_total`. El cliente envía únicamente el porcentaje
> (`ajusteManualPorcentaje`, `400 ajuste_manual_invalido` si es 0, está fuera de ±100 o trae más
> de dos decimales); todo importe lo calcula `CalculadorDeTotales`, en el camino online y en el
> offline. Sin tablas nuevas: ambas columnas viven en tablas operativas que ya tienen
> `id_tenant` y RLS.

> **Estado (artículos por importe — venta): implementada.** El único cambio de esquema es
> `items_comprobante_venta.por_importe boolean NOT NULL DEFAULT false` (sin backfill: ninguna fila anterior se vendió por
> importe): el snapshot de cómo se vendió la línea. La línea ya guarda `precio_unitario`; el artículo puede cambiar de
> unidad después de la venta, así que la reliquidación y la devolución asociada deciden por esta columna y no por la
> `unidad_venta` de hoy. Un artículo `unidad_venta = 'importe'` se vende por el monto que escribe el cajero. La línea del
> request (`LineaDeVenta`) y la de la vista previa (`LineaDeResolucion`, `POST /api/ofertas/resolver` y
> `resolver-con-combos`) suman `importe` (`decimal?`): **obligatorio** en un artículo por importe (`400
> importe_requerido`) y **rechazado** en cualquier otro (`400 importe_no_admitido`, en vez de ignorarse); mayor a cero,
> con a lo sumo 2 decimales y dentro de `numeric(14,2)` (`400 importe_invalido`, `ReglaDeImporteDeVenta`); la cantidad es
> exactamente 1 (`400 cantidad_importe_invalida`) y junto a `precioUnitario` solo se admite en una venta offline (`400
> importe_con_precio_no_admitido` en cualquier otra). `ServicioDeOfertas` resuelve la línea sin mirar ofertas ni combos: precio
> original = precio final = importe con el porcentaje de la lista derivada del cliente (la fija lo deja igual), descuento 0, sin ofertas aplicadas (`articulo_sin_precio_vigente` no aplica). La
> venta persiste el item como cualquier otro: `cantidad` 1, `precio_unitario` = el precio resuelto (importe con el porcentaje de la lista; offline, el que cobró la caja), `descuento` 0, `id_oferta`
> NULL, `id_combo` NULL, `id_lista_precio` la del cliente. El ajuste manual (`ajusteManualPorcentaje`) se aplica encima y
> el total de la línea sigue la regla de peso entero de `CalculadorDeTotales`: un importe con centavos conserva el
> precio unitario tipeado (150,50) y el total de la línea sale al peso (151).
>
> - **Offline.** La venta con número pre-asignado nunca re-deriva con la lista de hoy el precio de una línea por importe:
>   si la línea trae `precioUnitario` (el que cobró la caja) se graba tal cual y la diferencia contra el precio derivado hoy
>   queda en la auditoría de discrepancias; si trae solo `importe` (paquete anterior de la caja) se registra por el importe
>   tipeado. Online, `importe` + `precioUnitario` sigue siendo `400 importe_con_precio_no_admitido`. El "todas o ninguna" del
>   precio offline se mide solo sobre las líneas de lista, así que una venta solo de importes y una mixta se aceptan. Una línea que el dispositivo ya cobró con precio de lista para un artículo que hoy es por
>   importe se honra (no se le exige `importe`).
> - **Devolución (NCX).** Cada línea por importe lleva el importe devuelto; con comprobante asociado tiene que existir en él
>   una línea por importe (`por_importe`, cantidad positiva) de ese artículo con ese precio unitario y ese mismo
>   `ajuste_manual_porcentaje` (`400 importe_devolucion_inexistente`): el neto reembolsado no puede superar el cobrado por la
>   línea vendida. No se lleva
>   la cuenta de lo ya devuelto, igual que las cantidades de una devolución común. Una devolución offline (con número
>   pre-asignado) no se compara.
> - **Escaneo.** `GET /api/articulos/escaneo?entrada=150*500` sobre un artículo por importe responde `cantidad` 1 e
>   `importe` 150; con el código solo, sin `importe` (la clave se omite) y el cliente lo pide. El prefijo de un artículo por
>   importe se lee estricto (solo dígitos y punto decimal): uno vacío, cero, negativo, ilegible, con coma, con más de 2
>   decimales o fuera de rango es `400 importe_invalido`, en vez de caer a "cantidad 1" como en los demás artículos.
> - **Instantánea del POS.** El artículo por importe viaja siempre con `preciosPorLista` vacío (aunque conserve un precio de
>   lista, que el dispositivo no debe usar).
> - **Reenvío con número pre-asignado.** El importe de una línea por importe entra en la identidad del contenido: otro
>   importe bajo el mismo número es `409 numero_preasignado_con_otro_contenido`.
> - **Ofertas por familia.** Una línea por importe no suma a la cantidad agregada de su familia.
> - **Reliquidación de cuenta corriente.** La línea vendida por importe (`por_importe`) conserva el monto tipeado: no se reprecifica, el detalle la
>   marca como omitida con su motivo y el delta es cero.
> - **Presupuestos y remitos** rechazan un artículo por importe (`400 articulo_por_importe_no_admitido`). **Tienda online:**
>   no se puede ofrecer (encender `disponible_online` da `400 articulo_por_importe_no_publicable`; apagarla, o cambiar solo
>   `disponible_venta` con la bandera ya encendida, sigue siendo válido) y el catálogo público no lo lista.

### Fiscal ARCA (Etapa 19a)

El núcleo fiscal buildable sin credenciales (`openspec/changes/stage-19-fiscal-arca/`, slice 1
— schema): el certificado activo es el gate estructural, no un feature flag — sin uno, el
camino fiscal devuelve 409 y realiza **cero** llamadas de red (invariante I4). `resultado_fiscal`
y `ambiente_fiscal` son enums nativos nuevos, **enteramente creados por `CREATE TYPE`** — CERO
`ALTER TYPE … ADD VALUE`, así que esta sub-etapa no deja ningún artefacto irreversible tras un
`Down()` (a diferencia de las etapas 12/17).

```sql
certificados_fiscales (         -- [id_tenant + id_empresa NOT NULL] — DESVIACIÓN del catálogo,
                                 -- ver doc 09: un certificado es de UN CUIT, nunca compartido
    id_certificado, id_tenant, id_empresa,
    ambiente               ambiente_fiscal NOT NULL,  -- enum: homologacion | produccion
    alias                  varchar(60)  NOT NULL,     -- etiqueta humana ("Homo 2026")
    cuit_titular            varchar(11) NOT NULL,      -- CUIT al que ARCA emitió el certificado
    certificado_pem         text        NOT NULL,      -- parte PÚBLICA: no es secreto
    clave_privada_cifrada   bytea       NOT NULL,      -- AES-256-GCM
    nonce                   bytea       NOT NULL,      -- 12 bytes
    tag_autenticacion       bytea       NOT NULL,      -- 16 bytes
    id_clave_maestra        varchar(30) NOT NULL,      -- versión de la clave maestra (rotación)
    huella_sha256           varchar(64) NOT NULL,      -- fingerprint: trazar sin descifrar
    vigencia_desde, vigencia_hasta timestamptz NOT NULL,  -- del propio X.509
    activo                  boolean     NOT NULL
    -- EntidadBase completa (created_at/updated_at/deleted_at) — la rotación da de baja lógica
    -- la fila superada
);
-- UNIQUE (id_tenant, id_empresa, ambiente) WHERE activo AND deleted_at IS NULL
--   ⇒ a lo sumo UN certificado activo por empresa+ambiente

numeraciones_fiscales (         -- [operativa] id_tenant + id_punto_venta, PK-only sin auditoría
                                 -- (mismo criterio que numeraciones_comprobante, doc 09)
    id_punto_venta integer  NOT NULL,   -- el interno; su numero_fiscal es UNIQUE (ver arriba)
    codigo_afip    smallint NOT NULL,   -- CbteTipo de ARCA (1, 3, 6, 8, 11, 13, …)
    id_tenant      integer  NOT NULL,
    proximo_numero bigint   NOT NULL DEFAULT 1,
    ultimo_autorizado_arca bigint      NULL,   -- de FECompUltimoAutorizado; 0 = serie sin usar
    sincronizado_en         timestamptz NULL   -- par del anterior (IRelojDelSistema)
    -- PK (id_punto_venta, codigo_afip)
);
```

**Estado (Etapa 19a — CERRADA, DB CHANGE GATE ratificado por el orquestador).** Creadas por la
migración `FiscalArcaEtapa19a`, junto con los tres ALTERs
aditivos de arriba (`empresas`, `puntos_venta`, `comprobantes_venta`) y las dos nuevas `CREATE
TYPE`. RLS estándar (`ENABLE` + `FORCE`) en ambas tablas. `certificados_fiscales`: `EntidadBase`
**SÍ** — key material que se rota, nunca se edita in place. `numeraciones_fiscales`:
`EntidadBase` **NO** — mismo criterio exacto que `numeraciones_comprobante` (doc 09), necesita
filtro de tenant escrito a mano (`WaysDbContext.AplicarFiltroDeTenantEnNumeracionFiscal`) y un
guard de rechazo de escritura por `SaveChangesAsync` — su único escritor legítimo es
`AsignadorDeNumeroFiscal` (slice 4), con SQL crudo, **disciplina OPUESTA** a
`AsignadorDeNumeroComprobante`: toma el número DENTRO de la transacción de emisión (nunca en una
transacción propia previa) porque en una serie de ARCA un número quemado no abre un hueco
legítimo — **detiene la serie** (error 10016 de ARCA). El `codigo_afip` de los tres catálogos
(`tipos_comprobante`, `condiciones_fiscales`, `alicuotas_iva`) queda poblado por esta misma
migración vía tres data statements idempotentes + sus tres seed nets gemelos en
`InicializadorDeBaseDeDatos` (doble red de la etapa 17) — sin ningún `ALTER`, las tres tablas ya
tenían `codigo_afip smallint NULL` desde la etapa 1. Los caminos de escritura de negocio llegaron
en slices posteriores: `AsignadorDeNumeroFiscal`/`ServicioDeCertificados` (slice 4) y
`ServicioDeFacturacionFiscal` — la emisión end-to-end contra mocks, primer y único caller real de
`ResolvedorDeLetraComprobante` — (slice 5, cierre de la sub-etapa).

### Facturación por medio de pago (espejo fiscal)

Cada medio de pago define qué porcentaje de lo vendido debe salir facturado por ARCA (`medios_pago.porcentaje_facturacion`).
Un ticket del POS (`TX`) que debe facturarse genera después una **factura espejo**: una fila de `comprobantes_venta`
fiscal (con `resultado_fiscal`) enlazada al ticket por `id_venta_origen`. El ticket sigue siendo el documento económico: el
espejo lleva comprobante e ítems y, por convención de quien lo escribe (ninguna restricción lo impide), sin stock, pagos,
cuenta corriente ni turno; lo único que la base garantiza es que es fiscal y no tiene turno. Los agregados y listados de
ventas lo excluyen con un único predicado compartido (`ConsultasDeComprobantesVenta.SinEspejosFiscales`, en SQL crudo
`id_venta_origen IS NULL`); el libro IVA ventas es el único que lo necesita y no lo usa. Las lecturas por id no filtran, así
que los caminos que podrían tratarlo como una venta lo rechazan: una devolución no puede asociarse a un espejo
(`comprobante_asociado_es_espejo_fiscal`, 400) y la anulación por la vía de ventas lo rechaza (`comprobante_es_espejo_fiscal`,
409). `LectoresDeComprobantesVentaEstructuralesTests` es un barrido de texto sobre las sentencias de `src/` (con cantidad
exacta por lectura justificada y autoverificación de sus expresiones): detecta la sentencia que lee comprobantes, ítems o pagos
sin el predicado y sin estar justificada. No ve los lectores alcanzados por navegaciones ni por métodos auxiliares que
devuelven `IQueryable`, ni la carpeta `Persistencia/`, y no prueba que el predicado esté en la cláusula correcta; eso lo
cubren los tests de integración de cada reporte.

```sql
facturaciones_de_venta (        -- [operativa] id_tenant + id_empresa NOT NULL, EntidadBase completa
    id_facturacion_venta   integer PK identity,
    id_tenant, id_empresa  integer NOT NULL,
    id_comprobante_venta   integer NOT NULL,        -- la venta; clave de idempotencia de la decisión
    motivo                 motivo_facturacion NOT NULL,        -- enum: forzada | medio_exige_factura | cupo_alcanzado
    estado                 estado_facturacion_venta NOT NULL,  -- enum: pendiente | emitida | rechazada | cancelada
    aportes                jsonb NOT NULL,          -- [{ "idMedioPago": int, "aporte": decimal }] aplicados al acumulado
    periodo                date  NOT NULL,          -- primer día del mes al que se aplicaron los aportes
    id_comprobante_fiscal  integer NULL,            -- el espejo, una vez emitido
    id_facturacion_origen  integer NULL,            -- NULL = decisión de la factura; con valor = la decisión de la nota de
                                                    -- crédito de esa decisión de factura
    intentos               integer NOT NULL DEFAULT 0,
    ultimo_error           text NULL,
    proximo_intento_en     timestamptz NULL,
    created_at, updated_at, deleted_at
);
-- UNIQUE (id_comprobante_venta, id_tenant) WHERE id_facturacion_origen IS NULL  ux_facturaciones_de_venta_comprobante
-- INDEX  (id_comprobante_venta, id_tenant)                                  ix_facturaciones_de_venta_comprobante (soporte de la FK; el único es parcial)
-- UNIQUE (id_facturacion_origen, id_tenant) WHERE id_facturacion_origen IS NOT NULL  ux_facturaciones_de_venta_origen
-- UNIQUE (id_facturacion_venta, id_tenant)                                   ak_..._id_facturacion_venta_id_tenant
-- FK (id_facturacion_origen, id_tenant) → facturaciones_de_venta, RESTRICT   fk_facturaciones_de_venta_origen
-- UNIQUE (id_comprobante_fiscal, id_tenant) WHERE id_comprobante_fiscal IS NOT NULL  ..._comprobante_fiscal
-- INDEX  (id_tenant, proximo_intento_en) WHERE estado = 'pendiente'          ix_facturaciones_de_venta_pendientes
-- FK compuestas (id_empresa|id_comprobante_venta|id_comprobante_fiscal, id_tenant), todas RESTRICT:
--       fk_facturaciones_de_venta_empresa | _comprobante | _comprobante_fiscal
-- FK id_tenant → tenants, RESTRICT                                           fk_facturaciones_de_venta_tenant
-- INDEX  (id_empresa, id_tenant) · (id_tenant)                               ix_facturaciones_de_venta_empresa | _tenant
-- CHECK periodo día 1 · aportes es un arreglo jsonb · intentos >= 0 · motivo <> 'no_requerida' ·
--       estado <> 'emitida' OR id_comprobante_fiscal IS NOT NULL ·
--       id_facturacion_origen IS NULL OR aportes = '[]'::jsonb (una NC no mueve saldos) ·
--       id_facturacion_origen IS NULL OR id_facturacion_origen <> id_facturacion_venta

acumulados_de_facturacion (     -- [operativa] id_tenant + id_empresa, PK-only sin auditoría
                                -- (mismo criterio que numeraciones_fiscales)
    id_empresa      integer NOT NULL,
    id_medio_pago   integer NOT NULL,
    periodo         date    NOT NULL,               -- primer día del mes (CHECK de día 1)
    id_tenant       integer NOT NULL,
    saldo_a_facturar numeric(18,6) NOT NULL DEFAULT 0,  -- lo vendido por el medio que todavía no se facturó
    total_vendido    numeric(14,2) NOT NULL DEFAULT 0,
    total_facturado  numeric(14,2) NOT NULL DEFAULT 0
    -- PK (id_empresa, id_medio_pago, periodo)
);
-- CHECK EXTRACT(DAY FROM periodo) = 1                                        ck_acumulados_de_facturacion_periodo_dia_uno
-- FK compuestas (id_empresa, id_tenant) → empresas y (id_medio_pago, id_tenant) → medios_pago, RESTRICT:
--       fk_acumulados_de_facturacion_empresa | _medio_pago
-- FK id_tenant → tenants, RESTRICT                                           fk_acumulados_de_facturacion_tenant
-- INDEX  (id_empresa, id_tenant) · (id_medio_pago, id_tenant) · (id_tenant)  ix_acumulados_de_facturacion_empresa | _medio_pago | _tenant
```

**Estado (facturación por medio de pago, T3): implementada**, migración `FacturacionPorMedioDePagoEsquema`. Solo DDL (las
dos tablas nacen vacías y las columnas nuevas de `comprobantes_venta` tienen valor por defecto, así que no hay backfill).
RLS estándar (`ENABLE` + `FORCE`) en ambas tablas, creada en la misma migración.

**Datos legales congelados del espejo (`datos_legales_fiscales`, migración `DatosLegalesCongeladosEnEspejoFiscal`).**
`jsonb NULL` con `{ emisor, receptor }` (razón social, fantasía, CUIT, condición, IIBB, inicio de actividades y domicilio del
punto de venta del emisor; nombre, documento ARCA —tipo y número que viajaron a ARCA y entraron al QR—, condición y domicilio
del receptor). Se escribe una sola vez, en la transacción A que inserta el espejo, con las mismas lecturas con que se armó el
pedido a ARCA, y no se vuelve a tocar: la reimpresión (`GET /api/ventas/{id}`), el QR y el reintento del pedido usan estos datos
y no las filas actuales de empresa y cliente. `ck_comprobantes_venta_datos_legales_espejo` lo limita a un espejo
(`id_venta_origen IS NOT NULL`). Sin backfill: `NULL` significa "usar las filas actuales" y es el estado de toda venta y de los
espejos anteriores a la columna.

**Bloqueo de la migración.** Los CHECK (`ck_comprobantes_venta_espejo_fiscal`, `ck_comprobantes_venta_espejo_sin_turno`), la FK
autorreferente (`fk_comprobantes_venta_venta_origen`) y los índices (`ux_comprobantes_venta_venta_origen`, `ix_comprobantes_venta_venta_origen`) sobre
`comprobantes_venta` se crean sin `NOT VALID` ni `CONCURRENTLY`: la migración recorre esa tabla mientras bloquea las escrituras,
así que en un tenant con muchos comprobantes conviene aplicarla en una ventana de bajo tráfico.

- **Cupo mensual.** El cupo se cumple dentro de cada mes calendario y el saldo arranca en cero el día 1: cada mes es otra fila
  de `acumulados_de_facturacion`, y `facturaciones_de_venta.periodo` guarda a qué mes se aplicaron los aportes para que una
  anulación los revierta sobre el mismo mes.
- **Escala del saldo.** Un aporte es `importe × porcentaje / 100` con el importe en 2 decimales y el porcentaje en 2
  (`numeric(5,2)`), o sea hasta 6 decimales exactos; `numeric(18,6)` los guarda sin redondear y deja 12 dígitos enteros.
- **Único escritor del acumulado.** Como `numeraciones_fiscales`, no hereda de `EntidadTenant`, necesita el filtro de tenant
  escrito a mano (`AplicarFiltroDeTenantEnAcumuladoDeFacturacion`) y `SaveChangesAsync` rechaza escribirla: el saldo solo se
  mueve con SQL crudo (`INSERT … ON CONFLICT DO NOTHING`, `SELECT … FOR UPDATE` y un `UPDATE` que suma), bajo el lock de la fila, para que dos ventas concurrentes no se pisen (`ServicioDeDecisionDeFacturacion`, ver "Decisión al ingerir la venta").
- **Enums nativos nuevos** (`motivo_facturacion`, `estado_facturacion_venta`), creados por `CREATE TYPE`; el orden de los
  valores es el del ciclo de vida. `motivo_facturacion` conserva `no_requerida` (valor 0 del enum de dominio) para que el mapeo
  sea 1:1, y un CHECK impide usarlo: una venta que no se factura no tiene fila.
- **Baja de empresa y de medio de pago.** `acumulados_de_facturacion` es un dependiente `sinmarca` (sin baja lógica) de
  `empresas`, `medios_pago` y `tenants`; `facturaciones_de_venta` es `marcado`. La guarda de baja los cuenta como uso (el inventario de
  dependientes los incluye), igual que a un medio con pagos.
- **Respaldos de error.** Los tres índices únicos se traducen a `409` (`venta_ya_facturada`, `facturacion_de_venta_duplicada`,
  `factura_ya_asignada`); los CHECK no se traducen a propósito: los valores los deriva el servidor y una violación es un
  defecto del servidor, no un error del cliente.

**Decisión al ingerir la venta (T4): implementada**, migración `FacturacionDecididaEnVenta` (tres columnas en
`comprobantes_venta` más un índice parcial). `ServicioDeDecisionDeFacturacion` no emite la factura: decide, deja la fila de
decisión en `pendiente` y mueve el acumulado.

- **Qué significa `NULL`.** Una sola cosa: la decisión de esa venta está debida. Toda venta que la decisión examina queda marcada
  (`facturacion_decidida_en` con la hora del servidor), se facture o no: no es un ticket, está anulada, la empresa no tiene un
  certificado activo del ambiente configurado, no cobró nada. Solo los espejos fiscales (`id_venta_origen` no nulo) quedan en `NULL` para siempre, porque nunca
  se deciden, y por eso el índice y el barrido los excluyen. Los comprobantes que nunca son un ticket y que crean otros escritores
  nacen ya marcados, con la hora que ese escritor ya tiene: la consolidación de remitos (TXR), los recibos de cuenta corriente (RC)
  y la emisión fiscal directa (`ServicioDeFacturacionFiscal`); no compiten por los lugares del barrido. El resto de las filas nace en
  `NULL` y las decide el endpoint de ventas (`POST /api/ventas`: TX y NCX).
- **Intentos fallidos.** `facturacion_intentos integer NOT NULL DEFAULT 0` cuenta los intentos de decidir esa venta que fallaron y
  `facturacion_proximo_intento_en timestamptz NULL` es el instante antes del cual el barrido no la reintenta. Cada fallo (el de la
  venta recién emitida o el de una candidata del barrido) se anota con un `UPDATE` crudo aparte, fuera de la transacción que falló:
  suma uno al contador y fija la espera `1 min × 2^(intentos−1)` con tope de 4 horas (1, 2, 4, 8, 16, 32, 64,
  128 y luego 240 minutos), contada desde el reloj del servicio. Anotar el fallo nunca lanza y tiene su propio presupuesto de 1 s: si otra
  sesión tiene tomada la fila de la venta la anotación se omite y la venta sigue debida, sin espera creciente. Una decisión lograda no
  limpia nada: basta la marca y el contador queda como historia. La espera solo restringe al barrido; la decisión de la venta recién
  emitida y la llamada directa a `DecidirAsync` no la miran.
- **Migración.** `ADD COLUMN facturacion_decidida_en timestamptz NULL DEFAULT now()` seguido de `ALTER COLUMN … DROP DEFAULT`, ambos con
  `Sql()`; luego `facturacion_intentos integer NOT NULL DEFAULT 0` y `facturacion_proximo_intento_en timestamptz NULL`. Las tres son solo
  metadata: `now()` es `STABLE`, así que PostgreSQL lo evalúa una sola vez en el `ALTER` y lo guarda como valor "faltante" de la
  columna (no reescribe la tabla y toda venta que existe al migrar se lee como ya decidida, con la hora de la migración), y el entero con
  valor por defecto constante y el nullable tampoco la reescriben. Al quitar el valor por defecto de la marca las filas nuevas nacen en
  `NULL`. El modelo de EF describe la marca nullable sin valor por defecto. EF corre la migración en una sola transacción, así que el
  lock `ACCESS EXCLUSIVE` que toma `ADD COLUMN` se mantiene hasta que la transacción termina, incluido el `CREATE INDEX` del índice parcial
  `ix_comprobantes_venta_decision_debida`, que recorre la tabla una vez: durante todo ese tiempo se bloquean lecturas y escrituras de
  `comprobantes_venta`. En un tenant con muchos comprobantes conviene aplicar la migración en una ventana de bajo tráfico. Como todas las
  filas existentes ya están marcadas, el índice nace vacío. Lo prueba `FacturacionDecididaEnVentaMigracionTests` (mismo archivo de la tabla
  antes y después, `atthasmissing`, marca de las filas viejas, `NULL` en las nuevas y las dos columnas de intentos).
- **Cuándo corre.** `POST /api/ventas` la llama después del commit de la venta, en una transacción propia, de modo que el lock del
  acumulado nunca entra en el conjunto de locks del checkout, y sin el token de la solicitud: un corte del cliente no deja la decisión a
  medias. La decisión de la venta recién emitida tiene su propio presupuesto total de 3 segundos (`PresupuestoDeDecision`), que se pasa
  como token de cancelación a la estrategia de ejecución y a cada comando; si vence, o si la decisión falla, la transacción se deshace,
  el fallo se anota, la venta responde `sin_decidir` y el barrido no corre en esa solicitud: se omite siempre que la decisión de la venta
  actual termina en `sin_decidir`, sea cual sea la causa. Un test
  prueba el caso de una fila de la venta tomada por otra sesión (responde `sin_decidir` dentro del tope y la misma conexión sigue sana);
  que el token corte también los reintentos transitorios de la estrategia no tiene un test. El reenvío de una venta con número pre-asignado pasa por el mismo endpoint y vuelve a llamarla. La respuesta
  (`ComprobanteEmitido.fiscal.estado`) dice `pendiente` (se decidió facturar), `no_corresponde` o `sin_decidir`; desde T5c puede decir también `aprobado`, `rechazado` o `cancelado` (ver "Emisión en línea al cobrar (T5c)", que explica cuándo aparece cada estado). Las lecturas también lo informan, sin decidir ni emitir (T6a): `GET /api/ventas/{id}` devuelve el mismo `fiscal` que la emisión (con los datos de la factura si está
  aprobada, y `error` como código) y `GET /api/ventas/por-turno/{idTurno}` agrega a cada fila solo `estadoFiscal` (ver "Lecturas de una venta (T6a)").
- **Autocuración (barrido).** Si la decisión falla, el error se registra, la venta responde igual con `sin_decidir`, la marca queda en
  `NULL` y el fallo se anota (ver "Intentos fallidos"). Si la decisión de la venta actual terminó bien, el endpoint (a través de `EmisionEnLineaAlCobrar`, T5c) llama a
  `SanearDecisionesDebidasAsync`: toma las ventas del mismo punto de venta con la marca en `NULL`, `id_venta_origen` nulo y la espera
  vencida (`facturacion_proximo_intento_en IS NULL OR <= ahora`), las más viejas primero (por id), como máximo 5 por llamada
  (`LimiteDeBarrido`) y con un presupuesto total de 3 segundos (o lo que quede del tope de 8 segundos de T5c, si es menos), y decide cada una en su propia transacción con el mismo código que la
  venta recién emitida. Una venta que falla se anota con su espera y el barrido sigue con la siguiente; cada candidata se intenta una sola
  vez por llamada y la venta recién decidida se excluye. Una venta que falla siempre sale de los candidatos hasta que vence su espera
  (y la espera crece hasta 4 horas), de modo que no desplaza a las posteriores. Si el propio presupuesto de 3 segundos del barrido vence
  mientras decide una candidata, esa interrupción no es un fallo de la venta: no se anota (ni intento ni espera), el barrido termina y
  deja una sola línea de nivel debug; la candidata sigue debida y la decide una llamada posterior. Se distingue por el token del
  presupuesto del barrido: cualquier falla de una candidata con ese token ya cancelado cuenta como corte, y toda otra falla se anota
  como siempre. Limitación: una candidata que consume el presupuesto entero en cada barrido (por ejemplo, esperando una fila del acumulado retenida por otra sesión) no se anota ni se posterga, sigue primera por id y demora hasta 3 segundos cada venta de ese punto de venta mientras dure la causa, sin que el barrido llegue a las que la siguen. El presupuesto de la decisión de la venta actual no se comporta así: su vencimiento sí se anota como fallo. El índice `ix_comprobantes_venta_decision_debida
  (id_punto_venta, id_comprobante_venta) WHERE facturacion_decidida_en IS NULL AND id_venta_origen IS NULL` entrega esas filas ya
  ordenadas; la condición de la espera se evalúa sobre sus filas, así que las ventas que esperan se recorren (sin transacción) hasta
  reunir los candidatos. El punto de venta pertenece a un solo tenant (RLS agrega el filtro de tenant) y la lista que el barrido pide es
  la de su caja. Registro: advertencia en los primeros fallos de una venta y error con su id y la cantidad de intentos desde el quinto
  (`IntentosParaRegistrarComoError`); una candidata que todavía espera no se registra. Limitación: una venta cuya fila sigue tomada por
  otra sesión no puede anotar su fallo.
- **A qué ventas aplica.** Solo un ticket emitido (clase venta, no fiscal, signo positivo, que mueve stock: el TX) se decide. Una
  devolución, una venta anulada antes de decidirse y un comprobante que no es ticket se marcan sin fila ni acumulado. Una empresa sin
  un certificado fiscal activo del ambiente configurado (`OpcionesFiscales.ResolverAmbiente`) también, sin fila de decisión, sin
  movimiento del acumulado, sin intento fallido anotado y sin excepción: la venta se decide como no aplicable (`no_corresponde`) y se
  marca para siempre, tenga o no certificados activos de otro ambiente. Una venta hecha mientras la empresa no podía facturar en este
  despliegue nunca se factura retroactivamente, ni cuando después se activa un certificado del ambiente configurado: las ventas nuevas se
  deciden con normalidad y las anteriores quedan marcadas. Si además la empresa tiene un certificado activo de OTRO ambiente (p. ej.
  `ResolverAmbiente` cae a homologación con cualquier texto desconocido, así que un `produccion` mal escrito lo provocaría), el servicio
  deja un aviso en el log, con nivel de advertencia, que nombra la empresa y el ambiente configurado, para que se note un ambiente mal
  configurado; la venta se marca igual. El aviso se limita a uno por empresa y por hora y por proceso (un mapa en memoria, con
  candado, medido con el reloj del servicio; se avisa de nuevo al cumplirse la hora). Una venta sin pagos
  (o con importe neto cero) no tiene medio al que imputar: se marca y no mueve nada. Un pago con monto neto cero o negativo
  (`importe − vuelto ≤ 0`) no cuenta. Varios pagos del mismo medio suman en una sola fila del acumulado, y un medio de cuenta corriente
  acumula como cualquier otro según su `porcentaje_facturacion`: la decisión no mira el comportamiento del medio.
- **`ForzarFactura`.** Viaja en la solicitud, se guarda en `comprobantes_venta.factura_forzada` y entra en la identidad del reenvío
  (otro valor con el mismo número pre-asignado es `409 numero_preasignado_con_otro_contenido`). En una nota de crédito se rechaza
  (`400 forzar_factura_no_admitido`), no se ignora. Se acepta pero no tiene efecto cuando la empresa no tiene certificado activo del ambiente configurado y
  cuando el monto neto cobrado del ticket es cero; en ambos casos la respuesta es `no_corresponde` sin avisar que no se pudo forzar.
- **Idempotencia.** `comprobantes_venta.facturacion_decidida_en` es la marca de "ya decidida". Existe porque una venta que no se
  factura no tiene fila en `facturaciones_de_venta` (el CHECK prohíbe `no_requerida`) y sin marca decidirla otra vez la sumaría otra
  vez al acumulado. La decisión toma `FOR UPDATE` sobre la fila del comprobante, y bajo ese lock: si ya hay fila de decisión la
  devuelve, si hay marca devuelve `no_corresponde`, y si no decide. La fila de decisión y la marca se escriben en la misma transacción
  que el movimiento del acumulado, así que un reintento tras un commit ambiguo encuentra la marca y no cuenta dos veces. El lock de la
  venta serializa a todos los que la deciden, por eso el `INSERT` de la fila de decisión es simple: el UNIQUE de `id_comprobante_venta`
  es un respaldo y una violación es un defecto que sale como excepción (la venta queda `sin_decidir` y el barrido encuentra la fila
  existente y marca la venta).
- **Monto de cada pago.** `importe − vuelto`: lo que ese medio realmente cobró. Es la convención del arqueo (el vuelto sale del
  efectivo) y hace que los montos de los medios sumen el total del ticket. El porcentaje es el actual de `medios_pago` al decidir.
- **Período.** Primer día del mes de la fecha del comprobante en la `zona_horaria` del punto de venta (la misma que usan los reportes
  de ventas), no del día UTC. Un medio sin fila ese mes parte de saldo cero. La fecha del comprobante es el reloj del servidor al
  ingerir la venta: una venta offline hecha el último día de un mes y sincronizada días después cuenta para el mes en que se ingirió.
- **Locks y escrituras.** Orden: fila del comprobante, y luego las filas del acumulado por `id_medio_pago` ascendente. Para cada
  medio del ticket: `INSERT … ON CONFLICT DO NOTHING` de la fila del mes, un único `SELECT … FOR UPDATE ORDER BY id_medio_pago` que
  lee los saldos una vez bajo el lock, `PoliticaDeFacturacionPorMedio.Decidir`, y un `UPDATE` que suma `saldo_a_facturar` (el aporte),
  `total_vendido` (el monto) y `total_facturado` (el monto si se factura). Todo con SQL crudo.
- **Anular una venta ya decidida.** La anulación no toca la decisión ni el acumulado: la decisión `pendiente` de una venta anulada sin
  espejo se cancela de forma diferida cuando se procesa (ver "Emisión del espejo"), y ahí se devuelve el acumulado. Hasta que alguien
  procese esa decisión, el cupo sigue contado.

**Emisión del espejo (T5a): implementada.** `ServicioDeFacturacionDeVentas.ProcesarDecisionAsync(idFacturacion)`, expuesto como
`POST /api/fiscal/facturaciones/{id}/procesar` (política `OperacionDePos`, sin cuerpo; una decisión inexistente o de otro tenant
es `404` antes de cualquier otra cosa). No hay cambios de esquema. El procesador por lote y la espera entre intentos son T5b
(ver "Procesador por lote (T5b)" más abajo); la emisión en línea desde la venta es T5c (ver "Emisión en línea al cobrar (T5c)" más abajo).

- **Qué escribe el espejo.** Una fila de `comprobantes_venta` con `id_venta_origen` = la venta, `resultado_fiscal` `pendiente`
  al nacer, sin turno, `fecha` = el instante de emisión (no el de la venta), empleado, cliente y subtotales de la venta, totales
  fiscales compuestos desde los ítems guardados de la venta (no del catálogo), `facturacion_decidida_en` en `NULL` (los espejos
  nunca se marcan) e ítems copiados de la venta SIN lote y SIN costo. No escribe pagos, movimientos de stock ni de cuenta
  corriente. El tipo es la factura de la letra que resulta del cruce de condiciones (`FA`, `FB` o `FC`; las notas de débito
  comparten letra y signo, por eso se elige por código) y el receptor es el cliente de la venta: Consumidor Final o sin documento
  va como documento 99/0, y una factura A exige CUIT.
- **Pasos y locks, sin reintento automático.** ARCA puede autorizar un número aunque la respuesta nunca llegue, así que el número
  tiene que sobrevivir a esa falta de respuesta; la emisión directa (`POST /api/fiscal/comprobantes`) en cambio numera, inserta y
  llama en una sola transacción y deshace todo (lo fija un test). Cada paso es su propia transacción sin reintento (un reintento
  tras un commit ambiguo quemaría otro número) y retoma el lock de la serie. El ticket de acceso de WSAA se obtiene antes de
  tomar ningún número, así que una falla de WSAA no consume uno.
  - **A, crear el espejo** (decisión pendiente y sin espejo): `numeraciones_fiscales` con `SELECT … FOR UPDATE SKIP LOCKED` (si
    otra llamada la tiene responde `serie_ocupada` sin esperar y sin penalidad) → fila de la venta con `FOR UPDATE` → bajo ese lock
    relee tres cosas: que la venta siga emitida, que NO tenga ya un espejo (en cualquier estado) y que su decisión siga
    `pendiente` (`SELECT … FOR UPDATE` de la fila de la decisión) → recién entonces pregunta si otro espejo pendiente detiene la
    serie (`serie_bloqueada`, sin penalidad), toma el número (`proximo_numero + 1`) e inserta el espejo y sus ítems. Si la venta
    dejó de estar emitida cancela la decisión; si ya tiene espejo o la decisión ya no está pendiente no inserta nada ni toma número
    y se vuelve a evaluar el estado actual, sin llamar a ARCA. Así el índice único `ux_comprobantes_venta_venta_origen` no es
    alcanzable por este camino (lo demuestran los tests de lecturas viejas); si alguna vez saltara, el `409` ya traducido lo
    cubre. El espejo recién insertado nace bloqueado por A: nadie más lo ve hasta el commit, por eso el insert va después del lock
    de la decisión sin riesgo de ciclo. Al commitear, A deja la *época* del espejo (el `xmin` de su fila).
  - **C, consultar un espejo pendiente que ya existía** (de una llamada anterior, o de una decisión que ya no está pendiente): lock
    de la serie → fila del espejo con `FOR UPDATE` (la lectura única de su estado, fecha, número y época) → `FECompConsultar`
    (I2). Encontrado: lo adopta. No encontrado: toma el lock de la decisión y la relee. Si la decisión sigue pendiente evalúa las
    compuertas (si fallan, anota el error en la decisión y termina) y, si pasan, **guarda la fecha de hoy en el espejo y
    comitea** antes de pedir; su época nueva es el `xmin` de esa fila reescrita. Si la decisión ya no está pendiente, libera el
    espejo (ver "Un espejo pendiente siempre se resuelve"). Cualquier otro resultado de la consulta es no definitivo.
  - **S, pedir el CAE**: lock de la serie → fila del espejo con `FOR UPDATE` → solo sigue si la época del espejo es la que dejó el
    paso anterior *de la misma llamada* (A o C) y la decisión sigue pendiente; si no, otra llamada se metió en el medio (consultó,
    pidió, adoptó o liberó) y se vuelve a evaluar desde cero, que consulta primero. Pide el CAE con la fecha que el espejo ya
    tiene guardada (así la fecha guardada es siempre la que viajó, aunque la transacción se deshaga después del pedido) y aplica
    el resultado.
  - **Cancelar** (la venta está anulada y no hay espejo): fila de la venta con `FOR UPDATE` (la misma que toma A, así que se
    serializan) → relee que la venta siga sin espejo → lock de la decisión y relee que siga `pendiente` → recién entonces cambia el
    estado y revierte el acumulado. Si ya hay un espejo o la decisión ya no está pendiente no hace nada.
  - **Orden de locks** de las tres: `numeraciones_fiscales` (A, C y S; la cancelación no lo toma) → `comprobantes_venta` (la venta
    en A y en la cancelación; el espejo en C y S) → `facturaciones_de_venta` → `acumulados_de_facturacion` (por `id_medio_pago`
    ascendente). Toda transición de una decisión (a `emitida`, `rechazada` o `cancelada`) se hace con su fila bloqueada y su
    estado releído bajo ese lock; en C y S ese lock se toma después de la llamada a ARCA (a lo largo de la llamada solo se
    sostienen la serie y el espejo). La anotación de un fallo es la excepción: un único `UPDATE` guardado por
    `estado = 'pendiente'`, sin lock previo ni lectura previa.
- **Un espejo pendiente siempre se resuelve**, sea cual sea el estado de su decisión. Si el espejo está `pendiente` y su decisión
  ya no lo está (en esta rebanada ningún camino de producción deja una decisión no pendiente con un espejo pendiente; la
  resolución existe como salvaguarda y los tests la ejercen con estados fabricados), igual se consulta (C) y: si ARCA lo
  tiene se adopta su CAE y, de la decisión, solo se enlaza `id_comprobante_fiscal` si no tenía enlace (ningún CHECK de la tabla lo
  prohíbe: solo `emitida` exige enlace); si ARCA no lo tiene se libera (borra ítems y fila y devuelve el número con la guarda del
  contador) sin pedir un CAE nuevo. En los dos casos el estado de la decisión y el acumulado no se tocan y se registra un error en
  el log con la decisión, su estado y el espejo. Si el pedido ya había salido y la decisión cambió mientras ARCA contestaba, el
  resultado se aplica igual al espejo. `ProcesarDecisionAsync` intenta resolver ese espejo antes de devolver el estado de una
  decisión no pendiente; si la consulta no puede correr (falta un prerrequisito), la serie está ocupada o la consulta no es
  definitiva, devuelve ese estado con el espejo todavía pendiente. Un espejo ya resuelto con una decisión que figura pendiente
  se vuelve a leer una vez (otra llamada pudo comitear toda la resolución entre las dos lecturas sin lock): si la decisión ya no
  está pendiente se devuelve su estado actual sin llamar a ARCA; solo si sigue pendiente es una inconsistencia real: `500`, sin
  llamar a ARCA.
- **Consultar antes que las compuertas.** Para un espejo que ya existe, la consulta solo necesita lo que usa: certificado
  activo, CUIT del emisor, número fiscal del punto de venta y el tipo del propio espejo. Si ARCA ya tiene el comprobante se adopta
  aunque la condición fiscal, el cliente o los totales hayan cambiado desde entonces. Las compuertas completas se evalúan solo si
  ARCA no lo tiene (después de la consulta, no antes: una excepción al evaluarlas no impide adoptar un CAE que ARCA ya tiene), y si fallan antes de volver a pedir se registran como siempre. Si falta algo de lo que la consulta necesita,
  no se consulta y se registra la compuerta que corresponda.
- **Estados.**

  | Evento | Espejo | Decisión |
  |---|---|---|
  | Aprobado, aprobado con observaciones, o la consulta lo encuentra | `aprobado` / `aprobado_con_observaciones`, con CAE | `emitida`, con `id_comprobante_fiscal` |
  | Rechazo definitivo | borrado (ítems y fila) y número devuelto | `rechazada`, `ultimo_error` con los errores de ARCA, débito revertido |
  | Sin respuesta definitiva (incluye cualquier error de la consulta que no sea "inexistente") | sigue `pendiente` con su número | `pendiente`, `intentos + 1`, `ultimo_error`, espera de 30 s que se duplica, tope 10 min |
  | Una compuerta falla, sin espejo | no existe | `pendiente`, `intentos + 1`, `ultimo_error`, espera de 5 min que se duplica, tope 4 h |
  | Una compuerta falla, con espejo ya creado y ARCA no lo tiene | sigue `pendiente` con su número | igual que la fila anterior |
  | Una compuerta falla, con espejo ya creado y ARCA lo tiene | adoptado | `emitida` |
  | Venta anulada y sin espejo | no existe | `cancelada`, acumulado revertido |
  | Espejo pendiente y decisión no pendiente | adoptado o liberado, según ARCA | sin cambios (salvo el enlace al comprobante adoptado), error en el log |

  Es definitivo solo lo que está en la lista blanca: una respuesta de `FECAESolicitar` (aprobado, aprobado con observaciones o
  rechazado), un `FECompConsultar` que encuentra el número, y el error de nivel de llamada `arca_rechazo` **solo si** la consulta
  del mismo número confirma que ARCA no lo tiene. Todo lo demás (transporte, falla SOAP, ticket de acceso, numeración
  desincronizada, un error de la consulta) es no definitivo. Las compuertas, que se evalúan sin red, son
  `empresa_sin_condicion_fiscal`, `punto_venta_sin_numero_fiscal`, `condicion_fiscal_receptor_no_mapeada`,
  `certificado_fiscal_ausente`, `empresa_sin_cuit_valido`, `tipo_fiscal_invalido`, `receptor_sin_cuit`,
  `alicuota_sin_mapeo_afip` y `total_fiscal_no_coincide`. La respuesta trae `estado` (`aprobado`, `rechazado`, `pendiente`,
  `cancelado`), `error` y, si está aprobada, `comprobante` (tipo, letra, punto de venta fiscal, número, fecha, CAE, vencimiento,
  QR y observaciones). Procesar una decisión que ya no está pendiente y sin espejo pendiente devuelve ese estado sin ninguna
  llamada a ARCA.
- **Qué es "no encontrado".** `ClienteWsfe.ConsultarAsync` devuelve "no encontrado" solo cuando todos los errores de
  `FECompConsultar` son el código `602` (conjunto `CodigosDeComprobanteInexistente`, en un solo lugar). El `600` es
  `ticket_de_acceso_invalido`; cualquier otro error, o una respuesta sin errores ni datos, se lanza como `arca_no_definitivo`: no
  se refresca la fecha, no se pide un CAE nuevo y se anota el intento y la espera. Esto también vale para el reintento de la emisión
  directa (`ServicioDeFacturacionFiscal.ReintentarAsync`): con un "no encontrado" real se comporta como antes; con otro error de la
  consulta ahora corta con `503` y deja el comprobante pendiente, en vez de tratarlo como inexistente y pedir un CAE nuevo. Ante
  un `600` la consulta tiene el mismo tratamiento que la solicitud (`ConsultarConReintentoDeTicketAsync`): descarta el ticket
  que falló (solo si sigue siendo el cacheado, ver T5b), obtiene uno fresco y consulta una sola vez más, y el pedido que siga en la misma llamada usa el ticket fresco; un
  segundo `600` queda como no definitivo. Vale para `ReintentarAsync`, para el paso C del espejo y para la consulta previa a
  liberar un número tras un `arca_rechazo`.
- **Fecha (regla 2).** El espejo nace con la fecha de emisión (no la de la venta). Si hay que volver a pedir porque ARCA no tiene el
  número, la fecha se refresca al día de hoy en una transacción propia que comitea antes del pedido; por eso la fecha guardada
  es siempre la que viajó. Al adoptar un comprobante que ARCA ya tenía, el espejo toma la fecha que ARCA registró (`CbteFch` de
  `FECompConsultar`): si su día fiscal coincide con el guardado no se toca nada; si no, la fecha pasa al mediodía de ese día en la
  zona de la empresa.
- **Rechazo (reglas 1 y 3).** En el paso que lo aplica y bajo el lock de la serie: `UPDATE numeraciones_fiscales SET
  proximo_numero = N WHERE id_punto_venta = … AND codigo_afip = … AND proximo_numero = N + 1` devuelve el número solo si nadie tomó
  otro después; si afecta una fila se borran los ítems y la fila del espejo (`DELETE FROM items_comprobante_venta` y
  `DELETE FROM comprobantes_venta`, acotados a ese espejo). Si no afecta ninguna fila, no se borra nada: el espejo queda `rechazado`
  con su número, la serie queda desincronizada con ARCA y se registra un error en el log. En los dos casos la decisión pasa a
  `rechazada` y se devuelve solo el débito: `saldo_a_facturar += vendido`, `total_facturado −= vendido` por medio (lo cobrado,
  `importe − vuelto`), de modo que el cupo pasa a los tickets siguientes. El ticket no se modifica nunca. Un rechazo de detalle (una
  respuesta con resultado `R`) es definitivo directamente; un error de nivel de llamada (`arca_rechazo`) lo es solo tras consultar.
- **Acumulado faltante.** Si falta la fila del acumulado de un medio al revertir (rechazo o cancelación), la resolución fiscal no se
  frena: se registra un error en el log (decisión, medio y período) y se sigue con el resto. ARCA ya contestó; frenar acá dejaría
  el espejo pendiente y la serie bloqueada.
- **Cancelación.** Cuando la venta está anulada y no hay espejo, la decisión pasa a `cancelada` y se deshace TODO lo que ella
  aplicó sobre su `periodo`: `saldo_a_facturar −= aporte` (el guardado en la decisión), `total_vendido −= vendido` y
  `total_facturado −= vendido`. Una `rechazada` no se reintenta sola.
- **Anulación de una venta facturada (regla 5).** Ver "Anulación con nota de crédito (T8c)": con la factura aprobada la anulación
  decide la nota de crédito, con la factura en trámite responde `409 venta_con_factura_en_tramite`.
- **La espera registrada.** `proximo_intento_en` se escribe (30 s duplicándose hasta 10 min; 5 min hasta 4 h para una
  compuerta). Este servicio y su endpoint procesan la decisión que se les pide aunque la espera no haya vencido; la respeta el
  procesador por lote (T5b). Tampoco hay todavía quién llame a ninguno de los dos endpoints por sí solo (la emisión en línea de T5c usa el servicio, no el endpoint).
- **Limitaciones aceptadas.** La autorización es `OperacionDePos`, sin una política más angosta. Una lectura idempotente recalcula el QR y
  el punto de venta desde la configuración actual. Al volver a pedir un espejo preexistente, la letra se recalcula desde la
  configuración actual y no desde la del momento en que se creó. Un espejo `rechazado` que quedó por la guarda del contador detiene la
  numeración con el error `10016` de ARCA hasta que alguien la reconcilie (no bloquea la serie por sí mismo) y ocupa el lugar
  del espejo de esa venta: el reintento manual de T9 lo tiene que contemplar. El orden ascendente de los locks del acumulado, el
  alcance de los borrados, las lecturas viejas y las carreras entre pasos están cubiertos por tests de integración (locks
  observados desde otra sesión, pasos pausados con interceptores de transacción, espejos vecinos intactos).
- **Qué está probado y contra qué.** Todo el comportamiento de ARCA descrito acá (qué respuesta es "inexistente", que un error de
  nivel de llamada puede ocultar un número registrado, la fecha `CbteFch` de la consulta, el efecto de los reintentos de
  transporte) está probado SOLO contra espías y fixtures locales; nunca se ejerció contra un ARCA real ni contra homologación. El
  código `602` como "comprobante inexistente" viene de esos fixtures y de la documentación de WSFE y está sin verificar contra el
  servicio real: hay que confirmarlo en homologación antes de depender de él.

**Procesador por lote (T5b): implementado.** `ProcesadorDeFacturacionesPorLote.ProcesarLoteAsync`, expuesto como
`POST /api/fiscal/facturaciones/procesar` (política `OperacionDePos`). Cada decisión se procesa por el mismo
`ServicioDeFacturacionDeVentas.ProcesarDecisionAsync` de T5a, con sus pasos, locks y lista blanca; el lote solo agrega la
selección, el orden, la espera, el presupuesto de tiempo y el resumen. **Todavía nada lo llama** (el POS lo hará en T6) y todo lo
que se dice de ARCA está probado solo contra espías, no contra homologación.

- **Pedido.** `{ "idPuntoVenta": 3, "maximo": 10 }`. `idPuntoVenta` es obligatorio (si falta, `400 solicitud_invalida`); un
  punto de venta inexistente o de otro tenant es `404` ANTES de validar `maximo`. `maximo` ausente vale 10; fuera de 1..25 es
  `400 solicitud_invalida` (se rechaza, no se recorta); un `maximo` que no es entero lo rechaza el binding (`400 cuerpo_invalido`).
- **Selección.** Candidatas: decisiones `pendiente` de ventas de ESE punto de venta, sin espejo (cualquiera sea su estado), cuya
  espera (`proximo_intento_en`) es `NULL` o ya venció (`<=` el reloj del servicio), en orden de id de decisión (las más viejas
  primero), hasta juntar `maximo` intentos. Una decisión cuya espera no venció no se intenta: ni red ni anotación. El endpoint de
  una decisión suelta (`/{id}/procesar`) sigue ignorando la espera.
- **Cabeza de serie.** Un espejo pendiente detiene su serie (punto de venta y tipo fiscal), así que antes de las candidatas el lote
  procesa las decisiones que tienen un espejo pendiente en el punto de venta (a lo sumo una por serie), las más viejas primero:
  se consulta a ARCA antes de volver a pedir, como siempre. Los cabezas no cuentan para `maximo`, para que un espejo trabado no
  deje sin cupo al resto. Si la espera de ese cabeza no venció, ninguna candidata de su serie se intenta en la llamada
  (`cabeza_en_espera`: cero llamadas y sin penalidad); si se intentó y su espejo sigue pendiente, tampoco (`cabeza_pendiente`).
- **Cómo se sabe la serie de una candidata.** Se deduce SIN red con lo mismo que usa el servicio para elegir el tipo fiscal: la
  condición fiscal del emisor (la de la empresa del punto de venta) cruzada con la del cliente de la venta
  (`ResolvedorDeLetraComprobante`) y el código de la factura `F{letra}`. Es una segunda copia de esa regla, no una llamada al
  servicio: si divergieran, la candidata se intenta igual y la serie se aprende del resultado. Además, desde que una decisión
  responde `serie_ocupada` o `serie_bloqueada` no se intenta otra candidata de esa serie en la misma llamada. Las candidatas
  saltadas no gastan cupo. Si no se puede deducir la serie (emisor sin condición, cliente sin condición reconocida), la candidata
  se intenta y una serie "desconocida" bloqueada saltea a las demás candidatas desconocidas.
- **Aislamiento.** Si una decisión lanza (una excepción inesperada o un error de dominio que no es uno de los resultados
  registrados), el lote la anota como un intento fallido con la espera de un fallo no definitivo (intentos + 1, `ultimo_error` y
  `proximo_intento_en` con el mismo retroceso de siempre), la informa como `pendiente` y sigue con la siguiente; el lote no lanza
  por una decisión. El `error` de la respuesta y el `ultimo_error` son SOLO un código estable: el código del error de dominio o
  `error_inesperado`; el tipo y el mensaje de la excepción van únicamente al log del servidor. Como queda en espera, deja de ser la
  candidata más vieja: `maximo` decisiones que lanzan no dejan sin cupo a las más jóvenes en las llamadas siguientes. Lo que no se
  pudo intentar o no se resolvió sigue pendiente y sin penalidad: el lote solo anota lo que anota `ProcesarDecisionAsync`
  (compuertas, falta de respuesta definitiva) y estas excepciones.
- **Falla del propio lote.** Una falla fuera de toda decisión (las lecturas de cabezas y candidatas, la carga de las series) se
  registra y se PROPAGA: el endpoint responde `500 error_interno` por el manejador estándar, y quien llama sabe que no hubo intento
  (antes se tragaba y respondía `200` con todo vacío, indistinguible de "no había nada"). Lo que las decisiones anteriores de la
  misma llamada ya comitearon (espejos, CAE) queda comiteado; la llamada siguiente lo retoma. Esos resultados NO están en ninguna
  respuesta: la llamada siguiente no los informa porque ya no están pendientes. Quien llama puede leerlos con el endpoint de una
  decisión suelta (`/{id}/procesar`), que devuelve el estado actual de forma idempotente; para eso necesita los ids de las
  decisiones, y la respuesta fallida no los trae.
- **Presupuesto.** 20 s por defecto (`Ways:Fiscal:PresupuestoDeLoteSegundos`, valor ausente, no positivo o NaN = 20; un valor
  positivo menor que 1 s se sube a 1 s; tope 300 s). Al vencer el presupuesto el lote no empieza ninguna decisión nueva (la que no
  se intentó no se posterga ni se anota) y el trabajo en vuelo contra WSFE y la base de datos se cancela y se deshace; solo una
  firma de WSAA en vuelo se deja terminar. Por eso un pedido puede pasarse del presupuesto: con los valores por defecto, del
  orden de presupuesto más timeout de WSAA (20 s + 30 s) cuando una firma arranca sobre el final del presupuesto, más hasta unos
  10 s para los pasos de cierre (anotar o postergar la decisión cortada y contar las pendientes, 5 s como máximo cada uno) si la
  base está lenta; y puede seguir una segunda firma dentro de la misma decisión si ARCA rechaza el ticket. Es una aproximación,
  no un máximo exacto. Una llamada colgada a WSFE termina en su timeout (8 s). El token del presupuesto es un `CancellationTokenSource`
  propio creado desde el `TimeProvider` inyectado (`TimeProvider.System` en producción): no usa el
  token de la solicitud, así que un cliente que corta la conexión no deja el lote a medias. Un corte detiene el lote
  (`cortadoPorPresupuesto: true`) y NO es un fallo de la decisión que se estaba procesando: el servicio solo anota un intento cuando
  su token sigue vivo, y todas las transacciones del paso en curso se deshacen (un espejo ya creado queda `pendiente` con su
  número, y la llamada siguiente lo consulta antes de pedir). La decisión cortada se POSTERGA 1 minuto
  (`ServicioDeFacturacionDeVentas.EsperaPorCorteDeLote`): un único `UPDATE ... WHERE estado = 'pendiente'` que fija
  `proximo_intento_en = reloj + 1 min` con un token corto propio (el del lote ya está cancelado) y no toca `intentos` ni
  `ultimo_error`; nunca lanza. Así la llamada siguiente llega a las demás decisiones en vez de volver a cortar siempre en la más
  vieja; si la postergada es la cabeza de su serie, esa serie se saltea (`cabeza_en_espera`) hasta que venza. La decisión cortada
  no aparece en `resultados`. Excepción: si lo que lanzó pasado el presupuesto es un error de dominio (una falla real que terminó
  tarde, como una firma de WSAA que venció con `arca_no_definitivo`), no es una cancelación: se anota como intento fallido con su
  código y su espera creciente, aparece en `resultados` como `pendiente` con ese error, y el lote se corta igual
  (`cortadoPorPresupuesto: true`). Limitaciones que quedan: una decisión que ya no está `pendiente` (solo conserva un espejo
  pendiente) no se puede postergar NI se le puede anotar un intento fallido; si su llamada a ARCA siempre agotara el
  presupuesto, seguiría siendo la primera cabeza de cada llamada. Además, cortes repetidos postergan la decisión un minuto cada
  vez, sin espera creciente. El timeout de WSFE (8 s) es menor que el presupuesto (20 s), así que una llamada colgada a WSFE termina
  dentro de él y se anota como intento fallido con su espera en lugar de cortar el lote. Una firma colgada de WSAA (30 s) sobrevive
  al presupuesto: cuando por fin falla se anota como intento fallido (rama del error de dominio tardío), y cuando por fin termina
  bien pasado el presupuesto el ticket queda cacheado y la decisión se posterga un minuto sin contar un intento.
- **Respuesta.** `{ emitidas, rechazadas, canceladas, pendientesRestantes, cortadoPorPresupuesto, seriesBloqueadas[],
  resultados[] }`. `resultados` trae, en el orden en que se procesaron, `{ idFacturacion, idComprobanteVenta, fiscal }` con el
  mismo `EstadoFiscalDeVenta` de la venta (también para lo que quedó `pendiente` con su error, p. ej. `serie_ocupada`); los tres
  contadores salen de ese estado, así que una decisión ya resuelta por otra llamada igual cuenta. `pendientesRestantes` son las
  decisiones `pendiente` del punto de venta al terminar, estén o no vencidas, e incluye las que el lote nunca puede seleccionar
  (una decisión `pendiente` cuya venta tiene un espejo que no está pendiente: el estado de inconsistencia que el servicio
  informa como error). `seriesBloqueadas` trae `{ codigoAfip, motivo }` con
  motivo `cabeza_en_espera`, `cabeza_pendiente`, `serie_ocupada` o `serie_bloqueada` (`codigoAfip` es `null` si no se pudo saber la
  serie). Las lecturas son del punto de venta pedido y de su tenant (RLS); nada de otros puntos de venta ni tenants aparece.
- **Concurrencia.** Dos lotes del mismo punto de venta no duplican nada: la serie se toma con `FOR UPDATE SKIP LOCKED` y la decisión
  se relee bajo su lock en cada paso. El que llega mientras el otro sostiene la serie la ve ocupada (`serie_ocupada`) y no
  consulta ni pide nada; si llega cuando la decisión ya se resolvió, devuelve su estado sin llamar a ARCA.
- **Timeout de las llamadas a ARCA.** `Ways:Fiscal:TimeoutDeLlamadaSegundos` (por defecto 8; tope 120; valor ausente, no positivo
  o NaN = 8; un valor positivo menor que 1 ms se sube a 1 ms) es el `Timeout` del `HttpClient` de WSFE: vale por cada ida y vuelta.
  WSAA tiene el suyo, `Ways:Fiscal:TimeoutDeWsaaSegundos` (por defecto 30, mismas reglas y tope), más largo que el de WSFE y que
  el presupuesto del lote a propósito, porque la firma no se interrumpe una vez en vuelo: WSAA no
  emite un ticket nuevo mientras hay uno vigente (`coe.alreadyAuthenticated`), así que abandonar una firma cuya respuesta se
  perdió bloquea todo el facturado de ese emisor hasta que el ticket vence. Un timeout NO es un rechazo ni una cancelación del
  llamador. `ClienteWsfe` NO reintenta un timeout, en NINGUNA operación: es de inmediato `arca_no_definitivo` (503) con UN solo pedido
  enviado. Por dos razones: para `FECAESolicitar`, la seguridad del reenvío (ARCA pudo haber procesado el pedido y reenviarlo
  puede devolver un error de numeración en lugar del CAE; lo resuelve el "consultar primero" del intento siguiente); y para todas
  las operaciones, incluidas las lecturas (consulta, último autorizado, parámetros), que sí se podrían reenviar, que un solo
  intento acotado por el timeout es lo que deja que una llamada colgada termine dentro del presupuesto del lote y se anote. El
  costo: una lectura lenta ahora falla donde antes tres intentos la resolvían, y un CAE que ARCA ya emitió se adopta un retroceso
  más tarde. Los demás fallos transitorios (error de
  conexión, HTTP 5xx) conservan el reintento acotado de 3 intentos con retroceso. Para el circuito un timeout cuenta como una
  llamada fallida más. `ClienteWsaa` lo traduce al mismo código `arca_no_definitivo`. El resultado es no definitivo: el espejo
  sigue `pendiente` con su número y se anota el intento (con su espera). Con el token cancelado (el presupuesto del lote) la
  cancelación se propaga tal cual, sin reintentar y sin contar contra el circuito. **Firma de WSAA en vuelo:** el single-flight
  (`RepositorioEnMemoriaDeTicketDeAcceso.ObtenerOFirmarAsync`) no cancela la firma con el token del llamador (la acota solo el
  timeout de WSAA); guarda el ticket y recién entonces lanza la cancelación del llamador, así que un corte del lote durante la
  firma no la abandona y el lote siguiente reutiliza el ticket. Los que esperan el cerrojo sí se cancelan con su token; el costo
  es que un corte puede demorar hasta el timeout de WSAA mientras termina de firmar. **Cambia también la emisión directa (19a):**
  antes una llamada colgada esperaba los 100 s por defecto de `HttpClient` por intento y se reintentaba; ahora corta a los 8 s y NO
  se reintenta tras un timeout, la falla de WSAA por timeout es `arca_no_definitivo` (503) en vez de una excepción de transporte
  sin traducir, y la firma de un ticket tampoco se cancela con la cancelación de la solicitud.
- **Invalidación del ticket de acceso (comparar y descartar).** `IRepositorioDeTicketDeAcceso.InvalidarAsync(clave, fallido)` solo
  quita el ticket cacheado si es el que falló (igualdad por valor del ticket). Antes quitaba el que estuviera: dos llamadas que
  recibían un `600` con el mismo ticket viejo podían descartar el fresco que la primera acababa de firmar, y WSAA no emite uno nuevo
  mientras hay uno vigente. Ahora, si el ticket ya fue reemplazado, la invalidación no hace nada y la llamada reutiliza el fresco
  por `ObtenerOFirmarAsync`; en total se firma una sola vez. `SolicitarCaeConReintentoDeTicketAsync` y
  `ConsultarConReintentoDeTicketAsync` pasan el ticket con el que ARCA respondió `600`.

**Emisión en línea al cobrar (T5c): implementada.** `EmisionEnLineaAlCobrar.DecidirEmitirYSanearAsync(idVenta, idPuntoVenta)`,
que llama `POST /api/ventas` después del commit de la venta (reemplaza a la llamada directa a `DecidirYSanearAsync`, que queda
solo para los tests de la decisión). Intenta UNA vez emitir la factura espejo antes de responder, para que con conexión la caja
reciba CAE, número y QR en la respuesta de la propia venta. La venta nunca falla ni se frena por esto y ningún paso recibe el token
de la solicitud (la venta ya está comiteada). **El POS todavía no lee esta respuesta (T6) y todo lo que se dice de ARCA está
probado solo contra espías, no contra homologación.**

- **Tres pasos y un tope de 8 s.** El tiempo se cuenta desde que la venta se confirma, con el `TimeProvider` inyectado
  (`TimeProvider.System` en producción): (a) la decisión (`DecidirTrasLaVentaAsync`, sin cambios, con su presupuesto propio de 3 s);
  (b) solo si la decisión quedó `pendiente`, un intento de emisión con `min(4 s, lo que queda de los 8 s)`; (c) el barrido de las
  decisiones debidas con `min(3 s, lo que queda)`. Si no queda tiempo, ni el intento ni el barrido empiezan; el barrido tampoco corre
  cuando la decisión terminó `sin_decidir`, como antes. Una decisión `no_corresponde` o `sin_decidir` no hace ninguna llamada a ARCA.
- **Sin firmar en la caja.** La firma del ticket de acceso ante WSAA no se interrumpe una vez en vuelo y puede tardar hasta 30 s
  (T5b), así que el intento corre SOLO si el emisor ya tiene un ticket vigente en el cache
  (`ServicioDeFacturacionFiscal.HayTicketVigenteAsync`: `IRepositorioDeTicketDeAcceso.ObtenerVigenteAsync` con la clave empresa +
  ambiente configurado + `wsfe`, la misma que usa la emisión). Sin ticket cacheado el intento se salta entero: no se crea espejo,
  no se toma número, no se anota intento y la venta responde `pendiente`; el procesador por lote firma y emite después. Un ticket
  dentro del margen de seguridad de 10 minutos de su vencimiento cuenta como no cacheado. El cache es en memoria de cada
  proceso: con más de una instancia de la API el intento en línea solo corre en la que tiene el ticket; tras un reinicio, o cuando el
  ticket entra en su margen de seguridad, el intento no corre y la respuesta no da ningún motivo (`pendiente`, con a lo sumo un error viejo de la decisión), hasta que
  alguien lo firme. Hoy nadie llama al lote (T6), así que
  hasta entonces el intento en línea solo corre si otra llamada (la emisión directa o los endpoints de procesamiento) dejó el ticket
  cacheado.
- **Lo que no impide la regla del ticket.** Con el ticket cacheado un instante antes, `ProcesarDecisionAsync` todavía podría firmar
  en dos caminos, y ahí la firma no se interrumpe: si el ticket cruza el margen entre la verificación y el pedido de ese servicio, o
  si ARCA responde `600` (ticket inválido) a la solicitud o a la consulta y el servicio lo descarta y firma uno nuevo. Es una
  limitación aceptada: no se reestructuró el servicio para evitarlo.
- **Qué lleva la respuesta (`fiscal`).** `fiscal.estado` es uno de seis: `no_corresponde` (al ticket no le corresponde factura),
  `sin_decidir` (la decisión falló y se completa en un reenvío o en el barrido), `pendiente` (se decidió facturar y la factura no
  está emitida), `aprobado`, `rechazado` y `cancelado` (la venta se anuló antes de procesarse su facturación: se ve en un reenvío
  de la venta, o si la anulación llega durante el cobro). Emitida: `aprobado` con `comprobante` (tipo, letra, punto de venta
  fiscal, número, fecha, CAE, vencimiento, QR y observaciones; las mismas que las de la fila del espejo). Rechazada por ARCA:
  `rechazado` con `error`; la venta no se toca, el espejo se borra y el número vuelve a la serie (T5a). Sin respuesta definitiva,
  compuerta que falla, serie ocupada o bloqueada: `pendiente` con el `error` del servicio, y lo que el servicio anota (intento,
  espera) queda como él lo anota. Sin intento (sin ticket, sin tiempo): `pendiente` con el último error ya anotado de la decisión,
  si lo hay.
- **`fiscal.error` es solo un código.** El texto que se anota en `ultimo_error` puede traer el mensaje crudo de una excepción
  (HTTP, Npgsql, WSAA, certificados, configuración) o los mensajes de ARCA, y la respuesta de la venta llega a cualquier cajero, en
  cada venta y en cada reenvío. Por eso `EmisionEnLineaAlCobrar.CodigoDeError` se aplica a todo lo que esa clase devuelve (el
  resultado del intento, el estado leído en un reenvío o sin intento, y la excepción): se queda con lo que está antes del primer
  `:` si son solo minúsculas, dígitos y `_` (o con todo el texto si es solo eso), y en cualquier otro caso informa
  `error_inesperado`. Ejemplos: `arca_no_definitivo: HTTP 500 ...` pasa a `arca_no_definitivo`; `serie_bloqueada: ...` a
  `serie_bloqueada`; `HttpRequestException: ...` a `error_inesperado`; un rechazo de ARCA anotado como
  `10015: Rechazado por el receptor.; 10016: ...` a `10015` (solo el primer código; el cajero ve el estado `rechazado` y el
  detalle queda en la base). Un rechazo anotado sin errores (`ARCA rechazó el comprobante.`) también se informa como
  `error_inesperado`. **El endpoint de procesamiento de una decisión y el del lote siguen devolviendo el texto anotado tal cual**
  (fuera de alcance de este cambio).
- **Un corte no es un fallo.** Si el presupuesto del intento vence con la llamada en vuelo, la respuesta es `pendiente` sin `error`,
  no se suma ningún intento ni se anota error, y un espejo ya creado queda `pendiente` con su número: el próximo procesamiento lo
  resuelve consultando antes de pedir. El corte también puede caer después de que ARCA respondió y antes del commit local: el
  espejo queda `pendiente` aunque ARCA ya tenga el CAE, y el próximo procesamiento lo adopta consultando primero (no se pide un
  segundo CAE). La decisión NO se posterga (a diferencia del lote): lo que sigue en la caja es justamente el
  lote, y una espera de un minuto solo haría que se la saltee. Un error de dominio que llega pasado el corte sí se anota como
  intento fallido, como en el lote.
- **Consecuencia aceptada: un espejo pendiente bloquea la serie.** Tras un corte (o cualquier resultado no definitivo) con el
  espejo ya creado, ese espejo conserva su número `pendiente`. Mientras siga así, el intento en línea de toda venta posterior del
  mismo punto de venta y tipo fiscal encuentra la serie ocupada o bloqueada (`serie_ocupada` / `serie_bloqueada`), responde
  `pendiente` con ese código y no anota nada, y el barrido no resuelve espejos. Solo lo libera el procesador por lote, el endpoint de
  procesamiento de una decisión o el reenvío de esa misma venta. Hasta que el POS llame al lote (T6) nadie lo llama. Es el
  compromiso buscado: la caja nunca se bloquea y esos tickets se facturan después.
- **Una excepción no falla la venta.** Cualquier excepción del intento responde `201` con `pendiente` y `error` = el código del
  error de dominio o `error_inesperado`, y se anota como intento fallido (`RegistrarFalloInesperadoAsync`, con su espera de un
  fallo no definitivo), igual que el lote. El detalle va solo al log.
- **Reenvío (mismo número pre-asignado).** El endpoint devuelve la venta ya registrada y vuelve a llamar a la emisión en línea: una
  decisión ya resuelta (`aprobado`, `rechazado`, `cancelado`) se informa tal como está, con los datos de la factura si está emitida,
  sin ninguna llamada a ARCA (`ServicioDeFacturacionDeVentas.LeerEstadoAsync`, que ni siquiera resuelve un espejo pendiente
  que conserve una decisión ya cerrada). Una decisión todavía `pendiente` vuelve a intentar bajo las mismas reglas (ticket
  cacheado, presupuesto y tope), consultando antes de pedir si ya hay un espejo: nunca un segundo espejo ni un segundo número.
- **Lo que puede pasarse del tope de 8 s.** La decisión (su presupuesto de 3 s y la anotación de su fallo, de hasta 1 s, usan reloj real
  y no se recortan), la anotación del fallo de un intento y la lectura final del estado (1 s como máximo cada una, aunque el tope
  ya se haya agotado), la anotación de cada fallo del barrido (1 s de reloj real por venta que falla, sin recortar al tope; el barrido
  puede anotar varias en una pasada mientras su propio token no venza) y una firma de WSAA que arranque dentro del intento (hasta el
  timeout de WSAA, 30 s), que no se interrumpe: por un `600` de ARCA al ticket cacheado o porque el ticket cruce su margen entre la
  verificación y el uso.
- **Fuera de alcance.** Tampoco cambia lo que `ProcesarDecisionAsync` anota, ni la autorización. Las lecturas de una venta se
  describen en "Lecturas de una venta (T6a)".

#### Lecturas de una venta (T6a)

Las dos lecturas del POS informan el estado fiscal sin decidir, emitir ni llamar a ARCA: no toman locks y no escriben.

- **`GET /api/ventas/{id}` (reimpresión).** Devuelve `ComprobanteEmitido` con `fiscal` completo, igual que la emisión: el estado, el motivo, el
  id de la decisión y, si está `aprobado`, `comprobante` con los datos para reimprimir la factura. `error` es solo el código
  (`EmisionEnLineaAlCobrar.CodigoDeError`), nunca el texto anotado. Se completa en el endpoint con
  `ServicioDeFacturacionDeVentas.LeerEstadoDeVentaAsync`, no en `ServicioDeVentas.ObtenerAsync`, que también usa la anulación (la
  respuesta de una anulación sigue con `fiscal` en `null`, "no consultado"). Con fila de decisión informa lo que informaría
  `LeerEstadoAsync`; sin fila, `sin_decidir` si la venta no tiene la marca `facturacion_decidida_en` y no es un espejo, y
  `no_corresponde` en cualquier otro caso (decidida sin facturar, o la propia factura espejo, que nunca se decide).
- **Datos legales de la factura (T7b).** `comprobante` (`DatosDeFacturaFiscal`) agrega al final, sin tocar lo anterior, `emisor`,
  `receptor` y `totales`, para que la factura impresa lleve todo lo legal:
  - `emisor`: razón social, nombre de fantasía y CUIT de la empresa, el nombre de su condición fiscal (`null` si no tiene),
    `ingresosBrutos`, `inicioActividades` y el `domicilio` del punto de venta.
  - `receptor`: nombre a imprimir (la razón social del cliente si la tiene; si no, nombre y apellido), el documento tal como viajó
    a ARCA (`codigoTipoDocumento` 80 CUIT, 86 CUIL, 96 DNI, 94 Pasaporte, 99 sin identificar con número 0, y su etiqueta en
    `tipoDocumento`), el nombre de su condición fiscal y su domicilio.
  - `totales`: `impNeto`, `impIva`, `impOpEx`, `impTotConc`, `impTrib`, `impTotal` y `alicuotas` (porcentaje, neto e IVA de cada
    alícuota gravada). Se recomponen con `ComposicionDeTotalesFiscales.Componer` sobre las líneas del snapshot del espejo
    (`items_comprobante_venta`), la misma composición que la emisión, así que coinciden con lo que se mandó a ARCA; la
    alícuota del catálogo solo aporta nombre y código AFIP, y se lee aunque esté dada de baja.
  Siguen valiendo las lecturas que ignoran solo la baja lógica y el filtro de tenant, y el respaldo de T6a: si armar cualquiera de
  estos datos falla, la lectura informa el estado de la decisión sin `comprobante`.
- **`GET /api/ventas/por-turno/{idTurno}`.** Cada fila lleva `estadoFiscal`: solo el estado (`no_corresponde`, `pendiente`,
  `sin_decidir`, `aprobado`, `rechazado` o `cancelado`), sin la factura ni el error, que se piden por id. Se calcula con una única consulta
  por las decisiones de todas las ventas del turno (`id_comprobante_venta IN (...)`) y la misma regla de mapeo para las ventas sin fila.

#### Nota de crédito fiscal (T8b): implementada

El mismo `ServicioDeFacturacionDeVentas` y el mismo lote emiten la nota de crédito (NC) de una venta anulada cuya factura está
aprobada, sin cambios de esquema. La decisión de la nota la inserta la anulación (T8c, más abajo).

- **Clase de decisión.** `id_facturacion_origen` con valor es la decisión de una nota, y su espejo es el que tiene
  `id_comprobante_asociado` (el espejo de la factura). Cada decisión mira el espejo de su clase; una venta anulada con nota tiene
  dos decisiones y dos espejos con el mismo `id_venta_origen`.
- **Esperas.** La decisión de la nota queda `pendiente` con la espera de una compuerta (5 min duplicándose hasta 4 h) mientras la
  venta no esté `anulado` (`nota_de_credito_venta_no_anulada`) o no tenga una factura aprobada o aprobada con observaciones
  (`nota_de_credito_sin_factura_aprobada`). No hay número, espejo ni llamada a ARCA. La regla "venta no emitida → cancelar" es solo
  de la factura.
- **Qué se pide.** El tipo es el `NC` de la letra del tipo de la factura (NCA/NCB/NCC, códigos ARCA 3/8/13), que tiene que ser fiscal
  y activo (`tipo_fiscal_invalido`). El documento, la condición IVA del receptor y el CUIT del emisor salen de los datos legales
  congelados de la factura, nunca de las filas actuales (solo una factura anterior a esa columna usa las actuales). Los importes y los
  ítems son los del espejo de la factura, en positivo: el signo lo pone `tipos_comprobante.signo`; el total fiscal tiene que
  coincidir con el de la factura (`total_fiscal_no_coincide`). `CbtesAsoc` lleva la factura: su tipo ARCA, el punto de venta
  fiscal, su número, el CUIT del emisor congelado y su fecha fiscal. La fecha de la nota es el día de emisión.
- **Espejo.** La transacción A bloquea la serie de la nota (`numeraciones_fiscales`, punto de venta y código `NC`), después la venta,
  y bajo ese lock vuelve a verificar que la venta siga `anulado`, que no haya ya un espejo de nota, que la decisión siga pendiente y
  que la factura siga aprobada. El espejo copia cliente, importes, ítems y datos legales de la factura, y lleva
  `id_comprobante_asociado` = la factura e `id_venta_origen` = la venta. El orden de locks y la estrategia sin reintento son los de
  la factura. Las consultas, la adopción, la liberación del número ante un rechazo y el QR (con el tipo de la nota) valen para
  cualquier tipo.
- **Resultado.** Aprobada: la decisión de la nota pasa a `emitida` y, si la decisión de la factura está `emitida`, se revierte por
  completo su cupo (aportes inversos, `total_vendido` y `total_facturado` sobre el período de la factura), igual que una cancelación;
  se lee sin lock porque `emitida` es terminal, y en cualquier otro estado se deja el acumulado como está y se loguea. Rechazada: se
  libera el número, la decisión queda `rechazada` con el motivo, no se toca el acumulado y no se reintenta sola (T9).
- **Lote.** Procesa las dos clases. La cabeza de una serie es la decisión del espejo pendiente de su clase; la serie de una
  candidata nota sale de la letra del espejo de su factura. Las notas pendientes cuentan en `pendientesRestantes`.
- **Factura rechazada de una venta que se anula.** El rechazo solo había devuelto el débito (`saldo += vendido`,
  `total_facturado −= vendido`), así que la venta anulada seguía devengando y contando como vendida. Procesar esa decisión
  (el endpoint de la decisión suelta) la pasa a `cancelada`, conserva el motivo del rechazo y termina de revertir el acumulado
  (aportes y `total_vendido`), bajo los locks de la venta y de la decisión y una sola vez. El lote NO la toma como candidata (no
  toma número ni tiene espera, y recorrería el historial de decisiones del punto de venta en cada llamada): se liquida por la
  decisión suelta o, desde T8c, en la transacción de la anulación de la venta.

#### Anulación con nota de crédito (T8c): implementada

`POST /api/ventas/{id}/anulacion` anula el ticket como siempre (stock y cuenta corriente) y, en esa misma transacción sin
reintento y bajo el lock de la fila de la venta que toma el `UPDATE` de la anulación, resuelve qué hacer con la facturación. La regla
(`ReglaDeComprobantes.ResolverEfectoFiscalDeAnulacion`) parte del resultado de la factura de la venta: el del espejo sin
`id_comprobante_asociado` o, si no hay espejo, `rechazado` cuando la decisión de la factura quedó `rechazada` (el espejo que ARCA
rechaza se libera):

| La factura de la venta | La anulación |
|---|---|
| Ninguna (sin decisión, o decisión `pendiente` sin espejo) | Sin efecto fiscal. Una decisión pendiente la cancela después el lote, como antes. |
| `rechazada` (ARCA rechazó) | Liquida la factura rechazada (decisión a `cancelada`, acumulado revertido, una sola vez) en la misma transacción. |
| `pendiente` (en trámite) | `409 venta_con_factura_en_tramite`; nada cambia. |
| `aprobado` o `aprobado_con_observaciones` | Inserta la decisión de la nota de crédito. |

- **Decisión de la nota.** `SELECT … FROM facturaciones_de_venta WHERE id_comprobante_venta = … AND id_facturacion_origen IS NULL FOR
  UPDATE` (orden de locks: la venta, la decisión) e `INSERT` de la fila `pendiente`, `aportes = '[]'`, `intentos = 0`,
  `proximo_intento_en` NULL, con `id_empresa`, `motivo` y `periodo` de la decisión de la factura e `id_facturacion_origen` = esa
  decisión. Una factura aprobada sin decisión es una invariante rota (`InvalidOperationException`, rollback). La anulación NUNCA toca
  `numeraciones_fiscales`: el paso A del proceso toma la serie antes que la venta y se cruzaría. El número lo toma el proceso fiscal.
  Una segunda nota (carrera) cae en los únicos de T8a: `409 nota_de_credito_duplicada`; dos anulaciones concurrentes se serializan
  en el `UPDATE` y la segunda recibe `409 comprobante_ya_anulado`.
- **Intento en línea.** Con la anulación comiteada, el endpoint llama a `EmisionEnLineaAlCobrar.IntentarNotaDeCreditoAsync`: busca la
  decisión de la nota de la venta y, solo con un ticket de acceso ya cacheado (nunca firma ante WSAA), la procesa con un presupuesto
  de 4 s. Nunca lanza ni recibe el token de la solicitud; un fallo inesperado se anota como intento fallido y lo que no sale lo emite
  el lote.
- **Respuesta y lectura (`fiscal.notaDeCredito`).** La respuesta de la anulación y `GET /api/ventas/{id}` informan el estado fiscal con
  la factura intacta (`estado` `aprobado`, su `comprobante`) y, con una nota decidida, `notaDeCredito = { estado, idFacturacion,
  error, comprobante }`: `estado` es `pendiente`, `aprobado` o `rechazado`; `error` el código estable del último motivo anotado
  (`10015`, `nota_de_credito_venta_no_anulada`, …) y `comprobante` el espejo de la nota, solo aprobada, con el mismo contrato que la
  factura (`codigoTipoComprobante` NCA/NCB/NCC). El listado por turno agrega `estadoNotaDeCredito` (los mismos tres valores o
  `null`) con una consulta más por lote de ventas, sin N+1.

### Presupuestos (Etapa 17)

La intención comercial colocada sobre un cliente ANTES de que exista un `comprobantes_venta`
(doc-11:307-324). Estructural mirror de `ordenes_compra` (etapa 16, proposal decisión 1): el
checkout queda byte-idéntico por construcción porque un presupuesto vive en su propia tabla,
nunca en `comprobantes_venta`. Greenfield: doc-01 y `alsina/` muestran cero hits para
`presupuesto` (explore.md:15) — cada regla acá es una decisión, no un port.

```sql
presupuestos (                 -- [operativa]
    id_presupuesto, id_tenant, id_punto_venta,
    id_cliente,                                    -- Consumidor Final por defecto, como la venta
    id_empleado,                                   -- quién lo creó
    numero                 bigint      NULL,       -- correlativo propio por PV, serie 'PRES';
                                                    -- se asigna únicamente al ENVIAR
    fecha_emision           timestamptz NOT NULL,   -- IRelojDelSistema, sin DEFAULT now()
    fecha_envio             timestamptz NULL,       -- par de `numero`
    vencimiento             date        NULL,       -- NOT NULL desde 'enviado' (CHECK 1)
    observaciones,
    subtotal, descuento_total, total,
    estado estado_presupuesto NOT NULL             -- enum: borrador | enviado | convertido
                                                    --     | anulado
);

items_presupuesto (
    id_item, id_tenant, id_presupuesto, orden,
    id_articulo,                        -- NOT NULL: una línea sin artículo no puede convertirse
                                         -- en una línea de venta que mueve stock
    descripcion text,                   -- snapshot: el cliente lee un nombre
    cantidad         numeric(12,3),
    precio_unitario  numeric(14,2),     -- congelado al guardar el borrador — la provisión que
                                        -- la conversión reutiliza, nunca re-resuelta
    descuento        numeric(14,2) NOT NULL DEFAULT 0,
    total            numeric(14,2),
    id_lista_precio  integer,           -- procedencia del precio ofrecido, congelada
    id_oferta        integer NULL,
    id_alicuota_iva  integer, porcentaje_iva numeric(5,2)
    -- SIN id_area, SIN codigo_barra, SIN costo_unitario/costo_es_estimado, SIN id_lote:
    -- deliberadamente más angosta que items_comprobante_venta (un presupuesto no reserva
    -- stock ni congela un costo).
);
```

**Estado (Etapa 17, DB CHANGE GATE ejercido y aprobado en slice 1):
implementada — etapa completa (PRs #1-#8).** Creadas por la migración
`PresupuestosEtapa17`, junto con el ALTER de `comprobantes_venta` (§4) y el data statement 1
del cierre del PRE (§1). `EntidadBase`: **SÍ** en las dos tablas — un presupuesto es mutable
durante `borrador` (replace-set completo bajo `SELECT … FOR UPDATE`) y se edita de nuevo en
`enviar`/`anular` — hereda el filtro de tenant estándar y `EstamparTenant()`, sin filtro
clonado, mismo criterio que `ordenes_compra`. `ux_presupuestos_numero` es **UNIQUE PARCIAL**
`WHERE numero IS NOT NULL` — un borrador no tiene número; `ck_presupuestos_envio_completo`
exige que `numero`/`fecha_envio`/`vencimiento` lleguen juntos, salvo `anulado` (un borrador
puede anularse antes de enviarse). `estado_presupuesto` se registra únicamente vía
`npgsql.MapEnum<EstadoPresupuesto>` en `DependencyInjection.cs`/`WaysDbContextFactory.cs` —
nunca también con `HasPostgresEnum`. `ManejadorDeErrores.cs` gana 5 ramas exactas: 3 `23505`
(`ux_presupuestos_numero` → `numero_de_presupuesto_duplicado`, **cuarta ocurrencia** del
ordering trap, resuelta por encima de `ClasificarUnicidad`;
`ux_comprobantes_venta_presupuesto_origen` → `presupuesto_ya_convertido`;
`ux_items_presupuesto_orden` → `orden_de_item_duplicado`) + 2 `23514` (las dos CHECKs de este
slice). `ReglaDePresupuestos` (`Ways.Domain.Ventas`, sin base de datos) es la única autoridad
de `EstaVencido`/`EsConvertible` — `vencimiento < hoy` (nunca `<=`), resuelto siempre en la
zona horaria del punto de venta. `ServicioDePresupuestos` (draft CRUD, `enviar` con la serie
`'PRES'`, `anular`, la lectura con `vencido` derivado) es slice 2; la conversión dentro de la
transacción de venta (`EscriturasDePresupuesto`, el guard en `ServicioDeVentas`) es slice 3;
`Presupuestos.tsx`/`Presupuesto.tsx` + la entrada de conversión en `Pos.tsx` son slice 7 —
mergeados, la etapa cierra en slice 8 (regla 19 del programa: este header nunca queda claiming
"implementada" mientras un write path siga sin mergear).

### Remitos (Etapa 17)

La mercadería que sale bajo su propio documento, ANTES de la factura que la consolida
(doc-11:307-324, proposal decisión 6/7 del explore). Estructural mirror de `presupuestos`: vive
en su propia tabla, nunca en `comprobantes_venta` — la consolidación (`ServicioDeFacturacionDeRemitos`,
slice 6) recién crea el `comprobantes_venta` (tipo `TXR`, SIN items, N remitos → 1) que la
factura.

```sql
remitos (                      -- [operativa]
    id_remito, id_tenant, id_punto_venta,
    id_cliente, id_empleado,
    numero                  bigint      NULL,       -- correlativo propio por PV, serie 'REM';
                                                     -- se asigna únicamente al EMITIR
    fecha_emision            timestamptz NOT NULL,   -- creación del borrador (IRelojDelSistema)
    fecha_salida             timestamptz NULL,       -- par de `numero`: cuándo salió la mercadería
    direccion_entrega        text NULL,
    observaciones,
    subtotal, descuento_total, total,
    estado estado_remito NOT NULL,                  -- enum: borrador | emitido | facturado
                                                     --     | anulado
    id_comprobante_venta     integer NULL           -- la factura consolidada (N remitos → 1);
                                                     -- id_tipo_comprobante = 'TXR'
);

items_remito (
    id_item, id_tenant, id_remito, orden,
    id_articulo,                        -- NOT NULL: un remito entrega mercadería, nunca un
                                         -- servicio
    descripcion text,
    cantidad         numeric(12,3),
    precio_unitario  numeric(14,2),
    descuento        numeric(14,2) NOT NULL DEFAULT 0,
    total            numeric(14,2),
    id_lista_precio  integer,
    id_oferta        integer NULL,
    id_alicuota_iva  integer, porcentaje_iva numeric(5,2),
    costo_unitario    numeric(14,2) NULL,     -- congelado al SALIR la mercadería (etapa 9)
    costo_es_estimado boolean NOT NULL DEFAULT false,
    id_lote           integer NULL            -- FEFO resuelto y congelado, como el ítem de venta
    -- A diferencia de items_presupuesto, ESTA línea SÍ congela costo y lote: la mercadería
    -- efectivamente sale por este write site.
);
```

**Estado (Etapa 17, DB CHANGE GATE ejercido y aprobado en slice 4):
implementada — etapa completa (PRs #1-#8).** Creadas por la migración `RemitosEtapa17`, junto con el `ALTER TYPE
motivo_stock ADD VALUE 'remito'` (§6, **el único artefacto IRREVERSIBLE de la etapa**, aceptado y
registrado) y el ALTER de `movimientos_stock` (§6, `id_remito`). `EntidadBase`: **SÍ** en las dos
tablas, mismo criterio que `presupuestos`. `ux_remitos_numero` es **UNIQUE PARCIAL** `WHERE
numero IS NOT NULL`; `ck_remitos_salida_completa` exige que `numero`/`fecha_salida` lleguen
juntos, salvo `anulado`; `ck_remitos_facturacion` exige que `estado = 'facturado'` y
`id_comprobante_venta` viajen JUNTOS, en ambas direcciones — el desligue de la anulación de un
`TXR` (slice 6) los limpia a la vez. `estado_remito` se registra únicamente vía
`npgsql.MapEnum<EstadoRemito>` en `DependencyInjection.cs`/`WaysDbContextFactory.cs` — nunca
también con `HasPostgresEnum`. `ManejadorDeErrores.cs` gana 7 ramas exactas: 2 `23505`
(`ux_remitos_numero` → `numero_de_remito_duplicado`, **quinta ocurrencia** del ordering trap;
`ux_items_remito_orden` → `orden_de_item_duplicado`) + 5 `23514` (las cinco CHECKs de este
slice). `ServicioDeRemitos` (draft CRUD, `emitir` — el cuarto write site — con la serie `'REM'`,
`anular`) es slice 5; la consolidación (`ServicioDeFacturacionDeRemitos`) es slice 6;
`Remitos.tsx`/`Remito.tsx`/`FacturarRemitos.tsx` son slice 8 — mergeados, la etapa cierra acá
(regla 19 del programa).
