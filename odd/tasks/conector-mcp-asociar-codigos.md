# Conector MCP: asociar códigos a un artículo (S6)

## Objetivo

Dos herramientas de escritura del conector MCP para que Claude asocie a un artículo un código de
barras leído de una foto (`asociar_codigo_de_barras`) o un código de proveedor visto en una factura
(`asociar_codigo_de_proveedor`), con la identidad y el rol del usuario que autorizó la conexión.

## Problema y por qué

Hoy el conector solo expone `quien_soy`. El dueño quiere mandarle a Claude la foto de un código de
barras y decirle "asocialo a X", y que un código de proveedor nuevo de una factura quede asociado
para que la próxima vez se resuelva solo.

## Alcance

- `asociar_codigo_de_barras(id_articulo, codigo, confirmar_codigo_no_estandar = false)`:
  reusa `ServicioDeArticulos.AgregarCodigoBarraAsync` y `ListarCodigosBarraAsync`.
- `asociar_codigo_de_proveedor(id_articulo, id_proveedor, codigo)`: reusa el alta idempotente
  `ServicioDeArticulos.AgregarCodigoProveedorAsync`.
- Misma regla de rol que los endpoints (`Politicas.GestionDeCatalogo`, solo Admin), evaluada con
  `IAuthorizationService` dentro de la herramienta para devolver un error claro sin escribir.
- Regla GTIN (largo 8/12/13/14 y dígito verificador GS1) como regla de dominio pura, espejo de la
  advertencia de la web (`validacionCodigoBarra.ts`, PR #334). El servidor no la exige: la
  herramienta no escribe un código no estándar salvo confirmación explícita.
- Docs: sección del conector en `docs/07-despliegue.md`.

## Restricciones

- Sin cambios de esquema. Sin tocar `Instrucciones`, `src/Ways.Application/Articulos`, compras,
  proveedores ni `src/Ways.Web`. Sin editar servicios ni endpoints existentes.
- Registro y errores: patrón de #464 (S3). La clase se registra sola (`WithToolsFromAssembly`) y
  devuelve `CallToolResult` con `ResultadoDeHerramienta.EjecutarAsync`; los errores esperables cortan
  con `ErrorDeHerramienta`. El PR va contra `main` con los commits de #464 y depende de él.
- Docker: nunca suites de integración concurrentes; solo clases afectadas con filtro.

## Modelo de datos

Sin cambios. Lee y escribe `codigos_barra` y `codigos_proveedor` a través del servicio existente.

## Tareas

- [x] T1 — Regla GTIN de dominio + herramientas + registro + tests (unitarios e integración por
  `/mcp`) + docs. Ruta: delegada (writer: 2+ archivos no triviales).

## Criterios de aceptación

- Éxito con nombre del artículo y códigos actuales; repetido en el mismo artículo sin fallar;
  tomado por otro artículo nombra cuál (nombre y código interno); código no estándar pide confirmar
  los dígitos; usuario sin rol no escribe; artículo de otro tenant inalcanzable (con evidencia de
  mutación). Toda escritura se verifica leyendo la base.

## Verificación

Resultados observados (writer de T1, 2026-10-18):

- `dotnet build Ways.slnx`: 0 errores, 10 advertencias (restauración NU190x y analizadores de tests
  preexistentes), ninguna en `HerramientasDeCatalogo.cs`, `ReglaDeGtin.cs`, `RegistroDelConectorMcp.cs`
  ni en los tests nuevos.
- `dotnet test tests/Ways.Domain.Tests --filter "FullyQualifiedName~ReglaDeGtin"`: 17 superadas, 0 fallas.
- `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~ConectorMcp"`: 124 superadas,
  1 falla. Las 19 pruebas nuevas de `ConectorMcpHerramientasDeCatalogoTests` pasan. La falla es
  `ConectorMcpFlujoTests.ToolsListAnunciaQuienSoyDeSoloLecturaNoDestructivaIdempotenteYDeMundoCerrado`,
  que hace `Assert.Single` sobre `tools/list`; ahora hay tres herramientas. Está fuera de la
  superficie del writer: el arreglo es filtrar por nombre `quien_soy` en esa prueba.
- Pendiente fuera de la superficie del writer: la constante `Instrucciones` de
  `RegistroDelConectorMcp.cs` dice que el servidor "ahora solo expone la herramienta quien_soy".

Evidencia de mutación (`mutation-proof-tests`):

- Cláusula del rol: `EsAdminAsync` devolviendo `true` en lugar de evaluar `GestionDeCatalogo`.
  Fallan `UnUsuarioSinRolAdminNoEscribeUnCodigoDeBarras` y `UnUsuarioSinRolAdminNoEscribeUnCodigoDeProveedor`
  (2 de 2). Revertido: verdes.
- Cláusula del tenant: `IgnoreQueryFilters(["Tenant"])` en la lectura del artículo. Las 3 pruebas de otro
  tenant siguen verdes: lo cubre RLS (`ways_app`), así que el filtro de EF de la herramienta no queda
  demostrado por separado y solo se declara defensa en profundidad. Revertido.

Cierre del orquestador de la sesión:

- Arreglada la prueba de `tools/list` de `ConectorMcpFlujoTests`: ahora elige `quien_soy` por nombre.
- Mensaje de confirmación de la herramienta en español neutro (sin voseo).
- `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~ConectorMcpHerramientasDeCatalogoTests|FullyQualifiedName~ToolsListAnunciaQuienSoy"`:
  20 superadas, 0 fallas.
- La constante `Instrucciones` queda para la consolidación de la sesión orquestadora (fuera del alcance de S6).

Rebase sobre #464 (pedido de la sesión orquestadora):

- Mi commit pasó a la punta de `pull/464/head`; `RegistroDelConectorMcp.cs` y `ConectorMcpFlujoTests.cs`
  quedan como los dejó #464 (registro por ensamblado, `tools/list` por nombre).
- `HerramientasDeCatalogo` usa `ResultadoDeHerramienta.EjecutarAsync` y `ErrorDeHerramienta`.
- `docs/07-despliegue.md`: las dos herramientas entran a la tabla de #464.
- `dotnet build Ways.slnx`: 0 errores, ninguna advertencia en los archivos de S6.
- `dotnet test tests/Ways.Domain.Tests --filter "FullyQualifiedName~ReglaDeGtin"`: 17 superadas.
- `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~ConectorMcp"`: 129 superadas, 0 fallas.

## Próximo paso

Judgment-day sobre el commit de S6 y PR.
