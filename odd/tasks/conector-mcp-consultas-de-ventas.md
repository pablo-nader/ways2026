# Conector MCP: consultas de ventas y compras (etapa S5)

## Objetivo
Que el dueño pueda preguntarle a Claude, por el conector MCP, cuánto se vendió (hoy, esta semana, por medio de pago, por área, categoría o marca), qué artículos se venden más, cuánto se vendió de un artículo y a qué precio se compró la última vez.

## Problema y por qué
El conector MCP (PR #350) solo expone `quien_soy`. Las respuestas a esas preguntas ya existen en los reportes del back office, pero Claude no tiene cómo pedirlas.

## Alcance
- Tools de solo lectura (`ReadOnly`, `Idempotent`, no destructivas) en `src/Ways.Api/ConectorMcp/`, con salida en texto compacto y topes de filas.
- Reusan los servicios de reportes existentes. Las consultas nuevas van en archivos nuevos.
- Sin cambios de esquema, sin tools de escritura, sin cambios en la web ni en servicios existentes.

## Restricciones
- Fechas en la zona horaria de la empresa: el parámetro `zona_horaria` resuelto por `ServicioDeParametros` (punto de venta → empresa → default), igual que `ServicioDeReportesDeVentas`.
- "Vendido" igual que el reporte principal: comprobantes de clase venta, no anulados, sin espejos fiscales, `Total` con signo (las NCX restan). Por línea, igual que el top de artículos.
- Costos y precios de compra solo para Admin y Supervisor.
- Aislamiento de tenant por el filtro global de EF más RLS, verificado por test.
- No editar `src/Ways.Application/Compras`, proveedores, artículos ni `src/Ways.Web`. `RegistroDelConectorMcp.cs` solo para agregar el registro de las tools (si `main` todavía no usa `.WithToolsFromAssembly()`) y las `Instrucciones`.

## Diseño
- **Períodos con nombre** (`hoy`, `ayer`, `esta_semana`, `semana_pasada`, `este_mes`, `mes_pasado`): resolución pura en `Ways.Domain/Reportes`, a partir del "hoy" local (reloj del sistema convertido a la zona resuelta). Semana de lunes a domingo, como `date_trunc('week')`. "Esta semana" y "este mes" terminan hoy.
- **Empresa**: opcional. Sin empresa, todas las empresas del tenant, cada una con su zona; los resultados se suman y se muestra el desglose por empresa cuando hay más de una.
- **Rol** (decidido por la orquestadora, 2026-10-18): el conector nunca es más permisivo que la web. Las tools de ventas evalúan la política `LecturaDeReportes` (Supervisor y Admin); `articulo_info` queda abierta a todos los roles, sin costos ni compras para el Vendedor (política `LecturaDeCostos`); lo que en la web es solo Admin sigue siendo solo Admin.
- **Medio de pago** (decidido por la orquestadora): neto del vuelto, `(Importe − Vuelto) × Signo`, como el cierre de caja. El reporte web suma el importe sin restar el vuelto; su corrección es un follow-up de la orquestadora.

## Tareas
- [x] T1 — `ventas_resumen` y `ventas_agrupadas` por punto de venta, medio de pago, vendedor y día, con la resolución de períodos. Reusa `ServicioDeReportesDeVentas`. PR 1.
- [x] T2 — `ventas_agrupadas` por área, grupo, categoría y marca, y `ventas_por_articulo` con filtros y top N, sobre `ServicioDeReportesDeArticulos.ObtenerVentasPorArticuloAsync` (PR #458) en lugar de una consulta por línea propia. PR 2, apilado sobre el 1.
- [ ] T3 — `articulo_info`: precios vigentes por lista, stock por punto de venta, ventas de 30 días y últimas compras (solo Admin y Supervisor). PR 3, apilado sobre el 2.
- [ ] T4 — `Instrucciones` del servidor MCP y la sección del conector en `docs/07-despliegue.md` (se reparte entre los tres PRs, cada uno documenta sus tools).

## Criterios de aceptación y checks
- Build sin advertencias nuevas en `src`.
- Unitarios de la resolución de períodos.
- Integración por tool a través de `/mcp` con token real: camino feliz con dos tenants (el otro nunca aparece, con evidencia de mutación), período con nombre en la zona de la empresa (un test que falla con UTC), Vendedor sin costos, topes de filas.
- judgment-day antes de cada PR; PR con auto-merge y Auto-fix.

## Entrega
- Estrategia: `ask-on-risk` resuelta como `stacked-to-main` (preferencia del dueño). Pronóstico: unas 2.000 líneas en total, en tres PRs de 500 a 800.
- Rutas: cada tarea va delegada a un escritor (más de dos archivos no triviales).

## Progreso
- Exploración hecha (2026-10-18): servicios reusables y semántica de "vendido" mapeados.

## Evidencia T1 (sin tildar: un check pendiente)
- `dotnet build src/Ways.Api/Ways.Api.csproj -warnaserror -p:NuGetAudit=false`: 0 advertencias, 0 errores (sin `NuGetAudit=false` el baseline falla con NU1902/NU1903 por paquetes del SDK, ajeno a este cambio).
- `dotnet test tests/Ways.Domain.Tests --filter "FullyQualifiedName~PeriodoDeConsulta"`: 35 de 35.
- `dotnet test tests/Ways.IntegrationTests --filter "ConectorMcpVentasTests|ConectorMcpFlujoTests"`: 54 de 55. Falla `ConectorMcpFlujoTests.ToolsListAnunciaQuienSoyDeSoloLecturaNoDestructivaIdempotenteYDeMundoCerrado` porque asserta `Assert.Single` sobre `tools/list` y ahora hay tres herramientas. Está fuera de las superficies permitidas de T1: queda pendiente que la orquestadora lo autorice (cambio mínimo: buscar `quien_soy` entre las herramientas en lugar de exigir una sola). `ConectorMcpVentasTests.ToolsListAnunciaLasHerramientasDeVentasDeSoloLecturaYConSusArgumentos` ya cubre las tres.
- Guardas estructurales: ninguna aplica (sin tabla con `id_tenant`, sin `RemoveRange`/delete, sin endpoint no GET nuevo —`/mcp` ya existía—, sin estrategia de ejecución ni ruta nueva de la SPA).
- Decisiones de producto implementadas, pendientes de confirmar: solo Supervisor y Admin consultan ventas; el medio de pago es `importe - vuelto`.

Mutaciones (cada una aplicada, test fallando, revertida y verde):
- `HoyLocal(reloj.Ahora, zona)` cambiado a `TimeZoneInfo.Utc`: `HoyYAyerSeResuelvenEnLaZonaDeLaEmpresaYNoEnUtc` falla (`Assert.Contains() Failure: Item not found in collection`).
- `(x.Importe - x.Vuelto) * x.Signo` cambiado a `x.Importe * x.Signo`: `ElMedioDePagoSeMuestraNetoDelVuelto` falla (`Assert.Contains() Failure`).
- Política `LecturaDeReportes` reemplazada por `OperacionDePos` (admite al Vendedor) en `HerramientasDeVentas.EjecutarAsync`: `SoloSupervisorYAdminConsultanVentas` falla en las dos herramientas ("Assert.Equal() Failure: Strings differ"); revertida, pasa.
- `.IgnoreQueryFilters()` en el listado de `db.Empresas` del servicio: el test de punta a punta por HTTP (`VentasResumenSumaLoVendidoDelTenantYNuncaMuestraLasVentasDeOtro`) SOBREVIVE, porque como `ways_app` el RLS ya oculta las empresas del otro tenant. No se afirma aislamiento probado con ese test. Se bajó por debajo del confound con `SinRlsElFiltroDeEmpresasDelTenantAlcanzaParaNoSumarOtroTenant` (el servicio sobre un contexto de owner, sin RLS): con la mutación falla (`ErrorDominio : No existe la empresa 1.`, porque la empresa del otro tenant entra al alcance y el reporte la rechaza); sin ella pasa. Lo que aísla el listado es el filtro de EF; en producción lo respalda el RLS.

## Evidencia T2
- Diseño: se reutiliza `ObtenerVentasPorArticuloAsync` (una llamada por empresa). La clasificación es la ACTUAL del artículo, las líneas sin artículo no cuentan y una etiqueta nula va a "(sin área)", "(sin grupo)", "(sin categoría)" o "(sin marca)". `texto`, `marca` y `proveedor` recortan las filas del reporte por ids de artículo; el % es sobre la suma de las filas de una llamada al reporte sin ningún filtro. Se descartó el lector de líneas propio.
- `dotnet build src/Ways.Api/Ways.Api.csproj -p:NuGetAudit=false`: 0 advertencias, 0 errores.
- `dotnet test tests/Ways.Domain.Tests --filter "FullyQualifiedName~PeriodoDeConsulta"`: 35 de 35.
- `dotnet test tests/Ways.IntegrationTests -p:NuGetAudit=false --filter "FullyQualifiedName~ConectorMcp"`: 154 de 154.
- Mutaciones sobre cláusulas propias (aplicada, test rojo, revertida):
  - Sin el recorte por marca/proveedor/texto: 7 casos de `VentasPorArticuloAcotaLasFilasPeroElPorcentajeSigueSiendoSobreTodoLoVendido` fallan.
  - Etiqueta nula descartada en lugar de "(sin …)": fallan los tests de área, grupo, categoría y marca.
  - Denominador del % tomado de las filas filtradas: 5 casos del mismo test fallan (el de categoría+marca fija que el % sigue siendo sobre 1.650).
- Aislamiento: por construcción del reporte reutilizado; `LasConsultasPorLineaNuncaMuestranLasLineasDeOtroTenant` no aísla una cláusula propia y, como `ways_app`, el RLS ya oculta las filas del otro tenant.
- Estructurales: sin tabla, endpoint no GET, delete ni estrategia nuevos.

## Próximo paso
Judgment-day de T2 y pasar a T3.
