> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 3. Artículos, listas de precio y ofertas

### Ofertas

```sql
ofertas (                     -- [catálogo]
    id_oferta,
    id_empresa integer NULL,                 -- NULL = toda empresa del tenant
    nombre          citext,                  -- lo que imprime el ticket (no único)
    -- alcance: exactamente uno
    id_articulo integer NULL, id_grupo integer NULL, id_categoria integer NULL,
    id_familia  integer NULL,                -- FK (id_familia, id_tenant) -> familias; los miembros se leen al resolver
    -- vigencia
    fecha_desde date NULL, fecha_hasta date NULL,
    hora_desde  time NULL, hora_hasta  time NULL,
    dias_semana smallint[] NULL,             -- {1..7}; NULL = todos
    -- disparador
    cantidad_minima numeric(12,3) NULL,      -- NULL = sin mínimo (oferta directa)
    -- beneficio: exactamente uno
    precio_unitario numeric(14,2) NULL,      -- "a $X la unidad" (ex precioOferta/precioCant)
    porcentaje      numeric(5,2)  NULL,      -- "X% de descuento"
    importe_fijo    numeric(14,2) NULL,      -- "$X de descuento" / "3x2 a $X"
    prioridad       int NOT NULL DEFAULT 0,  -- ante solapamiento gana la mayor;
    acumulable      boolean NOT NULL DEFAULT false,  -- ¿se suma a otras o excluye?
    activo,
    disponible_online boolean NOT NULL DEFAULT true  -- la tienda online la aplica a los precios que muestra (§3, "Tienda online")
);
CHECK (num_nonnulls(id_articulo, id_grupo, id_categoria, id_familia) = 1);
CHECK (num_nonnulls(precio_unitario, porcentaje, importe_fijo) = 1);
CHECK (fecha_hasta >= fecha_desde AND hora_hasta >= hora_desde);  -- tolerante a NULL por eje
CHECK (dias_semana IS NULL OR dias_semana <@ ARRAY[1,2,3,4,5,6,7]::smallint[]);

ofertas_listas (               -- [catálogo] junction, sin auditoría (PK-only)
    id_oferta, id_lista_precio, id_tenant
);
-- PK (id_oferta, id_lista_precio)
```

**DEVIATION (stage-4-ofertas, aprobado 2026-08-03):** la columna única
`id_lista_precio NULL` que este documento definía originalmente en `ofertas` se
reemplaza por la junction `ofertas_listas` de arriba. Cero filas para una oferta
significa que aplica a **todas** las listas del tenant (incluidas las `derivada`);
una o más filas la restringen a exactamente esas listas — un targeting multi-lista
que una sola columna nullable no podía expresar.

Cubre todo el motor del legacy (por fecha, por hora, por cantidad, de grupo directa y
por cantidad) más lo que le faltaba: días de semana, ofertas por categoría, ofertas
limitadas a un subconjunto de listas, y reglas explícitas de solapamiento en lugar del
`elseif` accidental de `funciones.php`. El descuento aplicado queda **en el item** que
lo generó (ver abajo), no como línea fantasma con barra `OF...`.

**Alcance por familia (ofertas-por-familia, aprobado 2026-10-10):** una oferta puede apuntar a una familia de
artículos (`id_familia`, el cuarto alcance exclusivo). La pertenencia es dinámica: la oferta aplica a un artículo si
y solo si `articulos.id_familia` es esa familia **al resolver** (un artículo dado de baja no es miembro), así que los
comprobantes ya emitidos no cambian. La precedencia sigue siendo solo `prioridad`/`acumulable`: no hay orden
automático por tipo de alcance. Una familia con `activo = false` no se valida ni al resolver ni al crear la oferta
(igual que los grupos). Disolver una familia que alguna oferta referencia está bloqueado (`409 familia_en_uso`).

Residual conocido: el pre-chequeo de existencia de la familia al crear o editar una oferta no toma lock, así que
un alta o edición que corre en paralelo con la disolución de esa familia puede dejar una oferta viva apuntando a
una familia disuelta. Es inerte (la familia ya no tiene miembros, así que el resolvedor nunca la empareja) y es el
mismo residual abierto que `GuardaDeReferencias` documenta para los grupos; cerrarlo
(`BloquearSiEstaVivaAsync` en el escritor) es un seguimiento pendiente para los escritores que todavía no lo
cierran, ofertas incluidas; los escritores de artículos ya lo tienen cerrado.

Migración `OfertasPorFamilia`: revertirla (`Down`) aborta con `23514` mientras alguna oferta tenga alcance por
familia, porque el CHECK anterior de tres columnas no admite esa fila; esas ofertas hay que eliminarlas o
reasignarles otro alcance antes de revertir.
