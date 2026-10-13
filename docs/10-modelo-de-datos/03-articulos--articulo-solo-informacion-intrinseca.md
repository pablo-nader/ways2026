> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 3. Artículos, listas de precio y ofertas

## 3. Artículos, listas de precio y ofertas

### Artículo: solo información intrínseca

> **Modelo de disponibilidad (decisión de producto, 2026-08-02):** los artículos son
> **del tenant** — no usan el patrón `id_empresa NULL` de los padrones. Un artículo tiene
> un código interno único por tenant y N códigos de barra (cada código pertenece a UN solo
> artículo del tenant, sin overrides). Lo que varía por empresa es la **disponibilidad**:
> `disponible_para_todas = true` (default) significa todas las empresas del tenant,
> incluidas las que se creen después — automáticamente, porque no hay filas que backfillear;
> `false` acota a las empresas listadas en `articulos_empresas`.

```sql
articulos (                   -- [tenant-wide: id_tenant, SIN id_empresa]
    id_articulo,
    codigo_interno   citext NOT NULL,        -- el código corto tipeable (< 7 dígitos);
                                             -- decisión resuelta: obligatorio, autogenerado
                                             -- desde el contador por tenant cuando se omite
                                             -- en el alta (supersede la marca NULL anterior)
                                             -- UNIQUE (id_tenant, codigo_interno) WHERE deleted_at IS NULL
    nombre           citext,
    descripcion      text NULL,
    id_area, id_categoria NULL, id_marca NULL, id_grupo NULL,
    id_proveedor_habitual integer NULL,      -- para reposición; no exclusivo
    id_alicuota_iva  integer NOT NULL,
    unidad_venta     unidad_venta,           -- enum: unidad | peso | importe  (pesables: cantidad 12,3;
                                             -- importe: rubro sin detalle cobrado por el monto que escribe el
                                             -- cajero, cantidad siempre 1; solo ALTER TYPE ... ADD VALUE)
    unidades_por_bulto numeric(10,2) NULL,
    es_producto      boolean,                -- false = servicio: no toca stock
    controla_lote    boolean NOT NULL DEFAULT false, -- exige lote en todo movimiento de stock (§ lotes)
    acumula_en_venta boolean NOT NULL DEFAULT true,  -- true: agregar de nuevo suma cantidad en una línea;
                                             -- false: cada alta es una línea nueva (lo aplica el carrito)
                                             -- CHECK ck_articulos_importe_no_acumula:
                                             --   unidad_venta <> 'importe' OR acumula_en_venta = false
    costo_lista      numeric(14,2) NULL,     -- lista del proveedor, tal como se cargó (ver costo_lista_por / _incluye_iva)
    costo_lista_por  text NOT NULL DEFAULT 'Unidad', -- base del costo_lista: 'Unidad' | 'Bulto'
                                             -- CHECK ck_articulos_costo_lista_por; 'Bulto' exige unidades_por_bulto > 0
    costo_lista_incluye_iva boolean NOT NULL DEFAULT true, -- el costo_lista se cargó con IVA incluido
    descuento_proveedor numeric(5,2) NULL DEFAULT 0, -- el servidor guarda 0 cuando no se carga; la lectura
                                             -- devuelve null solo a quien no ve costos
    costo_nominal    numeric(14,2) NULL,     -- costo por unidad, con IVA y neto del descuento: el servidor lo
                                             -- calcula en el alta y cuando una edición cambia alguna entrada del
                                             -- cálculo; la compra lo pisa con el costo efectivo
    disponible_para_todas boolean NOT NULL DEFAULT true,
    activo,
    disponible_online boolean NOT NULL DEFAULT true, -- se ofrece en la tienda online (§3, "Tienda online");
                                             -- por artículo: no es un campo compartido de la familia
    disponible_venta boolean NOT NULL DEFAULT true,  -- false = de uso interno: se compra y se stockea, pero no se
                                             -- vende (POS, tienda ni venta); tampoco es un campo de la familia
    id_familia       integer NULL,           -- familia a la que pertenece (§3, "Familias de artículos");
                                             -- FK compuesta (id_familia, id_tenant), NULL = sin familia
    tecla_atajo      text NULL               -- tecla de función del POS de un artículo por importe: F5..F8;
                                             -- NULL = sin atajo. Por artículo: no es un campo compartido de la familia
                                             -- CHECK ck_articulos_tecla_atajo: tecla_atajo IN ('F5', 'F6', 'F7', 'F8')
                                             -- CHECK ck_articulos_tecla_atajo_solo_importe:
                                             --   tecla_atajo IS NULL OR unidad_venta = 'importe'
);
-- ux_articulos_tecla_atajo UNIQUE (id_tenant, tecla_atajo) WHERE deleted_at IS NULL AND tecla_atajo IS NOT NULL
-- (23505 -> 409 tecla_atajo_duplicada). Un artículo por importe nunca acumula (importe_no_acumula), solo él tiene
-- tecla (tecla_atajo_solo_importe) y no puede ser componente de un combo (combo_componente_por_importe). Se rechaza,
-- no se corrige: el alta sin acumula_en_venta toma false si es por importe; la edición exige mandar la tecla en
-- null al pasar a otra unidad (null = sin atajo, a diferencia de acumula_en_venta). Con alcance de familia, cambiar
-- la unidad compartida a una que no es por importe mientras OTRO miembro tiene tecla es 409
-- familia_miembro_con_tecla_atajo, y agrupar un artículo con tecla contra una referencia que no es por importe es
-- 409 articulo_con_tecla_atajo.
-- ix_articulos_disponible_online (id_tenant) WHERE disponible_online AND disponible_venta AND activo AND deleted_at IS NULL

articulos_empresas (          -- solo tiene filas cuando disponible_para_todas = false
    id_articulo, id_empresa, id_tenant,
    PRIMARY KEY (id_articulo, id_empresa),
    -- FKs compuestas con id_tenant a articulos y empresas
);

codigos_barra (id_codigo_barra, id_articulo, codigo citext, activo)   -- [tenant-wide]
-- UNIQUE (codigo, id_tenant) WHERE deleted_at IS NULL — N códigos por artículo,
-- cada código pertenece a exactamente un artículo del tenant.

codigos_proveedor (id_codigo_proveedor, id_articulo, id_proveedor, codigo citext)   -- [tenant-wide]
-- Código con el que un proveedor identifica el artículo en sus facturas (el editor de compras
-- busca por él). N códigos por (artículo, proveedor).
-- UNIQUE (id_tenant, id_proveedor, codigo) WHERE deleted_at IS NULL — un código de un proveedor
-- apunta a un único artículo vivo.
-- CHECK codigo = btrim(codigo) AND codigo <> ''
-- FKs compuestas con id_tenant a articulos y proveedores. Baja lógica (deleted_at).

familias (                    -- [tenant-wide: id_tenant, SIN id_empresa]
    id_familia,
    nombre citext NOT NULL,                  -- hasta 150 caracteres, como articulos.nombre
    activo boolean NOT NULL DEFAULT true
);
-- ux_familias_nombre UNIQUE (id_tenant, nombre) WHERE deleted_at IS NULL.
-- ak_familias_id_familia_id_tenant UNIQUE (id_familia, id_tenant): destino de la FK compuesta.
-- articulos.fk_articulos_familia (id_familia, id_tenant) → familias, MATCH SIMPLE (NULL = sin
-- familia), RESTRICT. ix_articulos_familia (id_familia, id_tenant) WHERE id_familia IS NOT NULL.
-- RLS estándar (HabilitarRlsDeTenant) — sin desvío. Sin backfill: todo artículo existente
-- queda con id_familia NULL.
```

El artículo **no tiene precio de venta**: el precio vive en las listas. Se acabaron
`precio`, `precioEmp`, `precioOferta` y `precioCant` como columnas.
