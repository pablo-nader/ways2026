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

- [x] T0 — Este documento y su espejo en Engram (observación 1659). Ruta: inline. Commit `2b008799`.
- [x] T1 — Un solo runner de resultados. Ruta: inline (writer único de la rebanada). Commit `2b008799`.
  RED observado: 4 de 7 tests nuevos fallaban (runner con `ErrorDominio` y con 23505 clasificado; las dos
  carreras de catálogo devolvían "An error occurred while saving…"). GREEN: `ConectorMcp` 347/347.
  Mutaciones: brazo `ErrorDominio` anulado → caen el unitario, 10 casos de `ventas_por_articulo` y 3 de
  la ficha; brazo `DbUpdateException` anulado → cae el unitario; la captura clasificada de catálogo
  anulada → cae la carrera de su hermano (barras y proveedor, uno por uno).
- [x] T2 — Una sola guarda de rol. Ruta: inline (writer único de la rebanada). Commit `49a94c92`.
  `PermisoDelConector` (antes `PermisoDeCompras`): `CumpleAsync(política)` y `ExigirAsync(política, mensaje)`;
  ventas y ficha usan `CumpleAsync` y conservan su texto sin `isError`. Refactor sin comportamiento nuevo.
  Mutación: `CumpleAsync` siempre verdadero → caen los 14 casos de rol del filtro (compras, carga de compras,
  catálogo, ventas y la ficha del Vendedor).
- [x] T3 — Búsquedas de artículos en consultas de ventas sin `LIKE`. Ruta: inline (writer único). Commit `cf55863c`.
  RED: `VentasPorArticuloBuscaElTextoYElArticuloSinDistinguirAcentos` (texto "FIDÉOS" y articulo "fidéo") fallaba en
  las dos filas. GREEN: ventas por artículo + ficha 42/42. Mutaciones (una por hermano): el "contiene" de `texto`
  vuelto a `LIKE` → cae la fila texto; el "contiene" de la búsqueda vuelto a `LIKE` → cae la fila articulo; el paso
  exacto de código interno o de código de barras roto (cada uno en una corrida distinta) → cae
  `LaCoincidenciaExactaGanaAntesDeBuscarPorContenidoYLaAmbiguaListaHastaDiezCandidatos`. Paso de nombre exacto
  roto (`a.Nombre == buscado + "#"`, corrido en la ronda 1 del judgment-day) → cae ese mismo test en
  `Buscar("GASEOSA")`: "'GASEOSA' es ambiguo…" en lugar de la ficha de Gaseosa.
- [x] T4 — Sin doble pasada en ventas por artículo. Ruta: inline (writer único). Commit `31cecfae`.
  Una lectura sin filtros por empresa; `AcotarComoElReporteAsync` aplica en memoria (más una consulta de ids de
  artículos) los criterios del reporte. RED: `VentasPorArticuloConUnFiltroDelReporteLeeLasLineasUnaSolaVezPorEmpresa`
  contaba 2 lecturas de `items_comprobante_venta`, esperado 1. GREEN: ventas por artículo + ficha 45/45, con
  `VentasPorArticuloAcotaLasFilasPeroElPorcentajeSigueSiendoSobreTodoLoVendido` sin cambios. Tests nuevos de
  caracterización: subcategorías y grupo. Mutaciones: área anulada → cae `area almacén`; sin descendientes →
  cae `gaseosas` con subcategoría; filtro de baja de vuelta → caen `area almacén` y `categoria aguas`; artículo
  anulado → caen `articulo cola-500` y `fidéo`; grupo anulado → cae el test de grupo.
  No hecho: la doble resolución de empresa de la ficha (`ServicioDeFichaDeArticulo.ObtenerAsync` resuelve la
  empresa y `VentasDeUnArticuloAsync` vuelve a leer las empresas en `ResolverAlcanceAsync`); no es trivial sin
  cambiar la firma pública del alcance, queda para después.

Judgment-day, ronda 1 de correcciones (ledger congelado sobre `7203e061`, sin CRITICAL):

- [x] L1 Prosa de ventas por artículo (resumen de clase y de `PorArticuloAsync`, docs/07, comentarios de tests) a
  la lectura única con acotado en memoria; el test de rol de ventas nombra `NegacionDeRolAsync`. Commit `eb39806b`.
- [x] L2 El runner y docs/07 prometen solo la clasificación de `ClasificarPostgresException` (restricciones y
  rangos); lo transitorio y la concurrencia siguen escapando. Commit `1049a1e4`.
- [x] L3 El comentario de la búsqueda exacta ya no promete índice sobre el nombre. Commit `742fb7b4`.
- [x] L4 Casos que discriminan cada brazo: "fíd-5" por código interno en `texto` y en `articulo`, y `Buscar("a1")`
  para el paso exacto sin distinguir mayúsculas. Commit `d95ab02c`. Mutaciones: brazo de código del contiene de
  `texto` vuelto a `LIKE` → cae la fila texto "fíd-5"; el de la búsqueda → cae la fila articulo "fíd-5"; código
  interno exacto sensible a mayúsculas (`a.CodigoInterno + "" == buscado`) → cae la ficha en `Buscar("a1")`.
- [x] L5 Teoría de dos filtros del reporte (artículo + categoría, área + categoría). Commit `a79763f7`.
  Mutaciones: `ids.Contains` anulado → cae la fila artículo + categoría; área O categoría en vez de Y → cae la fila
  área + categoría.
- [x] L6 `LeerArticulosVendidosAsync` sin los tres parámetros de clasificación muertos. Commit `274e33d7`.
- [x] L7 El resumen de `SinDuenoTrasLaCarrera` cubre los dos orígenes y dos tests alcanzan el brazo `ErrorDominio`
  (interceptor que asocia el código a otro artículo antes de la verificación del servicio y lo borra antes de la
  búsqueda del dueño). Commit `940f4211`. Mutación: los dos brazos `ErrorDominio` de duplicado anulados → caen
  los dos tests.
- [x] L8 Test unitario de `PermisoDelConector` sin request, con una autorización que aprueba todo; el resumen se
  acota a "sin request". Commit `12a560dc`. Mutación: sin el chequeo de nulo → cae `SinRequestNingunaPoliticaSeCumple`.
- [x] L9 Mutación del paso de nombre exacto corrida y registrada en T3. Commit `cdfe8e02`.
- Verificado: el sha de T0 es `2b008799` (commit que crea este documento), no `e5a1d43c`.
- Ronda 2 (re-juicio ciego del delta `7203e061..ceebdf48`): L2–L9 FIXED; L1 PARCIAL por la cláusula del
  resumen de clase de `ServicioDeConsultasDeVentas` que generalizaba "leen sin filtros y acotan acá" a la
  consulta de un solo artículo, que le pasa su id al reporte. Corregida en el commit `docs(mcp): el resumen
  de clase distingue el listado de la consulta de un artículo` (prosa, sin regla que mutar).
- Diferidos a la rebanada 2: L10 (esquemas de entrada de las 11 tools y que ninguna publique `log`), L11 (paridad
  de `AcotarComoElReporteAsync` con el reporte: helper compartido o test de paridad) y verificar con un test si el
  mensaje de una `McpException` llega al cliente antes de tocar esa prosa.

Rebanada 2, rama `pnader/mcp-consolidacion-2`, apilada sobre `pnader/mcp-consolidacion-1` (#478). Orden de
ejecución: T2, T3, T1, T4 (los renombres de T3 tocan la prosa, así que la prosa se reescribe una sola vez, después).

- [x] R2-T1 — Prosa de `Instrucciones` y docs/07, la `McpException` verificada y un test de que `instructions`
  nombra las once tools. Ruta: inline (writer único de la rebanada).
  - `McpException`: verificada con `ConectorMcpErroresQueEscapanTests`, que registra dos tools de prueba solo en
    el host del test (`ConfigureTestServices` + `McpServerTool.Create`). Resultado observado con el SDK 2.2.0:
    las dos llegan como resultado con `isError`; una excepción común trae solo
    `An error occurred invoking '<tool>'.` y una `McpException` trae ese texto seguido de `: <su mensaje>`. La
    afirmación de docs/07 y del resumen de `ResultadoDeHerramienta` ("incluso el de una `McpException`") era
    falsa y se corrigió; el primer borrador del test, que imprimía las dos respuestas, es la evidencia.
  - `Instrucciones`: matriz de roles por tool (todos: `quien_soy`, `articulo_info`; Supervisor y Admin: las tres
    de ventas; Admin: las cuatro de compras y las dos de códigos), la negación de ventas como texto y la de
    compras y códigos como error, `contexto_de_compras` con percepciones y rol, las dos de asociación atadas a la
    factura (si `buscar_articulos` no encontró la línea por el código del proveedor y el usuario confirma el
    artículo), y sin el fragmento `"Las consultas de " +` en una línea propia.
  - docs/07: tabla de roles e identidad (`quien_soy`), argumentos en camelCase, `zona_horaria` como parámetro de
    Ways resuelto por empresa o punto de venta (no argumento), el contiene "ni acentos" sobre nombre o código
    interno, las de códigos atadas a la factura, y la conducta real de la `McpException`. La descripción de
    `articulo_info` y de su argumento pasan a decir "código de barras activo", como el código (`c.Activo`).
  - Test: `LasInstruccionesNombranCadaHerramientaYLaFraseDeEscrituraNombraLasQueNoSonDeSoloLectura` (en la clase
    del conjunto; reemplaza al test de las tres de escritura de `ConectorMcpCrearBorradorDeCompraTests`). Sin RED
    (la prosa vieja también nombraba las once). Mutación: `ventas_resumen` → `ventas_total` en `Instrucciones` →
    cae con `Item: "ventas_resumen"`.
- [x] R2-T2 — Test del conjunto exacto de once tools. Ruta: inline (writer único de la rebanada).
  `ConectorMcpConjuntoDeHerramientasTests`: `tools/list` anuncia exactamente las once, cada una con su título, sus
  cuatro anotaciones, sus argumentos y sus requeridos (cierra L10: antes había esquemas fijados para 6 de 11); y
  un test estructural por reflexión: los argumentos publicados de cada método `[McpServerTool]` son exactamente sus
  parámetros con `[Description]`, así que ningún parámetro inyectado (`log`, `db`, servicios, autorización,
  accesor, urls, token) aparece en un esquema. Sin RED: el contrato actual ya cumple; el test fija lo que no estaba
  fijado. Mutaciones (una corrida, dos mutantes en archivos distintos): `buscar_articulos` con `Idempotent = false`
  → cae el test del conjunto en esa línea; `[Description]` quitado de `nombre` en `buscar_proveedor` → cae el
  estructural (`["cuit"]` esperado, `["cuit", "nombre"]` publicado). Retirado lo que quedó cubierto del todo: los
  tests de anotaciones de `quien_soy` (flujo) y de las tres de compras, y las aserciones de anotaciones, título y
  `Subset` de los tests de ventas, ficha, catálogo y borrador; se conservan sus aserciones de esquema.
- [x] R2-T3 — Constantes en las descripciones, el `MaximoDeCandidatos` repetido y la convención de nombres de
  argumentos. Ruta: inline (writer único de la rebanada).
  - Constantes: `ConectorMcpDescripcionesYConstantesTests` compara cada número de las descripciones con su
    constante (la `[Description]` no admite interpolar un `int`): palabras (6), candidatos por línea (5) y líneas
    (100) de `buscar_articulos`; líneas del borrador (100); candidatos listados (10), días de ventas (30) y compras
    (1 a 20, 5) de `articulo_info`; días de la tabla por día (31) de `ventas_resumen`; el tope del rango (366, la
    constante privada de `RangoDeReporte` leída por reflexión) en las tres de ventas y su `desde`; `limite` (1 a
    50, 20) de dos de ventas; y que los "N días" de las instrucciones sean exactamente {30, 366}. Para eso pasan a
    públicas `LimitePorDefecto`, `LimiteMaximo`, `MaximoDePalabras` y `DiasMaximosDeLaTablaPorDia`. Sin RED: hoy
    no había deriva. Mutaciones (una corrida): `LimiteMaximo` 60 → caen las dos filas de `limite`;
    `MaximoDeOpcionesListadasEnUnError` 9 → cae la fila de candidatos de la ficha; `DiasDeVentas` 31 → caen la
    fila de la ficha y el test de las instrucciones.
  - Nombres: el privado de 10 de `ServicioDeConsultasDeVentas` pasa a `MaximoDeOpcionesListadasEnUnError`
    (público) y reemplaza también el `Take(10)` literal del error de alcance; el público de 5 de compras pasa a
    `MaximoDeCandidatosPorLinea`.
  - Convención de argumentos: camelCase. Conteo de nombres publicados de más de una palabra: en snake_case
    `punto_venta`, `agrupar_por`, `id_articulo`, `id_proveedor` y `confirmar_codigo_no_estandar` (5 tools); en
    camelCase los 12 argumentos de `buscar_articulos` y `crear_borrador_de_compra` más todos los campos de las
    listas anidadas (`codigoProveedor`, `idArticulo`, `costoUnitario`…, que el SDK serializa en camelCase).
    **Cambio de contrato** de cinco tools: `ventas_resumen`, `ventas_agrupadas` y `ventas_por_articulo`
    (`punto_venta` → `puntoVenta`, `agrupar_por` → `agruparPor`; los valores de `agruparPor`, como
    `punto_venta`, no cambian), `asociar_codigo_de_barras` (`id_articulo` → `idArticulo`,
    `confirmar_codigo_no_estandar` → `confirmarCodigoNoEstandar`) y `asociar_codigo_de_proveedor` (`id_articulo`,
    `id_proveedor` → `idArticulo`, `idProveedor`). Claude relee el esquema en cada sesión; el mensaje de
    agrupación desconocida, las descripciones, las instrucciones, docs/07 y los tests usan los nombres nuevos.
  - Verificado: filtro `ConectorMcpVentas|…Catalogo|…BuscarArticulos|…Conjunto|…Descripciones|…FichaDeArticulo`
    123/123.
- [ ] R2-T4 — Test de paridad de `AcotarComoElReporteAsync` con `ObtenerVentasPorArticuloAsync` (L11), sin
  cambios de producción.

## Criterios de aceptación

- Un `ErrorDominio` o un `DbUpdateException` clasificado que escapa de cualquier tool llega como
  resultado con `isError` y un texto en castellano.
- La rama de carrera de catálogo sin dueño encontrado dice qué pasó, sin el mensaje crudo de EF.
- La negación de rol de ventas no cambia.
- `ventas_por_articulo` con texto o artículo acentuado distinto ("fidéos") encuentra "Fideos".
- `ventas_por_articulo` con filtro del reporte corre una sola consulta de líneas por empresa, con el
  mismo porcentaje sobre todo lo vendido.

## Verificación

Observado sobre `31cecfae` (rebaseado sobre `origin/main` `e22281db`), 2026-10-19:

- `dotnet build Ways.slnx -c Release`: compilación correcta, 0 errores (34 advertencias preexistentes; ninguna
  nueva en los archivos tocados).
- `dotnet test tests/Ways.Application.Tests -c Release`: 1059/1059.
- `dotnet test tests/Ways.IntegrationTests -c Release --filter "FullyQualifiedName~ConectorMcp"`: 353/353 (340
  previos + 13 nuevos).
- `dotnet test tests/Ways.IntegrationTests -c Release --filter "FullyQualifiedName~VentasPorArticulo|FullyQualifiedName~FichaDeArticulo|FullyQualifiedName~ConsultasDeVentas"`: 66/66.

Ronda 1 del judgment-day, observado sobre `cdfe8e02` (main sigue en `e22281db`):

- `dotnet build Ways.slnx -c Release`: compilación correcta, 0 errores.
- `dotnet test tests/Ways.IntegrationTests -c Release --filter "FullyQualifiedName~ConectorMcp"`: 361/361.
- `dotnet test tests/Ways.IntegrationTests -c Release --filter "FullyQualifiedName~VentasPorArticulo|FullyQualifiedName~FichaDeArticulo|FullyQualifiedName~ConsultasDeVentas"`: 70/70.

## Progreso

- Rebanada 1 completa en `pnader/mcp-consolidacion-1`, rebaseada sobre `origin/main` después del merge de #477.
- Rondas 1 y 2 del judgment-day corregidas (L1–L9 y el resumen de clase). Siguiente paso: re-juicio de la ronda 2
  (orquestador), PR con auto-merge y después la
  rebanada 2.
