# Landing pública del comercio y shell de la tienda online

## Objective

Under its own domain (feature `dominios-por-tenant`), a commerce publishes a landing page at `/`
with its logo, tagline, description and branches (address, hours, WhatsApp, social links), in the
Ways visual language, and `/online` serves the storefront shell with its header and cart entry
point. The catalog, cart contents and orders come in later features, after the owner decides how
articles are published.

## Problem / why

- There is no public read model and no public RLS mode: anonymous access only reaches
  `usuarios` and `dispositivos` in Login mode.
- Nothing stores a commerce's public identity (logo, tagline, description, accent color).
- `PuntoVenta` already holds domicilio, horario, whatsapp, instagram, facebook and web, which is
  exactly what the branches section needs.

## Scope (authorized by the owner, 2026-10-11, autonomous mode)

Stacked on feature `dominios-por-tenant` (PR 3 must be merged or be the base). Stops when the
landing and `/online` are reachable under a tenant domain and look right; article publication,
catalog, cart contents, orders, payments and customer accounts are explicitly later.

## Data model (evidence)

Table `vitrinas` (empresa scope: `id_tenant` + `id_empresa` NOT NULL, one per empresa):

| column | type | rule |
|---|---|---|
| `id_vitrina` | bigint PK identity | |
| `id_tenant` | bigint NOT NULL FK | |
| `id_empresa` | bigint NOT NULL FK → `empresas` | UNIQUE (one vitrina per empresa) |
| `publicada` | boolean NOT NULL DEFAULT false | partial unique index: at most one published per tenant |
| `tagline` | text NULL | ≤ 120 chars |
| `descripcion` | text NULL | ≤ 2000 chars |
| `color_acento` | text NULL | `#rrggbb`; NULL = Ways orange `#F7991C` |
| `tema` | text NOT NULL DEFAULT 'oscuro' | `claro` or `oscuro` (CHECK) |
| `logo` | bytea NULL | ≤ 1 MB; png, jpeg or webp only (never svg: inline scripts) |
| `logo_content_type` | text NULL | set together with `logo` |
| `logo_actualizado_en` | timestamptz NULL | ETag source |
| audit | `EntidadBase` columns | soft delete only |

RLS: standard `vitrinas_tenant` policy plus a new access mode `Publico` (`app_modo() = 'publico'`)
with SELECT-only policies, restricted to `id_tenant = app_tenant_actual()`, on exactly:
`vitrinas` (only `publicada`), `empresas`, `puntos_venta`. No other table gets a public policy.

## Constraints

- Same runners, skills and delivery rules as `dominios-por-tenant`.
- Visual language (from the owner's "Carteles Ways" canvas and `src/Ways.Web/src/estilos/tema.css`):
  Bricolage Grotesque for display headings (Google Fonts), Inter stack for body; orange `#F7991C`,
  near-black `#121212`, grey `#C4C5C9`; triple chevrons `›››` as the brand mark; big upper-case
  headings with tight letter-spacing (`-0.045em`), 8–24 px accent bars, square corners on
  posters but the app's `0.5rem` radius on cards and buttons. Dark theme default with light
  variant. No gradients, no emoji, no stock icons.
- Brand assets for the platform fallback and login header: `ways-logo-claro.png` (for light
  backgrounds) and `ways-logo-oscuro.png` (for dark backgrounds), to be committed under
  `src/Ways.Web/public/marca/`.
- The landing must work at phone width, with real `<a>`/`<button>` elements and 4.5:1 contrast.

## Tasks

- [x] **T1 — Backend: `vitrinas`, `Publico` mode, public and admin endpoints.** Delegated. PR 4. Commits `8f5c85a9`, `6250e913`, `a3f30aa7`, `f87efc49` (judgment-day fixes).
  - Entity, configuration, migration with RLS policies and the `publico` mode in
    `ITenantActual`/`TenantActualDeSesion` and the interceptor.
  - `GET /api/publico/vitrina` (anonymous; tenant from the host; Publico mode): nombre
    (Empresa.NombreFantasia or RazonSocial), tagline, descripcion, colorAcento, tema, tieneLogo,
    logoVersion, sucursales[] from the empresa's active puntos de venta (nombre, domicilio,
    horario, whatsapp, instagram, facebook, web). 404 `vitrina_no_publicada` when none.
  - `GET /api/publico/vitrina/logo` streams the bytes with `Cache-Control: public, max-age=3600`
    and an ETag from `logo_actualizado_en`; a matching `If-None-Match` answers 304 without reading
    the bytes. Both public endpoints answer 404 for a suspended tenant.
  - Admin (role Admin of the tenant): `GET/PUT /api/empresas/{id}/vitrina`,
    `PUT /api/empresas/{id}/vitrina/logo` (multipart, size and content-type validated by magic
    bytes), `DELETE .../logo`. 404-first; DTO fields all persisted. Added by the judgment-day fixes:
    `DELETE /api/empresas/{id}/vitrina` (soft delete, which also unpublishes; 204 and idempotent
    when there is no vitrina) and `GET /api/empresas/{id}/vitrina/logo` (preview: bytes with
    content type, `Cache-Control: private, no-store`, ETag + 304, 404 `logo_no_encontrado`, works
    for an unpublished vitrina).
  - Tests: unit for validation (color, tema, logo type sniffing); integration for Publico mode
    reading only the published vitrina of the host's tenant and nothing from another tenant
    (mutation evidence on the policy predicate), admin round-trip, 404s.
- [x] **T2 — Web admin: "Vitrina" page.** Delegated. PR 5. Commits `3505e91e` (client and helpers), `5d185496` (page, route, menu).
  - Under the empresa configuration: form (tagline, descripción, color de acento, tema), logo
    upload with preview, "Publicar"/"Despublicar", link to open the landing.
  - react-async-state gating, descriptor tests, data gates.
  - Done as `/organizacion/vitrina` (Root and Admin), menu entry "Vitrina" under Organización. Publicar/Despublicar is
    the "Publicada" switch inside the single Guardar write. The empresa selection lives in component state (not the URL).
- [ ] **T3 — Web public: landing at `/` and storefront shell at `/online`.** Delegated. PR 6.
  - `index.html` entry renders the landing from `/api/publico/vitrina`: hero (logo, name,
    tagline, chevrons, CTA "Comprar online" → `/online`, CTA WhatsApp), description block,
    branches grid (address, hours, WhatsApp button, Instagram/Facebook links), footer with
    "Hecho con Ways" and the Ways mark. Unpublished: a calm "Este comercio todavía no publicó su
    página" screen. Platform host: redirect to `/admin`.
  - `online.html` entry: header (logo, name, cart button with count 0), empty state "El
    catálogo se está preparando", footer. Cart state is a typed context with no items yet.
  - Tests for the mapping helpers and the two pages' states (published, unpublished, error).

## Verification evidence

### T1 (commits `8f5c85a9` modo publico, `6250e913` tabla y RLS, `a3f30aa7` API)

- `dotnet build src/Ways.Api/Ways.Api.csproj -warnaserror`: 0 warnings, 0 errors.
- `dotnet test tests/Ways.Domain.Tests`: 1192 passed. `dotnet test tests/Ways.Application.Tests`: 811 passed.
- `dotnet test tests/Ways.IntegrationTests` (alone, once, at the end): 3422 passed, 0 failed, 29 m 25 s.
- `dotnet format --verify-no-changes` on the changed files: only baseline noise (ENDOFLINE, IDE0008, IMPORTS, and the
  WHITESPACE findings on untouched lines of `Program.cs` and the comment lines of `TenantActualDeSesion.cs`).
- Deviation from the brief, found while writing the "Publico cannot read `articulos`" test: the standard policies
  `{tabla}_tenant` do not check the mode, so a public mode carrying its tenant in `app.tenant_id` would read and write
  every tenant table. The tenant of the public mode travels in its own GUC `app.tenant_publico`
  (`app_tenant_publico()`), the policies use it, and `app.tenant_id` stays empty. Second small addition: the vitrinas
  policy also requires `deleted_at IS NULL`.
- Bug caught by the tests before shipping: `NULL IN (...)` is NULL and a CHECK that evaluates to NULL passes, so
  `ck_vitrinas_logo` accepted a logo with no content type until the type got an explicit `IS NOT NULL`.

Mutation evidence (clause mutated, tests that went red, then reverted and green):

| # | Mutation | Red |
|---|---|---|
| M1 | `vitrinas_publico_lectura` without `id_tenant = app_tenant_publico()` | `EnModoPublicoLaPolicyDeVitrinasDejaVerSoloLaPublicadaYVivaDelTenantDelGuc`, `ElContextoPublicoDeEf...`, `EnModoPublicoSinTenantNoSeVeNada` |
| M2 | same, `empresas` | `EnModoPublicoLasPoliciesDeEmpresasYPuntosVenta...`, `ElContextoPublicoDeEf...`, `EnModoPublicoSinTenantNoSeVeNada` |
| M3 | same, `puntos_venta` | `EnModoPublicoLasPoliciesDeEmpresasYPuntosVenta...`, `ElContextoPublicoDeEf...`, `EnModoPublicoSinTenantNoSeVeNada` |
| M4 | vitrinas policy without `publicada` | `...DeVitrinasDejaVer...`, `ElLogoDeUnaVitrinaSinPublicarNoSeSirve`, `UnComercioSinVitrinaPublicadaViva404...` (+1) |
| M5 | vitrinas policy without `deleted_at IS NULL` | `...DeVitrinasDejaVer...`, `ElContextoPublicoDeEf...` |
| M6 | policies without `app_modo() = 'publico'` | `ElGucPublicoSinElModoPublicoNoAbreNada` x3 |
| M7 | `ComoGucs` also puts the tenant in `app.tenant_id` | `ElContextoPublicoDeEf...`, `ElReaplicadoSobreUnaConexionAbierta...`, `ElModoPublicoLlevaSuTenantEnElGucPublico...` (unit) +2 |
| M8 | extra public policy on `areas` | `EnModoPublicoNoSeLeeNingunaOtraTablaDelTenant`, `SoloVitrinasEmpresasYPuntosVenta...` |
| M9 | `GuardarAsync` validates before the 404 | `UnaEmpresaQueElAdminNoVeDa404NoEncontradoAntesQueCualquierValidacion` x3 |
| M10 | `GuardarAsync` with a retrying strategy | 2 structural tests (Application) + `UnaFallaTransitoriaAlCrearLaVitrinaNoSeReintentaNiGuardaNada` |
| M11 | no tenant lock | `DosPublicacionesSimultaneasDelMismoTenantDanUnOkYUn409VitrinaYaPublicada` |
| M12 | no translation of `ux_vitrinas_publicada_por_tenant` | `PublicarUnaSegundaEmpresaDelTenantDa409...`, `DosPublicacionesSimultaneas...` |
| M13 | logo sniffing accepts anything | 11 domain tests + `UnLogoConTipoDeclaradoOContenidoNoAdmitidoDa415...` |
| M14 | logo limit `>=` instead of `>` | `UnLogoDeExactamenteUnMegabyteSeAcepta` (unit) and the integration one |
| M15 | constant ETag | `ElEtagYLaVersionSalenDeLaUltimaCarga...`, `ElLogoSeSirveConSuTipoCacheDeUnaHora...` |
| M16 | vitrina created with `IdTenant = 0` | `LaPlataformaTambienAdministraLaVitrina...` (+2) |
| M17 | DELETE of the logo keeps the content type | `QuitarElLogoLoBorraEntero...` (+2) |

#### Judgment-day fixes (commit `f87efc49`, `fix(api): ajustes de judgment-day de la vitrina: ...`)

1. Conditional logo without loading the bytes: the reader got `ObtenerVersionDelLogoAsync` (type and `logo_actualizado_en`
   only) next to `ObtenerLogoAsync`; the shared `PublicoEndpoints.ServirLogoAsync` asks for the version first and answers 304
   when `If-None-Match` (ETag, weak ETag or `*`) matches, and only otherwise reads the bytea. The cache headers are set
   after the lookups, so a 404 is never publicly cacheable. ETag format unchanged (`EtagDelLogo.De`).
2. Suspended tenant: `DominioResuelto`/`IDominioActual` carry `TenantPuedeOperar` (`Tenant.PuedeOperar`), computed in
   `ResolvedorDeDominios` from the platform-context tenant read it already did for the name. Isolation choice: no new
   platform-context read site and no `tenants` policy for the public mode; the cost is the existing 60 s resolver cache
   (a suspension or reactivation takes up to 60 s to show on the domain). The domain still resolves
   (`/api/publico/sitio` stays 200); only the vitrina and its logo answer 404.
3. `DELETE /api/empresas/{id}/vitrina`: soft delete, 404-first, one read under the tenant lock, non-retrying strategy
   (listed in the structural test), idempotent 204. Raw-SQL usage guard ignores `deleted_at` (OD4), so a deleted vitrina
   still counts there and does not free the empresa for deletion; the guard was not touched and is not tested here.
   A later PUT creates a new live row (the unique indexes are partial on live rows).
4. `GET /api/empresas/{id}/vitrina/logo` (see contract above). GET routes need no authorization-allowlist entry (the
   allowlist is for non-GET routes; the GET policy walkers already cover the group); the DELETE vitrina route is in it.
5. Backstop comment in `ManejadorDeErrores` and docs/10 now say that the `ck_vitrinas_logo` backstop is unreachable
   through the API and answers 400 `logo_no_admitido`, while `ValidarLogo` answers 413 `logo_demasiado_grande` / 415
   `logo_no_admitido`.

Verification: `dotnet build src/Ways.Api/Ways.Api.csproj -warnaserror` 0 warnings, 0 errors;
`dotnet test tests/Ways.Application.Tests` 813 passed; the Vitrina|Dominios|SuperficieDeAutorizacion|Publico integration
filter 168 passed; `dotnet format --verify-no-changes` on the changed files: only baseline ENDOFLINE/IDE0008/IMPORTS noise.

| # | Mutation | Red |
|---|---|---|
| M18 | 304 path also calls `obtenerLogo()` | `UnaCondicionalQueCoincideDa304SinLeerLosBytesDelLogo` (bytes counter 4 instead of 1) |
| M19 | `TenantQueSirve` ignores `TenantPuedeOperar` | `UnTenantSuspendidoNoSirveSuVitrinaNiSuLogoPeroSuDominioSigueResolviendo` |
| M20 | resolver always reports `TenantPuedeOperar = true` | same test |
| M21 | `EliminarAsync` without `DeletedAt` | `DarDeBajaLaVitrinaLaDespublica...`, `ElBorradoDeLaVitrinaYLaVistaPreviaDelLogoSoloLosHacen...` |
| M22 | `ObtenerLogoAsync` (admin) without the empresa 404 | survived the HTTP theory (the version call always runs first); killed by `CadaLecturaDelLogoDelServicioDa404NoEncontrado...` added for it |

Open for the owner: `vitrinas` is a `marcado` dependent in the usage guard (same as `dominios`), so a tenant or empresa
that ever had a vitrina saved cannot be deleted, even after the vitrina itself is soft-deleted.

### Judgment-day of T1 (target a94c9cb3, round 0, 2026-10-11)

Judges A and B: no CRITICAL or HIGH finding. `JUDGMENT: APPROVED`. Coincident items fixed in
`f87efc49`: the public logo answers 304 without loading the bytea (version probe first), a
suspended tenant is not served publicly (404 on both endpoints, domain still resolves, 60 s
resolver cache applies), `DELETE /api/empresas/{id}/vitrina` (soft delete, idempotent) so the
admin is not locked, `GET /api/empresas/{id}/vitrina/logo` for the admin preview, and the
backstop comment/docs now match the codes. Verified with build `-warnaserror`, Application tests
and the `Vitrina|Dominios|SuperficieDeAutorizacion|Publico` integration filter (168 tests) plus
mutants M18–M22; no new judge round. Follow-ups: no server-side cache on the public endpoints
(negative caching), the empresa/tenant baja guard ignores `deleted_at` so a deleted vitrina
still counts, no per-branch opt-out of the public listing, PUT loads the logo bytea.

### T2 (commits `3505e91e` client and helpers, `5d185496` page, route and menu)

Route/topology: delegated bounded writer (trigger: 2+ non-trivial files). Branch `pnader/vitrina-web`, based on
`pnader/vitrina-backend` (`233f9456`), because the checkout the writer started in did not yet contain `f87efc49`.

- `api/cliente.ts`: `api.putBinario(ruta, archivo)` sends the raw `File`/`Blob` with its own `Content-Type`
  (`application/octet-stream` when it has none); `pedir` already let caller headers win over the JSON default.
- `api/vitrinas.ts`: `obtenerVitrina`, `guardarVitrina`, `eliminarVitrina`, `subirLogo`, `quitarLogo`, `urlDelLogoAdmin`
  (`?v=logoVersion`) and the pure helpers `esColorHex`, `colorParaElSelector`, `tiposDeLogoAdmitidos`, `validarLogo`
  (type first, then size, same order as the server), `aBorrador`/`aEdicion`, `opcionesDeEmpresaParaVitrina`,
  `copiaDeFalloDeVitrina` (the seven codes, plus 413/415 without a code), `debeReleerTrasFallo`. Types `VitrinaAdmin`,
  `VitrinaEdicion`, `TemaDeVitrina` in `api/tipos.ts`.
- `paginas/Vitrina.tsx`: three generation-gated reads (empresas, sitio, vitrina); every write behind a synchronous ref
  and a full-window inert state; the saved vitrina and the editable draft are separate states, so a logo upload or removal
  never overwrites unsaved form text. `PUT vitrina` and `PUT vitrina/logo` apply the `VitrinaAdmin` the server returns
  (the surface is inert for the whole write); the two `DELETE`s re-read, with the "se guardó, pero no se pudo actualizar la
  vista" notice when only the re-read fails; a 404 or an uncertain failure re-reads and keeps the message. "Ver la
  página" (`/`, new tab) only when `GET /api/publico/sitio` says tenant domain and the saved vitrina is published.
- Client-side rejections that send nothing: logo type, logo over 1 MB, color not `#rrggbb` (Guardar is disabled and the
  copy says so).

Verification (in `src/Ways.Web`): `npx tsc -b` exit 0; `npx oxlint` exit 0, 16 warnings, all pre-existing
(`only-export-components`), none in the new files; `npx vitest run` 183 files, 4724 tests passed; `npx vite build` ok.
New tests: 55 in `paginas/Vitrina.test.tsx`, 51 in `api/vitrinas.test.ts`, 4 for `api.putBinario` in `api/cliente.test.ts`,
two lines in `componentes/menu.test.ts`.

| # | Mutation | Red |
|---|---|---|
| M1 | `validarLogo` without the size check | `validarLogo > un byte más de 1 MB...`, `un archivo de más de 1 MB se rechaza sin salir a la red`, `elegir un archivo inválido borra el rechazo...` |
| M1b | size check `>=` instead of `>` | `un PNG de 1 MB justo se acepta`, `un archivo de exactamente 1 MB se sube` |
| M2 | `putBinario` without the `Content-Type` header | `manda el archivo tal cual con su propio Content-Type...`, `un archivo sin tipo conocido sale como application/octet-stream...` |
| M3 | vitrina read without the generation check | `una vitrina vieja que llega tarde no pisa la de la empresa elegida después` |
| M4 | ref guard removed from `guardar` | `dos envíos del mismo tick salen como una sola request` |
| M5 | ref guard removed from `elegirLogo` | `dos cambios del mismo tick salen como una sola subida` |
| M6 | `editar` keeps the rejection | `editar el formulario borra el rechazo...` |
| M7 | logo upload also resets the draft | `un archivo válido se sube crudo y la vista previa pasa a la versión nueva sin tocar el borrador` |
| M8 | link shown on the platform host | `bajo la plataforma no hay enlace...` |
| M9 | `guardar` does not clear previous messages | `guardar borra el aviso de éxito de una subida anterior` |
| M10 | link driven by the draft instead of the saved vitrina | the two `enlace a la página` tests about publishing |
| M11 | no re-read after a 404 / uncertain `guardar` failure | `un fallo incierto relee...`, `un 404 relee...` |
| M12 | Guardar enabled with an invalid color | `un color mal escrito se explica junto al campo y deshabilita Guardar` |
| M13 | upload without the client-side validation | the three client-rejection tests |

## Delivery slices

- PR 4: T1 (stacked on `dominios-por-tenant` PR 3). PR 5: T2. PR 6: T3.

## Next step

T3 — public landing at `/` (`index.html` entry) and storefront shell at `/online` (`online.html` entry), from
`GET /api/publico/vitrina` and `/api/publico/vitrina/logo`. Open for the owner after T2: the Vitrina page is wired as
`Root` and `Admin`; the "Ver la página" link assumes the landing is served at `/` of the tenant domain.
