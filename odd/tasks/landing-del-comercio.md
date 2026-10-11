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

- [ ] **T1 — Backend: `vitrinas`, `Publico` mode, public and admin endpoints.** Delegated. PR 4.
  - Entity, configuration, migration with RLS policies and the `publico` mode in
    `ITenantActual`/`TenantActualDeSesion` and the interceptor.
  - `GET /api/publico/vitrina` (anonymous; tenant from the host; Publico mode): nombre
    (Empresa.NombreFantasia or RazonSocial), tagline, descripcion, colorAcento, tema, tieneLogo,
    logoVersion, sucursales[] from the empresa's active puntos de venta (nombre, domicilio,
    horario, whatsapp, instagram, facebook, web). 404 `vitrina_no_publicada` when none.
  - `GET /api/publico/vitrina/logo` streams the bytes with `Cache-Control: public, max-age=3600`
    and an ETag from `logo_actualizado_en`.
  - Admin (role Admin of the tenant): `GET/PUT /api/empresas/{id}/vitrina`,
    `PUT /api/empresas/{id}/vitrina/logo` (multipart, size and content-type validated by magic
    bytes), `DELETE .../logo`. 404-first; DTO fields all persisted.
  - Tests: unit for validation (color, tema, logo type sniffing); integration for Publico mode
    reading only the published vitrina of the host's tenant and nothing from another tenant
    (mutation evidence on the policy predicate), admin round-trip, 404s.
- [ ] **T2 — Web admin: "Vitrina" page.** Delegated. PR 5.
  - Under the empresa configuration: form (tagline, descripción, color de acento, tema), logo
    upload with preview, "Publicar"/"Despublicar", link to open the landing.
  - react-async-state gating, descriptor tests, data gates.
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

(recorded per task as commits land)

## Delivery slices

- PR 4: T1 (stacked on `dominios-por-tenant` PR 3). PR 5: T2. PR 6: T3.

## Next step

Starts after `dominios-por-tenant` T3.
