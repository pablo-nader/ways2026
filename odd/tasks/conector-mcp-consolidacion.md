# Conector MCP: consolidación

## Objetivo

Que las once herramientas del conector MCP compartan un solo camino de resultados y errores, una sola
guarda de rol, y que las consultas de ventas busquen artículos como el resto del servidor, sin pasadas
de más sobre los reportes.

## Problema y por qué

Seis etapas del conector entraron en main desde sesiones distintas. Quedaron:

- Tres formas de devolver resultados: `ResultadoDeHerramienta.EjecutarAsync` (compras, catálogo, carga
  de compras), un `EjecutarAsync`/`Resultado` privado en ventas que la ficha toma prestado, y
  `quien_soy` devolviendo `string`. El runner común solo atrapa `ErrorDeHerramienta`: un `ErrorDominio`
  o un `DbUpdateException` que escapa llega a Claude como el error genérico del SDK.
- Las ramas de carrera de `asociar_codigo_de_barras` y `asociar_codigo_de_proveedor` devuelven el
  `Message` crudo de la excepción (en el caso de EF, un texto en inglés que no dice qué pasó).
- Dos guardas de Admin con la misma política (`GestionDeCatalogo`) y distinto mensaje
  (`PermisoDeCompras.ExigirAsync` y el `ExigirAdminAsync` privado de catálogo).
- `ServicioDeConsultasDeVentas` busca artículos con `lower(x) LIKE` sobre columnas citext (no usa los
  índices únicos y distingue acentos, a diferencia de `BusquedaSinAcentos.Coincide`).
- `ventas_por_articulo` con filtro de área, grupo, categoría o artículo corre el reporte por artículo
  dos veces por empresa (la segunda solo para el denominador del porcentaje).

## Alcance

Rebanada 1 (código), rama `pnader/mcp-consolidacion-1`, apilada sobre la S4 (#477):

- Runner único que atrapa `ErrorDeHerramienta`, `ErrorDominio` y el `DbUpdateException` que clasifica
  `ManejadorDeErrores.ClasificarPostgresException`; ventas, ficha y `quien_soy` pasan a usarlo.
- Una guarda de rol con política y mensaje por parámetro, para compras, carga de compras, catálogo,
  ventas y ficha.
- Búsqueda de artículos de las consultas de ventas: igualdad citext para los pasos exactos,
  `Coincide` para los "contiene".
- Una sola lectura del reporte por empresa en `ventas_por_articulo`.

Rebanada 2 (prosa y tests del conjunto), pendiente: `Instrucciones` y la sección de docs/07, un test
del conjunto exacto de once tools, y constantes y nombres de argumentos.

## Restricciones

- Sin cambios de esquema. La negación de rol de ventas sigue siendo texto sin `isError` (decisión de
  producto fijada por `ConectorMcpVentasTests`, `ConectorMcpVentasPorArticuloTests` y docs/07).
- La página de consentimiento, las `Instrucciones` del flujo de carga y la tool
  `crear_borrador_de_compra` en docs/07 ya las resolvió la S4.
- Nunca dos suites de integración contra el mismo daemon de Docker.

## Modelo de datos

Sin cambios.

## Tareas

Rebanada 1:

- [x] T0 — Este documento y su espejo en Engram (observación 1659). Ruta: inline. Commit `54651ce6`.
- [x] T1 — Un solo runner de resultados. Ruta: inline (writer único de la rebanada). Commit `54651ce6`.
  RED observado: 4 de 7 tests nuevos fallaban (runner con `ErrorDominio` y con 23505 clasificado; las dos
  carreras de catálogo devolvían "An error occurred while saving…"). GREEN: `ConectorMcp` 347/347.
  Mutaciones: brazo `ErrorDominio` anulado → caen el unitario, 10 casos de `ventas_por_articulo` y 3 de
  la ficha; brazo `DbUpdateException` anulado → cae el unitario; la captura clasificada de catálogo
  anulada → cae la carrera de su hermano (barras y proveedor, uno por uno).
- [ ] T2 — Una sola guarda de rol. Ruta: inline (writer único de la rebanada).
- [ ] T3 — Búsquedas de artículos en consultas de ventas sin `LIKE`. Ruta: inline (writer único).
- [ ] T4 — Sin doble pasada en ventas por artículo. Ruta: inline (writer único).

Rebanada 2 (pendiente):

- [ ] R2-T1 — Prosa de `Instrucciones` y docs/07 (matriz de roles por tool, identidad, herramientas de
  asociación atadas al flujo de factura, `zona_horaria` no es argumento, "código de barras activo").
- [ ] R2-T2 — Test del conjunto exacto de once tools (nombres, anotaciones, títulos).
- [ ] R2-T3 — Constantes en las descripciones (límites, candidatos, días), el `MaximoDeCandidatos`
  repetido y la convención de nombres de argumentos.

## Criterios de aceptación

- Un `ErrorDominio` o un `DbUpdateException` clasificado que escapa de cualquier tool llega como
  resultado con `isError` y un texto en castellano.
- La rama de carrera de catálogo sin dueño encontrado dice qué pasó, sin el mensaje crudo de EF.
- La negación de rol de ventas no cambia.
- `ventas_por_articulo` con texto o artículo acentuado distinto ("fidéos") encuentra "Fideos".
- `ventas_por_articulo` con filtro del reporte corre una sola consulta de líneas por empresa, con el
  mismo porcentaje sobre todo lo vendido.

## Verificación

Pendiente.

## Progreso

- Siguiente paso: T1.
