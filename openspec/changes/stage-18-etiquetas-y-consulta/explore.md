# Explore — Stage 18: Etiquetas, carteles y consulta de precios

Fecha: 2026-08-21. Fase ejecutada por sdd-explore (sonnet) bajo mandato autónomo; contenido
persistido verbatim por el orquestador (el agente de fase no tenía Write en su toolset).

## 1. Qué dice el doc 11 y qué tiene el legado

**Doc 11, Etapa 18** (`docs/11-programa-post-paridad.md:347-363`), cita textual completa:

> **Alcance.** Etiquetas de góndola y carteles de precio imprimibles (formatos configurables,
> selección por artículo, categoría, marca u oferta activa) y una vista o app de consulta de
> precios para el salón, pensada para lectura de código de barras desde un dispositivo del
> local.
>
> **Por qué acá.** Es operación de piso: mejora el día a día pero no habilita nada. Se hace
> cuando la infraestructura de impresión ya está resuelta y no hay que decidirla para esto.
> Ambos ítems vienen del roadmap del doc 06.
>
> **Dependencias.** Etapa 11 (infraestructura de impresión y descarga). **Tamaño:** media.
>
> **Decisiones abiertas.** Qué formatos de etiqueta se soportan y si son configurables por
> empresa; si la consulta de precios es una vista responsive del sistema o una superficie
> separada con autenticación propia; qué precio muestra cuando hay ofertas y listas
> diferenciadas.

**Legado alsina: cero paridad, confirmado por búsqueda exhaustiva.** `docs/06-roadmap.md:158-159`
lista explícitamente "App de consulta de precios para el salón" y "Etiquetas y carteles de
góndola" bajo el título **"Después del cutover (el 'escalarlo y mejorarlo')" — "Ninguna de estas
entra en la paridad funcional"**. Confirmado independientemente: `grep -i "etiqueta|cartel"` sobre
`docs/01-features-existentes.md` da solo dos falsos positivos sin relación —
`docs/01-features-existentes.md:71` ("Etiqueta: `articulos.nombreOferta`" — el texto que imprime
la línea de descuento en el ticket, no una etiqueta física) y `:382` ("Etiqueta en pantalla" — la
tabla de tipos de movimiento de cuenta corriente). `alsina/imprimirArticulos.php` (citado en
doc-11:11 como valor retenido fuera del contrato de paridad) es la **lista de reposición** por
proveedor (sin stock / bajo mínimo, `alsina/modulos/articulos/stock.php:56,107`) — no imprime
etiquetas de precio ni tiene formato configurable, solo un `window.print()` de una lista de
nombres. **Es greenfield puro**, mismo perfil que las Etapas 16/17.

## 2. Infraestructura heredada

### 2.1 Impresión (Etapa 11) — con una advertencia que la propia Etapa 11 dejó escrita

El patrón confirmado en `src/Ways.Web/src/estilos/impresion.css:1-6`: el mismo componente que se
ve en pantalla se imprime vía el "Guardar como PDF" del navegador, sin ruta ni fetch dedicados;
`@media print` oculta `#top` (nav) y `.d-print-none`. **Cero librería PDF en todo el repo**
(`verify-report.md:121` de la Etapa 11: `grep -i pdf` sobre todo `.csproj` y `package.json` no
devuelve nada).

**Hallazgo crítico para el proposal**: la Etapa 11 **evaluó QuestPDF y lo rechazó explícitamente,
pero difirió la decisión — no la cerró** — citando la Etapa 18 por nombre:
`openspec/changes/archive/2026-08-12-stage-11-exportacion-reportes/proposal.md:206-214`:

> QuestPDF was evaluated honestly: its Community licence is free only below a declared
> annual-gross-revenue threshold [...]. Deferred to a future stage that actually needs
> pixel/print-precise layout (Etapa 19 [...]) and **Etapa 18** (etiquetas y carteles, which
> have physical layout requirements). Deciding it here would decide it blind.

Esto significa que **la Etapa 18 es, textualmente, la etapa para la que ese rechazo quedó en
suspenso** — el proposal tiene que revisitar la decisión con datos reales de formato, no heredarla
por inercia.

### 2.2 Precedente de impresión mm-precisa: existe pero está muerto en el código nuevo

Hay un `src/Ways.Web/src/estilos/ticket.css` con anchos exactos en `mm` (`body { width: 80mm }`,
columnas de `45mm`/`35mm`) — prueba de que layouts físicos precisos vía CSS pura ya funcionaron en
el legado (`alsina/ticket.php` lo usa). **Pero en `Ways.Web` (React) ese archivo no está importado
por ningún componente** (`grep "ticket.css" src/Ways.Web/src` → 0 matches en imports; el único hit
es un string de test). Es un artefacto huérfano, copiado pero nunca cableado. Conclusión: hay
precedente de que "CSS con mm exactos" funciona para el hardware del negocio, pero **cero
precedente vivo** de una grilla de N etiquetas por hoja (que es un problema distinto —
alineación de múltiples celdas contra papel autoadhesivo pre-troquelado, no un ticket de una sola
columna).

### 2.3 Motor de precios/ofertas (Etapa 5, con enmiendas 4/17)

`src/Ways.Application/Precios/ServicioDePrecios.cs` resuelve el precio vigente por
(artículo, lista) a una fecha — `PrecioVigenteAsync`/`PreciosVigentesAsync`/
`PreciosVigentesEnLoteAsync` (esta última, `:319-439`, es la resolución batch pensada para "muchos
artículos a la vez", exactamente el shape que necesita una pantalla de selección múltiple de
etiquetas). El motor de ofertas, `src/Ways.Domain/Ofertas/ResolvedorDeOfertas.cs`, ya resuelve
"oferta activa" con toda la semántica de doc 10 (vigencia por fecha/hora/día de semana, alcance
artículo/grupo/categoría, base no-acumulable + acumulables apiladas).

**El endpoint que ya expone exactamente lo que el cartel necesita**: `POST /api/ofertas/resolver`
(`src/Ways.Api/Endpoints/OfertasEndpoints.cs:51-53`) devuelve, por línea,
`ResultadoDeResolucion(IdArticulo, IdListaPrecio, PrecioOriginal, PrecioFinal, DescuentoUnitario,
Aplicadas)` (`src/Ways.Application/Ofertas/Contratos.cs:109-115`) — `PrecioOriginal` vs
`PrecioFinal` es literalmente el par "precio tachado / precio final" que un cartel de oferta
necesita mostrar, sin construir nada nuevo del lado de resolución. Requiere `Politicas.OperacionDePos`
(ver §3).

### 2.4 Códigos de barra: existen, con motor de resolución ya listo

`docs/10-modelo-de-datos.md:255-257` — tabla `codigos_barra` tenant-wide, N códigos por artículo,
`UNIQUE(codigo, id_tenant)`. El motor de resolución ya existe y está en producción:
`src/Ways.Application/Ventas/ServicioDeEscaneo.cs` + `src/Ways.Domain/Ventas/ParserDeEscaneo.cs`
(`GET /api/articulos/escaneo?entrada=...`) — parsea `<cantidad>*<código>`, corta por longitud
(`< 7 dígitos` ⇒ `codigo_interno`, si no ⇒ `codigos_barra`), y devuelve identidad pura
(`ArticuloEscaneado`: id, código interno, nombre — **nunca precio**, por diseño explícito:
`ServicioDeEscaneo.cs:9-10`, "design decisión 7: la resolución de precio queda en el único camino
existente, `POST /api/ofertas/resolver`"). Este es exactamente el par de servicios que la
consulta de precios del salón necesita reusar: escanear → identidad → resolver precio+oferta en
un segundo llamado. La UX del POS (`src/Ways.Web/src/paginas/Pos.tsx:1076-1077`) ya resuelve el
patrón de input: un `<input autoFocus>` con `onKeyDown` en `Enter` — el patrón estándar de
"pistola lectora como teclado" (keyboard wedge), reusable tal cual.

### 2.5 Ejes de catálogo reales (schema, doc 10)

`areas` (rubro operativo, plano), `categorias` (jerárquica, `id_categoria_padre`), `marcas`
(comercial, ya no liga a proveedor) — los tres son tabla `[catálogo]` (`id_tenant` +
`id_empresa NULL`, doc-09:84). "Oferta activa" como eje de selección **no es una tabla
separada** — es una consulta contra `ofertas` con vigencia evaluada al momento de generar el
cartel (mismo resolvedor de §2.3). El doc-11 pide "por artículo, categoría, marca u oferta
activa" — los primeros tres son columnas directas de `articulos`/joins de catálogo; el cuarto
es una resolución de ofertas vigentes, no un filtro de columna.

## 3. Autorización — el punto de decisión central de la etapa

**No existe ningún precedente de acceso sin sesión o de rol mínimo en todo el sistema.** Verificado
exhaustivamente:

- `src/Ways.Domain/Usuarios/Rol.cs:22-28` — exactamente 4 roles fijos (`Root=1, Admin=2,
  Supervisor=3, Vendedor=4`). No hay un quinto rol "kiosco" ni concepto de dispositivo.
- `src/Ways.Api/Seguridad/Politicas.cs:94-153` — las 11 policies del sistema son TODAS
  `RequireAuthenticatedUser() + RequireClaim(RolId, ...)`. Ninguna admite anónimo.
- `src/Ways.Api/Endpoints/AuthEndpoints.cs` — autenticación por **cookie de sesión**
  (`CookieAuthenticationDefaults.AuthenticationScheme`, persistente, expiración deslizante de 1h),
  no JWT bearer. `POST /api/auth/login` es la única ruta `.AllowAnonymous()` de todo el API
  (grep sobre `Endpoints/` no encuentra otra).
- El único "modo sin sesión" que existe es `ModoDeAcceso.Login`
  (`src/Ways.Infrastructure/Multitenancy/TenantActualDeSesion.cs`) — un estado transitorio de
  UNA sola request (el propio login), no un modo de operación sostenido. No hay precedente de
  "tenant resuelto sin usuario" para servir tráfico de lectura real.
- Los dos endpoints que la consulta de precios necesitaría reusar (`GET /api/articulos/escaneo`,
  `POST /api/ofertas/resolver`) están AMBOS bajo `Politicas.OperacionDePos` — el rol mínimo hoy
  es Vendedor, con usuario y contraseña.

**Conclusión**: la "vista de consulta de precios para el salón, dispositivo sin usuario logueado"
que pide el doc 11 es una superficie de autorización **genuinamente nueva** — no hay ningún
patrón existente para adaptar. Cualquier opción implica diseñar desde cero (ver tabla §5).

## 4. Web — patrones reusables

- **Selección múltiple**: `src/Ways.Web/src/paginas/FacturarRemitos.tsx:134` — `useReducer` con
  un array de ids seleccionados, checkbox "elegir todos" (`:144`), botón de acción habilitado solo
  con `seleccionados.length > 0` (`:229`). Directamente aplicable a "elegir N artículos para
  imprimir etiquetas".
- **Impresión**: `impresion.css` + `@media print` (Etapa 11) para carteles tipo hoja completa;
  sin precedente vivo para grillas de etiqueta pequeña (§2.2).
- **Búsqueda/escaneo**: input con `autoFocus` + `Enter` de `Pos.tsx:1076-1077`, reusable para la
  pantalla de consulta.

## 5. Decisiones abiertas — opciones y recomendación

### Decisión A — Formato de impresión de etiquetas/carteles

| Opción | Pros | Contras | Esfuerzo |
|---|---|---|---|
| **A1. CSS de impresión puro** (extensión de `impresion.css`, grillas `@media print` con `mm`) | Cero dependencia nueva, cero riesgo de licencia, mismo patrón que Etapa 11 y que el legado (`ticket.css` prueba que `mm` exactos funcionan) | Sin precedente vivo de grilla N-por-hoja; alineación exacta contra papel autoadhesivo pre-troquelado varía por navegador/impresora — riesgo real de desfasaje de milímetros en producción | Media |
| **A2. Reabrir QuestPDF** (la decisión que la Etapa 11 dejó explícitamente para acá) | Control pixel-perfect de grilla, el caso de uso para el que fue diseñado | Reintroduce el problema de licencia que la Etapa 11 rechazó (gratis solo bajo un umbral de facturación anual); primera dependencia PDF del repo | Media-Alta |
| **A3. Exportar layout a una hoja de cálculo/plantilla externa** (imprimir desde Excel/Word con mail-merge) | Cero código de layout propio | Rompe el patrón "un click, imprime" del resto del sistema; UX degradada, fuera del hábito de este proyecto | Baja implementación, alta fricción de uso |

**Recomendación**: A1 primero, con un spike explícito en el proposal — antes de comprometerse,
prototipar UNA grilla de etiquetas contra un tamaño de papel real (p.ej. hojas A4 de etiquetas
autoadhesivas estándar) con `@media print` y medir el desfasaje real entre navegadores. Si el
spike falla (desfasaje no tolerable), recién ahí A2 se vuelve la opción, con la decisión de
licencia explícita en manos del dueño del producto — no una decisión técnica silenciosa.

### Decisión B — Autorización de la consulta de precios de salón

| Opción | Pros | Contras | Esfuerzo |
|---|---|---|---|
| **B1. Rol nuevo "Consulta" con usuario/contraseña débil o PIN corto** | Reusa 100% del pipeline de auth existente (cookie, `RolConocido`, `Politicas`) — cero superficie nueva | Sigue exigiendo login humano en un dispositivo fijo de salón — no resuelve "sin usuario logueado" que pide el doc 11; un PIN corto compartido es una forma débil de credencial, no una ausencia de ella | Baja |
| **B2. Sesión de "dispositivo" — token de larga vida atado al dispositivo, no a un usuario, resuelto por subdominio/tenant igual que hoy pero sin claim de rol de persona** | Cumple el requisito real (nadie loguea en el kiosco); acotable a un endpoint de solo-lectura muy angosto (escaneo + resolución de precio, nada más) | Superficie de autenticación nueva de punta a punta: nuevo modo de `TenantActualDeSesion`, nuevo mecanismo de emisión/rotación/revocación de token, nueva policy — ninguna pieza existe hoy; superficie de ataque nueva (un token de dispositivo robado expone precios de todo el catálogo, aunque sea solo lectura) | Alta |
| **B3. Ruta pública tenant-scoped sin ningún token (resuelta solo por subdominio, `AllowAnonymous` real)** | Más simple de operar (nada que rotar ni gestionar) | Cualquiera con el subdominio consulta el catálogo completo de precios del tenant sin control alguno — expone la lista de precios pública, que hoy es información interna; abre la puerta a scraping de catálogo por competidores | Baja-Media |

**Recomendación**: B2, con alcance deliberadamente angosto — el token de dispositivo NO debe
habilitar nada más que `GET escaneo` + `POST ofertas/resolver` de solo lectura (nunca ABM, nunca
reportes, nunca otro endpoint), y el diseño concreto (emisión, formato del token, revocación,
rotación) es trabajo del **design**, no de este explore. B1 es la opción de fallback de menor
esfuerzo si el dueño del producto decide que "un PIN de salón" es aceptable para el tamaño actual
del negocio — vale la pena presentarla como pregunta directa en el proposal, no asumirla resuelta.

### Decisión C — Schema: ¿tabla nueva o parámetro?

| Opción | Pros | Contras | Esfuerzo |
|---|---|---|---|
| **C1. Tabla `formatos_etiqueta` `[catálogo]`** (mismo patrón `id_tenant` + `id_empresa NULL` que `listas_precio`/`ofertas`, doc-09:84) — columnas de dimensión física, cantidad de columnas/filas por hoja, campos a imprimir | Formatos realmente configurables por tenant/empresa sin redeploy; auditable, versionable como el resto del catálogo | Migración nueva — cae bajo el gate de DB del proyecto; hay que decidir el shape completo (¿un formato por empresa o por tenant? ¿versionado como precios?) | Media |
| **C2. `ParametroConocido` nuevo** (mismo patrón que `zona_horaria`/`lotes_habilitado`, `src/Ways.Domain/Catalogos/ParametroConocido.cs`) — un JSON serializado como valor de parámetro | Cero migración, mismo mecanismo de resolución ya probado (`ResolucionDeParametros.Resolver`) | `ParametroConocido` está pensado para valores escalares tipados (decimal/int/bool/string), no para una lista de formatos con estructura propia; forzar un blob JSON ahí rompe la intención documentada del registro ("el ABM va a poder renderizar el editor correcto a partir del tipo declarado") | Baja |
| **C3. Formatos fijos en código** (2-3 plantillas predefinidas, sin configuración de tenant) | Cero schema, cero ABM que construir | Contradice literalmente la decisión abierta del doc-11 ("si son configurables por empresa") — cierra la pregunta sin dársela al dueño del producto | Mínimo |

**Recomendación**: C1 si el proposal confirma que "configurable por empresa" es un requisito real
(el doc-11 lo deja abierto, no lo decide) — el gate de DB del proyecto exige presentar el modelo
completo (tablas, columnas clave, constraints, scoping) para aprobación antes de migrar, así que
esto es trabajo del proposal, no de este explore. Si el dueño del producto prefiere arrancar
angosto, C3 con 2-3 plantillas fijas es un MVP legítimo y más barato, con C1 como expansión
natural después.

## 6. Riesgos

- **Riesgo de layout físico (Decisión A)**: sin spike previo, un formato de etiqueta que se ve
  bien en pantalla puede desalinearse contra papel autoadhesivo real — este es precisamente el
  riesgo que la Etapa 11 identificó y difirió a esta etapa.
- **Riesgo de seguridad (Decisión B)**: es el riesgo más alto de la etapa. Un diseño apurado de
  "acceso sin login" puede exponer accidentalmente más que precios (si el token de dispositivo se
  implementa con alcance amplio en vez de angosto, o si se reusa infraestructura de sesión
  existente sin aislar el claim set). El proposal y el design tienen que tratar esto con el mismo
  rigor que cualquier decisión de autorización nueva del proyecto — no es un detalle de UI.
- **Riesgo de alcance difuso**: "carteles de precio imprimibles" y "consulta de precios" son dos
  features con audiencias distintas (personal administrativo con sesión vs. dispositivo de salón
  sin sesión) que comparten infraestructura de datos pero no de autorización — el proposal debería
  considerar si conviene slicing en dos entregas independientes en vez de una sola PR grande,
  dado el guard de 400 líneas del protocolo de review.
- **Tamaño**: doc-11 lo marca "media". Con el spike de layout + el diseño de autorización de
  dispositivo, el tamaño real puede acercarse a "grande" si Decisión B resuelve B2 — vale la pena
  que el proposal lo reevalúe explícitamente en vez de heredar la etiqueta del doc-11 sin
  cuestionarla.

## Ready for Proposal

Sí, con tres preguntas que el proposal debe resolver explícitamente con el dueño del producto
antes de comprometerse a un modelo (no son ambigüedades que el proposal pueda decidir solo):
(1) ¿el negocio tiene ya un tamaño/hardware de etiqueta físico real contra el cual prototipar el
spike de Decisión A?; (2) ¿cuán tolerable es un PIN de salón (B1) versus la superficie nueva de
un token de dispositivo (B2) — esto es una decisión de apetito de riesgo, no solo técnica?;
(3) ¿"configurable por empresa" (doc-11) es un requisito real hoy o se puede diferir con C3?
