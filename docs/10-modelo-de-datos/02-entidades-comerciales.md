> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 2. Entidades comerciales

## 2. Entidades comerciales

**Decisión: `clientes` y `proveedores` son tablas separadas.** El patrón unificado
(*party model*) ahorra dos campos fiscales repetidos a cambio de un join extra en cada
consulta del POS y de volver al pecado original del legacy: `usuarios` conteniendo
cosas distintas discriminadas por un número mágico. Comparten *convenciones*
(identificación fiscal, condición fiscal, auditoría), no tabla.

```sql
clientes (                    -- [catálogo]
    id_cliente,
    numero              integer,             -- el "número de cliente" visible (4 dígitos hoy)
    nombre, apellido,
    razon_social        citext NULL,         -- si es empresa
    tipo_documento      tipo_documento,      -- enum: dni | cuit | cuil | pasaporte | otro
    numero_documento    citext NULL,
    id_condicion_fiscal integer NOT NULL,    -- default: Consumidor Final
    nacimiento date NULL, domicilio, telefono, celular, email, observaciones,
    id_lista_precio     integer NOT NULL,    -- qué lista le rige (default: la general)
    limite_credito      numeric(14,2) NOT NULL DEFAULT 0,
    credito_ilimitado   boolean NOT NULL DEFAULT false,
    saldo               numeric(14,2) NOT NULL DEFAULT 0,  -- cache; el libro es
                                             -- movimientos_cuenta_corriente
    activo
);
-- El Consumidor Final es una fila por tenant (numero = 1), seed automático.

proveedores (                 -- [catálogo]
    id_proveedor,
    razon_social citext, nombre_fantasia citext NULL,
    cuit varchar(13) NULL,
    id_condicion_fiscal integer NOT NULL,
    domicilio, telefono, email,
    vendedor, celular_vendedor, supervisor, celular_supervisor,   -- los contactos del legacy
    margen numeric(5,2) NULL,               -- margen sugerido de la línea
    observaciones, activo,
    percibe_iibb        boolean NOT NULL DEFAULT false,  -- sus facturas suelen traer percepción
    percibe_iva         boolean NOT NULL DEFAULT false,
    precios_incluyen_iva boolean NOT NULL DEFAULT false, -- sus precios ya traen el IVA
    saldo numeric(14,2) NOT NULL DEFAULT 0  -- cache; el libro es
                                             -- movimientos_cuenta_corriente_proveedor (§8, Etapa 15)
);

personal (                    -- [catálogo]
    id_personal,
    nombre citext, documento citext NULL, telefono citext NULL, observaciones,
    valor_hora          numeric(14,2) NOT NULL DEFAULT 0,  -- CHECK >= 0; valor por defecto de la
                                             -- próxima carga de horas, no el de cargas ya hechas
    saldo               numeric(14,2) NOT NULL DEFAULT 0,  -- cache; positivo: la empresa le debe al
                                             -- empleado. El libro es movimientos_personal (§8c)
    id_usuario          integer NULL,        -- FK simple a usuarios; único entre filas vivas
    id_cliente          integer NULL         -- FK compuesta (id_cliente, id_tenant) a clientes;
                                             -- único entre filas vivas; nunca el Consumidor Final
);
-- Los dos vínculos son opcionales e independientes. El nombre evita "empleado" porque
-- `id_empleado` ya significa el usuario que opera. Unicidad: `ux_personal_usuario` y
-- `ux_personal_cliente`, parciales `WHERE deleted_at IS NULL`.
```

---
