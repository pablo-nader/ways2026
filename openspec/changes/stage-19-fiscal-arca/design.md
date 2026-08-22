# Design: Stage 19a — Fiscal ARCA, the core buildable without credentials

## Technical Approach

**The certificate is the lock, the manual is the test suite, and the fiscal series is the only lock
in the program held across a network round trip — so it enters the total lock order at position 0
and takes nothing else with it.**

The proposal's `Modelo de datos propuesto` is the ratified contract (`state.yaml:9-139`): **one**
migration, two `CREATE TYPE`, two tables, three additive `ALTER`s, 5 FKs, 8 CHECKs, 8 indexes, 3 data
statements, 3 seed nets, 10 error branches, 1 policy. This design fixes the *how* and arbitrates the
four things the proposal deliberately left to it.

Seven structural facts decide the shape.

1. **Zero irreversible artifacts, and that is a first.** Etapas 12 and 17 paid `ALTER TYPE … ADD
   VALUE` (`RemitosEtapa17.cs:17-21`: *"IRREVERSIBLE — Postgres no soporta DROP VALUE"*). 19a adds
   **no value to an existing enum**: `resultado_fiscal` and `ambiente_fiscal` are born whole, so both
   are `DROP TYPE`-reversible and `Down()` is a true inverse, not a best effort. Every design choice
   below protects that property — it is what makes slice 1 revertible on a live database.

2. **The sibling table settles its own open question.** The proposal asked the design to verify
   whether `NumeracionComprobanteConfiguration` declares the composite FK to `puntos_venta`.
   **It does** (`:51-59`, `fk_numeraciones_comprobante_punto_venta`, against
   `ak_puntos_venta_id_punto_venta_id_tenant`, `Restrict`), and it also names its two indexes
   explicitly in snake_case to dodge the PascalCase convention trap (`:44-49`).
   `numeraciones_fiscales` mirrors all of it. **FK 5 ships.**

3. **The existing assigner is the counter-example, verbatim.** `AsignadorDeNumeroComprobante`
   `BeginTransactionAsync` / `CommitAsync` inside itself (`:48-55`), documented as *"su PROPIA
   transacción chica, comprometida ANTES de la transacción que escribe el resto"* (`:29-32`).
   `AsignadorDeNumeroFiscal` is a **shape-for-shape copy with the transaction removed** — same raw
   ADO on the caller's connection, same `INSERT … ON CONFLICT DO NOTHING` lazy row, same
   `UPDATE … RETURNING`, same `RechazarEscrituras…` guard in `WaysDbContext`. The only difference is
   the one that matters, and the diff makes it visible.

4. **`comprobantes_venta` already has the fiscal money columns.** `NetoGravado` and `IvaTotal` exist
   and are nullable *"mientras `tipos_comprobante.discrimina_iva = false`"* (`ComprobanteVenta.cs:64-67`).
   The fiscal composition **fills columns that were built for it**, adding no money column.

5. **The per-line snapshot is already ARCA's `Iva[]`.** `ItemComprobanteVenta` freezes
   `IdAlicuotaIva` **and** `PorcentajeIva` per line (`:46-47`), immutable by design (`:14-20`). The
   composition is a `GROUP BY IdAlicuotaIva` over frozen values — no re-derivation, no join to
   `alicuotas_iva` at emission time except to read `codigo_afip`.

6. **The POS guard's 19a action is literally zero edits.** `ServicioDeVentas.cs:1162` already reads
   `… || tipo.EsFiscal || !tipo.AfectaStock`. Decision 9's *"narrowed, never removed"* is the
   **programme-level** framing across 19a-19c; in 19a the concrete instruction is
   `git diff --exit-code src/Ways.Application/Ventas/ServicioDeVentas.cs`, plus a live regression test
   that `POST /api/ventas` with `FA` still returns `400 tipo_comprobante_invalido`. `FA`/`FB`/`FC` are
   seeded `es_fiscal = true, activo = true` (`InicializadorDeBaseDeDatos.cs:90-92`), so that one
   clause is the whole wall.

7. **`Politicas.cs` has 11 policies today.** After 19a it has **12**. That is a countable criterion,
   not a description.

**Size note.** The 800-word budget of `sdd-design` is overridden by the project's own precedent (the
archived stage-17 and stage-18 `design.md` are the format the orchestrator named as binding, and the
prompt requires numbered mutation targets and a slicing table).

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| **D1** | **`numeraciones_fiscales` enters the total lock order at POSITION 0, strictly before `turnos_caja`, and the fiscal emission transaction takes NO other existing-row lock.** New total order: **`numeraciones_fiscales → turnos_caja → comprobantes_venta → presupuestos → remitos → lotes → stock/stock_lotes → clientes → ledger INSERT`** | (a) Put it last, next to `clientes` — where a "counter" intuitively belongs. (b) Put it between `comprobantes_venta` and `presupuestos`. (c) Do not hold it across the round trip: commit the number, call ARCA, resolve in a second transaction | Proposal decision 13 **binds** the hold: *"el lock de la fila … se mantiene durante el round trip de WSFE bajo un timeout acotado del cliente"*. A lock held across network I/O must be a **prefix** of the order, or a transaction already holding an earlier lock can end up waiting on a socket. At position 0 with a **singleton** lock set the fiscal path is a prefix-singleton: acyclic by construction, and **no existing path can ever queue behind ARCA** — a WSFE stall cannot reach the POS, because a checkout never touches this row and this transaction never touches `turnos_caja`, `stock` or `clientes`. (a) and (b) are rejected for the exact opposite reason: from either position the fiscal transaction would first take `turnos_caja` (contended by every checkout, annulment and cash-shift close) and then hold it for the round trip — converting a 30 s ARCA timeout into a shop-wide stall. (c) is rejected because it is the burned-number discipline the proposal exists to refuse: a committed number whose emission then fails is an I1 hole, and holes **stop** an ARCA series (10016). The bound is named: `TimeoutDeWsfe = 30 s` (connect+read) and the command timeout, so the maximum hold is `30 s + ε`, stated, not hoped |
| **D2** | **`SobreSoap` is a pure static function `string Construir(string espacioDeNombres, string operacion, params XElement[] cuerpo)`** with `XDeclaration("1.0", "UTF-8", null)` + `SaveOptions.DisableFormatting`, and **it is the only file in `src/` that names SOAP, `soapenv` or a SOAP namespace** | An `HttpClient` handler that wraps/unwraps envelopes; a small `ISobreSoap` interface with a DI registration | The isolation target is the **protocol**, and the precedent is `ExportadorXlsx` (*"único archivo de src/ que referencia ClosedXML"*). A pure function makes the golden test trivial — it takes an `XElement` tree and returns bytes, with no `HttpClient`, no clock and no DI to stand between the test and the wire. A handler would push the envelope behind an I/O boundary and make byte-exact goldens need a fake transport. The one-file rule is a **verify criterion**, checked with `rg`, not a wish |
| **D3** | **Goldens are byte-for-byte over a fully pinned input: `RelojFijo`, a constant `TicketDeAcceso`, a constant `uniqueId`, and `CultureInfo.InvariantCulture` everywhere.** Stored LF, BOM-less UTF-8; the comparison normalizes **only** the line ending, never the XML | Compare with `XNode.DeepEquals`; canonicalize with C14N; compare "semantically" | Decision 7's honest risk is *namespaces, `SOAPAction` and element order*. `DeepEquals` and C14N are **exactly** the normalizations that would forgive a reordered element or a dropped prefix — they erase the property the test exists to prove. The cost is that every nondeterministic input must be injected: that cost is the design (see D4), and it is why the clock and the TA are ports rather than ambient values |
| **D4** | **`FECAESolicitar` is called only with a `PermisoDeSolicitud` token that `MaquinaDeEstadosCae` issues** — the pure domain machine is the only producer, and it refuses to issue one for a comprobante whose previous attempt was non-definitive until `FECompConsultar` has answered | A `bool consultarPrimero` argument; a comment on `IClienteWsfe.SolicitarCaeAsync`; an `if` inside the service | I2 is the invariant whose violation is *"the single most expensive failure mode available here"* — a duplicated legal document. A boolean is deletable by one careless edit and invisible in review; a type that cannot be constructed outside the machine makes the mistake **not expressible**. The machine stays pure and DB-free (the `PoliticaDeRoles` pattern the proposal names), so its whole truth table is a unit test with no container |
| **D5** | **The AAD is `UTF8("v1|" + idTenant + "|" + idEmpresa + "|" + ambiente + "|" + huellaSha256)` and it deliberately EXCLUDES `id_clave_maestra`** | Include the key version in the AAD; use the row's `id_certificado` instead of the fingerprint | The AAD binds a ciphertext to its **row identity**. A key version is not identity — it is which key opened it, and the key is already selected by that column. Including it would make a legitimate rotation change the AAD, so a rotation and a row-move would look alike to a reviewer. The `v1|` prefix is what makes a future AAD change a **versioned** migration instead of a silent decryption failure. `huella_sha256` is preferred over `id_certificado` because it also binds the ciphertext to the **public certificate it belongs to**: swapping in another empresa's PEM breaks authentication even inside the same row |
| **D6** | **`ambiente` is part of the master-key lookup shape, not part of the key.** Config: `Ways:Fiscal:ClaveMaestraActual` (an id) + `Ways:Fiscal:ClavesMaestras:<id>` (32 bytes base64). Missing/short/absent key ⇒ `503 clave_maestra_ausente` on the ABM and `409 certificado_fiscal_ausente` on emission — **never** a plaintext fallback, **never** a generated-on-boot key | One key per ambiente; a key derived from the connection string; generate-if-absent for Development | A generated-on-boot key is a plaintext fallback wearing a hat: it decrypts nothing after a restart, so the operator's first symptom is a dead certificate, not a loud config error. Two keys double the key-handling surface for no isolation gain — the AAD already refuses to move a homologación blob into a producción row |
| **D7** | **The test certificate is generated per fixture and re-loaded through PKCS#12 before signing**: `CertificateRequest(…).CreateSelfSigned(…)` then `X509CertificateLoader.LoadPkcs12(cert.Export(Pkcs12, pwd), pwd, X509KeyStorageFlags.Exportable)` | Use the `CreateSelfSigned` result directly | On Windows the ephemeral key `CreateSelfSigned` returns is not always usable by `CmsSigner`, and the dev machine **is** Windows 11 while CI is Linux. A green Linux suite that fails on the owner's machine is the worst possible outcome for the one slice whose whole point is that it works without ARCA. The round trip costs three lines and removes the platform question |
| **D8** | **The TA cache is `ConcurrentDictionary` + one `SemaphoreSlim` per `(idEmpresa, ambiente, servicio)` key (single-flight), valid iff `reloj.Ahora < Expiracion - MargenDeSeguridad` with `MargenDeSeguridad = 10 min`** | A plain dictionary with a lock; no single-flight; margin as a percentage of TTL | The named risk is the WSAA minimum interval (10 min Testing / 2 min Production) versus a cache that dies on restart. Single-flight is the part of that risk that **is** fixable in memory: a cold start with N concurrent emissions must issue **one** `LoginCms`, not N. The absolute 10-minute margin (not a percentage) is chosen because the throttle is absolute; a percentage of a 12 h TTL would be 36 min and would waste a third of every ticket |
| **D9** | **`ResolverTipoFiscalAsync` lives in `ServicioDeFacturacionFiscal` and is the mirror image of the POS resolver**: it requires `Activo && Clase == Venta && EsFiscal` — it does **not** read `AfectaStock`, and it never touches `ServicioDeVentas` | Extract a shared resolver with a `permitirFiscal` flag; add an overload to the POS resolver | A shared resolver with a flag is one boolean away from opening the counter to `FA`, which is the stage-17 *"PRE latente"* class with a legally irreversible document at the end. Two resolvers that never meet cost ~12 duplicated lines and make the mistake require a deliberate merge with its own review. Verify criterion 6 is enforced by `git diff --exit-code`, so the duplication is **checked**, not assumed |
| **D10** | **The four gates run in a fixed order, each with its own named 409, all of them BEFORE any port is resolved**: `empresa_sin_condicion_fiscal` → `punto_venta_sin_numero_fiscal` → `tipo_fiscal_invalido` → `condicion_fiscal_receptor_no_mapeada` → `certificado_fiscal_ausente` | One combined `fiscal_no_configurado`; check the certificate first because it is the real blocker | Five distinct facts deserve five distinct answers: the operator's next action is different for each. The certificate goes **last** on purpose — with it first, a shop that never set `id_condicion_fiscal` would be told to upload a certificate it already has. I4 holds regardless: the `HttpMessageHandler` spy must record **zero** requests on all five paths, which is the test, not the ordering |
| **D11** | **`Exento` → `ImpOpEx` and `No gravado` → `ImpTotConc` are decided by the alícuota's `codigo_afip IS NULL` plus its `nombre`, and the composition FAILS LOUDLY on any other NULL-coded alícuota** | Map by `porcentaje == 0`; treat every NULL code as exempt | `0%`, `Exento` and `No gravado` all carry `porcentaje = 0.00` (`InicializadorDeBaseDeDatos.cs:51-53`) — mapping by percentage would put `0%` (a real alícuota, code 3, belongs in `Iva[]`) into `ImpOpEx` and produce an arithmetically valid, legally wrong invoice. Silently bucketing unknown NULL codes is the same failure with a different trigger, so an unrecognised NULL-coded alícuota throws `alicuota_sin_mapeo_afip` instead of guessing |
| **D12** | **19a's fiscal write plan is `comprobante + items` only. No `movimientos_stock`, no `pagos_comprobante`, no `movimientos_cuenta_corriente`, no turno guard — and there is an explicit test that asserts those three are EMPTY, labelled as the known 19c gap** | Duplicate the checkout's write plan inside `ServicioDeFacturacionFiscal`; call `ServicioDeVentas` | The proposal's Affected Areas lists **no** stock, payment or cuenta-corriente file, marks `ServicioDeVentas` **Untouched**, and budgets slice 5 at ~420 lines — a second checkout does not fit and would be the duplication decision 9 exists to prevent. The gap is safe **because of I4**: with no certificate no such row can exist in production, and a `git revert` of the stage leaves nothing to repair. It is registered as **T1** with its binding 19c contract, and the emptiness test is the trip-wire that turns 19c's addition into a visible red test instead of a discovery. `FA`/`FB`/`FC` carry `afecta_stock = true` in the catalogue, so this **is** a real inconsistency — named here rather than hidden |
| **D13** | **`FECompUltimoAutorizado` reconciliation NEVER writes `proximo_numero`.** It writes only `ultimo_autorizado_arca` + `sincronizado_en`, and a divergence raises `409 numeracion_fiscal_desincronizada` for an operator | Auto-heal: set `proximo_numero = ultimo_autorizado_arca + 1` | Auto-healing a fiscal series is a program deciding, unattended, that a legal document either does or does not exist. If ARCA is ahead, a local comprobante is missing its CAE and must be found by I2's `FECompConsultar`; if we are ahead, a number was burned and only an operator may release it (I1's explicit-action clause). Both are decisions, not repairs |
| **D14** | **`observaciones_fiscales` stores `[{ "codigo": int, "mensaje": string }]` and carries BOTH `Observaciones[]` (on an approval) and `Errors[]` (on a rejection)**, mapped through `.HasColumnType("jsonb")` (the `Auditoria` precedent) | Two columns; a child table; store the raw SOAP response | The gate authorises one `jsonb` column and it is a display/audit payload, never a query key. Storing the raw response would persist `Token`/`Sign` — a bearer credential in a table that has no encryption path. One shape for both arrays is what makes the CHECK-2 coherence rule expressible: the column's presence tracks `resultado_fiscal`, not which array filled it |

## The migration — exact statement order

`FiscalArcaEtapa19a`, PostgreSQL 17, `src/Ways.Infrastructure/Persistencia/Migraciones/`.

```
Up()
 01  AlterDatabase()   ← THE statement that emits both CREATE TYPEs
       + Annotation "Npgsql:Enum:ambiente_fiscal"  = "homologacion,produccion"
       + Annotation "Npgsql:Enum:resultado_fiscal" = "pendiente,aprobado,aprobado_con_observaciones,rechazado"
       (every pre-existing enum annotation repeated verbatim in both Annotation and OldAnnotation,
        the two new ones ABSENT from OldAnnotation — the RemitosEtapa17 shape)
       ⚠ `dotnet ef migrations add` serialises enum values ALPHABETICALLY; both lists are
         hand-corrected to LIFECYCLE order (the documented residue of etapas 15/16/17).
         ZERO `ALTER TYPE … ADD VALUE` in the whole file.
 §B  02  AddColumn        empresas.id_condicion_fiscal integer NULL
     03  AddForeignKey    fk_empresas_condicion_fiscal → condiciones_fiscales  RESTRICT  (simple)
     04  CreateIndex      ix_empresas_condicion_fiscal (id_condicion_fiscal)   (simple — §14 trap)
 §C  05  AddColumn        puntos_venta.numero_fiscal integer NULL
     06  AddCheck         ck_puntos_venta_numero_fiscal_rango
     07  CreateIndex      ux_puntos_venta_numero_fiscal  UNIQUE, filter numero_fiscal IS NOT NULL
 §D  08  AddColumn ×4     comprobantes_venta: cae, cae_vencimiento, resultado_fiscal, observaciones_fiscales
     09  AddCheck         ck_comprobantes_venta_fiscal_coherente
     10  AddCheck         ck_comprobantes_venta_cae_digitos
     11  CreateIndex      ix_comprobantes_venta_fiscal_pendientes  filter resultado_fiscal = 'pendiente'
 §E  12  CreateTable      certificados_fiscales  (PK, FK2, FK3, CHECK4, CHECK5, CHECK6 inline)
     13  CreateIndex ×3   ix_..._tenant · ix_..._empresa · ux_certificados_fiscales_activo (UNIQUE, filter)
 §F  14  CreateTable      numeraciones_fiscales  (PK, FK4, FK5, CHECK7, CHECK8 inline)
     15  CreateIndex ×2   ix_..._tenant · ix_..._punto_venta
 §G  16  Sql  DS1  tipos_comprobante     — 7 rows, WHERE codigo = … AND codigo_afip IS NULL
     17  Sql  DS2  condiciones_fiscales  — 5 rows, same guard
     18  Sql  DS3  alicuotas_iva         — 4 rows, WHERE nombre IN ('0%','10.5%','21%','27%') AND codigo_afip IS NULL
     19  HabilitarRlsDeTenant("certificados_fiscales")
     20  HabilitarRlsDeTenant("numeraciones_fiscales")          ← RLS LAST (the etapa 12-17 convention)
```

**Why this order.** The two `ALTER TABLE`s that add a column typed by a new enum (`§D`) must follow
statement 01. RLS is last because the migration connection has no `app_tenant_actual()` and the data
statements must not run under a policy they cannot satisfy (`RemitosEtapa17.cs:332-334`) — the three
catalogues are global, so this is convention discipline rather than necessity, and it stays uniform.
`DropTable` drops a table's RLS policy with it, so `Down()` needs no explicit policy statement.

```
Down()   — exact inverse, in reverse order
 01  Sql  UPDATE alicuotas_iva        SET codigo_afip = NULL WHERE nombre = '21%' AND codigo_afip = 5;  (×4)
 02  Sql  UPDATE condiciones_fiscales SET codigo_afip = NULL WHERE codigo = 'RI' AND codigo_afip = 1;   (×5)
 03  Sql  UPDATE tipos_comprobante    SET codigo_afip = NULL WHERE codigo = 'FA' AND codigo_afip = 1;   (×7)
 04  DropTable numeraciones_fiscales           (drops PK, 2 FKs, 2 CHECKs, 2 indexes, RLS policy)
 05  DropTable certificados_fiscales           (idem, 3 indexes)
 06  DropIndex ix_comprobantes_venta_fiscal_pendientes
 07  DropCheck ck_comprobantes_venta_cae_digitos · ck_comprobantes_venta_fiscal_coherente
 08  DropColumn ×4 on comprobantes_venta       ← must precede 12: DROP TYPE fails while a column uses it
 09  DropIndex ux_puntos_venta_numero_fiscal · DropCheck ck_puntos_venta_numero_fiscal_rango
 10  DropColumn puntos_venta.numero_fiscal
 11  DropIndex ix_empresas_condicion_fiscal · DropForeignKey fk_empresas_condicion_fiscal
     · DropColumn empresas.id_condicion_fiscal
 12  AlterDatabase()  Annotation/OldAnnotation swapped ⇒ DROP TYPE resultado_fiscal; DROP TYPE ambiente_fiscal
```

**The reversibility clause, and why it is stronger than 12/17.** Every value-carrying statement above
has a true inverse. The three data statements are the only ones that touch existing rows, and their
`Down()` is **doubly guarded**: `WHERE codigo = 'FA' AND codigo_afip = 1` reverts only rows this
migration actually set, so a row that already carried a code (impossible today, cheap to guarantee
forever) is left alone. `Down()` restores **exactly** the pre-migration state, and the round trip
`Up → Down → Up` plus `dotnet ef migrations has-pending-model-changes` clean is a verify criterion.
Etapas 12 and 17 could not say this: `ALTER TYPE … ADD VALUE` has no `DROP VALUE`, so their `Down()`
stranded an enum value forever. 19a leaves nothing behind.

### The 8 new indexes, by definition

| # | Name | Definition (`pg_indexes.indexdef`, normalized) | Declared for |
|---|---|---|---|
| 1 | `ix_empresas_condicion_fiscal` | `btree (id_condicion_fiscal)` | FK 1 support. **Simple, not led by `id_tenant`** — the stage-14 amendment trap: a composite led by `id_tenant` does not cover a simple FK |
| 2 | `ux_puntos_venta_numero_fiscal` | `UNIQUE btree (id_tenant, id_empresa, numero_fiscal) WHERE (numero_fiscal IS NOT NULL)` | Decision 2 — injectivity of `(PtoVta, CbteTipo) → (id_punto_venta, codigo_afip)` |
| 3 | `ix_comprobantes_venta_fiscal_pendientes` | `btree (id_punto_venta, id_tenant) WHERE (resultado_fiscal = 'pendiente'::resultado_fiscal)` | The pending-resolution read; consumer ships in slice 5 |
| 4 | `ix_certificados_fiscales_tenant` | `btree (id_tenant)` | RLS predicate + FK 2 |
| 5 | `ix_certificados_fiscales_empresa` | `btree (id_empresa, id_tenant)` | FK 3 |
| 6 | `ux_certificados_fiscales_activo` | `UNIQUE btree (id_tenant, id_empresa, ambiente) WHERE (activo AND deleted_at IS NULL)` | At most one active signer per empresa+ambiente — a database guarantee, not a service one |
| 7 | `ix_numeraciones_fiscales_tenant` | `btree (id_tenant)` | RLS predicate + FK 4 |
| 8 | `ix_numeraciones_fiscales_punto_venta` | `btree (id_punto_venta, id_tenant)` | FK 5. **Not** covered by the PK (its second column is `codigo_afip`) |

Asserted by **definition string**, never by name (the stage-16 lesson), and the new-index count over
`main` must be exactly 8.

### Configurations

| File | Action | Content |
|---|---|---|
| `Configuraciones/EmpresaConfiguration.cs` | Modify | `id_condicion_fiscal` property + simple `HasOne<CondicionFiscal>().WithMany().HasForeignKey(e => e.IdCondicionFiscal)` `Restrict` + `HasIndex(e => e.IdCondicionFiscal).HasDatabaseName("ix_empresas_condicion_fiscal")` |
| `Configuraciones/PuntoVentaConfiguration.cs` | Modify | `numero_fiscal` + `ToTable(t => t.HasCheckConstraint(…))` + `HasIndex(…).IsUnique().HasFilter("numero_fiscal IS NOT NULL").HasDatabaseName("ux_puntos_venta_numero_fiscal")` |
| `Configuraciones/ComprobanteVentaConfiguration.cs` | Modify | 4 properties (`cae varchar(14)`, `cae_vencimiento date`, `resultado_fiscal` enum, `observaciones_fiscales` `HasColumnType("jsonb")` with a value converter), 2 CHECKs, the partial index |
| `Configuraciones/CertificadoFiscalConfiguration.cs` | **Create** | 18 columns, PK, 2 FKs (tenant simple; empresa composite against `ak_puntos_venta`-style AK on `empresas`), 3 CHECKs, 3 indexes, all names explicit snake_case |
| `Configuraciones/NumeracionFiscalConfiguration.cs` | **Create** | **Line-for-line mirror of `NumeracionComprobanteConfiguration`**: PK `(IdPuntoVenta, CodigoAfip)`, `IdTenant` non-key, `ProximoNumero` `HasDefaultValue(1L)`, both index names written by hand (`:44-49` trap), composite FK to `puntos_venta` (fact 2), FK to `tenants` |
| `WaysDbContext.cs` | Modify | `DbSet<CertificadoFiscal>` (exposed in `IWaysDbContext`) · `DbSet<NumeracionFiscal>` **not** exposed (sibling criterion, `:78-84`) · `AplicarFiltroDeTenantEnNumeracionFiscal` · `RechazarEscriturasDeNumeracionFiscal` · two `HasPostgresEnum` registrations |

## Interfaces / Contracts

### `SobreSoap` — the only file that knows SOAP exists

```csharp
// src/Ways.Infrastructure/Fiscal/SobreSoap.cs
// ÚNICO archivo de src/ que nombra SOAP (precedente invertido de ExportadorXlsx: acá el
// aislado es el PROTOCOLO, y quedan cero dependencias nuevas). Puro: sin HttpClient, sin
// reloj, sin DI — para que el golden compare bytes y no una interfaz.
internal static class SobreSoap
{
    private static readonly XNamespace Soapenv = "http://schemas.xmlsoap.org/soap/envelope/";

    public const string EspacioWsaa = "http://wsaa.view.sua.dvadac.desa.afip.gov";
    public const string EspacioWsfe = "http://ar.gov.afip.dif.FEV1/";

    /// <summary>SOAPAction: "" para WSAA; "<EspacioWsfe><operacion>" para WSFE.</summary>
    public static string AccionDe(string espacioDeNombres, string operacion);

    public static string Construir(string espacioDeNombres, string operacion, params object[] cuerpo);

    /// <summary>Body → el primer hijo, o el soap:Fault con faultcode/faultstring.</summary>
    public static RespuestaSoap Leer(string xml);
}
```

`Construir` emits, with **no** indentation and **no** stray whitespace:

```xml
<?xml version="1.0" encoding="UTF-8"?><soapenv:Envelope xmlns:soapenv="…/soap/envelope/" xmlns:ar="…"><soapenv:Header /><soapenv:Body><ar:{operacion}>…</ar:{operacion}></soapenv:Body></soapenv:Envelope>
```

Formatting rules that the goldens pin: money as `ToString("0.00", InvariantCulture)`; dates as
`yyyyMMdd`; `MonId = "PES"`, `MonCotiz = 1`; element order **exactly** as printed in
`manual-desarrollador-ARCA-COMPG-v4-0.pdf` (RG 4291, rev. 15/01/2025); an optional element with no
value is **omitted**, never emitted empty. Every fixture file names its manual section; the set is
pinned in `REVISION.md` together with `Especificacion_Tecnica_WSAA_1.2.2.pdf`.

### TRA / CMS

```xml
<?xml version="1.0" encoding="UTF-8"?><loginTicketRequest version="1.0"><header><uniqueId>{uint}</uniqueId><generationTime>{ahora-10m:yyyy-MM-ddTHH:mm:sszzz}</generationTime><expirationTime>{ahora+10m:…}</expirationTime></header><service>wsfe</service></loginTicketRequest>
```

```csharp
// src/Ways.Infrastructure/Fiscal/GeneradorDeTra.cs   — puro salvo IRelojDelSistema
public sealed class GeneradorDeTra(IRelojDelSistema reloj)
{
    public static readonly TimeSpan Ventana = TimeSpan.FromMinutes(10);
    public string Construir(string servicio);   // uniqueId = unix seconds ⊕ Interlocked tiebreak
}

// src/Ways.Infrastructure/Fiscal/FirmanteCms.cs     — BCL puro, cero dependencias
public static class FirmanteCms
{
    public static string FirmarBase64(string tra, X509Certificate2 certificado);
    // new SignedCms(new ContentInfo(Encoding.UTF8.GetBytes(tra)))
    //   .ComputeSignature(new CmsSigner(certificado) {
    //        IncludeOption = X509IncludeOption.EndCertOnly,
    //        DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") })   // SHA-256
    //   .Encode() → Convert.ToBase64String
}
```

`uniqueId` mixes the clock with an `Interlocked.Increment` tiebreak so two TRAs produced inside the
same second differ — WSAA rejects a repeated `(uniqueId, generationTime)` pair for a CUIT.

### Ports and the CAE machine

```csharp
// src/Ways.Application/Fiscal/…
public interface IClienteWsaa   { Task<TicketDeAcceso> ObtenerTicketAsync(SolicitudDeTicket s, CancellationToken ct); }

public interface IRepositorioDeTicketDeAcceso
{
    Task<TicketDeAcceso?> ObtenerVigenteAsync(ClaveDeTicket clave, CancellationToken ct);
    Task GuardarAsync(ClaveDeTicket clave, TicketDeAcceso ticket, CancellationToken ct);
}
public readonly record struct ClaveDeTicket(int IdEmpresa, AmbienteFiscal Ambiente, string Servicio);

public interface IClienteWsfe
{
    Task<RespuestaCae>  SolicitarCaeAsync(PermisoDeSolicitud permiso, SolicitudDeCae s, CancellationToken ct);
    Task<ConsultaDeComprobante> ConsultarAsync(ClaveDeSerie clave, long numero, CancellationToken ct);
    Task<long>          UltimoAutorizadoAsync(ClaveDeSerie clave, CancellationToken ct);   // 0 = serie sin usar
    Task<IReadOnlyList<ParametroArca>> ParametrosAsync(string operacion, CancellationToken ct);
}

public interface IAlmacenDeClavesFiscales
{
    /// El material descifrado vive SOLO dentro de este callback; el byte[] se limpia con
    /// CryptographicOperations.ZeroMemory al salir, pase lo que pase.
    Task<T> UsarCertificadoAsync<T>(int idEmpresa, AmbienteFiscal ambiente,
                                    Func<X509Certificate2, Task<T>> uso, CancellationToken ct);
}

// src/Ways.Domain/Fiscal/MaquinaDeEstadosCae.cs — pura, sin base de datos (patrón PoliticaDeRoles)
public readonly record struct PermisoDeSolicitud   // D4: solo la máquina lo construye
{ internal PermisoDeSolicitud(int idComprobante, long numero) { … } }

public static class MaquinaDeEstadosCae
{
    public static bool EsTerminal(ResultadoFiscal r) =>
        r is ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones;

    public static DecisionDeReintento Decidir(EstadoDeIntento previo);   // I2
    public static ResultadoFiscal Mapear(char resultadoArca, bool hayObservaciones);  // A/A+obs/R
}
```

### `AsignadorDeNumeroFiscal` — decision 13's discipline

```csharp
// src/Ways.Application/Fiscal/AsignadorDeNumeroFiscal.cs
/// DISCIPLINA OPUESTA a AsignadorDeNumeroComprobante (design D1, proposal decisión 13): NO abre
/// ni comitea transacción propia — corre DENTRO de la del llamador, y el row lock del UPDATE se
/// sostiene hasta el COMMIT de la emisión, incluido el round trip a WSFE. En la serie interna un
/// número quemado abre un hueco legítimo; en una serie de ARCA DETIENE la serie (error 10016).
public static class AsignadorDeNumeroFiscal
{
    public static Task AsegurarContadorAsync(IWaysDbContext db, int idTenant, int idPuntoVenta,
                                             short codigoAfip, CancellationToken ct);
    //  INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero)
    //  VALUES ($1,$2,$3,1) ON CONFLICT (id_punto_venta, codigo_afip) DO NOTHING

    public static Task<long> AsignarSiguienteAsync(IWaysDbContext db, int idPuntoVenta,
                                                   short codigoAfip, CancellationToken ct);
    //  UPDATE numeraciones_fiscales SET proximo_numero = proximo_numero + 1
    //   WHERE id_punto_venta = $1 AND codigo_afip = $2 RETURNING proximo_numero - 1
}
```

Raw ADO on the caller's open connection and current transaction (`Database.SqlQuery<T>`/`FromSqlRaw`
are forbidden here — the stage-1-slice-2 finding the sibling documents at `:14-17`).

### The ARCA error taxonomy → domain codes

| Source | Code | Domain code | HTTP | Retry |
|---|---|---|---|---|
| WSAA fault | `500` / `501` / `502` (CMS malformed, cert untrusted, cert expired) | `certificado_fiscal_rechazado` | 409 | **No** — a configuration fact |
| WSAA fault | `600` / `602` (not authorized to the service, cert not found) | `certificado_fiscal_sin_autorizacion` | 409 | **No** |
| WSAA fault | `601` (already authenticated / minimum interval) | `wsaa_en_intervalo_minimo` | 503 | Backoff; the TA cache is the mitigation |
| WSFE `Errors[]` | `600` (invalid token/sign) | `ticket_de_acceso_invalido` | 503 | Invalidate the TA and retry **once** |
| WSFE `Errors[]` | `10016` (non-correlative number) | `numeracion_fiscal_desincronizada` | 409 | **No** — triggers reconciliation (D13), never an auto-advance |
| WSFE `Resultado = 'R'` | any excluding validation | `arca_rechazo` (+ `Errors[]` into `observaciones_fiscales`) | 409 | **No** — `rechazado`, no CAE, the number stays bound |
| WSFE `Resultado = 'A'` + `Observaciones[]` | — | **success** | 200 | — |
| Transport: timeout, 5xx, socket, circuit open | — | `arca_no_definitivo` | 503 | **Yes**, and I2 arms: the next attempt asks `FECompConsultar` first |

## Data flow

```
POST /api/fiscal/comprobantes                    [OperacionDePos]
  │
  ├─ FUERA de toda transacción — los CUATRO GATES (D10), cada uno su 409 nombrado:
  │    empresas.id_condicion_fiscal ─┐  puntos_venta.numero_fiscal ─┐
  │    ResolverTipoFiscalAsync ──────┤  clientes.id_condicion_fiscal (NO_RESP ⇒ 409)
  │    certificados_fiscales activo ─┘        ⇒ I4: CERO bytes en el cable
  ├─ ResolvedorDeLetraComprobante.Resolver(emisor, receptor)   ← SU PRIMER CALLER
  └─ ComposicionDeTotalesFiscales  (GROUP BY id_alicuota_iva sobre el snapshot por línea)

  EstrategiaSinReintento ⇒ BEGIN                       ← LOCK SET = { numeraciones_fiscales }
   1. AsegurarContadorAsync            INSERT … ON CONFLICT DO NOTHING
   2. AsignarSiguienteAsync            UPDATE … RETURNING    ← lock tomado acá, posición 0 (D1)
   3. INSERT comprobantes_venta        resultado_fiscal = 'pendiente', numero = el fiscal
   4. INSERT items_comprobante_venta   (snapshot; CERO stock / pagos / CC — D12, T1)
   5. ── el round trip, con el lock sostenido, TimeoutDeWsfe = 30 s ──────────────
        IRepositorioDeTicketDeAcceso → (miss) → GeneradorDeTra → FirmanteCms → SobreSoap
                                              → ClienteWsaa.LoginCms → TA (12 h, margen 10 min)
        MaquinaDeEstadosCae.Decidir → PermisoDeSolicitud → ClienteWsfe.FECAESolicitar
   6. UPDATE comprobantes_venta SET cae…, resultado_fiscal = …, observaciones_fiscales = …
        WHERE id = $ AND id_tenant = $ AND resultado_fiscal = 'pendiente'   ← I3
   7. UPDATE numeraciones_fiscales SET ultimo_autorizado_arca, sincronizado_en   (solo si aprobó)
  COMMIT            ← el lock se libera acá; el único que puede haber esperado es OTRA emisión
                      fiscal del MISMO (PV fiscal, codigo_afip) — exactamente la serialización
                      que ARCA impone de todos modos

POST /api/fiscal/comprobantes/{id}/reintentar     [OperacionDePos]      ← I2
  lee el comprobante 'pendiente' (ix_comprobantes_venta_fiscal_pendientes)
  MaquinaDeEstadosCae.Decidir(previo = no-definitivo) ⇒ ConsultarPrimero
    FECompConsultar(PtoVta, CbteTipo, CbteNro)
      hallado  ⇒ ADOPTA el CAE existente (paso 6) — CERO FECAESolicitar
      no hallado ⇒ emite el PermisoDeSolicitud → FECAESolicitar con el MISMO número
```

## File changes

| File | Action | Description |
|---|---|---|
| `…/Migraciones/*_FiscalArcaEtapa19a.cs` | Create | The single migration, 20 `Up` statements / 12 `Down` statements above |
| `src/Ways.Domain/Fiscal/{ResultadoFiscal,AmbienteFiscal}.cs` | Create | The two enums, member order = lifecycle = `CREATE TYPE` order |
| `src/Ways.Domain/Fiscal/{CertificadoFiscal,NumeracionFiscal}.cs` | Create | `EntidadBase` **yes** / **no** respectively (proposal §E/§F) |
| `src/Ways.Domain/Fiscal/MaquinaDeEstadosCae.cs` | Create | Pure, DB-free; `PermisoDeSolicitud`, `EsTerminal`, `Decidir`, `Mapear` |
| `src/Ways.Domain/Fiscal/PayloadQrFiscal.cs` | Create | The 13 RG 4291 fields + base64 + `https://www.afip.gob.ar/fe/qr/?p=` |
| `src/Ways.Domain/Organizacion/{Empresa,PuntoVenta}.cs` | Modify | One nullable property each |
| `src/Ways.Domain/Ventas/ComprobanteVenta.cs` | Modify | Four nullable fiscal properties |
| `src/Ways.Domain/Ventas/ResolvedorDeLetraComprobante.cs` | **Unmodified** | Gains a caller; the rule and its doc-comment's *"dormant"* line change only in slice 5's comment |
| `src/Ways.Application/Fiscal/{IClienteWsaa,IClienteWsfe,IAlmacenDeClavesFiscales,IRepositorioDeTicketDeAcceso}.cs` | Create | The four ports |
| `src/Ways.Application/Fiscal/Contratos.cs` | Create | `TicketDeAcceso`, `SolicitudDeCae`, `RespuestaCae`, `ClaveDeSerie`, `CertificadoFiscalDto` (**no key material**) |
| `src/Ways.Application/Fiscal/AsignadorDeNumeroFiscal.cs` | Create | Decision 13's discipline |
| `src/Ways.Application/Fiscal/ComposicionDeTotalesFiscales.cs` | Create | `GROUP BY` alícuota; `ImpOpEx` / `ImpTotConc` per D11 |
| `src/Ways.Application/Fiscal/{ServicioDeFacturacionFiscal,ServicioDeCertificados}.cs` | Create | The emission use case (four gates) and the certificate ABM |
| `src/Ways.Infrastructure/Fiscal/SobreSoap.cs` | Create | **The only file in `src/` that names SOAP** |
| `src/Ways.Infrastructure/Fiscal/{GeneradorDeTra,FirmanteCms}.cs` | Create | `SignedCms`, BCL only |
| `src/Ways.Infrastructure/Fiscal/{ClienteWsaa,ClienteWsfe}.cs` | Create | The two adapters + backoff + circuit breaker |
| `src/Ways.Infrastructure/Fiscal/RepositorioEnMemoriaDeTicketDeAcceso.cs` | Create | D8: `ConcurrentDictionary` + per-key single-flight |
| `src/Ways.Infrastructure/Fiscal/CifradoDeClavesFiscales.cs` | Create | AES-256-GCM, AAD of D5, key versioning, `ZeroMemory` |
| `…/Configuraciones/{CertificadoFiscal,NumeracionFiscal}Configuration.cs` | Create | See the Configurations table |
| `…/Configuraciones/{Empresa,PuntoVenta,ComprobanteVenta}Configuration.cs` | Modify | The six new columns and their constraints |
| `…/Persistencia/WaysDbContext.cs` | Modify | 2 `DbSet`, the hand-written tenant filter, the write guard, 2 enum registrations |
| `…/Persistencia/InicializadorDeBaseDeDatos.cs` | Modify | The three seed nets (`CodigoAfip` on the three `…Base` arrays) |
| `src/Ways.Api/Endpoints/FiscalEndpoints.cs` | Create | The five routes |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modify | **+1**: `AdministracionFiscal` (Admin only). 11 → **12** |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modify | **+10** branches: 2 × `23505`, 8 × `23514` |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | **Unmodified** | `git diff --exit-code` (D9, criterion 6) |
| `tests/**/Fiscal/Fixtures/**` + `REVISION.md` | Create | The manual's contract, versioned |
| `tests/**/Fiscal/CertificadoDePrueba.cs` | Create | D7's runtime generator — **no key material committed** |
| `docs/{09,10,11}` | Modify | Scoping table, §4-adjacent subsections, Etapa 19a status block |
| `src/Ways.Web/**` | **Unmodified** | No UI in 19a — that is 19c |

## Lock order — arbitrated

| Path | Locks taken, in order | Verdict |
|---|---|---|
| `ServicioDeFacturacionFiscal` (fiscal emission) | **`numeraciones_fiscales` only** | **New position 0.** Its comprobante and items are `INSERT`s, and per the stage-17 clause *"a new-row `INSERT` is not a position in this order"* |
| `ServicioDeFacturacionFiscal` (retry / I2) | **`comprobantes_venta`** (the guarded `UPDATE` of step 6) only | Suffix of the order; never contends with the assignment path because a `pendiente` row already owns its number |
| `AsignadorDeNumeroFiscal` reconciliation (D13) | `numeraciones_fiscales` only | Same singleton |
| `EjecutarTransaccionAsync` (sale), `EjecutarAnulacionAsync`, remitos, presupuestos, compras, stock | **unchanged** | None of them touches `numeraciones_fiscales` or `certificados_fiscales` |

**Total order over contended (existing-row) locks, extended:**
**`numeraciones_fiscales → turnos_caja → comprobantes_venta → presupuestos → remitos → lotes →
stock/stock_lotes → clientes → ledger INSERT`.**

The load-bearing clause is not the position by itself — it is the **conjunction** of position 0 and a
singleton lock set. A lock held across network I/O is only safe as a **prefix**; a singleton prefix is
trivially acyclic and, more importantly, unreachable from every other path in the program. The proof
obligation is therefore two-sided and both sides are tested (targets 51-52): *the fiscal transaction
takes this lock first*, and *the fiscal transaction takes no other existing-row lock*.

**Concurrency.** *Two fiscal emissions on the same `(numero_fiscal PV, codigo_afip)`*: the second
blocks on the row lock of step 2 and resumes only after the first commits — it receives `N+1`, never
`N`, and never a hole. That is I1, and it is also the domain's real constraint (ARCA serializes that
series regardless). *Two emissions on different types of the same PV*: different rows, no contention.
*A fiscal emission and a checkout*: disjoint lock sets — a WSFE stall is invisible to the counter.
*Two retries of the same `pendiente`*: serialized on `comprobantes_venta`; the loser's guarded
`UPDATE` matches 0 rows under the lock and reclassifies to `409 comprobante_fiscal_ya_resuelto`.

## Guarded `UPDATE`s — conjunct enumeration (mutation-proof-tests rule 3 v1.1, up front)

Listed **before** any test is written, per rule 3's *"list EVERY conjunct of EVERY guarded UPDATE in
the slice and pair each one with the test that kills it"*.

| Statement | Conjuncts | The test that kills each |
|---|---|---|
| **U1** `UPDATE numeraciones_fiscales SET proximo_numero = proximo_numero + 1 WHERE …` | (a) `id_punto_venta = $1` · (b) `codigo_afip = $2` | (a) sibling PV of the same tenant with its own row: assigning for PV A leaves PV B's `proximo_numero` untouched (rule 12c). (b) sibling `codigo_afip` on the **same** PV (`FA`=1 vs `FB`=6): assigning `FA` leaves `FB` untouched |
| **U2** `UPDATE comprobantes_venta SET cae…, resultado_fiscal… WHERE …` | (a) `id_comprobante_venta = $` · (b) `id_tenant = $` · (c) `resultado_fiscal = 'pendiente'` | (a) a sibling `pendiente` comprobante of the same PV stays `pendiente`. (b) executed on the `ways_app` connection under tenant B's context ⇒ 0 rows (rule 5). (c) **two kills**: a below-the-confound direct call on an already-`aprobado` row ⇒ 0 rows (I3), **and** the TOCTOU race — two retries rendezvous, the loser re-evaluates under the lock and matches 0 |
| **U3** `UPDATE numeraciones_fiscales SET ultimo_autorizado_arca, sincronizado_en WHERE …` | (a) `id_punto_venta` · (b) `codigo_afip` | Same sibling pair as U1, asserting the neighbour's `ultimo_autorizado_arca` stays `NULL` |
| **U4** `UPDATE certificados_fiscales SET activo = false … WHERE …` (rotation **and** deactivation) | (a) `id_tenant` · (b) `id_empresa` · (c) `ambiente` · (d) `activo` · (e) `deleted_at IS NULL` | (a) `ways_app` under tenant B ⇒ 0 rows. (b) another empresa's active certificate stays active. (c) the **homologación** certificate stays active while **producción** rotates. (d) an already-inactive row is not touched — asserted on the affected-row count, not on final state (rule 4). (e) a soft-deleted twin is neither resurrected nor counted |

The `23505` on `ux_certificados_fiscales_activo` is U4's **race backstop**, not its guard: both are
tested, and the backstop's branch in `ManejadorDeErrores` is one of the two new `23505` arms.

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (no DB) | `MaquinaDeEstadosCae`: the full transition table, terminality of both approvals, `Decidir` over every `EstadoDeIntento`, and that `PermisoDeSolicitud` **cannot be constructed** outside the machine (a compile-time property asserted structurally) | xUnit pure, the `PoliticaDeRoles` pattern |
| Domain unit | `ComposicionDeTotalesFiscales` over a mixed invoice (21 %, 10.5 %, exento, no gravado, two lines sharing 21 %): `Iva[]` has **two** entries, `ImpOpEx`/`ImpTotConc` carry the other two, `ImpTotal = ImpNeto + ImpIVA + ImpOpEx + ImpTotConc + ImpTrib` exactly | Hand-built lines; no database |
| Domain unit | `PayloadQrFiscal` against a **hand-computed** base64 vector (13 fields, `tipoCodAut = "E"`) | Golden string |
| Golden XML (unit) | The `LoginCms` envelope, the TRA, and the `FECAESolicitar` envelope compared **byte-for-byte** with the manual's example, under `RelojFijo`, a constant TA and a constant `uniqueId` | D3; `.request.xml` files, LF, BOM-less; only the line ending normalized |
| Response parsing (unit) | The three `FECAESolicitar` responses, `FECompConsultar` found/not-found, `FECompUltimoAutorizado` head **and empty series = `0`**, every WSAA fault code, and the WSFE `Errors[]`/`Observaciones[]` arrays including **10016** | Fixture files that cite their manual section |
| Crypto (unit) | AES-GCM round trip; **four** AAD tamper cases (tenant, empresa, ambiente, fingerprint), each failing authentication; rotation re-encrypts and still decrypts (AAD unchanged, D5); missing master key ⇒ the named error, **never** plaintext | `CertificadoDePrueba` generated at runtime (D7) |
| Clock (unit) | The TA margin boundary pair under a clock that **advances** (`RelojQueAvanza`, the stage-18 rule): at `Expiracion − Margen − 1 s` the cache is used; at `Expiracion − Margen` a new `LoginCms` is issued. Plus: N concurrent asks ⇒ exactly **one** `LoginCms` | `RelojFijo` cannot see "read once vs read twice"; only an advancing clock can |
| Integration — schema | 8 indexes **by definition** against `pg_indexes`; 8 CHECKs each violated by a write that must fail with `23514`; the two partial uniques (duplicate rejected **and** the legitimate case allowed: two `NULL` fiscal numbers; a soft-deleted twin) | `mutation-proof-tests`; the stage-16 name-only lesson |
| Integration — RLS | `ENABLE` + `FORCE` on both new tables, cross-tenant **read and write** pair, on the `ways_app` connection at statement level | Rule 5 — a superuser fixture proves nothing |
| Integration — data statements | Each of the three nets tested **independently**: the migration statement on an already-migrated DB, and the seed array on a fresh DB — removing either one alone must fail its own test; `Exento`/`No gravado` still `NULL` | The stage-17 double net |
| Integration — reversibility | `Up → Down → Up` clean; `has-pending-model-changes` clean; after `Down()` the three catalogues' `codigo_afip` are `NULL` again and **no other column moved** | Verify criterion |
| Integration — numbering | I1: a `rechazado` emission leaves `proximo_numero` **unchanged relative to the number it bound**, and the number stays bound to the comprobante; two concurrent emissions of the same series get `N` and `N+1` | Rendezvous fixture |
| Integration — lock order | `pg_locks` polled from a **second connection** while the fiscal transaction is held open at a known point: `numeraciones_fiscales` is present and `turnos_caja`/`stock`/`clientes` are **absent** | Rule 13: an order guarantee is invisible to a single-resource race test; the net is structural, never a probabilistic deadlock |
| Integration — I2 | A transport timeout on attempt 1 followed by a retry: the mock records `FECompConsultar` **before** anything else and **exactly one** `FECAESolicitar` across both attempts; the *found* variant adopts the CAE and issues **zero** solicitations | The call log **is** the assertion |
| Integration — I4 | Each of the five gate paths returns its own 409 and the `HttpMessageHandler` spy records **zero** requests | The spy's full log, not a count |
| Integration — the POS is unchanged | `POST /api/ventas` with `FA` ⇒ `400 tipo_comprobante_invalido`; a non-fiscal sale issues the **same** number of EF commands as on `main` (`ContadorDeComandos`, `VentasCheckoutTests:930`); `git diff --exit-code` on `ServicioDeVentas.cs` | Criterion 6, three independent nets |
| Integration — exposure | The serialized certificate responses contain **no property named** `clavePrivadaCifrada`, `nonce`, `tagAutenticacion`, `certificadoPem` or `claveMaestra` — matched by property **name**, walked recursively | `dto-contract-honesty`; the stage-18 substring trap avoided |
| Integration — authorization | `AdministracionFiscal`: Admin 200; Supervisor, Vendedor, Root 403. Fiscal emission under `OperacionDePos`: Vendedor 200, Root 403. `SuperficieDeAutorizacionTests` green | One test per role |
| Integration — read-model honesty | Every positional field of `CertificadoFiscalDto` and the fiscal comprobante response read back at least once with **pairwise-distinct** values (rule 12b); a sibling row of the same tenant seeded on every listing test (rule 12c) | `mutation-proof-tests` 12 |
| **Structural only (honest limits)** | Four properties cannot be observed dynamically in 19a and are asserted structurally, with the gap named: (a) **no real ARCA server is ever contacted** — the fixtures are a transcription, so a manual revision or a transcription error is invisible until 19b, whose first task is the fixture-vs-reality diff; (b) **no key material in the repository** — a repo-wide scan for PEM/PFX/`PRIVATE KEY` markers under `src/` and `tests/`; (c) **no real ARCA hostname as a default** — a scan of shipped configuration; (d) **`SobreSoap` is the only SOAP file** — an `rg` over `src/`. And **(e) D12's gap**: a fiscal emission writes **zero** rows in `movimientos_stock`, `pagos_comprobante` and `movimientos_cuenta_corriente` — asserted **as the known 19c gap**, so 19c's addition turns this test red on purpose instead of discovering the hole | Rule 13's posture: when a property cannot be observed dynamically, assert it structurally and **name** the gap |
| Exempt | Any behaviour that requires a real WSAA/WSFE server; the `NO_RESP` mapping; `FEParamGet*` catalogue values — all deferred to 19b with the reason recorded | — |

## Mutation targets

`mutation-proof-tests`: name the clause, apply the mutation, watch the named test fail, revert, record
the evidence in the PR body. **76 numbered targets** (23 · 13 · 15 · 12 · 13 across the five slices,
plus target 76 which is cross-slice). **S** = *structural* (the net is a
file/state/definition assertion, not a runtime behaviour) — recorded as such, never dressed up as a
runtime kill.

| # | Slice | Clause | Mutation | Test that MUST fail |
|---|---|---|---|---|
| 1 | 1 | `CREATE TYPE` value order = lifecycle, for **both** enums | restore EF's alphabetical order | the enum-order test: C# member index ↔ `pg_enum.enumsortorder` |
| 2 **S** | 1 | Zero `ALTER TYPE … ADD VALUE` | add one | the migration-source scan (verify criterion 2) |
| 3 **S** | 1 | The 8 index **definitions** | change any filter/column/order, one at a time | the by-definition `pg_indexes` comparison (8 sub-mutations, count = 8) |
| 4 | 1 | CHECK 1 `ck_puntos_venta_numero_fiscal_rango` | widen to `>= 0` | the `numero_fiscal = 0` and `= 100000` writes ⇒ `23514` |
| 5 | 1 | CHECK 2 `…fiscal_coherente` — **four conjuncts**: all-four-NULL · `(cae IS NULL) = (cae_vencimiento IS NULL)` · approval **iff** CAE · non-fiscal carries none | delete each conjunct, one at a time | four writes, one per conjunct: CAE without expiry; `rechazado` with a CAE; `aprobado` without a CAE; `cae` set with `resultado_fiscal` NULL |
| 6 | 1 | CHECK 3 `cae ~ '^[0-9]{14}$'` | drop the anchors | a 13-digit and an alphanumeric CAE |
| 7 | 1 | CHECK 4 `vigencia_hasta > vigencia_desde` | `>=` | equal-timestamps write |
| 8 | 1 | CHECK 5 `cuit_titular ~ '^[0-9]{11}$'` | drop | a 10-digit CUIT |
| 9 | 1 | CHECK 6 GCM sizes — **three conjuncts** (`nonce = 12`, `tag = 16`, `ciphertext > 0`) | delete each | three writes: 11-byte nonce, 15-byte tag, empty ciphertext |
| 10 | 1 | CHECK 7 `proximo_numero` range, `ultimo_autorizado_arca` allowing **0** | forbid `0` / widen the upper bound | the `0` write must **succeed**; `100000000` must fail |
| 11 | 1 | CHECK 8 `(ultimo_autorizado_arca IS NULL) = (sincronizado_en IS NULL)` | drop | either half written alone |
| 12 | 1 | `ux_puntos_venta_numero_fiscal` UNIQUE **and** its `WHERE numero_fiscal IS NOT NULL` | drop `IsUnique` / drop the filter | duplicate fiscal number ⇒ `23505`; **and** two `NULL`s must still be accepted (the filter's own kill) |
| 13 | 1 | `ux_certificados_fiscales_activo` and its two filter conjuncts | drop each | a second active certificate ⇒ `23505`; a **soft-deleted** twin must be accepted; an inactive twin must be accepted |
| 14 | 1 | RLS `ENABLE` + `FORCE` on `certificados_fiscales` | drop `FORCE` / the policy | the cross-tenant read **and** write pair on `ways_app` |
| 15 | 1 | RLS on `numeraciones_fiscales` | idem | idem |
| 16 | 1 | `AplicarFiltroDeTenantEnNumeracionFiscal` (hand-written — the entity is not `EntidadTenant`) | delete it | a tenant-B context reading tenant A's counter through EF |
| 17 | 1 | `RechazarEscriturasDeNumeracionFiscal` | delete it | a `SaveChangesAsync` over a tracked `NumeracionFiscal` must throw |
| 18 | 1 | DS1/DS2/DS3 (migration net) | delete one statement | its own already-migrated-DB test |
| 19 | 1 | The three seed nets (`CodigoAfip` on the `…Base` arrays) | delete one field | its own fresh-DB test — **each net independently** |
| 20 | 1 | `Exento` / `No gravado` stay `NULL` | add them to DS3 | the still-NULL assertion |
| 21 **S** | 1 | `ix_empresas_condicion_fiscal` is **simple** | make it `(id_tenant, id_condicion_fiscal)` | the definition test (the stage-14 amendment trap) |
| 22 **S** | 1 | `Down()` is a true inverse, and its data reverts are doubly guarded | drop a `Down` statement / widen `WHERE codigo_afip = <value>` | `Up → Down → Up` + `has-pending-model-changes` + the pre-state comparison |
| 23 **S** | 1 | `NumeracionFiscalConfiguration`'s explicit snake_case index names | remove `HasDatabaseName` | the definition test (PascalCase convention trap, `:44-49`) |
| 24 | 2 | TRA element names and order (`uniqueId`, `generationTime`, `expirationTime`, `service`) | reorder / rename one | the TRA golden |
| 25 | 2 | `generationTime = Ahora − 10 min`, `expirationTime = Ahora + 10 min`, from `IRelojDelSistema` | use `DateTimeOffset.UtcNow` / change the window | the golden under `RelojFijo` (ambient time desynchronises it) |
| 26 | 2 | `uniqueId` tiebreak | drop the `Interlocked` increment | two TRAs generated in the same clock tick must differ |
| 27 | 2 | `CmsSigner` `SHA-256` + `EndCertOnly` | `SHA-1` / `WholeChain` | the CMS structure test (digest OID; certificate count = 1) |
| 28 | 2 | The `LoginCms` envelope: namespace URI, `soapenv` prefix, `in0` element, `SOAPAction: ""` | change any one | the envelope golden, byte-for-byte |
| 29 **S** | 2 | `SobreSoap` is the only SOAP-naming file in `src/` | name a SOAP namespace in `ClienteWsaa` | the `rg` scan |
| 30 | 2 | `SaveOptions.DisableFormatting` + `XDeclaration("1.0","UTF-8",null)` | allow indentation / drop the declaration | every golden (whitespace **is** the contract) |
| 31 | 2 | The TA cache hit | always call `LoginCms` | a second emission within TTL issues **zero** extra `LoginCms` (spy count) |
| 32 | 2 | `MargenDeSeguridad = 10 min` boundary | change the margin / drop it | the `−1 s` / exact-boundary pair under `RelojQueAvanza` |
| 33 | 2 | Per-key single-flight (`SemaphoreSlim`) | remove it | N concurrent cold asks ⇒ exactly one `LoginCms` |
| 34 | 2 | The WSAA fault taxonomy arms (500/501/502/600/601/602) | collapse two arms | one test per code, asserting the **domain code**, not the HTTP status alone |
| 35 **S** | 2 | No PEM/PFX/private-key material under `src/` or `tests/` | commit a `.pfx` | the repository scan (verify criterion 7) |
| 36 | 2 | D7's PKCS#12 re-load before signing | sign with the raw `CreateSelfSigned` result | the signing test on Windows (recorded as a platform-conditional kill) |
| 37 | 3 | The `FECAESolicitar` envelope: namespace, `SOAPAction`, `Auth`/`FeCabReq`/`FeDetReq` order | reorder / rename / drop a prefix | the request golden, byte-for-byte |
| 38 | 3 | Money `"0.00"` + `InvariantCulture`, `CbteFch` `yyyyMMdd`, `MonId = "PES"`, `MonCotiz = 1` | use current culture / `yyyy-MM-dd` | the golden (a `,` decimal separator is a wire defect) |
| 39 | 3 | Optional elements omitted, never emitted empty | emit `<FchServDesde/>` | the golden for a `Concepto = 1` invoice |
| 40 | 3 | `Iva[]` excludes `Exento`/`No gravado` | include them | the mixed-invoice test: `Iva[]` has exactly two entries |
| 41 | 3 | `ImpOpEx` ← exento, `ImpTotConc` ← no gravado | swap them | the mixed-invoice test asserting both fields with **distinct** amounts |
| 42 | 3 | The `GROUP BY` alícuota | one `AlicIva` per line | two lines of 21 % must collapse to **one** entry with summed `BaseImp`/`Importe` |
| 43 | 3 | `ImpTotal` identity | drop a term | the exact-sum assertion |
| 44 | 3 | D11's bucketing by `codigo_afip IS NULL` + name, not by `porcentaje == 0` | bucket by percentage | the `0%` alícuota must land in `Iva[]` with code 3, **not** in `ImpOpEx` |
| 45 | 3 | The `alicuota_sin_mapeo_afip` throw | bucket unknown NULL codes as exempt | a seeded NULL-coded alícuota must raise, not invoice |
| 46 | 3 | Three response states | fold `A + Observaciones` into `rechazado` (or into plain `aprobado`) | **two** kills: the observed approval writes a CAE **and** persists `observaciones_fiscales` |
| 47 | 3 | `EsTerminal` over both approvals | exclude `aprobado_con_observaciones` | the machine's transition table |
| 48 | 3 | `10016` ⇒ `numeracion_fiscal_desincronizada`, never auto-advance | auto-heal `proximo_numero` | the 10016 fixture: `proximo_numero` unchanged, 409 raised (D13) |
| 49 | 3 | WSFE `Errors[]` `600` ⇒ invalidate the TA + retry **once** | retry without invalidating / retry twice | the call log: `LoginCms`, `FECAESolicitar`, exactly one repeat |
| 50 | 3 | Backoff + circuit breaker bounds | unbounded retries / never open | the attempt-count test and the open-circuit test (**zero** requests while open) |
| 51 | 3 | `FECompUltimoAutorizado` empty series = `0` | map to `null` or `1` | the empty-series fixture |
| 52 | 4 | **U1 conjunct (a)** `id_punto_venta` | delete it | the sibling-PV test (rule 12c) |
| 53 | 4 | **U1 conjunct (b)** `codigo_afip` | delete it | the sibling-type test on the same PV |
| 54 | 4 | I1: a failed emission does not advance the series | commit the number before the round trip (the sibling's discipline) | the `rechazado` test: the number stays bound, `proximo_numero` consistent, no hole |
| 55 **S** | 4 | D1: `numeraciones_fiscales` is the **first** lock **and** the only existing-row lock of the fiscal transaction | move the assignment after another lock / add a turno guard | the `pg_locks` poll from a second connection — **both** halves asserted (rule 13) |
| 56 | 4 | **U3 conjuncts (a)(b)** | delete each | the sibling pair, asserting the neighbour's `ultimo_autorizado_arca` stays `NULL` |
| 57 | 4 | The AAD's four components (D5) | tamper with each, one at a time | four decryption-failure tests |
| 58 | 4 | The AAD **excludes** `id_clave_maestra` | add it | the rotation test: a re-encrypted row must still decrypt |
| 59 | 4 | No plaintext fallback on a missing/short master key (D6) | fall back / generate on boot | the missing-key test asserting the named error and that **nothing** was written |
| 60 **S** | 4 | `CryptographicOperations.ZeroMemory` on the decrypted buffer | remove it | the structural assertion that `UsarCertificadoAsync` clears its buffer in a `finally` |
| 61 | 4 | **U4 conjuncts (a)-(e)** | delete each | five kills: cross-tenant on `ways_app`; sibling empresa; sibling ambiente; already-inactive row (affected-row count); soft-deleted twin |
| 62 | 4 | The certificate DTO exposure clause | add `ClavePrivadaCifrada` to the DTO | the recursive **property-name** assertion over the serialized response |
| 63 | 4 | `AdministracionFiscal` is a **new** policy, Admin only | reuse `GestionDeCatalogo` / widen to Supervisor | the role matrix + the `Politicas.cs` count 11 → **12** |
| 64 | 5 | I4: the four gates run before any port is resolved | move the certificate check after the client call | the spy's **zero-request** assertion on all five gate paths |
| 65 | 5 | Each gate's own named 409, in order | collapse two into one code | four kills, one per gate, asserting the **code** |
| 66 | 5 | I2: `FECompConsultar` **first** on a non-definitive retry | retry directly | the call log: `FECompConsultar` precedes, exactly **one** `FECAESolicitar` across both attempts |
| 67 | 5 | I2: adoption of an existing CAE | re-solicit anyway | the *found* fixture: **zero** `FECAESolicitar`, the CAE written locally |
| 68 | 5 | **U2 conjuncts (a)(b)(c)** | delete each | four kills: sibling `pendiente`; cross-tenant on `ways_app`; already-`aprobado` direct call (I3); the TOCTOU rendezvous |
| 69 | 5 | `D4`'s `PermisoDeSolicitud` gate | give `SolicitarCaeAsync` a public constructor path | the structural assertion that the machine is the only producer |
| 70 | 5 | The letter resolver's first caller | hardcode `'B'` | RI→RI ⇒ `A`, RI→CF ⇒ `B`, end to end against the mocks |
| 71 | 5 | `NO_RESP` ⇒ `409 condicion_fiscal_receptor_no_mapeada` | map it to `5` | the NO_RESP receptor test: 409 and **zero** requests |
| 72 | 5 | The QR's 13 fields, `tipoCodAut = "E"`, the base64 and the URL prefix | drop a field / change the prefix | the hand-computed vector |
| 73 **S** | 5 | The POS guard is untouched | edit `ServicioDeVentas.cs:1162` | `git diff --exit-code` **and** the live `POST /api/ventas` with `FA` ⇒ 400 |
| 74 **S** | 5 | A non-fiscal sale issues zero extra SQL statements | add a fiscal read to the checkout | the `ContadorDeComandos` equality against `main` |
| 75 **S** | 5 | D12's declared gap | — | the zero-rows assertion over `movimientos_stock` / `pagos_comprobante` / `movimientos_cuenta_corriente`, **labelled as the 19c gap** |
| 76 **S** | 1-5 | No real ARCA hostname as a default | add `wswhomo.afip.gov.ar` to `appsettings.json` | the shipped-configuration scan (verify criterion 8) |

Rows 5, 9, 12, 13, 46, 52-53, 56, 57, 61, 65, 68 expand into more than one kill each; the table
numbers **clauses**, and the per-conjunct kills are enumerated inside the row so no neighbour is
covered by assumption (rule 3 v1.1).

## Slicing (5 PRs, stacked-to-main — the proposal's plan, ratified)

| # | Branch | Content | ~Lines | Depends on | Rollback |
|---|---|---|---|---|---|
| 1 | `feat/stage19a-slice1-schema-fiscal` | The migration (§A-G) with its `Down()`, the two entities + two enums, 5 configurations, the hand-written tenant filter + write guard, RLS, the 10 error branches, the 3 data statements + 3 seed nets, index/CHECK/RLS/reversibility tests, docs 09/10. **Targets 1-23** | ~450 | — | `dotnet ef migrations remove` or `Down()`. **Both `CREATE TYPE`s drop cleanly** — no stranded enum value, unlike etapas 12/17. No operational row is modified except the three catalogues' `codigo_afip`, reverted by exact value |
| 2 | `feat/stage19a-slice2-wsaa` | `SobreSoap`, `GeneradorDeTra`, `FirmanteCms`, `ClienteWsaa`, the in-memory TA cache with single-flight, `CertificadoDePrueba`, the WSAA fixtures + `REVISION.md`. **Targets 24-36** | ~430 | 1 (test host only) | `git revert`: no consumer exists until slice 3 |
| 3 | `feat/stage19a-slice3-wsfe-y-cae` | `ClienteWsfe` (`FECAESolicitar` / `FECompConsultar` / `FECompUltimoAutorizado` / `FEParamGet*`), the request mapper, `ComposicionDeTotalesFiscales`, `MaquinaDeEstadosCae` + `PermisoDeSolicitud`, the three response fixtures, the error taxonomy, backoff + circuit breaker. **Targets 37-51** | ~480 | 2 | `git revert`: still no caller — the use case lands in slice 5 |
| 4 | `feat/stage19a-slice4-numeracion-y-certificados` | `AsignadorDeNumeroFiscal` + I1 + the concurrency and `pg_locks` tests, D13 reconciliation, `CifradoDeClavesFiscales`, `ServicioDeCertificados`, the `AdministracionFiscal` policy and its three ABM routes. **Targets 52-63** | ~460 | 1 | `git revert`: `numeraciones_fiscales` is empty in production (nothing was ever emitted) and `certificados_fiscales` has no row until an owner uploads one; the policy is one line |
| 5 | `feat/stage19a-slice5-emision-y-qr` | `ServicioDeFacturacionFiscal` end to end against mocks (the four gates, the letter resolver's first caller, I2/I3/I4), `PayloadQrFiscal`, the two emission routes, doc 11. **Targets 64-75**, plus the cross-slice **76** | ~420 | 1, 2, 3, 4 | `git revert` removes two routes; nothing else calls them |

Merge order `1 → 2 → 3 → 4 → 5`. Slices 2 and 4 depend only on slice 1 and may interleave; slice 3
needs slice 2's fixture harness.

**Decision needed before apply: No** (the DB gate is **RATIFICADO**, `state.yaml:9-139`) ·
**Chained PRs recommended: Yes** (`chain_strategy: stacked-to-main`, one `judgment-day` round per
slice) · **400-line budget risk: High** — all five slices sit at or above the cap on the estimate
alone, and the calibration of etapas 13-17 (1.5-3× the naive estimate) applies with **every** inflator
present at once: schema, concurrency, cryptography and a wire protocol.

**Pre-approved degradation**, in the proposal's priority order: (1) slice 3 splits into `3a`
(client + envelope + `FEParamGet*` + fixtures, targets 37-39 + 46-51) and `3b` (totals + machine,
targets 40-45); (2) slice 4 splits into `4a` (numbering + I1, targets 52-56) and `4b` (certificates +
policy + ABM, targets 57-63); (3) slice 1 splits into `1a` (the two new tables + RLS, targets 9-11 +
13-17 + 23) and `1b` (the three `ALTER`s + data statements + seed nets, the rest).
**Never degraded**: I1 and I2 with their tests, the untouched POS guard, the AAD binding, the
no-key-material scan, and D1's two-sided lock proof. A duplicated legal document, a dead fiscal
series or a leaked private key is worse than no sub-stage at all.

## Binding verify criteria

The eight from the ratified gate, plus five this design adds.

1. Exactly **one** new migration named `FiscalArcaEtapa19a`; `dotnet ef migrations
   has-pending-model-changes` **clean**; it is the **last** migration of the sub-stage.
2. **Zero `ALTER TYPE … ADD VALUE`** anywhere in the migration.
3. New index count = **8**, verified **by definition** against `pg_indexes` (definition string, not
   name), including both partial uniques and both FK-support indexes.
4. New CHECK count = **8**, each with a mutation-proof test that violates it and observes `23514`.
5. RLS present and `FORCE`d on **both** new tables, with the cross-tenant read **and** write pair on
   the `ways_app` connection.
6. A non-fiscal sale is byte-identical to `main`: `git diff --exit-code
   src/Ways.Application/Ventas/ServicioDeVentas.cs` clean, the same EF command count, and
   `POST /api/ventas` with a fiscal code still `400`.
7. **Zero** PEM/PFX/private-key material under `src/` or `tests/`, asserted by a repository scan.
8. No real ARCA hostname appears as a default in any configuration file merged to `main`.
9. **`Down()` is a true inverse**: `Up → Down → Up` succeeds, `has-pending-model-changes` is clean
   after each leg, and after `Down()` the three catalogues' `codigo_afip` are `NULL` again with no
   other column moved.
10. **`SobreSoap.cs` is the only file under `src/` that names SOAP or a SOAP namespace** (`rg`).
11. **`src/Ways.Api/Seguridad/Politicas.cs` gains exactly one `public const`**: 11 → **12**, and
    `ManejadorDeErrores.cs` gains exactly **10** branches (2 × `23505`, 8 × `23514`).
12. **D1's lock proof, both halves**: `pg_locks` shows `numeraciones_fiscales` held by the fiscal
    transaction and shows **no** lock on `turnos_caja`, `stock`, `stock_lotes` or `clientes`.
13. Mutation evidence recorded in the PR body for **every** row of the table above belonging to that
    slice; **S** rows record the file/state/definition assertion instead of a runtime failure, and say
    so. Domain / Application / Integration suites green.

## Threat Matrix

N/A — 19a changes no routing beyond five additive authenticated routes under existing or new
policies, runs no shell command, spawns no subprocess, automates no VCS/PR action, classifies no
executable file and integrates with no external process. Its one genuinely new boundary is **outbound
HTTP to a third party**, and that boundary is closed by construction in 19a: invariant I4 (no
certificate ⇒ zero bytes), verify criterion 8 (no real hostname as a default) and the
`HttpMessageHandler` spy assertions. Its real risk surfaces — an irreversible legal document, a dead
fiscal series, leaked key material, a wrong `codigo_afip` — are covered by the mutation-target table,
which **is** binding.

## Migration / Rollout

One reversible migration (slice 1) and four code-only slices. Rollout is five merges. `git revert` of
all five plus `Down()` leaves `main` behaviourally identical, and because no fiscal document can have
been issued (I4, structural), **there is no fiscal history to repair** — the property that makes the
certificate, and not a feature flag, the correct gate.

## Open questions / tensions with the proposal

- [ ] **T1 — the fiscal write plan is `comprobante + items` only (D12).** The proposal's Affected
      Areas names no stock, payment or cuenta-corriente file, marks `ServicioDeVentas` untouched, and
      budgets slice 5 at ~420 lines. `FA`/`FB`/`FC` nevertheless carry `afecta_stock = true` in the
      catalogue, so a fiscal comprobante emitted in 19a would be inconsistent with its own type — which
      is **safe only because I4 makes such a row unreachable in production**. **Binding 19c contract**:
      the fiscal emission gains the stock, payment and cuenta-corriente loops together with the screen
      that supplies them, and target 75's zero-rows test is the trip-wire that must go red then. If
      `sdd-spec` states a full sale write plan for 19a, the two disagree; the proposal's letter governs
      and `sdd-tasks` reconciles.
- [ ] **T2 — I1's release path is not shipped.** The proposal's I1 says a number *"is released only
      when it is the top of its series, by an explicit operator action"*, and the API surface contains
      **no** such route. 19a therefore ships the half of I1 that is enforceable — the number stays
      bound to its `pendiente` comprobante and is never silently reused — and the operator release is
      registered for **19c**, alongside the durable queue that would drain the same rows.
- [ ] **T3 — the WSAA fault codes are the proposal's numbering, not verified strings.** The
      specification publishes symbolic fault codes (`ns1:cms.sign.invalid`, `ns1:coe.alreadyAuthenticated`
      and siblings); the proposal names them `500/501/502/600/601/602`. The taxonomy table above uses the
      proposal's numbering and the fixtures transcribe it; **confirming the exact wire strings is a 19b
      task**, and the mapping is one fixture edit away from correct either way.
- [ ] **T4 — the goldens can only be as true as the transcription.** Decision 8's whole value rests on
      the fixture files matching `manual-desarrollador-ARCA-COMPG-v4-0.pdf` (rev. 15/01/2025) exactly.
      No test in 19a can detect a transcription error; 19b's first task is the fixture-vs-reality diff,
      and this design records the limitation rather than implying a coverage it does not have.
- [ ] **T5 — `certificado_pem` is absent from every DTO in 19a**, even though it is public material.
      It has no consumer until 19c's configuration screen, and the smallest honest surface is the one
      with nothing speculative in it (`dto-contract-honesty` rule 1). 19c adds it with its reader.
- [ ] **Deferred, unchanged**: `tickets_acceso_fiscal` (a 19b gate item, decision 10), the durable
      offline queue and CAEA (19c), the fiscal consolidation type with its writer (19c), libro IVA,
      the whole web surface, and the owner's reserved carryovers (the `importe` CHECK micro-gate, the
      `articulos_empresas` replace-set gap, `ways_owner`, `stage-13b`).
