# Exploration: Etapa 13 — Stock inteligente (mínimos, alertas, reposición, sugerencia de compra)

## Current State

### 1. `stock.minimo`/`stock.reposicion` — dormidas desde la Etapa 5, no desde ahora

- Migración de origen: `src/Ways.Infrastructure/Persistencia/Migraciones/20260804143427_VentasStockYCuentaCorrienteEtapa5.cs` (columnas `minimo`, `reposicion` forman parte del `stock` original, no una migración posterior).
- Entidad: `src/Ways.Domain/Stock/Stock.cs:24-32` — `decimal? Minimo`, `decimal? Reposicion`, junto a `IdArticulo`/`IdPuntoVenta`/`IdTenant`/`Cantidad`. La PK es `(id_articulo, id_punto_venta)` — **ya modela "por artículo Y punto de venta"** exactamente como pide el alcance de la etapa. No hace falta una tabla nueva de "punto de pedido" separada.
- Mapeo: `src/Ways.Infrastructure/Persistencia/Configuraciones/StockConfiguration.cs:35-36` — `numeric(12,3)`, ambas nullable, sin default, sin CHECK.
- Doc 10 §6 (`docs/10-modelo-de-datos.md:508-513`) documenta las columnas desde el schema original de la Etapa 5.
- **Quién las escribe hoy: nadie.** `grep -rn "\.Minimo\b|\.Reposicion\b" src/` solo encuentra `StockConfiguration.cs` (la definición del mapeo). Cero servicios de aplicación, cero endpoints, cero tests las tocan.
- **Quién las expone en la UI: nadie.** `GET /api/stock` (`StockEndpoints.cs:14-19`) devuelve `StockActual(idPuntoVenta, idArticulo, cantidad)` — sin `minimo`/`reposicion` en el contrato. `Existencias.tsx` (el único reporte de stock, `src/Ways.Web/src/paginas/Existencias.tsx:134-159`) solo pinta `Artículo / Nombre / Cantidad`. `Articulos.tsx` (el editor de artículo) no tiene campos de mínimo/reposición.
- Conclusión: la Etapa 13 activa columnas reservadas hace 3 etapas, con la forma exacta que pide el alcance. No requiere ALTER TABLE para el mínimo/reposición por sí solos.

### 2. El canal de alertas PULL de la Etapa 12 — la forma exacta a replicar

Tres capas, todas reusables como plantilla:

- **Servicio** — `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs`:
  - `ObtenerVencimientosAsync` (línea 57-66): listado clasificado, no paginado.
  - `ObtenerResumenDeVencimientosAsync` (línea 71-81): el tile de Tablero — **reusa `ObtenerVencimientosAsync` internamente, nunca una segunda query de agregación** ("el tile no puede divergir del reporte").
  - `ObtenerVencimientosParaExportacionAsync` (línea 89-104): shape de **listado que crece** — `Contar → GuardaDeTope.Exigir → Take(tope+1) → GuardaDeTope.Exigir` otra vez, antes de generar el archivo.
  - Contraste con `ObtenerExistenciasAsync` (línea 38-52): shape de **agregado acotado por catálogo** — sin `COUNT(*)` propio, la guarda corre sobre `TablaExportable.Filas.Count` ya mapeada.
- **Endpoints** — `ReportesEndpoints.cs:381-426`: `GET /api/reportes/stock/vencimientos`, `.../vencimientos/resumen` (tile), `.../vencimientos/export` — los tres bajo `Politicas.LecturaDeReportes`, heredada por co-locación del `MapGroup`.
- **Web** — `src/Ways.Web/src/paginas/Tablero.tsx:342-403` (`PanelDeVencimientos`): requiere un `idPuntoVenta` concreto (el endpoint no acepta "Todos"), usa `usePanelDeReporte`, `data-testid` por cifra, link al reporte.
- Umbral configurable: `ParametroConocido.DiasAlertaVencimiento` (default `30`), resuelto PV → empresa → default. Para bajo-stock el umbral **ya vive en la fila** (`stock.minimo`), así que este patrón de parámetro global no aplica 1:1 — diferencia real entre las dos alertas que el proposal tiene que resolver explícitamente.

### 3. Rotación desde `movimientos_stock` — el precedente de SQL crudo

- `src/Ways.Application/Reportes/LectorDeSerieTemporal.cs` — **el único archivo de SQL crudo de la Etapa 10** ("one file is one review target and one grep target"). Reglas duras a replicar: la conexión se abre con `db.Database.OpenConnectionAsync()` (solo ese camino corre `InterceptorDeContextoDeTenant` que setea los GUCs de RLS); la granularidad se inlinea como literal validado desde un `switch`; la zona viaja como parámetro posicional con `timezone($1, ...)`.
- Ninguna consulta existente lee `movimientos_stock` para rotación — contenido nuevo. Decisión de diseño abierta: ¿extender ese archivo (convención "un solo archivo de SQL crudo") o abrir un lector propio (cohesión por tabla distinta: `movimientos_stock` con `motivo = venta`)? Precedente citable en ambos sentidos.

### 4. Proveedor habitual para agrupar la lista de reposición

- `articulos.id_proveedor_habitual` (doc 10:212, "para reposición; no exclusivo") **no está dormida** — circuito completo desde la Etapa 3: en los tres DTOs (`Contratos.cs:21,48,75`), editable en `Articulos.tsx` (41,65,90,123,760-762), y ya leída por `ServicioDeCompras.cs:878-890` para `margenPorProveedor`.
- `proveedores` (doc 10:175-184): nota — la entidad real tiene `IdEmpresa` (nullable) además de `IdTenant`, algo que el comentario "[catálogo]" del doc 10 no explicita; vale una nota en el design sobre el scoping real.
- Agrupar la lista de reposición por proveedor es un `JOIN` sobre FK existente — sin plomería nueva.

### 5. "Stock en tránsito" — probablemente vacío, y hay que documentarlo así

- `MotivoStock` tiene ocho motivos; **no existe estado intermedio "en tránsito"**.
- Transferencia: dos filas espejadas **en una sola transacción** — no hay ventana donde el stock esté "en camino".
- Compra: **confirmar** genera los movimientos en una sola transacción; no hay recepción parcial (eso es explícitamente Etapa 16, doc 11:232).
- Conclusión: "stock en tránsito" para la sugerencia de compra, con el modelo actual, **vale cero siempre**. El proposal debe decidir: (a) omitir el término hasta que exista OC con estado (Etapa 16), o (b) declararlo hard-coded en 0 con comentario. No hay tercera opción real hoy.

### 6. Infra export de la Etapa 11 — dos formas, no una

- Contrato (`openspec/specs/exportacion-de-reportes/spec.md`): `GET {ruta}/export` co-localizado, `formato=xlsx`, tope 25000 rechazado (nunca truncado), nombre determinístico, header filas 1-4, sin re-query.
- `ExportacionDeReportes.cs` — mappers puros; una lista de reposición agrega un mapper acá.
- Dos shapes: **agregado acotado por catálogo** (existencias) vs. **listado que crece** (vencimientos). Una lista "artículos bajo mínimo" está acotada por el catálogo — forma agregado, salvo que el diseño pida un listado histórico de alertas (no lo pide el alcance).

### 7. Specs existentes — candidatas a delta

- `stock/spec.md` — requirement nueva (ADDED) para el read/write path de `minimo`/`reposicion` (hoy la spec no las menciona). El invariante del ledger no se toca.
- `lotes-y-vencimientos/spec.md` — sin delta; precedente citable del canal de alertas.
- `reportes-de-gestion/spec.md` — candidata principal para ADDED (bajo-stock/reposición/sugerencia bajo `LecturaDeReportes`).
- `exportacion-de-reportes/spec.md` — sin delta esperable (consumidores del contrato).
- `articulos/spec.md` — improbable delta; la regla de agrupación pertenece a la spec nueva.
- `parametros-operativos/spec.md` — delta condicional (solo si aparece una clave nueva).
- **Candidata no pedida pero real**: `conteo-de-inventario/spec.md` — su Purpose excluye "any full-count snapshot/freeze/variance workflow", y el backlog del doc 11 (línea 367) asigna ese workflow completo a la Etapa 13. **Scope real de la etapa** que hay que levantar en el proposal.

### 8. Superficies web candidatas

- `Existencias.tsx` — columna mínimo/estado o pantalla separada de bajo stock; decisión de diseño.
- `Articulos.tsx` — TENSIÓN REAL: `minimo`/`reposicion` son por `(artículo, PV)` pero el editor de artículo es tenant-wide — el valor por-PV no tiene hogar obvio (¿editor por PV? ¿grilla en Existencias?). El proposal la resuelve.
- `Tablero.tsx` — tile de bajo-stock, patrón `PanelDeVencimientos`.
- `Compras.tsx` — candidata para la sugerencia de compra (no profundizada).

### 9. Multi-tenancy / gate de DB — veredicto preliminar

- Mínimo/punto de pedido por artículo+PV: **cero migración** (columnas existentes en la fila operativa-scoped).
- Agrupación por proveedor: **cero migración** (FK existente).
- Rotación: lectura pura — **cero migración**.
- Stock en tránsito: sin tabla, y no debería crearla esta etapa (la 16 lo modela mejor con estados reales).
- **Veredicto preliminar: probable SIN gate de DB**, o gate mínimo de una clave de `ParametroConocido` (patrón sin-migración). Confirmar en el proposal.

### 10. Constraints estructurales heredadas

- Ningún write path de checkout lee `minimo`/`reposicion` y el alcance no lo pide — las alertas son PULL. Consistente con "The Module Off Switch Costs The Checkout Hot Path Nothing" (etapa 12).
- **La Etapa 13 no debería tocar los write paths** de ServicioDeVentas/Compras/Stock. Si el proposal quisiera un aviso síncrono en el POS ("esta venta deja el stock bajo mínimo"), es decisión de alcance NUEVA que rompería la garantía del budget — pregunta explícita para el proposal.
- El orden de locks no aplica: esta etapa no escribe movimientos.

## Approaches

Única bifurcación técnica genuina — dónde vive la agregación de rotación:

1. **Extender `LectorDeSerieTemporal.cs`** — Pros: convención "un solo archivo de SQL crudo", reusa ejecutor y apertura con RLS. Cons: el archivo hoy solo conoce `comprobantes_venta`/`gastos`; `movimientos_stock` es tabla y filtro distintos. Effort: bajo.
2. **Lector propio (`LectorDeRotacion.cs`)** — Pros: cohesión por dominio. Cons: rompe la convención declarada como invariante del design de la etapa 10; habría que justificar la excepción. Effort: bajo.

Ninguna es claramente superior sin saber los joins concretos de "rotación" (¿por artículo? ¿artículo+PV? ¿ventana móvil?) — decisión de diseño.

## Recommendation

Activar `stock.minimo`/`stock.reposicion` tal como están (sin migración); replicar el patrón de tres capas de la etapa 12 para bajo-stock; agrupar la reposición por `id_proveedor_habitual`; documentar "stock en tránsito" como cero explícito hasta la Etapa 16; mantener la sugerencia de compra fuera de los write paths. El proposal debe absorber explícitamente el workflow de conteo completo (snapshot/variance) que el backlog del doc 11 asignó a esta etapa.

## Risks

- El mínimo es por `(artículo, PV)` pero el editor natural es tenant-wide — la UX no tiene hogar obvio; puede requerir pantalla nueva.
- "Stock en tránsito" sin representación — si el proposal no lo declara cero explícito, la fórmula queda ambigua o inventa un concepto que la Etapa 16 modelará distinto.
- El workflow de conteo completo asignado por el backlog — si el proposal no lo toma, queda scope perdido otra vez.
- La convención "un solo archivo de SQL crudo" puede tensionarse — decisión de diseño explícita, no por inercia.
- Cero cobertura heredada sobre `minimo`/`reposicion` — tests desde cero.

## Ready for Proposal

Sí. Las decisiones abiertas del doc 11 (mínimo fijo/rotación, pull/push, sugerencia→OC/listado) son del proposal. Incluir explícitamente en la ronda de proposal el ítem de conteo de inventario completo (hallazgo 7).
