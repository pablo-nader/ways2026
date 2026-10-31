# 10 — Modelo de datos normalizado

> Reemplaza el schema propuesto en el [doc 03](03-modelo-destino-postgres.md). El doc 03
> sigue siendo la referencia del **mapeo legacy → destino** para la migración de datos;
> este documento define el modelo definitivo del producto, pensado para venderse a
> terceros. Todo se define acá de entrada, aunque se implemente por etapas (ver final).

## Principios

1. **Scoping según doc 09.** Toda tabla declara su categoría: catálogo
   (`id_tenant` + `id_empresa NULL`), operativa (`id_tenant` + `id_punto_venta`)
   o global. En los SQL de abajo el scoping se indica con `-- [catálogo]`,
   `-- [operativa]`, `-- [global]` y no se repite en cada columna.
2. **Convención de nombres del doc 03** (castellano, `snake_case`, plural,
   `items_x`, `movimientos_x`) con la forma de PK del doc 08: `id_articulo`, no
   `articulo_id`.
3. **`EntidadBase`**: `created_at`, `updated_at`, `deleted_at` (soft delete con
   unique parciales `WHERE deleted_at IS NULL`) en todas las tablas.
4. **Los padrones son datos, no enums.** El legacy murió por `tipo=95` y `c1..c6`.
   Enum nativo de Postgres solo para estados de máquina de estados (`estado_turno`);
   catálogo editable por el usuario → tabla.
5. **`numeric(14,2)`** para importes, `numeric(12,3)` para cantidades (pesables).
6. **Los comprobantes son inmutables una vez emitidos.** Todo lo que el comprobante
   necesita para reimprimirse igual dentro de cinco años (descripción, precio,
   alícuota, condición fiscal) se guarda como *snapshot* en el item, aunque sea
   redundante con el catálogo.
7. **Nada de saldos sin libro.** Todo saldo cacheado (cliente, stock) tiene su tabla
   de movimientos que lo reconstruye y audita.
8. **Extensiones y funciones de base.** `citext` (comparación sin mayúsculas en columnas de
   texto) y `unaccent`, más la función `public.sin_acentos(text)` (`IMMUTABLE`, envoltorio de
   `unaccent`). Toda búsqueda de texto del servidor compara
   `lower(sin_acentos(columna)) LIKE lower(sin_acentos(patrón))`, así "jose" encuentra
   "José"; `unaccent` mapea ñ a n. Ver `BusquedaSinAcentos` (Application).

---

## Cómo leer este documento

El modelo está partido en un archivo por sección dentro de
[`10-modelo-de-datos/`](10-modelo-de-datos/). Este archivo es el índice: ubique la tabla en la
columna **Tablas** y lea solo el archivo que la contiene. Las referencias `§N` de cualquier
parte apuntan a la sección `N` de este índice.

| Sección | Archivo | KB | Tablas |
|---|---|---|---|
| 1. Padrones auxiliares | [01-padrones-auxiliares.md](10-modelo-de-datos/01-padrones-auxiliares.md) | 24 | `dominios`, `vitrinas`, `condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante`, `empresas`, `medios_pago` |
| 2. Entidades comerciales | [02-entidades-comerciales.md](10-modelo-de-datos/02-entidades-comerciales.md) | 3 | `clientes`, `proveedores`, `personal` |
| 3. Artículos, listas de precio y ofertas — Artículo: solo información intrínseca | [03-articulos--articulo-solo-informacion-intrinseca.md](10-modelo-de-datos/03-articulos--articulo-solo-informacion-intrinseca.md) | 7 | `articulos`, `articulos_empresas`, `familias` |
| 3. Artículos, listas de precio y ofertas — Tienda online | [03-articulos--tienda-online.md](10-modelo-de-datos/03-articulos--tienda-online.md) | 13 | `imagenes_articulo` |
| 3. Artículos, listas de precio y ofertas — Familias de artículos | [03-articulos--familias-de-articulos.md](10-modelo-de-datos/03-articulos--familias-de-articulos.md) | 36 | — |
| 3. Artículos, listas de precio y ofertas — Listas de precio, con historia | [03-articulos--listas-de-precio.md](10-modelo-de-datos/03-articulos--listas-de-precio.md) | 1 | `listas_precio`, `precios` |
| 3. Artículos, listas de precio y ofertas — Ofertas | [03-articulos--ofertas.md](10-modelo-de-datos/03-articulos--ofertas.md) | 4 | `ofertas`, `ofertas_listas` |
| 3. Artículos, listas de precio y ofertas — Combos | [03-articulos--combos.md](10-modelo-de-datos/03-articulos--combos.md) | 36 | `combos`, `combos_componentes` |
| 4. Comprobantes de venta | [04-comprobantes-de-venta.md](10-modelo-de-datos/04-comprobantes-de-venta.md) | 34 | `comprobantes_venta`, `items_comprobante_venta`, `cobros`, `certificados_fiscales`, `numeraciones_fiscales`, `facturaciones_de_venta`, `acumulados_de_facturacion`, `presupuestos`, `items_presupuesto`, `remitos`, `items_remito` |
| 5. Comprobantes de compra | [05-comprobantes-de-compra.md](10-modelo-de-datos/05-comprobantes-de-compra.md) | 28 | `comprobantes_compra`, `alicuotas_comprobante_compra`, `percepciones_comprobante_compra`, `items_comprobante_compra`, `pagos`, `items_pago`, `aplicaciones_pago`, `ordenes_compra`, `items_orden_compra` |
| 6. Stock | [06-stock.md](10-modelo-de-datos/06-stock.md) | 8 | `stock`, `movimientos_stock`, `lotes`, `stock_lotes` |
| 7. Caja: turnos, arqueos y tesorería | [07-caja-turnos-arqueos-y-tesoreria.md](10-modelo-de-datos/07-caja-turnos-arqueos-y-tesoreria.md) | 23 | `turnos_caja`, `movimientos_caja`, `arqueos_turno`, `movimientos_tesoreria`, `cuentas_tesoreria`, `movimientos_cuenta` |
| 8. Cuenta corriente de clientes | [08-cuenta-corriente-de-clientes.md](10-modelo-de-datos/08-cuenta-corriente-de-clientes.md) | 23 | `movimientos_cuenta_corriente`, `aplicaciones_cobro`, `movimientos_cuenta_corriente_proveedor`, `movimientos_personal`, `jornadas_personal` |
| 9. Parámetros operativos | [09-parametros-operativos.md](10-modelo-de-datos/09-parametros-operativos.md) | 1 | `parametros` |
| 10. Auditoría | [10-auditoria.md](10-modelo-de-datos/10-auditoria.md) | 4 | `auditoria` |
| 11. Conector MCP: almacén OAuth | [11-conector-mcp.md](10-modelo-de-datos/11-conector-mcp.md) | 4 | `aplicaciones_oauth`, `autorizaciones_oauth`, `alcances_oauth`, `tokens_oauth` |
| Diagrama de referencias (resumen) | [diagrama-de-referencias.md](10-modelo-de-datos/diagrama-de-referencias.md) | 2 | — |
| Etapas sugeridas (definido todo, implementado por partes) | [etapas-sugeridas.md](10-modelo-de-datos/etapas-sugeridas.md) | 6 | — |
| 9. Dispositivos de escritorio (stage-desktop-pos) — 9.1 Invariante "una PC-caja = un punto de venta" (stage-desktop-pos) | [09-dispositivos--9.1-invariante-una-pc-caja.md](10-modelo-de-datos/09-dispositivos--9.1-invariante-una-pc-caja.md) | 9 | `dispositivos`, `puntos_venta` |
| 9. Dispositivos de escritorio (stage-desktop-pos) — 9.2 Reserva de numeración para venta offline (stage-pos-reserva-de-numeracion) | [09-dispositivos--9.2-reserva-de-numeracion-para.md](10-modelo-de-datos/09-dispositivos--9.2-reserva-de-numeracion-para.md) | 9 | `reservas_numeracion` |
| 9. Dispositivos de escritorio (stage-desktop-pos) — 9.3 Venta offline: instantánea de catálogo y precios congelados (stage-pos-venta-offline-backend) | [09-dispositivos--9.3-venta-offline-instantanea-de.md](10-modelo-de-datos/09-dispositivos--9.3-venta-offline-instantanea-de.md) | 10 | — |
| 9. Dispositivos de escritorio (stage-desktop-pos) — 9.4 Rendición de la cola offline y guarda de cierre de turno | [09-dispositivos--9.4-rendicion-de-la-cola.md](10-modelo-de-datos/09-dispositivos--9.4-rendicion-de-la-cola.md) | 14 | — |
