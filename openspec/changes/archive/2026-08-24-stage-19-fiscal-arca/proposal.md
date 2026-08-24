# Proposal: Stage 19a — Facturación electrónica ARCA, el núcleo construible sin credenciales

## Intent

doc-11:36 puts electronic invoicing last *"a pesar de ser la de mayor tamaño"*, and doc-11:393-404
names what is missing. `explore.md` proved the shape of the problem and the exact line where the
repository stops being able to make progress on its own.

| Fact | Evidence | Consequence |
|---|---|---|
| **The legacy never invoiced fiscally** | `alsina/ticket.php:16-30` is a `window.print()`; zero SOAP, zero certificate, zero CAE in the whole tree | Greenfield. Nothing to port, nothing to preserve, no parity contract to honour |
| **The fiscal schema is built and asleep** | `condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante` (with `es_fiscal`, `letra`, `codigo_afip`) exist with RLS since stage 1; `FA/FB/FC/NCA/NCB/NCC/NDA` are **already seeded, `es_fiscal = true`, `activo = true`** (`InicializadorDeBaseDeDatos.cs:90-96`) | The catalogue is ready. What is missing is the emitter's own identity, the number, the certificate and the protocol |
| **The fiscal path is closed on purpose, not by omission** | `ServicioDeVentas.cs:1162` rejects every `EsFiscal == true` with a 400; `ResolvedorDeLetraComprobante.cs:16-22` documents itself as *"dormant: el POS de esta etapa solo emite TX/NCX"* | There is a door with a lock on it, and a fully tested rule waiting behind it with **no caller** |
| **The blocker is a human action, not code** | WSASS (the homologación certificate self-service) requires a **real Clave Fiscal Nivel 2 login by a real person** — there is no anonymous testing CUIT for WSFE/WSAA | Everything up to *"send bytes to `wswhomo.afip.gov.ar`"* is buildable and testable **today** |

Three costs the business pays today. **The system cannot issue a legal invoice**: a customer who
needs a Factura A gets a Ticket X, and the shop settles it outside the system. **The stage-17 remito
consolidation is non-fiscal by construction** — `TXR` exists precisely because Etapa 19 had not
happened (`archive/2026-08-21-stage-17…/proposal.md:74,440`). And **the fiscal identity of the
business is not modelled at all**: `Empresa` carries a `Cuit` but no `IdCondicionFiscal`
(`Empresa.cs:9-17`), and the `PPPP` printed on every comprobante is the internal `id_punto_venta`,
not a point of sale ARCA ever assigned (`docs/09:143-145`).

**What 19a delivers.** The day the owner logs into WSASS with their Clave Fiscal, the only remaining
work is to point an already-tested client at `wswhomo` and load a real certificate. Everything else —
schema, domain, state machine, signing, numbering, encryption, QR, and a mock suite that embodies the
official manual's wire contract — is finished, reviewed and merged.

## Orchestrator Decisions (binding)

### OD1 — Stage 19 executes as **THREE sub-stages aligned to the explore's cut line**. This proposal covers **19a only**.

| Sub-stage | Content | Status |
|---|---|---|
| **19a** (this change) | Fiscal schema + domain + CAE state machine + TRA/CMS generator with a self-signed test certificate + WSAA/WSFE clients against mocks carrying the real contract + fiscal numbering + encrypted certificate storage + QR with a synthetic `codAut` | **Ready. Unblocked today.** |
| **19b** | Real homologación: point the tested client at `wswhomo.afip.gov.ar`, load the real certificate, confirm the `FEParamGet*` catalogues, run one real CAE cycle | **BLOCKED — `alta WSASS pendiente del dueño (login con Clave Fiscal)`. It is documented, never requested, never estimated.** |
| **19c** | Fiscal printing with QR, certificate/PV configuration UI, operational contingency (offline queue + CAEA as last resort), the fiscal consolidation type with its writer, libro IVA | **Future change.** Depends on 19a; independent of 19b for everything except a real CAE on paper |

**Rationale.** Approach 2 of the explore is the only one that honours the mandate literally: it keeps
100%-unblocked work from being held hostage by an external human action, and it gives each sub-stage
its own ~400-line slicing (the 16/17 pattern). The coordination overhead the explore warned about is
paid down by the manual itself: `manual-desarrollador-ARCA-COMPG-v4-0.pdf` (RG 4291, rev. 15/01/2025)
gives the exact request/response shape, so the mocks carry **zero shape uncertainty**.

**19b's blocking reason is a fact about the world, not a task.** No slice, no estimate, no
"pending" checkbox that looks like team debt. It is registered in `state.yaml` and it stays there
until the owner says otherwise.

## Scope

### In Scope

- **The fiscal schema** — one migration, `FiscalArcaEtapa19a`, fully specified in the DB gate section
  below: `empresas.id_condicion_fiscal`, `puntos_venta.numero_fiscal`, four additive fiscal columns
  on `comprobantes_venta`, the `certificados_fiscales` table, the `numeraciones_fiscales` table, two
  new enum types, three idempotent data statements plus their three seed nets.
- **The emitter's fiscal identity waking up** — `ResolvedorDeLetraComprobante` gets its **first
  caller** (emisor from `empresas.id_condicion_fiscal`, receptor from `clientes.id_condicion_fiscal`),
  and the totals composition maps `items_comprobante_venta`'s existing per-line alícuota snapshot
  into `ImpNeto` / `ImpIVA` / `Iva[]` / `ImpOpEx` / `ImpTotConc`.
- **The CAE state machine** — `pendiente → aprobado | aprobado_con_observaciones | rechazado`, with
  the four invariants (no holes, idempotency, terminality, inertia) stated in Approach.
- **The TRA/CMS generator** — `System.Security.Cryptography.Pkcs.SignedCms` (BCL, zero dependency),
  100% testable with a **self-signed certificate the tests generate at runtime** (decision 12).
- **The WSAA and WSFE clients** — hand-rolled SOAP 1.1 over `HttpClient` + `XLinq` (decision 7),
  covering `LoginCms`, `FECAESolicitar`, `FECompConsultar`, `FECompUltimoAutorizado` and
  `FEParamGetTiposCbte` / `FEParamGetTiposIva` / `FEParamGetCondicionIvaReceptor`, exercised against
  a fixture suite that **is** the manual's contract (decision 8).
- **The Access Ticket cache** — 12 h TTL behind `IRelojDelSistema` (decision 10).
- **Fiscal numbering** — `numeraciones_fiscales` and `AsignadorDeNumeroFiscal`, whose discipline is
  the opposite of `AsignadorDeNumeroComprobante`'s (decision 13). This is the single sharpest
  technical tension in the sub-stage.
- **Encrypted certificate storage** — AES-256-GCM with row-bound AAD, master-key versioning, rotation
  and expiry (decision 1), plus the `AdministracionFiscal` policy and its ABM endpoints.
- **The RG 4291 QR** — the JSON payload, its base64 encoding and the `afip.gob.ar/fe/qr/?p=` URL,
  verified in shape with a synthetic `codAut` (a real CAE only exists after 19b).
- **The fiscal emission use case and its endpoint**, structurally inert in production: with no active
  certificate for the empresa, it returns 409 and issues **zero** network calls.
- **doc 09 / doc 10 / doc 11** gain their Etapa 19a blocks (schema, scoping, status).

### Out of Scope

- **Any call to a real ARCA server** (`wswhomo` or `servicios1`). **Reopen condition: 19b**, which is
  born blocked on the owner's WSASS registration. No slice of 19a may contain a real endpoint URL as
  a default.
- **Production** — production certificates, the production CUIT, the real fiscal point of sale.
  **Reopen condition: after 19b passes.**
- **The fiscal invoicing UI** — screens for issuing a Factura, the certificate configuration screen,
  the fiscal print view with the QR rendered on paper. **Reopen condition: 19c.** 19a ships the API
  and the payload; nobody can press a button yet.
- **Operational CAEA and the offline queue.** The June 2026 change makes CAEA contingency-only, with
  a 5% monthly cap — so there is no routine CAEA mode to build. The contingency *path* is **19c**;
  19a ships only the `pendiente` state that a queue would later drain, and adds **no speculative enum
  value** for it (the stage-17 rule: a value ships with its writer).
- **The fiscal consolidation type for remitos** (explore decision 3). The type parameterization of
  `ServicioDeFacturacionDeRemitos` and the new `tipos_comprobante` row ship **together with their
  writer in 19c**. `TXR` stays valid history, exactly as stage 17 required
  (`archive/…stage-17…/proposal.md:480-482`).
- **Libro IVA ventas / compras.** 19c or later.
- **`NDB` / `NDC`** — never seeded, no writer, not added here.
- **Any change to the POS checkout.** `ServicioDeVentas.EmitirAsync` and its resolver stay
  byte-identical for TX/NCX/TXR/RC (decision 9), asserted as a binding criterion.
- **The owner's reserved carryovers** — the `importe` CHECK micro-gate, the `articulos_empresas`
  replace-set gap, `ways_owner`, `stage-13b` conteo por planilla. Untouched.

## Capabilities

### New Capabilities

- **`fiscal-arca`** — the protocol boundary: what a TRA is and how it becomes a CMS, the Access
  Ticket lifecycle and its 12 h expiry, the SOAP envelope contract, which operations exist, the fixture
  suite as the executable form of the manual (with its cited revision), the ARCA error taxonomy
  (500/501/502/600/601/602 and the WSFE `Errors[]`), and the rule that no real endpoint is ever a
  default.
- **`comprobante-fiscal`** — the CAE state machine and its three response states (including that
  *approved-with-observations is an approval*), the four invariants, the letter rule waking up, the
  neto/IVA/exento/no-gravado composition from the per-line snapshot, the QR payload, the inertia gate,
  and what a fiscal comprobante may never do (be edited, lose a CAE, skip a number).
- **`certificados-fiscales`** — the storage and encryption model, the one-active-per-empresa-and-
  ambiente rule, rotation and expiry, the `AdministracionFiscal` gate, and the clause that no key
  material ever appears in a DTO, a log or the repository.
- **`numeracion-fiscal`** — the fiscal series: keyed by (fiscal point of sale, ARCA comprobante type),
  strictly correlative, **hole-free**, reconcilable against `FECompUltimoAutorizado`, and why it
  cannot reuse the existing commit-early counter.

### Modified Capabilities

- **`comprobantes-venta`** — **ADDED**: the four fiscal columns, their coherence rule, and the fiscal
  emission path as a **separate** write site. **UNCHANGED (asserted)**: TX/NCX/TXR/RC emission,
  annulment, numbering, stock, cuenta corriente and payment loops — a non-fiscal sale issues **zero**
  extra statements.
- **`auxiliary-catalogs`** — **ADDED**: `codigo_afip` populated for `tipos_comprobante`,
  `condiciones_fiscales` and `alicuotas_iva`, with the rule that *Exento* and *No gravado* stay `NULL`
  **by design** because they are not alícuotas (decision 11).
- **`tenant-organization`** — **ADDED**: `empresas.id_condicion_fiscal` (nullable, with its
  no-honest-default rationale) and `puntos_venta.numero_fiscal` (nullable, unique per empresa).
- **`operacion-de-pos`** — **ADDED**: one requirement stating that `AdministracionFiscal` is a **new**
  policy for certificate and fiscal-identity administration (Admin only), and that fiscal emission
  stays under `OperacionDePos` (decision 9).

**Not modified**: `stock`, `lotes-y-vencimientos`, `turnos-de-caja`, `presupuestos`, `remitos`,
`precios`, `ofertas`, and every other capability — consumed unchanged or not consumed at all.

## Approach

**Build the entire fiscal machine behind a lock that only a real certificate can open, and make the
mocks the manual.**

1. **The missing certificate is the gate — not a feature flag.** With no active
   `certificados_fiscales` row for the empresa and ambiente, the fiscal path returns 409
   `certificado_fiscal_ausente` and performs **zero** network calls. Inertness is structural, so 19a
   can ship the whole path merged to `main` without changing what the shop does tomorrow morning.
2. **The mocks are the contract, and they cite their source.** Every fixture file names the manual
   section it was transcribed from (`manual-desarrollador-ARCA-COMPG-v4-0.pdf`, RG 4291, rev.
   15/01/2025) and the fixture set is versioned with that revision. Golden XML request fixtures are
   compared byte-for-byte against the manual's example envelope, so a namespace or element-order
   mistake fails a test instead of failing in front of ARCA.
3. **The CAE state machine has four invariants**, and they are the reason this sub-stage exists.

   | # | Invariant | Why |
   |---|---|---|
   | **I1** | **No holes.** A fiscal number, once assigned, is either authorized or re-submitted with the **same** number. It is released only when it is the top of its series, by an explicit operator action | ARCA requires `CbteDesde = último autorizado + 1`. A burned number does not create a gap — it **stops the series permanently** |
   | **I2** | **Idempotency.** No `FECAESolicitar` is issued for a number whose previous attempt did not end in a definitive answer without first asking `FECompConsultar`. If ARCA already authorized it, the existing CAE is **adopted**, never re-requested | A CAE is an irreversible legal fact. A timeout after ARCA committed is the single most expensive failure mode available here |
   | **I3** | **Terminality.** `aprobado` and `aprobado_con_observaciones` are terminal. A CAE is never overwritten and never deleted; correcting an invoice is a nota de crédito | The document left the building |
   | **I4** | **Inertia.** No certificate ⇒ 409, zero bytes on the wire | Point 1 |

   And **three response states, not two**: an approval carrying `Observaciones` is a **valid
   invoice**. Treating it as a failure would silently duplicate documents.
4. **The fiscal number is assigned with the opposite discipline to the existing counter**
   (decision 13). `AsignadorDeNumeroComprobante` commits its number in its **own small transaction,
   before** the sale's transaction, deliberately, so *"el número se consume aunque falle el resto"*
   (`AsignadorDeNumeroComprobante.cs:29-32`). For a fiscal series that exact behaviour is fatal. The
   fiscal assigner therefore takes its number **inside** the emission transaction and keeps it bound
   to the `pendiente` comprobante until the series resolves it.
5. **The POS checkout does not change.** The `EsFiscal` guard at `ServicioDeVentas.cs:1162` is
   **narrowed, not removed** (decision 9): the counter still cannot emit a Factura. The fiscal path is
   a separate write site with its own resolver — the stage-17 *"PRE latente"* lesson applied before
   the mistake, not after.
6. **Crypto is BCL only.** `SignedCms` for the CMS, `AesGcm` for the private key at rest,
   `CertificateRequest` for the test certificate. Zero new NuGet package in the whole sub-stage
   (decision 7 removes the SOAP one too).
7. **DB CHANGE GATE (CLAUDE.md).** The section below is the complete model, at table level, and it is
   the contract.

## Decisiones

The explore left six recommendations. All six are resolved below — four adopted and hardened, one
narrowed, one ratified verbatim — plus seven the proposal had to take on its own.

---

### 1 — **Certificate storage: own table, private key encrypted at the application layer with AES-256-GCM bound to its row.** *(Explore 1a, adopted and hardened.)*

**Options.** (a) `certificados_fiscales` with an app-encrypted private key; (b) an external secret
manager referenced by id.

**Decision: (a).** Adding a vault for a single secret type, on a single-node EasyPanel deployment, is
disproportionate — and it would move the hardest operational question (who holds the master key)
outside the repository without answering it.

**The encryption model, explicitly** (the explore said *"cifrada"* and stopped there):

| Element | Choice | Why |
|---|---|---|
| Algorithm | **AES-256-GCM** (`System.Security.Cryptography.AesGcm`, BCL) | Authenticated encryption: tampering fails loudly instead of decrypting to garbage. No dependency |
| Master key | From configuration/environment (`Ways:Fiscal:ClaveMaestra`, 32 bytes base64). **Never in the DB, never in the repository** | A database dump leaks **nothing** usable — the stated goal of option (a) |
| **AAD** | `id_tenant \| id_empresa \| ambiente \| huella_sha256` | Binds the ciphertext to **its own row**. Copying a blob into another empresa's row fails authentication. This is the structural anti-tamper property, and it costs nothing |
| Key version | `id_clave_maestra varchar(30)` | Rotation is a row-by-row re-encrypt with no downtime and no ambiguity about which key opened what |
| Missing key | The fiscal path is **inert** (409/503) | **Never** a silent plaintext fallback. The one failure mode that must be loud |
| In memory | Decrypted only inside the CMS signing call, into a `byte[]` cleared with `CryptographicOperations.ZeroMemory` | Never logged, never returned, never cached |
| In the API | `clave_privada_cifrada`, `nonce` and `tag_autenticacion` are **absent from every DTO** — not hidden in the UI | `dto-contract-honesty`, the stage-18 decision-10 clause applied to key material |

**Cost of reversing.** Moving to a vault later replaces one adapter behind
`IAlmacenDeClavesFiscales`; the table becomes a reference row.

---

### 2 — **`puntos_venta.numero_fiscal integer NULL`: two parallel numbering schemes, and the fiscal one is UNIQUE per empresa.** *(Explore 2a, adopted and hardened.)*

**Decision.** The internal `id_punto_venta` keeps numbering the historical series (TX/NCX/TXR/RC/PRE/
REM) exactly as today. `numero_fiscal` is a **separate, nullable** attribute holding the point of sale
ARCA assigned, in `1..99999`.

**The hardening the explore did not state.** `ux_puntos_venta_numero_fiscal (id_tenant, id_empresa,
numero_fiscal) WHERE numero_fiscal IS NOT NULL` is **load-bearing, not cosmetic**: it is what makes
the map from ARCA's series key `(PtoVta, CbteTipo)` to our row key `(id_punto_venta, codigo_afip)`
**injective**. Without it, two internal points of sale could share a fiscal point of sale and each
keep its own counter — two local series, one ARCA series, and a permanently broken correlativity.

**Why nullable.** A point of sale that never invoices fiscally must stay legal forever. Migration
states are the normal state here, not an exception: a shop can operate fiscal and non-fiscal points of
sale simultaneously during the transition.

**Cost of reversing.** Replacing the internal numbering with the fiscal one later is a data migration
over live history — which is precisely why it is not done now.

---

### 3 — **The fiscal consolidation type ships in 19c, together with its writer. No speculative row here.** *(Explore 3, adopted in principle, deferred in delivery.)*

**Context.** Stage 17 recorded a binding design constraint: Etapa 19 **may not replace** `TXR` in
place; existing `TXR` comprobantes stay valid history and the consolidation path *"gains a type
parameter"* (`archive/…stage-17…/proposal.md:480-482`).

**Decision.** That is exactly what happens — in **19c**, where the UI that would choose the parameter
also lives. 19a inserts **no** new `tipos_comprobante` row, because the project's own rule is that a
catalogue value ships with its writer (stage-17's `PRE` incident is the reason the rule exists).

**Registered contract for 19c.** `ServicioDeFacturacionDeRemitos` receives the comprobante type as a
parameter instead of hardcoding `'TXR'`; the default stays `'TXR'`, so the existing path is
byte-identical until a caller passes something else.

**Cost of reversing.** None here — nothing is built.

---

### 4 — **Contingency: 19a ships `pendiente` and the retry discipline. The offline queue and CAEA are 19c.** *(Explore 4, narrowed.)*

**Context.** Since 1 June 2026 CAEA is **contingency-only**, capped at 5% of monthly availability per
branch — so there is no routine CAEA mode to build, which shrinks the scope the explore assumed.

**Decision.** 19a ships the `pendiente` state, the exponential-backoff retry policy with a circuit
breaker in the client, and the `FECompConsultar`-first rule (decision 6). The **operational**
contingency — a durable queue, its draining worker, CAEA request and reporting — is 19c, with its own
enum values arriving alongside their writers.

**Why not now.** A queue whose only producer cannot reach a real server produces nothing to drain. It
would be untestable ceremony, and its enum values would be speculative.

**Cost of reversing.** Additive: `resultado_fiscal` gains values, `pendiente` gains a consumer.

---

### 5 — **Homologación is intrinsically per empresa.** *(Explore 5, ratified verbatim — a fact, not a decision.)*

WSASS authenticates a Clave Fiscal per CUIT, and each empresa carries its own CUIT (doc-11:402-404).
Therefore every empresa needs its own certificate, in both ambientes. The schema reflects the fact:
`certificados_fiscales` is keyed per `(empresa, ambiente)` and never shared across empresas — the one
deviation from the doc-09 catálogo shape (`id_empresa NOT NULL`, the `puntos_venta` shape), documented
in the gate.

---

### 6 — **`FECompConsultar` before any retry is part of the client's contract, not an option.** *(Explore 6, adopted and sharpened.)*

**Decision.** Whenever a previous attempt for a `(PtoVta, CbteTipo, CbteNro)` did **not** end in a
definitive answer — timeout, transport error, ambiguous response — the client asks `FECompConsultar`
first. If ARCA already has that comprobante, its CAE is **adopted** and written locally; no second
`FECAESolicitar` is ever issued for it.

**The sharpening.** The explore framed this as duplicate protection. It is also the mechanism that
makes invariant I1 survivable: a request that timed out **after** ARCA authorized leaves the local
transaction rolled back and the number unbound — and the next attempt, using the same number, finds
the CAE waiting instead of colliding with it. The two invariants close each other.

**Cost of reversing.** None. Removing it reintroduces duplicate legal documents.

---

### 7 — **SOAP: a hand-rolled SOAP 1.1 envelope over `HttpClient` + `System.Xml.Linq`, isolated in one file. `System.ServiceModel.Http` is rejected.**

**Context.** The explore left this open. Five operations total, all document/literal with flat
request shapes printed verbatim in the manual.

| Option | Verdict |
|---|---|
| **Hand-rolled envelope, `HttpClient` + `XLinq`, one file** | **Chosen** |
| `System.ServiceModel.Http` + a WSDL-generated client | **Rejected** |
| A community NuGet (`AfipWsfeClient`, `tecnocode-sa/afipwsfeclient`) | **Rejected** — near-zero adoption, no active maintenance; fails the project's audited-dependency criterion outright |

**Why the generated client loses, despite being the "official" route.**

1. **Reviewability.** A WSDL-generated proxy is thousands of lines of machine output. The project's
   PR protocol is an adversarial `judgment-day` review of every diff against a 400-line budget —
   generated code cannot be reviewed, only waved through. That is the opposite of the protocol.
2. **The mocks would stop being the contract.** With a generated proxy, tests mock the **interface**;
   the XML on the wire is never asserted. Decision 8's whole value is that the fixtures encode the
   manual's **bytes**. Hand-rolling is what makes a golden XML request comparison possible.
3. **Dependency criterion.** The ClosedXML precedent is *"a pinned dependency, isolated to a single
   file"* (`ExportadorXlsx.cs:7`). Here the same discipline yields **zero** dependency: the isolation
   target is the protocol, not a package. The genuinely sensitive part — the PKCS#7 signature — is
   already BCL (`SignedCms`), so no third party ever touches key material.

**The honest risk.** Namespaces, `SOAPAction`, and element order/omission semantics must be exactly
right. **Mitigation, binding**: golden XML fixtures of the request compared byte-for-byte against the
manual's example envelope, plus a response-parsing suite over the manual's example responses. A
mistake fails a test, not a submission.

**Cost of reversing.** Additive: the clients sit behind `IClienteWsaa` / `IClienteWsfe`. Swapping in a
generated proxy later is one adapter, and the fixtures keep working as its acceptance tests.

---

### 8 — **The mocks embody the manual, and the fixture set is versioned with the cited manual revision.**

**Decision.** A fixture directory whose files are transcriptions of the official manual, each naming
its source section, and a `REVISION.md` pinning
`manual-desarrollador-ARCA-COMPG-v4-0.pdf` (RG 4291, rev. 15/01/2025) plus the WSAA spec
(`Especificacion_Tecnica_WSAA_1.2.2.pdf`). Minimum contents:

- `LoginTicketRequest` (the TRA we produce) and `LoginTicketResponse` (Token + Sign, with the 12 h
  window), plus the WSAA fault codes.
- `FECAESolicitar` — the request golden, and **three** responses: approved, approved-with-
  observations, and rejected with an excluding validation.
- `FECompConsultar` — found (with a CAE) and not-found.
- `FECompUltimoAutorizado` — a series head, and the empty-series case (`0`).
- The error taxonomy: WSAA `500/501/502/600/601/602` and the WSFE `Errors[]` / `Observaciones[]`
  arrays, including **10016** (non-correlative number) — the code that proves invariant I1 exists for
  a reason.

**Why versioned.** When ARCA revises the manual, the diff between fixture sets is the impact
analysis. An unversioned mock is a memory of a document nobody can re-read.

**Cost of reversing.** None — deleting fixtures deletes the only evidence the client is correct.

---

### 9 — **The POS `EsFiscal` guard is NARROWED, never removed. Fiscal emission is a separate write site with its own resolver and its own endpoint.**

**Context.** `ServicioDeVentas.ResolverTipoComprobanteAsync` rejects `tipo.EsFiscal` unconditionally
(`:1162`), and its doc-comment says this is *"a propósito, no solo por no existir el flujo"*. Stage 17
added a second guard on the same line after the *"PRE latente"* ghost-sale finding, and the seed net
that accompanies it.

**Decision.** That line **does not change in 19a**. The fiscal path gets `ResolverTipoFiscalAsync` of
its own, reachable only through the fiscal use case, which additionally requires: the empresa's
`id_condicion_fiscal`, the point of sale's `numero_fiscal`, and an active certificate. Four conditions,
each producing its own named 409, none of them a boolean flag.

**Why this is the most important safety decision here.** `FA`, `FB`, `FC`, `NCA`, `NCB`, `NCC` and
`NDA` are **already seeded active with `es_fiscal = true`** (`InicializadorDeBaseDeDatos.cs:90-96`).
The only thing standing between the counter and an accidental fiscal document is that one clause.
Relaxing it to "let fiscal types through now that we support them" would reproduce the exact class of
defect stage 17 spent a slice closing — and here the escaped document would be **legally
irreversible** rather than merely wrong.

**Binding criterion for verify.** A non-fiscal sale (TX/NCX/TXR/RC) issues **zero** extra SQL
statements and takes byte-identical code paths; a POST of a fiscal code to the POS endpoint still
returns 400.

**Cost of reversing.** Merging the two paths later is a deliberate act with its own review. Nothing
here blocks it.

---

### 10 — **The Access Ticket is cached in memory behind a port, with `IRelojDelSistema` and a safety margin. DB persistence is a registered 19b gate item.**

**Decision.** `IRepositorioDeTicketDeAcceso` with an in-memory implementation keyed by
`(empresa, ambiente, servicio)`, TTL = the TA's own `expirationTime` minus a margin, evaluated through
`IRelojDelSistema` — the mechanism `RelojFijo` already tests everywhere else in the programme.

**The named risk, honestly.** WSAA enforces a minimum interval between login requests for the same
service (10 minutes in Testing, 2 in Production) and answers a request made while a valid TA exists
with an error. An in-memory cache is lost on restart, so a restart storm could hit that throttle.

**Why it is still the right call for 19a.** Persisting a TA means persisting a bearer credential,
which means encrypting it, which means a second table and a second key path — bought for a value that
**cannot exist** until 19b, since no real TA is obtainable today. The port is defined now so the
adapter is a drop-in; `tickets_acceso_fiscal` is registered as a **19b gate item** with this exact
rationale.

**Cost of reversing.** One adapter plus one table, both additive.

---

### 11 — **The `codigo_afip` mapping is data, delivered with the double net. `Exento` and `No gravado` stay `NULL` by rule.**

**Verified.** All three catalogues already have `codigo_afip smallint NULL`
(`20260801233937_CatalogosGlobales.cs:36,55,80`) and all three are seeded with it **`NULL` on purpose**
— *"completar acá con un valor inventado sería peor que dejarlo pendiente y visible"*
(`InicializadorDeBaseDeDatos.cs:34-36`). 19a is when it stops being pending. **No `ALTER` is needed.**

**Decision.** Three idempotent data statements (`WHERE codigo_afip IS NULL`) for already-migrated
databases **plus** the three seed arrays gaining the value for fresh databases — the stage-17 double
net, with each net tested independently.

**The rule that is not a mapping.** `Exento` and `No gravado` keep `codigo_afip NULL` **by design**:
they are not alícuotas. Their amounts belong in `ImpOpEx` and `ImpTotConc` respectively, and they must
**never** appear in the `Iva[]` array. A mapping here would produce arithmetically valid, legally
wrong invoices.

**The one flagged uncertainty.** `NO_RESP` ("No Responsable") has no exact counterpart in the RG 5616
`CondicionIVAReceptorId` table. It is mapped to the nearest value and **flagged in the artifact**: the
mapping is confirmed against `FEParamGetCondicionIvaReceptor` in **19b**, and until then a receptor
with that condition is rejected with a named 409 rather than invoiced on a guess.

**Cost of reversing.** One `UPDATE` per value.

---

### 12 — **The test certificate is generated by the tests at runtime. No key material, ever, in the repository.**

**Decision.** `CertificateRequest` (BCL) builds a self-signed X.509 with an RSA key inside the test
fixture. Nothing is committed: not a `.pfx`, not a `.pem`, not a `.key`, not base64 in a JSON file.
A `.gitignore` entry plus a repository-wide assertion in verify (no PEM/PFX/private-key markers under
`src/` or `tests/`) makes it structural.

**Why it matters more than it looks.** Committed test keys are how real keys get committed later: the
review muscle that says *"a key in a diff is always wrong"* only works if it has no exceptions.

**Cost of reversing.** None.

---

### 13 — **Fiscal numbering gets its OWN table, `numeraciones_fiscales`, and the opposite assignment discipline. `numeraciones_comprobante` is not touched.**

**The tension, stated exactly.** `AsignadorDeNumeroComprobante` opens and commits its **own small
transaction before** the caller's, deliberately, so that *"el número se consume aunque falle el resto"*
(`AsignadorDeNumeroComprobante.cs:29-32`, stage-5 design). Holes in the internal series are legitimate
and documented (stage-17 decision T6 accepted a burned number explicitly). For an ARCA series the same
behaviour is **fatal**: `CbteDesde` must equal *último autorizado + 1*, so one burned number does not
create a gap — it **stops the series**, and every later request is rejected with code 10016.

| Option | Verdict |
|---|---|
| Extend `numeraciones_comprobante` with a row per fiscal `(PV, tipo)` | **Rejected.** It would put two writers with **opposite** transactional discipline on one table whose entity doc-comment states `AsignadorDeNumeroComprobante` is *"el único punto de escritura legítimo"* (`NumeracionComprobante.cs:15-17`). The next maintainer would reuse the wrong assigner, and the failure would be a legal one |
| Add nullable ARCA columns to `numeraciones_comprobante` | **Rejected.** Every non-fiscal row carries two permanently-`NULL` columns, and the reconciliation state has no meaning for them |
| **Own table `numeraciones_fiscales`, keyed `(id_punto_venta, codigo_afip)`, with `AsignadorDeNumeroFiscal`** | **Chosen** |

**The discipline.** The fiscal number is taken **inside** the emission transaction (`UPDATE …
RETURNING`, the lock held for the WSFE round trip under a bounded client timeout), and it stays bound
to the `pendiente` comprobante until the series resolves it. Serializing emission per
`(fiscal PV, comprobante type)` is not a scalability loss — it is **the domain's actual constraint**;
ARCA serializes that series regardless.

The table also carries `ultimo_autorizado_arca` and `sincronizado_en` — reconciliation state that
`numeraciones_comprobante` has no concept of, and the second reason this is a different table and not
a wider one.

**Cost of reversing.** The table has exactly one writer and one reader. Deleting it deletes the fiscal
series with it.

---

## Modelo de datos propuesto

> **DB CHANGE GATE (CLAUDE.md) — this section is the contract.** It states the complete model at
> table level. Anything a later phase writes that is not here is a **scope violation that reopens the
> gate**. On implementation, **doc 09 and doc 10 are updated** (scoping table, §4-adjacent
> subsections, "Estado (Etapa 19a)" annotations), following the convention already used there.

**Gate verdict proposed: ONE migration** (`FiscalArcaEtapa19a`). PostgreSQL 17.
**Two new enum types (both `CREATE TYPE` — zero `ALTER TYPE ADD VALUE`, so ZERO irreversible
artifacts). Two new tables. THREE additive `ALTER TABLE`s over existing tables (6 new columns
total, all NULL-able). THREE idempotent data statements + THREE seed changes. 5 new FKs. 8 new CHECKs.
8 new indexes** (excluding the 2 new PKs).

### A. New enum types

```sql
CREATE TYPE resultado_fiscal AS ENUM ('pendiente','aprobado','aprobado_con_observaciones','rechazado');
CREATE TYPE ambiente_fiscal  AS ENUM ('homologacion','produccion');
```

Declaration order = lifecycle = C# member order. **Every value ships with its writer in 19a**:
`pendiente` ← the fiscal emission use case before the WSFE call; the three outcomes ← the response
mapper; `homologacion`/`produccion` ← the certificate registration endpoint, which accepts both. What
is absent for `produccion` is the **certificate**, not the writer. **No CAEA value** — it arrives in
19c with its writer (the stage-17 rule).

**Both types are `DROP TYPE`-reversible.** This sub-stage has no irreversible database artifact.

### B. `ALTER TABLE empresas` — 1 additive column

```sql
ALTER TABLE empresas ADD COLUMN id_condicion_fiscal integer NULL;
```

| Element | Name | Definition |
|---|---|---|
| FK 1 | `fk_empresas_condicion_fiscal` | `(id_condicion_fiscal) → condiciones_fiscales(id_condicion_fiscal)` RESTRICT — **simple, not composite**: `condiciones_fiscales` is global (ADR-11), the `fk_items_comprobante_venta_alicuota_iva` precedent |
| Index 1 | `ix_empresas_condicion_fiscal` | `(id_condicion_fiscal)` **simple** — a composite index led by `id_tenant` would **not** cover a simple FK (the stage-14 amendment trap) |

**Why NULL and not `NOT NULL DEFAULT`.** There is **no honest default**. `clientes` defaults to
Consumidor Final because a walk-in customer genuinely is one; an **emisor's** condition is a
real-world fact that decides the letter A/B/C. Defaulting to `RI` would silently emit Factura A to
every Responsable Inscripto customer of a Monotributista. The nullable column plus a named 409
(`empresa_sin_condicion_fiscal`) is the honest shape, and it is the gate 19c's UI will fill in.

**RLS/scoping**: unchanged — `empresas` keeps its existing category and policy.

### C. `ALTER TABLE puntos_venta` — 1 additive column

```sql
ALTER TABLE puntos_venta ADD COLUMN numero_fiscal integer NULL;
```

| Element | Name | Definition |
|---|---|---|
| CHECK 1 | `ck_puntos_venta_numero_fiscal_rango` | `numero_fiscal IS NULL OR (numero_fiscal BETWEEN 1 AND 99999)` — ARCA's `PtoVta` is 5 digits |
| Index 2 | `ux_puntos_venta_numero_fiscal` | `(id_tenant, id_empresa, numero_fiscal)` **UNIQUE, PARTIAL** `WHERE numero_fiscal IS NOT NULL` — **load-bearing** (decision 2): it is what makes the ARCA-series-to-row map injective. Partial because most points of sale have no fiscal number |

Every existing row has `numero_fiscal IS NULL`, so both constraints validate trivially on `ALTER`.

### D. `ALTER TABLE comprobantes_venta` — 4 additive columns

```sql
ALTER TABLE comprobantes_venta
    ADD COLUMN cae                    varchar(14)      NULL,
    ADD COLUMN cae_vencimiento        date             NULL,
    ADD COLUMN resultado_fiscal       resultado_fiscal NULL,
    ADD COLUMN observaciones_fiscales jsonb            NULL;
```

`resultado_fiscal IS NULL` means *"not a fiscal comprobante"* — **100% of existing traffic, and
permanently legitimate** (TX/NCX/TXR/RC never become fiscal). `observaciones_fiscales` holds ARCA's
`Observaciones[]`/`Errors[]` as `[{ "codigo": …, "mensaje": … }]`; `jsonb` follows the `auditoria`
precedent (`Auditoria.cs:40-45`). No new column for the fiscal number: `comprobantes_venta.numero`
carries it, sourced from the fiscal series instead of the internal one — disjoint by comprobante type.

| Element | Name | Definition |
|---|---|---|
| CHECK 2 | `ck_comprobantes_venta_fiscal_coherente` | `(resultado_fiscal IS NULL AND cae IS NULL AND cae_vencimiento IS NULL AND observaciones_fiscales IS NULL) OR (resultado_fiscal IS NOT NULL AND ((cae IS NULL) = (cae_vencimiento IS NULL)) AND ((resultado_fiscal IN ('aprobado','aprobado_con_observaciones')) = (cae IS NOT NULL)))` — a CAE and its expiry arrive **together**; exactly the two approval states carry a CAE; a non-fiscal comprobante carries none of the four. **Validates on every existing row** (all four NULL) |
| CHECK 3 | `ck_comprobantes_venta_cae_digitos` | `cae IS NULL OR cae ~ '^[0-9]{14}$'` |
| Index 3 | `ix_comprobantes_venta_fiscal_pendientes` | `(id_punto_venta, id_tenant)` **PARTIAL** `WHERE resultado_fiscal = 'pendiente'` — the pending-resolution read and the reconciliation path. Partial on a state that is empty in 100% of existing rows, so its size is ~0; without it, resolving pendings scans the hottest table in the system. **Its consumer ships in this sub-stage** (stage-13 anti-speculation criterion) |

**No index on `cae`** — nothing looks a comprobante up by its CAE.

### E. New table — `certificados_fiscales`

**Scoping (doc 09): `id_tenant` + `id_empresa NOT NULL`** — a documented deviation from the catálogo
shape (`id_empresa NULL` = shared). A certificate belongs to **one CUIT** and can never be shared
(decision 5). Precedent for the shape: `puntos_venta` (`PuntoVenta.cs:15`). **`EntidadBase`: YES** —
rotation soft-deletes the superseded row and audit columns are exactly what a key-material table
should carry.

```sql
certificados_fiscales (                     -- [por empresa, nunca compartido]
    id_certificado         integer         GENERATED BY DEFAULT AS IDENTITY,
    id_tenant              integer         NOT NULL,
    id_empresa             integer         NOT NULL,
    ambiente               ambiente_fiscal NOT NULL,
    alias                  varchar(60)     NOT NULL,   -- etiqueta humana ("Homo 2026")
    cuit_titular           varchar(11)     NOT NULL,   -- CUIT al que ARCA emitió el certificado
    certificado_pem        text            NOT NULL,   -- parte PÚBLICA: no es secreto
    clave_privada_cifrada  bytea           NOT NULL,   -- AES-256-GCM (decisión 1)
    nonce                  bytea           NOT NULL,   -- 12 bytes
    tag_autenticacion      bytea           NOT NULL,   -- 16 bytes
    id_clave_maestra       varchar(30)     NOT NULL,   -- versión de la clave maestra (rotación)
    huella_sha256          varchar(64)     NOT NULL,   -- fingerprint: trazar sin descifrar
    vigencia_desde         timestamptz     NOT NULL,   -- del propio X.509
    vigencia_hasta         timestamptz     NOT NULL,
    activo                 boolean         NOT NULL,
    created_at, updated_at, deleted_at,
    CONSTRAINT pk_certificados_fiscales PRIMARY KEY (id_certificado)
);
```

**18 columns** (15 + 3 audit). `vigencia_*` are `timestamptz` set from the certificate itself, never
`DEFAULT now()` — `IRelojDelSistema` is the single time source in this codebase.

| Element | Name | Definition |
|---|---|---|
| FK 2 | `fk_certificados_fiscales_tenant` | `(id_tenant) → tenants` RESTRICT |
| FK 3 | `fk_certificados_fiscales_empresa` | `(id_empresa, id_tenant) → empresas` RESTRICT, composite — against the same AK `puntos_venta` already uses (`PuntoVenta.cs:13-15`) |
| CHECK 4 | `ck_certificados_fiscales_vigencia` | `vigencia_hasta > vigencia_desde` |
| CHECK 5 | `ck_certificados_fiscales_cuit` | `cuit_titular ~ '^[0-9]{11}$'` |
| CHECK 6 | `ck_certificados_fiscales_material` | `octet_length(nonce) = 12 AND octet_length(tag_autenticacion) = 16 AND octet_length(clave_privada_cifrada) > 0` — GCM's parameter sizes enforced by the database, so a truncated blob fails on write, not on the first invoice |
| Index 4 | `ix_certificados_fiscales_tenant` | `(id_tenant)` — RLS predicate + FK 2 |
| Index 5 | `ix_certificados_fiscales_empresa` | `(id_empresa, id_tenant)` — FK 3 |
| Index 6 | `ux_certificados_fiscales_activo` | `(id_tenant, id_empresa, ambiente)` **UNIQUE, PARTIAL** `WHERE activo AND deleted_at IS NULL` — **at most one active certificate per empresa and ambiente**. Rotation = deactivate + activate inside one transaction; the database, not a service, is what guarantees there is never an ambiguous signer |
| RLS | `certificados_fiscales_tenant` | `HabilitarRlsDeTenant("certificados_fiscales")` → `ENABLE` + `FORCE` + `USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())`. **Standard, no deviation** |

**FK-coverage audit:** 2 FKs, 2 support indexes, plus 1 unique declared for its own reason.
**Total: 3 indexes + 1 PK.** No AK — nothing references this table.

### F. New table — `numeraciones_fiscales`

**Scoping: operativa** (`id_tenant` + `id_punto_venta`) — the same category as
`numeraciones_comprobante` (doc-09:86). **`EntidadBase`: NO**, PK-only without audit or soft delete,
the exact criterion `NumeracionComprobante` documents (`:10-13`) — so it needs a **hand-written tenant
filter** (`WaysDbContext.AplicarFiltroDeTenantEnNumeracionFiscal`, mirroring the existing one).

```sql
numeraciones_fiscales (                     -- [operativa]
    id_punto_venta         integer     NOT NULL,   -- el interno; su numero_fiscal es UNIQUE (§C)
    codigo_afip            smallint    NOT NULL,   -- CbteTipo de ARCA (1, 3, 6, 8, 11, 13, …)
    id_tenant              integer     NOT NULL,
    proximo_numero         bigint      NOT NULL DEFAULT 1,
    ultimo_autorizado_arca bigint      NULL,       -- de FECompUltimoAutorizado
    sincronizado_en        timestamptz NULL,       -- par del anterior (IRelojDelSistema)
    CONSTRAINT pk_numeraciones_fiscales PRIMARY KEY (id_punto_venta, codigo_afip)
);
```

**6 columns.** PK shape mirrors `numeraciones_comprobante`: `id_punto_venta` is already a global
identity (doc 09), so `id_tenant` rides as a non-key column for RLS and the FK, exactly as in the
sibling table. `codigo_afip` rather than a `tipo_comprobante` string because **ARCA's series key is
the numeric type** — using our code would need a translation on the hottest path of the invariant.

| Element | Name | Definition |
|---|---|---|
| FK 4 | `fk_numeraciones_fiscales_tenant` | `(id_tenant) → tenants` RESTRICT |
| FK 5 | `fk_numeraciones_fiscales_punto_venta` | `(id_punto_venta, id_tenant) → puntos_venta` RESTRICT — **design verifies this mirrors exactly what `NumeracionComprobanteConfiguration` declares**; if the sibling omits it, this one is dropped for symmetry rather than diverging silently |
| CHECK 7 | `ck_numeraciones_fiscales_rango` | `proximo_numero BETWEEN 1 AND 99999999 AND (ultimo_autorizado_arca IS NULL OR ultimo_autorizado_arca BETWEEN 0 AND 99999999)` — ARCA's `CbteDesde` range; `0` is a legal *"series never used"* answer |
| CHECK 8 | `ck_numeraciones_fiscales_sincronizacion` | `(ultimo_autorizado_arca IS NULL) = (sincronizado_en IS NULL)` — a reconciled value and its timestamp arrive together |
| Index 7 | `ix_numeraciones_fiscales_tenant` | `(id_tenant)` — RLS predicate + FK 4 |
| Index 8 | `ix_numeraciones_fiscales_punto_venta` | `(id_punto_venta, id_tenant)` — FK 5. **Not** covered by the PK: the PK's second column is `codigo_afip`, so it is not a prefix of this FK |
| RLS | `numeraciones_fiscales_tenant` | `HabilitarRlsDeTenant("numeraciones_fiscales")`. **Standard, no deviation** |

**FK-coverage audit:** 2 FKs, 2 support indexes, zero surprises. **Total: 2 indexes + 1 PK.**

### G. Data statements (3, all idempotent) + seed changes (3)

**No `ALTER` is required for any of these** — the three catalogues already carry
`codigo_afip smallint NULL` (`20260801233937_CatalogosGlobales.cs:36,55,80`), left NULL on purpose
(`InicializadorDeBaseDeDatos.cs:34-36`).

**DS1 — `tipos_comprobante`** (7 rows, `WHERE codigo = … AND codigo_afip IS NULL`):

| Código | `codigo_afip` | | Código | `codigo_afip` |
|---|---|---|---|---|
| `FA` | 1 | | `FC` | 11 |
| `NDA` | 2 | | `NCC` | 13 |
| `NCA` | 3 | | `FB` | 6 |
| `NCB` | 8 | | | |

**No row is inserted, activated or deactivated.** `FA…NDA` already exist with `es_fiscal = true` and
`activo = true`; decision 9 is what keeps them unreachable from the counter.

**DS2 — `condiciones_fiscales`** (RG 5616 `CondicionIVAReceptorId`): `RI` → 1, `EXENTO` → 4, `CF` → 5,
`MONOTRIBUTO` → 6. **`NO_RESP` is the flagged uncertainty** (decision 11): it has no exact counterpart
in that table, so it is mapped to the nearest value **and marked for confirmation against
`FEParamGetCondicionIvaReceptor` in 19b**; until confirmed, a receptor with that condition is rejected
with a named 409 instead of invoiced on a guess.

**DS3 — `alicuotas_iva`** (`FEParamGetTiposIva`): `0%` → 3, `10.5%` → 4, `21%` → 5, `27%` → 6.
**`Exento` and `No gravado` are deliberately NOT touched and stay `NULL`** — they are not alícuotas;
their amounts go to `ImpOpEx` and `ImpTotConc` and must never enter the `Iva[]` array (decision 11).
DS3's `WHERE` clause names the four rows explicitly, and a test asserts the other two are still NULL.

**Seed changes (the stage-17 double net).** `TiposComprobanteBase`, `CondicionesFiscalesBase` and
`AlicuotasIvaBase` each gain the `CodigoAfip` field, because the seeder only runs against an **empty**
table and **after** migrations (`InicializadorDeBaseDeDatos.cs:432`) — a data statement alone leaves a
freshly-seeded database with NULL codes. **Each net is tested independently**: removing either one,
alone, must fail its own test.

### H. Error handling surface

`ManejadorDeErrores` gains: **2 branches for `23505`** (`ux_puntos_venta_numero_fiscal`,
`ux_certificados_fiscales_activo`) and **8 branches for `23514`** (CHECKs 1-8), each with its own
named domain error. `Politicas.cs` gains exactly **one** policy, `AdministracionFiscal` (Admin only) —
a genuinely new kind of risk (private key material and the emitter's legal identity), the stage-15
criterion for adding a name. Fiscal **emission** stays under `OperacionDePos`: the letter, the totals
and the CAE are all server-decided, so the risk is not who presses the button (decision 9).

### I. Binding criteria for verify

1. Exactly **one** new migration, named `FiscalArcaEtapa19a`, under
   `src/Ways.Infrastructure/Persistencia/Migraciones/`; `dotnet ef migrations
   has-pending-model-changes` clean.
2. **Zero `ALTER TYPE … ADD VALUE`** anywhere in the migration — this sub-stage has no irreversible
   artifact.
3. New index count = **8**, verified **by definition** against `pg_indexes` (the stage-16 name-only
   lesson), including both partial uniques and both partial/simple FK-support indexes.
4. New CHECK count = **8**, each with a mutation-proof test that violates it.
5. RLS present and `FORCE`d on **both** new tables, with the cross-tenant read/write test pair.
6. A non-fiscal sale is **byte-identical** to `main`: same code paths, **zero** extra SQL statements.
7. No PEM/PFX/private-key material anywhere under `src/` or `tests/` (decision 12), asserted by a
   repository scan.
8. No real ARCA hostname appears as a default in any configuration file shipped to `main`.

## API surface

| Route | Method | Policy | Status |
|---|---|---|---|
| `/api/fiscal/certificados` | GET / POST / DELETE | **`AdministracionFiscal`** (new) | **New** — register, list (never returning key material), deactivate |
| `/api/fiscal/empresas/{id}/condicion-fiscal` | PUT | **`AdministracionFiscal`** | **New** |
| `/api/fiscal/puntos-venta/{id}/numero-fiscal` | PUT | **`AdministracionFiscal`** | **New** |
| `/api/fiscal/comprobantes` | POST | `OperacionDePos` | **New** — fiscal emission. Returns 409 `certificado_fiscal_ausente` while inert |
| `/api/fiscal/comprobantes/{id}/reintentar` | POST | `OperacionDePos` | **New** — resolves a `pendiente` through invariant I2 |
| `/api/ventas` | POST | `OperacionDePos` | **Unchanged** — still 400 on any fiscal type (decision 9) |

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Infrastructure/Persistencia/Migraciones/…FiscalArcaEtapa19a.cs` | New | The single migration of the sub-stage |
| `src/Ways.Domain/Organizacion/{Empresa,PuntoVenta}.cs` | Modified | One nullable property each |
| `src/Ways.Domain/Ventas/ComprobanteVenta.cs` | Modified | Four nullable fiscal properties |
| `src/Ways.Domain/Fiscal/{CertificadoFiscal,NumeracionFiscal,ResultadoFiscal,AmbienteFiscal}.cs` | New | The two entities and the two enums |
| `src/Ways.Domain/Fiscal/MaquinaDeEstadosCae.cs` | New | The transitions and the four invariants, pure and DB-free (the `PoliticaDeRoles` pattern) |
| `src/Ways.Domain/Ventas/ResolvedorDeLetraComprobante.cs` | **Untouched** | Gains its first **caller**; the rule itself does not change |
| `src/Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs` | New | The emission use case: the four gates, the number, the call, the state write |
| `src/Ways.Application/Fiscal/AsignadorDeNumeroFiscal.cs` | New | Decision 13's discipline — inside the transaction, hole-free |
| `src/Ways.Application/Fiscal/ComposicionDeTotalesFiscales.cs` | New | Neto / IVA per alícuota / ImpOpEx / ImpTotConc from the per-line snapshot |
| `src/Ways.Application/Fiscal/{IClienteWsaa,IClienteWsfe,IAlmacenDeClavesFiscales,IRepositorioDeTicketDeAcceso}.cs` | New | The ports |
| `src/Ways.Application/Fiscal/PayloadQrFiscal.cs` | New | RG 4291 JSON + base64 + URL |
| `src/Ways.Infrastructure/Fiscal/SobreSoap.cs` | New | **The only file that knows SOAP exists** (decision 7) |
| `src/Ways.Infrastructure/Fiscal/{ClienteWsaa,ClienteWsfe}.cs` | New | The two adapters + retry/circuit breaker |
| `src/Ways.Infrastructure/Fiscal/{GeneradorDeTra,FirmanteCms}.cs` | New | `SignedCms`, BCL only |
| `src/Ways.Infrastructure/Fiscal/CifradoDeClavesFiscales.cs` | New | AES-256-GCM + row-bound AAD + key versioning |
| `src/Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs` | Modified | The three seed nets (§G) |
| `src/Ways.Api/Endpoints/FiscalEndpoints.cs` | New | The five new routes |
| `src/Ways.Api/…/Politicas.cs` | Modified | **One** new policy, `AdministracionFiscal` |
| `src/Ways.Api/…/ManejadorDeErrores.cs` | Modified | 2 × `23505`, 8 × `23514` |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | **Untouched** | Binding criterion 6 |
| `tests/**/Fiscal/Fixtures/**` | New | The manual's contract, with `REVISION.md` (decision 8) |
| `docs/{09,10,11}` | Modified | Scoping table, §4-adjacent subsections, Etapa 19a status block |
| `src/Ways.Web/**` | **Untouched** | No UI in 19a — that is 19c |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **A burned fiscal number stops the series permanently** (ARCA 10016) | **High if unmanaged** | Decision 13's own table and inside-the-transaction discipline; invariant I1 with a concurrency test; fixture 10016 proves the failure mode is understood, not imagined |
| **A duplicated legal document after a timeout** | **High if unmanaged** | Invariant I2: `FECompConsultar` before any non-definitive retry, adopting the existing CAE. Tested with a fixture that answers *found* on the second call |
| **Treating approved-with-observations as a failure**, then re-invoicing | Med-High | Three response states are first-class in the enum, in the state machine and in the fixtures; a dedicated test asserts the observed approval writes a CAE and is terminal |
| **An accidental fiscal emission from the POS** — the types are already seeded active | **High if unmanaged** | Decision 9: the guard is narrowed, never removed; four independent named gates; binding criterion 6 asserts byte-identical non-fiscal behaviour |
| **Hand-rolled SOAP gets a namespace or element order wrong** | Med | Decision 7's golden XML request fixtures compared byte-for-byte with the manual's example; the failure surfaces in CI, not at ARCA |
| **A wrong `codigo_afip` mapping produces legally wrong invoices** | Med | Decision 11: `Exento`/`No gravado` stay NULL by rule; `NO_RESP` is a **flagged** uncertainty rejected with a 409 rather than guessed; all mappings re-confirmed against `FEParamGet*` in 19b |
| **Key material leaking** (repository, DTO, log, DB dump) | Med-High | Decision 1 (AES-GCM + row-bound AAD + no plaintext fallback), decision 12 (runtime-generated test cert + repository scan), the DTO absence clause, and criterion 7 |
| **The mocks drift from reality**, so 19b discovers the client is wrong | Med | The fixtures cite manual sections and are pinned to a manual revision; 19b's first task is a fixture-vs-reality diff, and its whole purpose is to catch exactly this |
| **The TA cache is lost on restart and hits the WSAA throttle** | Low today, Med in production | Decision 10 names it, defines the port, and registers `tickets_acceso_fiscal` as a 19b gate item |
| **Size** — *"la mayor del programa"* (doc-11:419) | **High** | OD1's three sub-stages; 5 slices here with three pre-authorized split points |
| **19b never unblocks** because the owner does not register with WSASS | Out of our control | 19a is **complete and useful on its own**: schema, domain and a tested client. The blocked item is documented, never estimated, never asked for |

## Rollback Plan

Every slice is independently revertible, and the sub-stage's only database artifact is a
**fully reversible** migration.

| Slice | Rollback |
|---|---|
| **1 — schema** | `dotnet ef migrations remove` / `Down()`: both `CREATE TYPE`s drop cleanly (**no `ALTER TYPE ADD VALUE` to strand**), the two new tables drop, the six columns drop, the three data statements are reverted by `UPDATE … SET codigo_afip = NULL` on the exact rows they touched, and the seed change is a code revert. **No existing row is modified** except the three catalogues' `codigo_afip` — so no operational data can be damaged |
| **2 — WSAA** | Revert. The files have no consumer until slice 3 |
| **3 — WSFE + CAE machine** | Revert. Still no caller — the emission use case arrives in slice 5 |
| **4 — numbering + certificates** | Revert; `numeraciones_fiscales` is empty in production (nothing has been emitted), and `certificados_fiscales` has no row until an owner uploads one. The policy registration is one line |
| **5 — emission + QR** | Revert removes two routes. No other path calls them |
| **Whole sub-stage** | `git revert` of the five merges plus the `Down()` leaves `main` behaviourally identical: no fiscal document was ever issued (structural inertia, invariant I4), so **there is no fiscal history to repair** |

**The one thing that would not be rollbackable is precisely what 19a cannot do**: issue a real CAE.
That is why the certificate — not a feature flag — is the gate.

## Dependencies

- **Etapa 1** (archived) — `condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante` with
  `es_fiscal` / `letra` / `codigo_afip`, their RLS, and `ResolvedorDeLetraComprobante`.
- **Etapa 5** (archived) — `comprobantes_venta`, `items_comprobante_venta` with the per-line alícuota
  snapshot (the direct source of ARCA's `Iva[]`), `numeraciones_comprobante` and
  `AsignadorDeNumeroComprobante` (**consumed as a counter-example**, decision 13).
- **Etapa 17** (archived) — the binding constraint that `TXR` may not be replaced, the "no speculative
  catalogue value" rule, and the double-net data-statement + seed pattern.
- **`IRelojDelSistema` / `RelojFijo`** — the TA expiry and the reconciliation timestamp.
- **`SignedCms`, `AesGcm`, `CertificateRequest`, `HttpClient`, `XLinq`** — all BCL, `net10.0`.
- **NO new NuGet package, no new web dependency, no scheduler, no queue, no external service.**
- **Owner action, documented and NOT requested**: WSASS registration with Clave Fiscal Nivel 2. It
  blocks **19b**, not 19a.
- Skills: `mutation-proof-tests`, `dto-contract-honesty`, `work-unit-commits`, `judgment-day` before
  every PR.

## Success Criteria

- [ ] **Exactly one migration** named `FiscalArcaEtapa19a`; `has-pending-model-changes` clean; **zero
      `ALTER TYPE … ADD VALUE`**.
- [ ] New index count = **8** and new CHECK count = **8**, both verified by definition against the
      catalogue, each CHECK with a mutation-proof test.
- [ ] RLS enabled and `FORCE`d on `certificados_fiscales` and `numeraciones_fiscales`, with the
      cross-tenant read **and** write test pair.
- [ ] **A non-fiscal sale is byte-identical to `main`** — same paths, zero extra SQL statements — and
      `POST /api/ventas` with a fiscal code still returns 400 (decision 9).
- [ ] `ResolvedorDeLetraComprobante` has a **caller**, and an RI→RI emission resolves `A` while
      RI→CF resolves `B`, end to end against mocks.
- [ ] The generated `FECAESolicitar` envelope matches the manual's example **byte-for-byte** for a
      reference invoice (golden XML).
- [ ] The three responses are handled distinctly: approved writes a CAE; **approved-with-observations
      writes a CAE and its observations and is terminal**; rejected writes no CAE and keeps the number
      reserved.
- [ ] **Invariant I1**: a failed emission does **not** advance the fiscal series — proven by a
      concurrency test plus a mutation test on the assigner.
- [ ] **Invariant I2**: a timeout followed by a retry issues `FECompConsultar` **first** and adopts the
      existing CAE, issuing exactly **one** `FECAESolicitar` across both attempts.
- [ ] **Invariant I4**: with no active certificate, the emission endpoint returns 409 and the HTTP
      mock records **zero** requests.
- [ ] Totals composition: a mixed invoice (21%, 10.5%, exento, no gravado) puts only the two real
      alícuotas in `Iva[]`, exento in `ImpOpEx`, no-gravado in `ImpTotConc`, and
      `ImpTotal = ImpNeto + ImpIVA + ImpOpEx + ImpTotConc + ImpTrib` exactly.
- [ ] A certificate's private key round-trips through AES-GCM; **decryption fails** when the AAD row
      identity is altered; **no DTO, log line or API response** contains key material.
- [ ] The QR payload contains the 13 RG 4291 fields, base64-encodes to the documented URL shape, and
      is asserted against a hand-computed vector.
- [ ] **Zero PEM/PFX/private-key material** in the repository; no real ARCA hostname as a default.
- [ ] Domain / Application / Integration suites green.

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one `judgment-day` round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage19a-slice1-schema-fiscal` | The migration (§A-G), entities, EF configurations, the hand-written tenant filter, RLS, the 10 error branches, the three data statements and their three seed nets, RLS/CHECK/index tests, doc 09/10 updates | ~450 |
| 2 | `feat/stage19a-slice2-wsaa` | `SobreSoap`, `GeneradorDeTra`, `FirmanteCms` (`SignedCms`), `ClienteWsaa`, the TA cache with `IRelojDelSistema`, the runtime-generated self-signed certificate, the WSAA fixtures + `REVISION.md` | ~430 |
| 3 | `feat/stage19a-slice3-wsfe-y-cae` | `ClienteWsfe` (`FECAESolicitar` / `FECompConsultar` / `FECompUltimoAutorizado` / `FEParamGet*`), the request mapper, `ComposicionDeTotalesFiscales`, `MaquinaDeEstadosCae`, the three response fixtures + the error taxonomy, retry + circuit breaker | ~480 |
| 4 | `feat/stage19a-slice4-numeracion-y-certificados` | `AsignadorDeNumeroFiscal` with invariant I1 and its concurrency test, reconciliation against `FECompUltimoAutorizado`, `CifradoDeClavesFiscales` (AES-GCM + AAD + rotation), the `AdministracionFiscal` policy and its three ABM routes | ~460 |
| 5 | `feat/stage19a-slice5-emision-y-qr` | `ServicioDeFacturacionFiscal` end-to-end against mocks (the four gates, the letter resolver's first caller, invariants I2/I3/I4), `PayloadQrFiscal`, the two emission routes, doc 11 status block | ~420 |

Merge order `1 → 2 → 3 → 4 → 5`. Slice 2 depends only on 1 for its test host; slices 2 and 3 could be
built in parallel if the mock harness lands in 2.

**Pre-approved degradation**, in priority order:

1. **If slice 3 overflows** — split `3a` (client + envelope + `FEParamGet*` + fixtures) and `3b` (the
   totals composition + the state machine).
2. **If slice 4 overflows** — split `4a` (numbering + I1) and `4b` (certificates + policy + ABM).
3. **If slice 1 overflows** — split `1a` (the two new tables + RLS) and `1b` (the three `ALTER`s +
   the data statements + the seed nets).
4. **Never degraded**: invariants I1 and I2 with their tests, the narrowed POS guard (decision 9), the
   AAD binding, and the no-key-material-in-the-repository scan. A duplicated legal document, a dead
   fiscal series, or a leaked private key is worse than no sub-stage at all.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~2 240 lines** across 5 slices. Calibrated against stages 13-17, which came in
  1.5-3× their naive estimate because test depth inflates a slice — and this sub-stage has schema,
  concurrency, cryptography **and** a wire protocol, so the inflators are all present. Realistic
  outturn: **6-8 PRs**.
- `Decision needed before apply: Yes` — the DB gate needs the owner's ratification before slice 1.
- `Chained PRs recommended: Yes` — `chain_strategy: stacked-to-main`
- `400-line budget risk: High` — all five slices sit at or above the cap on the estimate alone; three
  split points are pre-authorized.
- `size:exception` anticipated: **No** — the splits absorb it.

## Tensiones con el explore

| # | Explore position | Verdict |
|---|---|---|
| 1 | *"Numeración: `numeraciones_comprobante` … es exactamente el mecanismo que entra en tensión con la numeración fiscal"* (`:38-43`) | **Ratified and resolved, with the sharp edge the explore did not name.** The tension is not the key shape — it is the **commit-early discipline**: the existing assigner commits its number in its own transaction *before* the caller's, on purpose (`AsignadorDeNumeroComprobante.cs:29-32`). For ARCA that is fatal, because a burned number does not gap a series, it **stops** it (error 10016). Hence decision 13: own table, own assigner, opposite discipline |
| 2 | Decision 1 — *"tabla `certificados_fiscales` con clave privada cifrada"* | **Adopted and made concrete.** The explore said *"cifrada"*; decision 1 specifies AES-256-GCM, a row-bound AAD (so a blob cannot be moved between empresas), master-key versioning, the no-plaintext-fallback rule, and the DTO-absence clause |
| 3 | Decision 2 — *"`puntos_venta.numero_fiscal integer NULL` con DOS numeraciones paralelas"* | **Adopted and hardened.** The explore did not state that the fiscal number must be **UNIQUE per empresa**; without that partial unique the ARCA-series-to-row map is not injective and correlativity breaks silently |
| 4 | Decision 3 — *"tipo fiscal nuevo con el flag de consolidación; `ServicioDeFacturacionDeRemitos` recibe el tipo como parámetro"* | **Adopted in principle, deferred in delivery.** The design is right; shipping the `tipos_comprobante` row **without its writer** would violate the project's own rule — the rule stage 17 wrote after the `PRE` incident. It ships in 19c with the caller |
| 5 | Decision 4 — *"cola de pendientes con reintento exponencial + circuit breaker; CAEA como último recurso"* | **Narrowed.** The retry policy and the circuit breaker ship in 19a; the **durable queue and CAEA** are 19c, because a queue whose only producer cannot reach a server has nothing to drain, and its enum values would be speculative |
| 6 | Decision 5 — *"la homologación es intrínsecamente por empresa; no es decisión abierta"* | **Ratified verbatim**, and reflected in the schema: `certificados_fiscales` keyed per `(empresa, ambiente)` with `id_empresa NOT NULL` |
| 7 | Decision 6 — *"`FECompConsultar` antes de cada `FECAESolicitar`"* | **Adopted, with two refinements.** (a) Not *"before each"* but *"before each retry whose predecessor was not definitive"* — an unconditional pre-check doubles the call volume on the happy path for no gain. (b) It is not only duplicate protection: it is what makes invariant I1 survivable after a post-authorization timeout. The two invariants close each other |
| 8 | Risk — *"`System.ServiceModel.Http` vs armar el sobre a mano. Decisión del proposal"* | **Resolved against the "official" option** (decision 7). A WSDL-generated proxy is thousands of unreviewable lines against a 400-line adversarial-review budget, and it would move the mocks from the **wire** to an interface — destroying the very property that makes 19a valuable without credentials |
| 9 | *"Cliente WSAA/WSFE: completo contra mocks locales con las respuestas reales del manual"* (`:145`) | **Ratified and made a versioned artifact** (decision 8): each fixture cites its manual section, and the set is pinned to `manual-desarrollador-ARCA-COMPG-v4-0.pdf` rev. 15/01/2025 so a future ARCA revision produces a readable diff |
| 10 | *"Dominio del comprobante fiscal: completo — **activar `es_fiscal` en el resolver**"* (`:142`) | **Overruled, and this is the most important correction.** Activating `es_fiscal` in `ServicioDeVentas.ResolverTipoComprobanteAsync` would open the POS checkout to `FA`/`FB`/`FC`, which are **already seeded active** — reproducing stage 17's *"PRE latente"* ghost-sale class, except the escaped document would be legally irreversible. Decision 9 keeps that guard intact and gives the fiscal path its own resolver and its own endpoint |
| 11 | *"Impresión / UI: completa … pantallas de configuración de certificado"* in the buildable column (`:150`) | **Moved to 19c by OD1.** It is genuinely buildable without credentials — but it belongs with the printing work, and pulling it into 19a would add a whole front-end surface to the largest stage of the programme. 19a ships the API those screens will call |
| 12 | *"Contingencia CAEA: completa como máquina de estados y cola offline"* in the buildable column (`:149`) | **Moved to 19c** (tension 5's rationale) |

**New material the explore did not raise at all**: the commit-early discipline as the real numbering
tension (13); the partial unique that makes the fiscal series injective (2); the AAD row binding (1);
that `FA…NDA` are **already seeded active** and what that implies for the guard (9); that
`codigo_afip` needs **no `ALTER`** because all three catalogues already carry the column (11); that
`Exento`/`No gravado` are not alícuotas and must never enter `Iva[]` (11); the `NO_RESP` mapping gap
(11); the `empresas.id_condicion_fiscal` no-honest-default argument (§B); the reviewability argument
against a generated SOAP proxy (7); the TA-cache restart/throttle risk with its named 19b gate item
(10); and the runtime-generated test certificate rule (12).

## Proposal question round

Execution mode is `automatic-autonomous`, so these were resolved rather than asked. Each records the
assumption so a correction is cheap. **None blocks spec/design.** The first is the one that most
changes the shape of the work.

1. **Is the shop's empresa a Responsable Inscripto or a Monotributista?** Assumed **unknown, and
   deliberately not defaulted** (§B): the column is nullable and the fiscal path refuses to emit until
   it is set. If the owner answers now, it becomes one seed row for their tenant — not a schema change.
2. **Does the business need Factura A (RI), or only B/C?** Assumed **both**, because
   `ResolvedorDeLetraComprobante` already implements the full rule and the catalogue already carries
   all three letters. If only C is ever needed, nothing built here is wasted — the rule simply always
   returns `C`.
3. **Who may upload a certificate — the owner only, or any Admin?** Assumed **Admin**
   (`AdministracionFiscal`), the highest existing role. If the owner wants a narrower gate, that is a
   role question the programme has deferred before (`ways_owner`, a registered carryover).
4. **Should a fiscal invoice be issuable from the counter, or only from an office screen?** Assumed
   **the counter**, under `OperacionDePos` (decision 9) — but nobody can do it until 19c ships a
   screen, so the answer is cheap to change before then.
5. **Which fiscal point-of-sale numbers will ARCA assign?** Assumed **unknown** — the column is
   nullable, unique per empresa, and set by an ABM route. This is knowable only after the WSASS
   registration, and 19a is built so the answer is a configuration value, never a code change.
6. **Is `NO_RESP` ("No Responsable") a condition any real customer of this shop has?** Assumed
   **rarely or never** (decision 11): it is the one mapping left unconfirmed, and until 19b confirms it
   against `FEParamGetCondicionIvaReceptor`, such a receptor is rejected with a named error rather than
   invoiced on a guess.
