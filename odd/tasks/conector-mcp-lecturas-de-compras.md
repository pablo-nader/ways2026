# Conector MCP: lecturas para resolver una factura de proveedor (etapa S3)

## Objetivo

Que Claude, conectado por MCP con la identidad del dueño, pueda resolver todos los ids que necesita
un borrador de compra a partir de la foto de una factura de proveedor: punto de venta receptor, tipo
de comprobante, alícuotas de IVA, proveedor y artículos. La tool de escritura (borrador) llega en
otra etapa, sobre esta.

## Problema

El conector solo expone `quien_soy`. Los endpoints REST equivalentes no sirven tal cual: la búsqueda
de proveedores es substring y no compara CUIT por dígitos (el CUIT se guarda como se tipeó), y no
hay una forma de resolver un lote de líneas de factura en una sola llamada.

## Alcance

- Tres tools de solo lectura (`ReadOnly`, `Idempotent`, no destructivas) en `src/Ways.Api/ConectorMcp`:
  `contexto_de_compras`, `buscar_proveedor`, `buscar_articulos`.
- Autorización: la misma política que toda escritura de compras (`Politicas.GestionDeCatalogo`,
  solo Admin), evaluada con `IAuthorizationService` sobre el principal del token. Sin permiso: error
  de tool (`isError`) sin datos.
- Normalización de CUIT (solo dígitos, tolera guiones, espacios y puntos; exige 11 dígitos) en el
  dominio, comparada en SQL contra el CUIT guardado sin guiones, puntos ni espacios.
- Los errores de las tools vuelven como `CallToolResult` con `isError`: el SDK 2.2.0 reemplaza por
  uno genérico el mensaje de cualquier excepción que escapa, incluida `McpException`.
- Instrucciones del servidor MCP y sección del conector en `docs/07-despliegue.md`.

## Restricciones

- Sin cambios de esquema.
- `RegistroDelConectorMcp.cs`: solo la línea de `.WithTools<...>()` y la constante `Instrucciones`
  (la sesión S1 reescribe OpenIddict en ese archivo).
- Sin tocar `src/Ways.Application/Compras`, `ServicioDeProveedores`, endpoints de compras/proveedores
  ni la web (sesión S2). Las consultas nuevas van en archivos nuevos del conector.
- Salida en texto compacto; sin costos.

## Modelo (sin cambios de esquema)

Lecturas sobre `puntos_venta`, `empresas` (alícuotas de percepción por defecto), `tipos_comprobante`
(clase Compra, activos), `alicuotas_iva` (activas), `proveedores`, `articulos`, `codigos_barra`,
`codigos_proveedor`. Todas pasan por el filtro de tenant de EF, RLS y la baja lógica.

## Tareas

- [x] T1 — Guarda de permiso de compras, resultado de herramienta con `isError` y
  `contexto_de_compras`, más instrucciones y docs. Ruta: inline (archivos nuevos del conector, ya
  entendidos). Tests: integración por `/mcp` (feliz con aislamiento, rechazo por rol, anotaciones).
- [x] T2 — `buscar_proveedor` + normalización de CUIT. Ruta: inline. Tests: unitarios de CUIT;
  integración por `/mcp` (CUIT con separadores, nombre sin acentos, tope, errores, aislamiento,
  rechazo por rol).
- [x] T3 — `buscar_articulos` en lote (código del proveedor exacto → código de barras exacto →
  nombre, hasta 5 candidatos). Ruta: inline. Tests: integración con las tres fuentes, línea sin
  resultado, proveedor ajeno, aislamiento, rechazo por rol, sin costos.

## Entrega

Estrategia `stacked-to-main`, una tool por PR (T1 → T2 → T3): el total supera las 400 líneas y cada
tool es una frontera coherente. Cada PR pasa judgment-day antes de abrirse; al abrir: auto-merge y
Auto-fix.

## Criterios de aceptación

- Las tres tools aparecen en `tools/list` con anotaciones de solo lectura.
- Un usuario que no es Admin recibe un error claro y ningún dato.
- Un proveedor o artículo de otro tenant nunca aparece (test con evidencia de mutación).
- `buscar_proveedor` encuentra "20-12345678-9" buscando "20 12345678 9" o "20123456789".
- La salida de artículos no incluye costos.

## Verificación

- Build de `src` sin advertencias nuevas.
- Tests unitarios del dominio (CUIT) y de integración del conector filtrados por clase.

## Progreso

- Exploración hecha (2026-10-18). RDD apagado por decisión local del clon: rige judgment-day.
- Registro de tools por ensamblado (`WithToolsFromAssembly`), pedido de la orquestadora para que las
  sesiones paralelas no editen `RegistroDelConectorMcp.cs`.
- Verificación: 16 unitarios de CUIT; 30 de integración del conector en verde sobre T3 (5 sobre T1 y
  16 sobre T2, cada rebanada probada aislada). Build de `src` sin advertencias nuevas.
- Mutaciones (cada una hace fallar su test, revertidas): modo de tenant → plataforma (6 tests de
  aislamiento), sin guarda de permiso (6 casos de rol), sin `IdProveedor` en el código del
  proveedor, sin chequeo de proveedor visible, sin quitar guiones del CUIT guardado, sin chequeo
  de línea nula, INNER JOIN a alícuotas, nombre sin aplanar, split solo por espacio, sin rama de
  línea sin datos.
- Judgment-day: T1 y T2 sin hallazgos severos (una sugerencia informativa en T2: el CUIT guardado
  solo se limpia de guiones, puntos y espacios). T3: advertencias corregidas (línea nula, alícuota
  dada de baja, línea sin datos, saltos de línea en nombres) y re-judgment acotado limpio. Queda
  informativo: hasta 3 consultas por línea, mismo costo que el listado de artículos.
  Veredicto: APPROVED en las tres rebanadas.
