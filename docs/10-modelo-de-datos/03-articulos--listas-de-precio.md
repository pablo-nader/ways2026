> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 3. Artículos, listas de precio y ofertas

### Listas de precio, con historia

```sql
listas_precio (               -- [catálogo]
    id_lista_precio, nombre,
    es_default        boolean,               -- la lista del mostrador
    modo              modo_lista,            -- enum: fija | derivada
    id_lista_base     integer NULL,          -- si derivada: de cuál
    porcentaje        numeric(5,2) NULL,     -- si derivada: ±% sobre la base ("5% desc.")
    activo
);

precios (                     -- [catálogo]
    id_precio, id_articulo, id_lista_precio,
    precio          numeric(14,2),
    vigente_desde   timestamptz NOT NULL,
    vigente_hasta   timestamptz NULL         -- NULL = precio vigente
);
-- UNIQUE (id_articulo, id_lista_precio) WHERE vigente_hasta IS NULL AND deleted_at IS NULL
```

Un cambio de precio **cierra** la fila vigente y abre una nueva — nunca se pisa. Eso da
gratis: auditoría de precios, reportes de evolución, y la feature más particular del
legacy (F4: reliquidar el fiado a precio del día) consultando `precios` a una fecha en
lugar de recalcular a mano.

Las listas `derivadas` (ej. "10% descuento" sobre la general) no guardan filas en
`precios`: se resuelven al momento de vender y el item guarda el snapshot.
