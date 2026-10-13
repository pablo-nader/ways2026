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
-- UNIQUE (id_venta_origen, id_tenant) WHERE id_venta_origen IS NOT NULL  ⇒ a lo sumo un espejo por venta
-- CHECK id_venta_origen IS NULL OR resultado_fiscal IS NOT NULL  ⇒ un espejo es siempre fiscal
-- CHECK id_venta_origen IS NULL OR id_turno_caja IS NULL         ⇒ un espejo no pertenece a ningún turno

items_comprobante_venta (
    id_item, id_comprobante_venta, orden,
    id_articulo integer NULL,                -- NULL solo en líneas de concepto libre
    -- snapshot: el ticket se reimprime igual aunque el catálogo cambie
    descripcion text, codigo_barra text NULL,
    id_area,
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

pagos_comprobante (
    id_pago, id_comprobante_venta,
    id_medio_pago, importe numeric(14,2),
    referencia text NULL,                    -- cupón, nro de operación
    vuelto numeric(14,2) NOT NULL DEFAULT 0  -- solo medios que admiten vuelto
);
```

Cambios de fondo respecto del legacy:

| Legacy | Nuevo |
|---|---|
| `ventas.articulos` string serializado | `items_comprobante_venta` |
| Descuento como línea fantasma `OF...` | `descuento` + `id_oferta` **en el item** |
| Precio cambiado a mano en caja, sin rastro | `ajuste_manual_porcentaje` + `ajuste_manual` **en el item** |
| Columnas `efectivo/tarjetas/c_corriente/vuelto` | N filas en `pagos_comprobante` |
| `tipo` 1/2 (venta/devolución) | `tipos_comprobante` con `signo` (ticket X / NC X) |
| `tipo` 3/4/5 metidos en `ventas` | `movimientos_cuenta_corriente` (§8) |
| `eliminado = 1` | `estado = anulado` + contramovimientos de stock auditados |
| Tolerancia $10 / vuelto máx $20 hardcodeados | parámetros por punto de venta (§9) |

**Anular** un comprobante no lo borra ni edita: lo marca `anulado`, genera los
`movimientos_stock` inversos y el contramovimiento de cuenta corriente si lo hubo.
El bug de "restaurar suma stock" muere por diseño: el stock solo se mueve por
movimientos, y los movimientos no se editan.

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
> exactamente 1 (`400 cantidad_importe_invalida`) y junto a `precioUnitario` no tiene destino (`400
> importe_con_precio_no_admitido`). `ServicioDeOfertas` resuelve la línea sin mirar lista, ofertas ni combos: precio
> original = precio final = importe, descuento 0, sin ofertas aplicadas (`articulo_sin_precio_vigente` no aplica). La
> venta persiste el item como cualquier otro: `cantidad` 1, `precio_unitario` = importe, `descuento` 0, `id_oferta`
> NULL, `id_combo` NULL, `id_lista_precio` la del cliente. El ajuste manual (`ajusteManualPorcentaje`) se aplica encima y
> el total de la línea sigue la regla de peso entero de `CalculadorDeTotales`: un importe con centavos conserva el
> precio unitario tipeado (150,50) y el total de la línea sale al peso (151).
>
> - **Offline.** La línea por importe lleva `importe` y nunca `precioUnitario`/`descuentoUnitario`; el "todas o ninguna" del
>   precio offline se mide solo sobre las líneas de lista, así que una venta solo de importes y una mixta se aceptan. No es
>   una discrepancia de precio. Una línea que el dispositivo ya cobró con precio de lista para un artículo que hoy es por
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
    intentos               integer NOT NULL DEFAULT 0,
    ultimo_error           text NULL,
    proximo_intento_en     timestamptz NULL,
    created_at, updated_at, deleted_at
);
-- UNIQUE (id_comprobante_venta, id_tenant)                                   ux_facturaciones_de_venta_comprobante
-- UNIQUE (id_comprobante_fiscal, id_tenant) WHERE id_comprobante_fiscal IS NOT NULL  ..._comprobante_fiscal
-- INDEX  (id_tenant, proximo_intento_en) WHERE estado = 'pendiente'          ix_facturaciones_de_venta_pendientes
-- FK compuestas (id_empresa|id_comprobante_venta|id_comprobante_fiscal, id_tenant), todas RESTRICT:
--       fk_facturaciones_de_venta_empresa | _comprobante | _comprobante_fiscal
-- FK id_tenant → tenants, RESTRICT                                           fk_facturaciones_de_venta_tenant
-- INDEX  (id_empresa, id_tenant) · (id_tenant)                               ix_facturaciones_de_venta_empresa | _tenant
-- CHECK periodo día 1 · aportes es un arreglo jsonb · intentos >= 0 · motivo <> 'no_requerida' ·
--       estado <> 'emitida' OR id_comprobante_fiscal IS NOT NULL

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

**Bloqueo de la migración.** Los CHECK (`ck_comprobantes_venta_espejo_fiscal`, `ck_comprobantes_venta_espejo_sin_turno`), la FK
autorreferente (`fk_comprobantes_venta_venta_origen`) y el índice único (`ux_comprobantes_venta_venta_origen`) sobre
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
  (`ComprobanteEmitido.fiscal.estado`) dice `pendiente` (se decidió facturar), `no_corresponde` o `sin_decidir`. Una lectura por id no
  consulta el estado fiscal (`fiscal` viene `null`).
- **Autocuración (barrido).** Si la decisión falla, el error se registra, la venta responde igual con `sin_decidir`, la marca queda en
  `NULL` y el fallo se anota (ver "Intentos fallidos"). Si la decisión de la venta actual terminó bien, el endpoint llama a
  `SanearDecisionesDebidasAsync`: toma las ventas del mismo punto de venta con la marca en `NULL`, `id_venta_origen` nulo y la espera
  vencida (`facturacion_proximo_intento_en IS NULL OR <= ahora`), las más viejas primero (por id), como máximo 5 por llamada
  (`LimiteDeBarrido`) y con un presupuesto total de 3 segundos, y decide cada una en su propia transacción con el mismo código que la
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
- **Limitación conocida (fuera de T4).** Anular una venta ya decidida todavía no devuelve su aporte al acumulado ni cancela su fila
  `pendiente` (planificado para T8). Hasta entonces la emisión (T5) tiene que saltear y cancelar las decisiones `pendiente` cuya venta
  esté anulada; no hay una garantía de que eso ocurra antes de T8.

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
