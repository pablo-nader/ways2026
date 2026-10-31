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
    solo_multiplos  boolean NOT NULL DEFAULT false,  -- el beneficio alcanza solo a grupos completos de cantidad_minima
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

**Solo múltiplos (ofertas-solo-multiplos, 2026-10-30):** con `solo_multiplos = true` el beneficio alcanza solo a
`N × floor(q / N)` unidades (`N = cantidad_minima`); el resto paga el precio original. Con un artículo a 4000 y
"2 x 7500" (precio unitario 3750, mínimo 2): 1 → 4000, 2 → 7500, 3 → 11500, 4 → 15000; sin la marca, 3 → 11250
(la fórmula del legacy, `docs/01` B2). Vale para los tres beneficios: cada uno es un descuento unitario `d` y el
descuento de la línea es `d × N × floor(q / N)`. Exige `cantidad_minima` (400 `solo_multiplos_sin_cantidad_minima`;
una edición que quita el mínimo sin mandar el campo la vuelve común). No hay CHECK de esquema: el único escritor es
`ServicioDeOfertas`, que lo valida, y el motor trata una fila con la marca y sin mínimo como oferta común.

- `q` es la cantidad de la línea que cobra la oferta: sin las unidades que consumió un combo. Una oferta de familia
  cuenta los grupos sobre la suma de las líneas del documento que la llevan y reparte las unidades cubiertas en el
  orden de las líneas: las que no completan un grupo caen en las últimas.
- La elección de la oferta ganadora no cambia (prioridad, descuento unitario, id). La detección de combos compara
  contra el descuento unitario completo, así que es conservadora frente a una oferta "solo múltiplos".
- El item guarda el descuento como importe (`descuento = bruto − neto`, neto al peso entero) y conserva `id_oferta`.
- La resolución (`/api/ofertas/resolver`, `/resolver-con-combos`) devuelve `descuentoDeLinea` solo en las líneas con
  una oferta "solo múltiplos", y `multiploDe`/`porFamilia` en esas ofertas aplicadas.
- La instantánea offline todavía no las representa: quedan fuera de la curva de escalones y de la grilla de familia,
  así que el dispositivo cobra a lista y, si la cantidad completa al menos un grupo, la venta sincronizada queda marcada como discrepante.

Migración `OfertasPorFamilia`: revertirla (`Down`) aborta con `23514` mientras alguna oferta tenga alcance por
familia, porque el CHECK anterior de tres columnas no admite esa fila; esas ofertas hay que eliminarlas o
reasignarles otro alcance antes de revertir.
