# 04 — Plan de migración de datos (MySQL → PostgreSQL)

> Volumen: 345.665 ventas, 5.992 artículos, 13.492 gastos, 92 MB de dump.
> El 95% del trabajo real es **parsear `ventas.articulos`**.

## Estrategia general

Migración en **dos etapas**, no una:

1. **Etapa A — Fiel (lift):** cargar el dump MySQL tal cual en un schema `legacy` dentro
   de Postgres. Sin transformar nada. Esto da una fuente consultable con SQL y permite
   validar contra el original todas las veces que haga falta.
2. **Etapa B — Transformación (shift):** poblar el schema `public` (modelo destino) con
   scripts SQL idempotentes que leen de `legacy`.

Ventaja: si un script de transformación está mal, se corrige y se vuelve a correr sin
tocar el dump ni MySQL.

---

## Etapa A — Cargar el legacy en Postgres

### Opción elegida: contenedor MySQL efímero + `pgloader`

```bash
# 1. Levantar MySQL temporal y cargar el dump
docker run -d --name ways-mysql-tmp \
  -e MYSQL_ROOT_PASSWORD=tmp -e MYSQL_DATABASE=ways_legacy \
  -p 3307:3306 mysql:8

docker exec -i ways-mysql-tmp mysql -uroot -ptmp ways_legacy < alsina/localhost.sql

# 2. Migrar a Postgres con pgloader (maneja latin1 → utf8 solo)
docker run --rm --network host dimitri/pgloader:latest pgloader \
  mysql://root:tmp@127.0.0.1:3307/ways_legacy \
  postgresql://ways:ways@127.0.0.1:5432/ways?search_path=legacy
```

`pgloader` convierte tipos automáticamente. Después hay que corregir a mano:
- `float(10,2)` → `numeric(14,2)` (`ALTER TABLE ... ALTER COLUMN ... TYPE numeric(14,2)`).
- Verificar el encoding de `nombre`, `descripcion`, `domicilio`, `obs`.

### Alternativa sin MySQL
`mysql2pgsql` o `sed` sobre el dump. **No recomendado** para 92 MB con backticks y
`latin1`: el riesgo de corromper texto es alto y el contenedor efímero cuesta 5 minutos.

### Validación de la Etapa A

```sql
-- Los conteos tienen que dar exactamente esto:
areas 6, articulos 5992, articulos_oferta 0, cajagral 0, cajas 2793, cajav 1763,
cajaz 6194, codigos_barra 3954, combos 0, gastos 13492, grupos 136, listas_precio 3,
marcas 539, precios 0, proveedores 62, puntos_venta 2, roles 3, stock 0,
usuarios 30, usuario_rol_puntoventa 4, ventas 345665
```

Y estos checksums de negocio, que se repiten al final de la Etapa B:

```sql
SELECT tipo, count(*), sum(total), sum(efectivo), sum(tarjetas), sum(c_corriente)
FROM legacy.ventas WHERE eliminado = 0 GROUP BY tipo ORDER BY tipo;

SELECT id_punto_venta, count(*), sum(total), sum(g_total) FROM legacy.cajas GROUP BY 1;
SELECT sum(saldo) FROM legacy.usuarios;
SELECT sum(existencia) FROM legacy.articulos WHERE activo = 1;
```

---

## Etapa B — Transformación

Orden obligatorio (dependencias de FK):

```
 1. puntos_venta, areas, roles
 2. proveedores, grupos, marcas
 3. listas_precio
 4. empleados          ← usuarios WHERE tipoUser IN (2,3,4)
 5. clientes           ← usuarios WHERE tipoUser = 1  (+ el id 1, Consumidor Final)
 6. asignaciones_empleado ← usuario_rol_puntoventa
 7. articulos
 8. codigos_barra
 9. precios            ← desde articulos.precio / precioEmp
10. ofertas            ← desde articulos.Oferta* y grupos.oferta*
11. stock              ← desde articulos.existencia / existencia_2
12. cajas + turnos_caja_totales
13. movimientos_tesoreria       ← cajaz
14. arqueos_recargas + arqueos_recargas_canales ← cajav
15. ventas             ← ventas WHERE tipo IN (1,2)
16. items_venta       ← PARSEO de ventas.articulos   ← el paso caro
17. gastos
18. movimientos_cuenta_corriente ← ventas WHERE tipo IN (3,4,5) + las de tipo 1/2 con c_corriente <> 0
19. movimientos_stock  ← opcional, sintetizado desde items_venta
```

**Preservar los IDs originales** en todas las tablas (`OVERRIDING SYSTEM VALUE` +
`setval` de las secuencias al final). El negocio conoce a los clientes y los tickets por
número; cambiar los IDs rompe la trazabilidad con los tickets impresos.

### El paso 16: parsear `ventas.articulos`

Formato: `barra/cantidad/descripcion/precio/total` separado por `*`.

```sql
INSERT INTO items_venta (venta_id, orden, codigo_barra, cantidad, descripcion,
                          precio_unitario, total, articulo_id, area_id)
SELECT
  v.id,
  l.orden,
  p[1]                                   AS codigo_barra,
  NULLIF(p[2], '-')::numeric(12,3)       AS cantidad,
  p[3]                                   AS descripcion,
  NULLIF(p[4], '-')::numeric(14,2)       AS precio_unitario,
  p[5]::numeric(14,2)                    AS total,
  a.id                                   AS articulo_id,
  COALESCE(a.area_id, 1)                 AS area_id
FROM legacy.ventas v
CROSS JOIN LATERAL unnest(string_to_array(v.articulos, '*'))
        WITH ORDINALITY AS l(linea, orden)
CROSS JOIN LATERAL (SELECT string_to_array(l.linea, '/')) AS s(p)
LEFT JOIN articulos a ON a.id = (
    SELECT cb.articulo_id FROM codigos_barra cb WHERE cb.codigo = p[1]
)
WHERE v.articulos <> '' AND v.tipo IN (1, 2);
```

**Casos borde que hay que contemplar antes de correr esto en serio:**

| Caso | Cómo se detecta | Qué hacer |
|---|---|---|
| Líneas de descuento (`OF...`) | `p[1] LIKE 'OF%'` | `articulo_id = NULL`, `cantidad = NULL`, `precio = NULL`, marcar como línea de descuento |
| Líneas de combo (`COMBO...`) | `p[1] LIKE 'COMBO%'` | idem |
| Descripción con `/` | `array_length(p,1) > 5` | reconstruir: los campos 1-2 y los últimos 2 son fijos, el resto es la descripción |
| Menos de 5 campos | `array_length(p,1) < 5` | volcar a `migracion_errores`, no descartar en silencio |
| `articulos` vacío | ventas tipo 3/4/5 | no generan líneas |
| Código que ya no existe en el catálogo | `articulo_id IS NULL` | conservar el snapshot de `descripcion` y `codigo_barra` |
| Cantidad `0.00` con total > 0 | ver ejemplo real id 1 | conservar tal cual; es dato histórico |

**Tabla de cuarentena obligatoria:**

```sql
CREATE TABLE migracion_errores (
  id bigserial PRIMARY KEY,
  tabla_origen text, id_origen bigint,
  contenido text, motivo text, creado_el timestamptz DEFAULT now()
);
```

Regla: **cero descartes silenciosos.** Todo lo que no parsea va a `migracion_errores` y
se revisa antes del cutover.

### Estimación de volumen

345.665 ventas × ~4 líneas promedio ≈ **1,3–1,5 millones de filas** en `items_venta`.
Es perfectamente manejable en Postgres. Correr con índices deshabilitados y crearlos al final.

---

## Validación post-migración

Correr los mismos checksums de la Etapa A contra el modelo nuevo:

```sql
-- Totales de venta por tipo
SELECT tipo, count(*), sum(total) FROM ventas WHERE NOT anulada GROUP BY tipo;

-- Las líneas tienen que sumar el subtotal de la venta
SELECT count(*) FROM (
  SELECT v.id FROM ventas v
  JOIN items_venta l ON l.venta_id = v.id
  GROUP BY v.id, v.subtotal
  HAVING abs(sum(l.total) - v.subtotal) > 0.05
) t;   -- debe dar 0 (o una lista corta y explicada)

-- Saldos de cuenta corriente
SELECT c.id, c.saldo,
       (SELECT sum(importe) FROM movimientos_cuenta_corriente m WHERE m.cliente_id = c.id)
FROM clientes c WHERE c.saldo <> 0;

-- Stock
SELECT sum(cantidad) FROM stock;   -- vs sum(existencia) del legacy
```

⚠ Los totales **no van a cerrar al centavo** en algunos casos, y está bien: el legacy usaba
`float`. La tolerancia razonable es 0,05 por ticket. Lo que hay que documentar es
**cuántos tickets** quedan fuera de tolerancia, no forzar que sea cero.

---

## Cutover

1. Congelar el sistema viejo (cartel de mantenimiento).
2. Dump final de MySQL.
3. Correr Etapa A + Etapa B completas (script único, ~20–40 min estimados).
4. Correr las validaciones. Si algo falla → rollback, se sigue con el viejo.
5. Levantar la app nueva apuntando a la base migrada.
6. **Guardar el dump MySQL original y el schema `legacy` en Postgres.** No se borran nunca.

## Entregables de esta etapa

```
migracion/
├── 00-setup.sh              levanta MySQL temporal, carga el dump, corre pgloader
├── 01-legacy-fixups.sql     tipos, encoding, índices en el schema legacy
├── 02-checksums-origen.sql  snapshot de control ANTES
├── 10-catalogos.sql         pasos 1-11
├── 20-cajas.sql             pasos 12-14
├── 30-ventas.sql            pasos 15-16 (el parser)
├── 40-gastos-cc.sql         pasos 17-19
├── 90-secuencias.sql        setval de todas las secuencias
├── 99-validacion.sql        checksums destino + reporte de migracion_errores
└── run.sh                   orquesta todo, falla al primer error
```
