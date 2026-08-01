# 03 — Modelo destino (PostgreSQL)

> ⚠ **Superado en su parte de schema por el [doc 10](10-modelo-de-datos.md)**, que define
> el modelo normalizado definitivo (multi-tenant, comprobantes, medios de pago, fiscal).
> Este documento sigue vigente como referencia del **mapeo legacy → destino** para la
> migración de datos.

> Objetivo: paridad funcional con el legacy, pero con el modelo que el legacy ya había
> empezado a construir (`listas_precio`, `precios`, `articulos_oferta`, `stock`) y nunca terminó.

## Convención de nombres

El legacy tiene `caja`, `cajas`, `cajaz`, `cajav` y `cajagral`: cinco cosas distintas con
el mismo nombre. La convención nueva es:

1. **Castellano, `snake_case`, sustantivo en plural.**
2. **Tablas de detalle o de cruce:** `<plural>_<calificador_singular>` —
   `items_venta`, `turnos_caja`, `asignaciones_empleado`.
3. **Tablas de movimientos:** `movimientos_<ámbito>` —
   `movimientos_stock`, `movimientos_tesoreria`, `movimientos_cuenta_corriente`.
4. **El nombre dice qué es la fila, no de dónde viene.** `cajaz` no dice nada;
   `movimientos_tesoreria` sí.

| Legacy | Nuevo | Qué es realmente |
|---|---|---|
| `caja` → `areas` | `areas` | El rubro del artículo. Ya estaba bien |
| `cajas` | `turnos_caja` | Un turno de caja abierto y cerrado, con su arqueo |
| — | `turnos_caja_totales` | Los `c1..c6` / `g_c1..g_c6` como filas, no columnas |
| `cajaz` | `movimientos_tesoreria` | El libro del fondo de caja, encadenado inicio→final |
| `cajav` | `arqueos_recargas` | Conciliación diaria del negocio de recargas |
| — | `arqueos_recargas_canales` | Los bloques `v*`/`c*`/`s*`/`e*` como filas |
| `ventas` | `ventas` | Se conserva |
| `ventas.articulos` (string) | `items_venta` | Tabla real, ~1,4M de filas |
| `cajagral` | — | No se migra, 0 filas |

## Decisiones de modelado

| Decisión | Motivo |
|---|---|
| `numeric(14,2)` para todo importe | `float` acumula error. En .NET mapea a `decimal` |
| `citext` / `text` + UTF-8 | se termina el problema de acentos de `latin1` |
| Nombres en `snake_case`, singular→plural consistente | evita el `ID` vs `id` del legacy |
| Separar `clientes` de `empleados` | son dos conceptos distintos que hoy comparten tabla |
| `items_venta` como tabla real | mata el string serializado |
| `stock` por `(articulo_id, punto_venta_id)` | dos locales, dos stocks |
| `precios` por `(articulo_id, lista_precio_id)` | mata `precio`/`precioEmp`/`precioOferta`/`precioCant` |
| `ofertas` como entidad con prioridad | mata los 9 campos `Oferta*` de `articulos` |
| `areas` como dato, no columnas | mata `c1..c8` y `g_c1..g_c8` |
| Soft delete con `eliminado_el timestamptz NULL` | ya es la convención de las tablas nuevas del legacy |
| Auditoría `creado_el/editado_el` + `creado_por/editado_por` | trazabilidad mínima |

---

## Esquema propuesto

### Organización

```sql
puntos_venta      (id, nombre, domicilio, horario, whatsapp, instagram, facebook, web, ...)
areas             (id, nombre)
roles             (id, nombre)                       -- Administrador, Encargado, Vendedor
permisos          (id, codigo, descripcion)          -- NUEVO: el legacy nunca autorizó nada
permisos_rol      (rol_id, permiso_id)
```

### Personas

```sql
empleados         (id, usuario, password_hash, nombre, apellido, dni, email, activo, ...)
asignaciones_empleado (empleado_id, rol_id, punto_venta_id)   -- ex usuario_rol_puntoventa

clientes          (id, numero, nombre, apellido, dni, nacimiento, domicilio,
                   telefono, celular, email, observaciones,
                   lista_precio_id, limite_credito numeric(14,2), saldo numeric(14,2),
                   credito_ilimitado boolean, ...)
```

> `limite_credito` + `credito_ilimitado` reemplazan el `acuerdo = -1` mágico.
> El cliente `1` (Consumidor Final) se conserva con el mismo id para no romper el histórico.

### Catálogo

```sql
proveedores       (id, nombre, razon_social, cuit, domicilio, telefono,
                   vendedor, celular, supervisor, celular_supervisor, margen, ...)
marcas            (id, nombre, proveedor_id, grupo_id)
grupos            (id, nombre, margen numeric(5,2))

articulos         (id, codigo_interno, nombre, nombre_oferta,
                   area_id, proveedor_id, marca_id, grupo_id,
                   costo_lista, descuento_proveedor, costo_nominal,
                   unidades_por_bulto, es_producto, activo, ...)
codigos_barra     (id, codigo citext UNIQUE, articulo_id)

listas_precio     (id, nombre, es_default, es_precio_fijo,
                   porcentaje_descuento, lista_base_id)
precios           (id, articulo_id, lista_precio_id, precio, UNIQUE(articulo_id, lista_precio_id))
```

> El `precio` del legacy → `precios` con `lista_precio_id = 1`.
> El `precioEmp` → `lista_precio_id = 2`.

### Ofertas

```sql
ofertas (
  id,
  articulo_id  NULL,   -- una de las dos
  grupo_id     NULL,
  nombre,                          -- ex nombreOferta / grupos.descripcion
  prioridad          int,
  -- ventana temporal
  aplica_fechas      boolean, fecha_desde date, fecha_hasta date,
  aplica_horario     boolean, hora_desde time, hora_hasta time,
  -- disparador
  cantidad_minima    numeric(10,2) NULL,
  -- beneficio (uno de los tres)
  precio_unitario    numeric(14,2) NULL,   -- ex precioCant / precioOferta
  porcentaje         numeric(5,2)  NULL,   -- ex ofertaDirecta
  importe_fijo       numeric(14,2) NULL,   -- ex grupos.precio
  activo             boolean
)
CHECK ((articulo_id IS NULL) <> (grupo_id IS NULL))
```

Unifica en una sola tabla los tres tipos de oferta de artículo y los dos de grupo.

### Stock

```sql
stock             (articulo_id, punto_venta_id, cantidad numeric(12,3),
                   minimo numeric(12,3), reposicion numeric(12,3),
                   PRIMARY KEY (articulo_id, punto_venta_id))

movimientos_stock (id, articulo_id, punto_venta_id, cantidad, motivo,
                   venta_id NULL, compra_id NULL, empleado_id, creado_el)
```

> `movimientos_stock` es nuevo y no existe en el legacy. Es lo que permite
> auditar por qué el stock quedó como quedó, y arreglar el bug de "restaurar ticket suma stock".

### Ventas

```sql
ventas (
  id, numero, fecha, punto_venta_id, turno_caja_id NULL,
  empleado_id, cliente_id,
  tipo             smallint,   -- 1 venta, 2 devolución, 3 pago a cuenta,
                               -- 4 actualización de precios, 5 ajuste
  subtotal, descuento, total,
  efectivo, tarjetas, cuenta_corriente, vuelto, diferencia_caja,
  direccion_entrega text NULL,
  observaciones text NULL,
  cerrada boolean, anulada boolean, precios_actualizados boolean
)

items_venta (
  id, venta_id, orden,
  articulo_id NULL,          -- NULL en líneas de descuento
  oferta_id   NULL,          -- set en líneas de descuento
  area_id,
  descripcion text,          -- snapshot del nombre al momento de la venta
  codigo_barra text,         -- snapshot
  cantidad numeric(12,3),
  precio_unitario numeric(14,2),
  total numeric(14,2)
)
```

> Los totales por área (`c1..c6`) dejan de ser columnas: salen de
> `SELECT area_id, SUM(total) FROM items_venta GROUP BY area_id`.
> Si querés performance en reportes, una vista materializada o una tabla
> `venta_totales_area` mantenida por trigger.

### Gastos y compras

```sql
gastos (
  id, fecha, punto_venta_id, turno_caja_id NULL, empleado_id,
  categoria      smallint,   -- 1 proveedor, 2 sueldos, 3 viáticos, 4 impuestos,
                             -- 5 otros, 6 retiro de efectivo
  proveedor_id NULL,         -- ya no se encaja el id de proveedor dentro de `tipo`
  area_id, concepto, detalle,
  numero_factura text NULL,  -- text, no bigint: preserva el formato NNNN-NNNNNNNN
  importe, cerrada boolean
)
```

### Caja

```sql
turnos_caja (                                              -- ex cajas
  id, punto_venta_id, empleado_id, fecha_apertura, fecha_cierre,
  cantidad_tickets, primer_ticket_en, ultimo_ticket_en,
  total_ventas, total_efectivo, total_tarjetas, total_cuenta_corriente,
  diferencia, total_gastos, total_retiros, estado
)
turnos_caja_totales (turno_caja_id, area_id, ingresos, egresos)  -- ex c1..c6 / g_c1..g_c6

movimientos_tesoreria (id, punto_venta_id, fecha, concepto,      -- ex cajaz
                       inicio, ingreso, egreso, final, tipo, empleado_id)

arqueos_recargas (id, punto_venta_id, fecha, concepto, empleado_id, ...)  -- ex cajav
arqueos_recargas_canales (arqueo_recarga_id, canal, inicial, ventas, cantidad,
                          adicionales, depositos, comisiones, final)
```

> A diferencia del legacy, `turnos_caja` tiene una fila desde que se **abre** el turno,
> no solo cuando se cierra. Hoy la caja abierta es implícita (los tickets con `cerrada = 0`),
> lo que hace imposible saber a qué hora se abrió.

> `canal` ∈ {virtual, claro, sube, efectivo}. Mata los 4 bloques de columnas de `cajav`.

### Cuenta corriente

```sql
movimientos_cuenta_corriente (
  id, cliente_id, fecha, punto_venta_id, empleado_id,
  tipo smallint,          -- 1 consumo, 2 pago, 3 ajuste, 4 actualización de precios
  venta_id NULL,
  importe numeric(14,2),  -- + aumenta deuda, − la reduce
  saldo_resultante numeric(14,2),
  detalle text
)
```

> Hoy los movimientos de cuenta corriente viven mezclados en `ventas` con `tipo` 3/4/5.
> Separarlos vuelve el saldo auditable y reconstruible.

---

## Mapeo legacy → destino

| Legacy | Destino |
|---|---|
| `articulos.precio` | `precios (lista 1)` |
| `articulos.precioEmp` | `precios (lista 2)` |
| `articulos.precioOferta` + `OfertaDia*` | `ofertas` (aplica_fechas) |
| `articulos.precioCant` + `OfertaCant*` | `ofertas` (cantidad_minima + precio_unitario) |
| `articulos.OfertaHora*` | `ofertas.aplica_horario` |
| `articulos.existencia` | `stock (punto_venta_id = 1)` |
| `articulos.existencia_2` | `stock (punto_venta_id = 2)` si tiene datos |
| `grupos.ofertaCantidad/ofertaDirecta/...` | `ofertas` con `grupo_id` |
| `usuarios` `tipoUser IN (2,3,4)` | `empleados` |
| `usuarios` `tipoUser = 1` | `clientes` |
| `usuarios.acuerdo = -1` | `clientes.credito_ilimitado = true` |
| `ventas.articulos` (string) | `items_venta` |
| `ventas.c1..c6` | agregación de `items_venta.area_id` |
| `ventas` `tipo 3/4/5` | `movimientos_cuenta_corriente` |
| `cajas.c*/g_c*` | `turnos_caja_totales` |
| `cajaz` | `movimientos_tesoreria` |
| `cajav` | `arqueos_recargas` + `arqueos_recargas_canales` |
| `gastos.tipo < 90` | `gastos.categoria = 1` + `proveedor_id = tipo` |
| `gastos.tipo 95..99` | `gastos.categoria` 6/4/3/2/5 |
| `cajagral`, `combos` | **no se migran** (0 filas) |

## Índices mínimos

```sql
CREATE INDEX ON ventas (punto_venta_id, fecha DESC);
CREATE INDEX ON ventas (turno_caja_id) WHERE turno_caja_id IS NOT NULL;
CREATE INDEX ON ventas (cliente_id, fecha DESC);
CREATE INDEX ON ventas (punto_venta_id, cerrada) WHERE cerrada = false;
CREATE INDEX ON items_venta (venta_id);
CREATE INDEX ON items_venta (articulo_id, venta_id);
CREATE UNIQUE INDEX ON codigos_barra (codigo);
CREATE INDEX ON articulos USING gin (nombre gin_trgm_ops);   -- búsqueda por nombre del POS
CREATE INDEX ON gastos (punto_venta_id, cerrada, fecha DESC);
CREATE INDEX ON movimientos_cuenta_corriente (cliente_id, fecha DESC);
```
