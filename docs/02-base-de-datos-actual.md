# 02 — Base de datos actual (MySQL)

> 21 tablas, InnoDB, `latin1` (algunas columnas en `utf8mb4`).
> Fuente: `alsina/localhost.sql` + migraciones `alsina/sql/1.0.sql` y `2.0.sql`.

## Mapa de relaciones

```
puntos_venta ──┬── usuario_rol_puntoventa ──┬── usuarios ──┬── ventas
               │                            └── roles      ├── gastos
               ├── ventas                                  ├── cajas
               ├── gastos                                  ├── cajav
               ├── cajas ◄── ventas.id_caja                └── cajaz
               ├── cajav      gastos.id_caja
               ├── cajaz
               └── stock ── articulos

articulos ──┬── codigos_barra (1:N)
            ├── areas        (id_area)
            ├── proveedores  (id_proveedor)
            ├── marcas       (id_marca)  ── grupos, proveedores
            ├── grupos       (id_grupo)
            ├── precios ── listas_precio        [sin uso]
            ├── articulos_oferta                [sin uso]
            └── stock ── puntos_venta           [sin uso]

combos      [sin uso, 0 filas]
cajagral    [sin uso, 0 filas]
```

---

## Tablas en uso

### `articulos` — 5.992 filas
El catálogo. Mezcla identificación, precios, stock, clasificación y configuración de ofertas.

| Columna | Tipo | Notas |
|---|---|---|
| `ID` | int PK | ⚠ mayúsculas, único caso en la base |
| `barra` | bigint | código de barras "principal", legacy. El POS lo usa como clave en el ticket |
| `codigo` | int | código interno corto |
| `nombre`, `nombreOferta` | text | `nombreOferta` es la etiqueta del descuento en el ticket |
| `lista` | float(10,2) | costo de lista (con IVA) |
| `dtoGral` | float(10,2) | descuento % del proveedor |
| `costo` | float(10,2) | costo nominal |
| `costoOferta` | float(10,2) | costo en oferta |
| `precio` | float(10,2) | **precio de venta** |
| `precioOferta` | float(10,2) | precio de oferta por fecha |
| `precioCant` | float(10,2) | precio unitario al alcanzar `OfertaCantN` |
| `precioEmp` | float(10,2) | precio para clientes con `lista = 2` |
| `tolerancia` | float(10,2) | siempre `0.00`, sin uso |
| `id_area` / `id_proveedor` / `id_marca` / `id_grupo` | int FK | |
| `OfertaDia`, `OfertaDiaDesde/Hasta` | tinyint, date | oferta por rango de fechas |
| `OfertaHora`, `OfertaHoraDesde/Hasta` | tinyint, time | gate horario |
| `OfertaCant`, `OfertaCantN` | tinyint, int | oferta por cantidad |
| `producto` | tinyint | `1` = producto físico, `0` = servicio |
| `existencia` | int | ⚠ **stock global, no por local** |
| `existenciaMinima`, `reposicion`, `uBulto` | int | |
| `activo` | tinyint | soft delete |
| `existencia_2` | int NULL | ⚠ intento abandonado de stock por local |
| `fecha_creacion/edicion/eliminacion` | datetime | |

### `codigos_barra` — 3.954 filas
`id`, `codigo varchar(20)`, `id_articulo`, timestamps. Permite N EAN por artículo.

### `ventas` — 345.665 filas
La tabla más grande y la más problemática.

| Columna | Notas |
|---|---|
| `id`, `fecha` | |
| `articulos` | **text con el detalle serializado**: `barra/cantidad/descripcion/precio/total` separado por `*` |
| `subtotal`, `descuento`, `total` | `descuento` se guarda **negativo** |
| `efectivo`, `tarjetas`, `c_corriente`, `vuelto` | medios de pago |
| `saldo` | diferencia entre vuelto declarado y calculado (sobrante/faltante de caja) |
| `cerrada` | `1` cuando la caja del período cerró |
| `actualizada` | `1` si ya pasó por "Actualizar precios" de cuenta corriente |
| `id_usuario` | operador que hizo la venta |
| `cliente` | FK a `usuarios`; `1` = Consumidor Final |
| `c1..c6`, `c8` | importe por área. `c8` no se usa |
| `tipo` | `1` venta, `2` devolución, `3` pago a cuenta, `4` actualización de precios, `5` ajuste |
| `id_caja` | FK a `cajas`, se completa al cerrar |
| `eliminado` | anulación lógica |
| `obs` | detalle del ajuste (tipo 5) |
| `id_punto_venta` | FK |

**Ejemplo real de `articulos`:**
```
9100/170.00/Otros/1.00/170.00
```
→ barra `9100`, cantidad `170.00`, descripción `Otros`, precio `1.00`, total `170.00`.
⚠ La descripción puede contener `-` y espacios; el separador `/` no está escapado.

### `gastos` — 13.492 filas
`id`, `nombre`, `fecha`, `tipo` (ver tabla de tipos en `01-features`), `otrosDetalle`,
`importe`, `id_area`, `facturaNumero bigint`, `cerrada`, `id_usuario`, `articulos text`
(sin uso), `subtotal`, `diferencia`, `impuestos`, `id_caja`, `id_punto_venta`.

### `cajas` — 2.793 filas
Un registro por cierre de caja. Ingresos por área (`c1..c6`, `c8`), egresos por área (`g_c1..g_c8`),
egresos por tipo (`gProveedores`, `gOtros`, `gSueldos`, `gViaticos`, `gImpuestos`, `gTotal`),
`retiros`, `total`, `efectivo`, `tarjetas`, `c_corriente`, `saldo`, `cantidad`, `primero`,
`ultimo`, `fecha`, `id_usuario`, `id_punto_venta`.

### `cajaz` — 6.194 filas
Libro del fondo de caja: `fecha`, `concepto`, `inicio`, `ingreso`, `egreso`, `final`, `tipo`,
`id_usuario`, `id_punto_venta`. Encadenado: el `final` de un registro es el `inicio` del siguiente.

### `cajav` — 1.763 filas
Caja virtual (recargas). Cuatro bloques de columnas con prefijos `v` (Virtual), `c` (Claro),
`s` (SUBE), `e` (Efectivo), más `final`, `diferencia`, `tipo`, `id_usuario`, `id_punto_venta`.

### `usuarios` — 30 filas
⚠ **Clientes y operadores en la misma tabla.**

`id`, `user`, `tipoUser`, `pass varchar(255)` (**texto plano**), `nombre`, `apellido`, `dni`,
`nacimiento`, `domicilio`, `tel`, `cel`, `mail`, `saldo`, `acuerdo`, `obs`, `uCompra`,
`uConexion`, `uDesconexion`, `lista`.

- `acuerdo = -1` → crédito ilimitado.
- `lista` → `1` normal, `2` empleado (usa `precioEmp`).
- `uCompra`/`uConexion`/`uDesconexion` nunca se escriben desde el código actual.

### Catálogos

| Tabla | Filas | Contenido |
|---|---:|---|
| `areas` | 6 | `1` N/A, `2` Almacén, `3` Verdulería, `4` Cigarrillos, `5` Carga Virtual, `6` Rotisería |
| `grupos` | 136 | nombre, margen %, config de oferta de grupo (cantidad/directa, días, horas) |
| `marcas` | 539 | nombre, grupo, proveedor |
| `proveedores` | 62 | nombre, razón social, CUIT, domicilio, contactos, margen |
| `puntos_venta` | 2 | nombre, domicilio, horario, whatsapp, instagram, facebook, web |
| `roles` | 3 | Administrador, Encargado, Vendedor — **definidos pero nunca consultados** |
| `usuario_rol_puntoventa` | 4 | asignación usuario↔rol↔local |

---

## Tablas creadas y nunca cableadas (0 filas)

Alguien —vos, hace unos años— empezó una refactorización correcta y quedó a mitad de camino.
Estas tablas son la dirección correcta y hay que retomarla en el modelo nuevo:

| Tabla | Intención |
|---|---|
| `listas_precio` | Listas de precio nombradas, con precio fijo o % sobre una lista base. Ya tiene 3 filas: Default, Empleados (-10%), y una tercera |
| `precios` | Precio por (artículo, lista). Reemplaza a `precio`/`precioEmp` |
| `articulos_oferta` | Ofertas como entidad propia: rango de cantidad, rango de fechas, porcentual o importe fijo, con **prioridad**. Reemplaza los 9 campos `Oferta*` de `articulos` |
| `stock` | Stock por (artículo, punto de venta). Reemplaza `articulos.existencia` |
| `combos` | Combos de 2 artículos con precio propio y ventana horaria. Abandonado |
| `cajagral` | Caja general con conceptos `c1..c7`. Reemplazado por `cajaz` |

---

## Problemas del schema (resumen ejecutivo)

| # | Problema | Impacto |
|---|---|---|
| 1 | `float(10,2)` para dinero | error de redondeo acumulativo; los totales históricos ya arrastran ruido |
| 2 | `ventas.articulos` como string | imposible reportar por artículo sin parsear 345k strings |
| 3 | `articulos.existencia` global | el stock de dos locales está mezclado |
| 4 | Clientes y operadores en `usuarios` | no se puede modelar permisos ni dar de baja un empleado sin tocar su cuenta corriente |
| 5 | `pass` en texto plano | riesgo directo |
| 6 | `latin1` | acentos y `ñ` rotos en varios registros |
| 7 | Columnas `c1..c8`/`g_c1..g_c8` | las áreas son datos, no columnas. Agregar un área implica un ALTER |
| 8 | Sin FK en tablas viejas | `2.0.sql` agregó algunas, pero `articulos.barra` ↔ `codigos_barra.codigo` no tiene integridad |
| 9 | `tipo` con significado dual en `gastos` | `< 90` es un `id_proveedor`, `>= 95` es una categoría |
| 10 | Sin auditoría | no hay quién/cuándo para cambios de precio o stock |
