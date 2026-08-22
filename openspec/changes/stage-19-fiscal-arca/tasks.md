# Tasks: Stage 19a — Fiscal ARCA, the core buildable without credentials

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2 240 across 5 slices (calibrated against stages 13-17: 1.5-3× the naive estimate) |
| 400-line budget risk | High — all five slices sit at or above the 400-line cap on the estimate alone |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 (schema) → PR 2 (WSAA) → PR 3 (WSFE+CAE) → PR 4 (numeración+certificados) → PR 5 (emisión+QR) |
| Delivery strategy | auto-chain |
| Chain strategy | stacked-to-main |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

The DB gate is **RATIFICADO** (`state.yaml:9-36`), so no owner decision blocks slice 1. Three
pre-approved degradation points exist (named in each slice's header) if a slice overflows its
estimate; none of them may touch invariants I1/I2, the untouched POS guard, the AAD binding, or the
no-key-material scan (design.md:576-578).

## Reconciliaciones

1. **Capability naming — no drift found, proposal's table stands.** All eight `specs/*/spec.md`
   files use the proposal's exact names verbatim (`fiscal-arca`, `comprobante-fiscal`,
   `certificados-fiscales`, `numeracion-fiscal` as new; `comprobantes-venta`, `auxiliary-catalogs`,
   `tenant-organization`, `operacion-de-pos` as modified — `proposal.md:106-142`, confirmed against
   every spec's `## Requirements` header). `design.md` describes the same domains under the same
   vocabulary — `SobreSoap`/`ClienteWsaa`/`ClienteWsfe` under `fiscal-arca`'s protocol boundary,
   `MaquinaDeEstadosCae`/`ComposicionDeTotalesFiscales` under `comprobante-fiscal`,
   `CifradoDeClavesFiscales`/`ServicioDeCertificados` under `certificados-fiscales`,
   `AsignadorDeNumeroFiscal` under `numeracion-fiscal` — no alternate name is ever used for the same
   concept. **No reconciliation action required**; the proposal's Capabilities table governs.
2. **Counts — design's 76 numbered targets supersede the proposal's tentative slice plan.**
   `proposal.md:825-861` estimated 5 slices without a target count; `design.md:461-550,552-578`
   delivers the binding breakdown: **76 targets, 23·13·15·12·13 across the same 5 slices**. Adopted
   verbatim. Every target below is mapped to exactly one test task (verified: 23+13+15+12+13=76,
   none dropped, none duplicated — see the per-slice task lists).
3. **T1-T4 (design.md open tensions) placed at the slice that owns the concern.** **T1** (D12: the
   fiscal write plan is `comprobante + items` ONLY, target 75 is the trip-wire) is transcribed as a
   **BINDING WARNING** in Slice 5's header — it is the emission slice. **T2** (I1's operator-release
   path deferred to 19c) is annotated in Slice 4's header, where `AsignadorDeNumeroFiscal`/I1 live.
   **T3** (WSAA fault codes are the proposal's numbering, not verified wire strings) is annotated in
   Slice 2's header, the WSAA client slice. **T4** (goldens are only as true as the manual
   transcription) is annotated in **both** Slice 2 and Slice 3 headers, since design names both
   fixture sets under this limitation (`design.md:627-651`).
4. **U1-U4 guarded-`UPDATE` conjunct enumeration (design.md:420-433) transcribed to the slice that
   owns the statement, per `mutation-proof-tests` rule 3 v1.1 (up front, before any test is
   written).** U1 (`numeraciones_fiscales.proximo_numero`) and U3 (`…ultimo_autorizado_arca`) and U4
   (`certificados_fiscales.activo`) go into **Slice 4**'s header (`AsignadorDeNumeroFiscal` /
   `ServicioDeCertificados`). U2 (`comprobantes_venta` CAE write) goes into **Slice 5**'s header
   (`ServicioDeFacturacionFiscal`'s emission transaction, data-flow step 6). Each conjunct is paired
   with its own task-level kill in the corresponding slice's test list.
5. **DB gate double field — already reconciled, cited as resolved, no action taken.**
   `state.yaml:9-36` carries both `db_gate: APROBADO — RATIFICADO POR EL ORQUESTADOR` and
   `db_gate_approval`, and both fields cite the **same** five independently-verified facts and the
   **same** binding criteria. This phase treats the gate as closed and does not reopen it.
6. **`Contratos.cs` and the four port interfaces — an inferred per-slice decomposition, registered
   as such.** `design.md`'s File changes table (`:358-391`) lists `Contratos.cs` and
   `{IClienteWsaa,IClienteWsfe,IAlmacenDeClavesFiscales,IRepositorioDeTicketDeAcceso}.cs` as single
   `Create` items with no slice-level split. This phase infers the natural decomposition from each
   slice's own first consumer — `IClienteWsaa`/`TicketDeAcceso`/`IRepositorioDeTicketDeAcceso` →
   Slice 2; `IClienteWsfe`/`SolicitudDeCae`/`RespuestaCae`/`ClaveDeSerie` → Slice 3;
   `IAlmacenDeClavesFiscales`/`CertificadoFiscalDto` → Slice 4 — each later slice **extending** the
   same `Contratos.cs` file rather than redefining it. Registered per the stage-17 process-rule
   precedent (every deviation registered, never left to verify-phase archaeology).

## Binding Verify Criteria (all slices)

Carried verbatim from `design.md:584-607` (the 8 ratified in the gate plus 5 this design adds).
None of these may be relaxed by any slice.

1. Exactly **one** new migration named `FiscalArcaEtapa19a`, under
   `src/Ways.Infrastructure/Persistencia/Migraciones/`; `has-pending-model-changes` clean; it is the
   **last** migration of the sub-stage.
2. **Zero `ALTER TYPE … ADD VALUE`** anywhere in the migration.
3. New index count = **8**, verified **by definition** against `pg_indexes` (definition string, not
   name).
4. New CHECK count = **8**, each with a mutation-proof test observing `23514`.
5. RLS present and `FORCE`d on **both** new tables, cross-tenant read **and** write pair on
   `ways_app`.
6. **A non-fiscal sale is byte-identical to `main`** — `git diff --exit-code` clean on
   `ServicioDeVentas.cs`, same EF command count, `POST /api/ventas` with a fiscal code still `400`.
7. **Zero** PEM/PFX/private-key material under `src/` or `tests/`, repository scan.
8. No real ARCA hostname as a default in any configuration merged to `main`.
9. `Down()` is a **true inverse**: `Up → Down → Up` clean, `has-pending-model-changes` clean each
   leg, `codigo_afip` reverted with no other column moved.
10. **`SobreSoap.cs` is the only file under `src/`** that names SOAP or a SOAP namespace (`rg`).
11. `Politicas.cs` gains exactly **one** `public const` (11 → **12**); `ManejadorDeErrores.cs` gains
    exactly **10** branches (2 × `23505`, 8 × `23514`).
12. **D1's lock proof, both halves**: `pg_locks` shows `numeraciones_fiscales` held by the fiscal
    transaction and shows **no** lock on `turnos_caja`, `stock`, `stock_lotes` or `clientes`.
13. Mutation evidence recorded in the PR body for every row of the mutation-target table belonging
    to that slice; **S** rows record the file/state/definition assertion. Domain / Application /
    Integration suites green.

## Suggested Work Units

Merge order `1 → 2 → 3 → 4 → 5`. Slices 2 and 4 depend only on slice 1 and may interleave; slice 3
needs slice 2's fixture harness (`design.md:562-563`).

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | Schema fiscal + RLS + 10 error branches + docs 09/10 | PR 1 | `dotnet test --filter FullyQualifiedName~FiscalSchemaTests\|FullyQualifiedName~ManejadorDeErroresFiscalTests` | Testcontainers Postgres 17, `ways_app` NOSUPERUSER NOBYPASSRLS | `dotnet ef migrations remove` / `Down()` — both `CREATE TYPE`s drop cleanly, no operational row modified except `codigo_afip` |
| 2 | WSAA client + TRA/CMS + TA cache + fixtures | PR 2 | `dotnet test --filter FullyQualifiedName~ClienteWsaaTests\|FullyQualifiedName~SobreSoapTests\|FullyQualifiedName~GeneradorDeTraTests` | N/A — pure unit + byte-for-byte golden-file comparison, no container | `git revert`: no consumer exists until slice 3 |
| 3 | WSFE client + totals composition + CAE state machine | PR 3 | `dotnet test --filter FullyQualifiedName~ClienteWsfeTests\|FullyQualifiedName~ComposicionDeTotalesFiscalesTests\|FullyQualifiedName~MaquinaDeEstadosCaeTests` | N/A — pure unit + golden-file comparison | `git revert`: still no caller, the use case lands in slice 5 |
| 4 | Fiscal numbering (I1) + certificate encryption + `AdministracionFiscal` ABM | PR 4 | `dotnet test --filter FullyQualifiedName~AsignadorDeNumeroFiscalTests\|FullyQualifiedName~CifradoDeClavesFiscalesTests\|FullyQualifiedName~ServicioDeCertificadosTests` | Real Postgres, forced rendezvous on `AsignarSiguienteAsync`, `pg_locks` polled from a second connection | `git revert`: `numeraciones_fiscales`/`certificados_fiscales` empty in production, policy is one line |
| 5 | Fiscal emission end-to-end against mocks + QR + doc 11 | PR 5 | `dotnet test --filter FullyQualifiedName~ServicioDeFacturacionFiscalTests\|FullyQualifiedName~PayloadQrFiscalTests` | Real Postgres + `HttpMessageHandler` spy against WSAA/WSFE fixtures, forced rendezvous (two retries) | `git revert` removes two routes; nothing else calls them |

**Pre-approved degradation**, priority order (`design.md:571-578`): (1) slice 3 splits into `3a`
(client+envelope+`FEParamGet*`+fixtures, targets 37-39+46-51) / `3b` (totals+machine, targets
40-45); (2) slice 4 splits into `4a` (numbering+I1, targets 52-56) / `4b` (certificates+policy+ABM,
targets 57-63); (3) slice 1 splits into `1a` (two new tables+RLS, targets 9-11+13-17+23) / `1b`
(three `ALTER`s+data statements+seed nets, the rest). **Never degraded**: I1 and I2 with their
tests, the untouched POS guard, the AAD binding, the no-key-material scan, D1's two-sided lock
proof.

---

## Slice 1: Schema fiscal completo + RLS + ramas de error + docs 09/10 (PR 1)

**Branch**: `feat/stage19a-slice1-schema-fiscal`. **Start**: `main`. **Finish**: two `CREATE TYPE`
enums + two new tables + three additive `ALTER`s exist with standard RLS, 8 new indexes, 8 new
CHECKs, 10 `ManejadorDeErrores` branches, 3 data statements + 3 seed nets, docs 09/10 carry the
Etapa 19a scoping block. No write path calls anything yet (slices 2-5). **Rollback**: `dotnet ef
migrations remove` / `Down()` — **both `CREATE TYPE`s drop cleanly**, no stranded enum value
(unlike etapas 12/17); no operational row is modified except the three catalogues' `codigo_afip`,
reverted by exact value (proposal.md:758-770). **Budget note**: pre-authorized split `1a`/`1b`
above if this slice overflows. **Skills required**: `mutation-proof-tests` v1.1 (targets 1-23; no
guarded-`UPDATE` conjunct set in this slice — U1-U4 belong to slices 4-5), `db-error-backstops`
(the full **10** new branches: 2 × `23505` + 8 × `23514`), `work-unit-commits`. **Done** = tests
green + `judgment-day` clean round + PR merged.

- [x] 1.1 Migration `FiscalArcaEtapa19a`, statement 01: `AlterDatabase()` emitting `CREATE TYPE
  ambiente_fiscal ('homologacion','produccion')` and `CREATE TYPE resultado_fiscal
  ('pendiente','aprobado','aprobado_con_observaciones','rechazado')`, lifecycle order hand-corrected
  (EF serializes alphabetically), **zero** `ALTER TYPE … ADD VALUE`. *(design.md:84-93,
  proposal.md:490-501)*
- [x] 1.2 Same migration §B: `AddColumn empresas.id_condicion_fiscal integer NULL` + FK1
  `fk_empresas_condicion_fiscal` (simple, RESTRICT) + Index 1 `ix_empresas_condicion_fiscal`
  (simple, **not** `id_tenant`-led — the stage-14 amendment trap). *(design.md:94-96,
  proposal.md:503-521)*
- [x] 1.3 Same migration §C: `AddColumn puntos_venta.numero_fiscal integer NULL` + CHECK 1
  `ck_puntos_venta_numero_fiscal_rango` (1..99999) + Index 2 `ux_puntos_venta_numero_fiscal`
  UNIQUE PARTIAL `WHERE numero_fiscal IS NOT NULL`. *(design.md:97-99, proposal.md:522-533)*
- [x] 1.4 Same migration §D: `AddColumn ×4` on `comprobantes_venta` (`cae`, `cae_vencimiento`,
  `resultado_fiscal`, `observaciones_fiscales`) + CHECK 2 `ck_comprobantes_venta_fiscal_coherente`
  (4 conjuncts) + CHECK 3 `ck_comprobantes_venta_cae_digitos` + Index 3
  `ix_comprobantes_venta_fiscal_pendientes` PARTIAL `WHERE resultado_fiscal = 'pendiente'`.
  *(design.md:100-103, proposal.md:535-557)*
- [x] 1.5 Same migration §E: `CreateTable certificados_fiscales` — 18 columns, PK, FK2 (tenant
  simple), FK3 (empresa composite against `puntos_venta`'s AK), CHECK 4 `…vigencia`, CHECK 5
  `…cuit`, CHECK 6 GCM sizes (3 conjuncts) inline. *(design.md:104, proposal.md:559-606)*
- [x] 1.6 Same migration: `CreateIndex ×3` on `certificados_fiscales` — `ix_…_tenant`,
  `ix_…_empresa`, `ux_certificados_fiscales_activo` UNIQUE PARTIAL (2 filter conjuncts: `activo AND
  deleted_at IS NULL`). *(design.md:105, proposal.md:599-601)*
- [x] 1.7 Same migration §F: `CreateTable numeraciones_fiscales` — 6 columns, PK
  `(id_punto_venta, codigo_afip)`, FK4 (tenant), FK5 (punto_venta composite, **mirrored from**
  `NumeracionComprobanteConfiguration`), CHECK 7 `…rango` (0 legal for "serie sin usar"), CHECK 8
  `…sincronizacion` inline. *(design.md:106, proposal.md:607-641, fact 2:22-27)*
- [x] 1.8 Same migration: `CreateIndex ×2` on `numeraciones_fiscales` — `ix_…_tenant`,
  `ix_…_punto_venta` (not covered by the PK, whose second column is `codigo_afip`).
  *(design.md:107)*
- [x] 1.9 Same migration §G: `Sql` DS1 `tipos_comprobante` (7 rows, `WHERE codigo_afip IS NULL`),
  DS2 `condiciones_fiscales` (5 rows), DS3 `alicuotas_iva` (4 rows) — all idempotent, **zero** rows
  inserted/activated/deactivated. *(design.md:108-110, proposal.md:649-670)*
- [x] 1.10 Same migration: `HabilitarRlsDeTenant` on `certificados_fiscales` and
  `numeraciones_fiscales`, **LAST** in `Up()`. *(design.md:111-112)*
- [x] 1.11 Write `Down()` — exact inverse in reverse order: 3 doubly-guarded `UPDATE` reverts
  (DS3/DS2/DS1), `DropTable ×2`, `DropIndex`/`DropCheck`/`DropColumn ×4` on `comprobantes_venta`
  (**before** the `DROP TYPE`), same on `puntos_venta` and `empresas`, `AlterDatabase()` swap ⇒
  `DROP TYPE ×2`. *(design.md:122-145)*
- [x] 1.12 Create `src/Ways.Domain/Fiscal/{ResultadoFiscal,AmbienteFiscal}.cs` — member order =
  lifecycle = `CREATE TYPE` order. *(design.md:363)*
- [x] 1.13 Create `src/Ways.Domain/Fiscal/{CertificadoFiscal,NumeracionFiscal}.cs` — `EntidadBase`
  **yes**/**no** respectively. *(design.md:364, proposal.md §E/§F)*
- [x] 1.14 Create `Configuraciones/CertificadoFiscalConfiguration.cs` — 18 columns, PK, 2 FKs, 3
  CHECKs, 3 indexes, explicit snake_case names. *(design.md:170)*
- [x] 1.15 Create `Configuraciones/NumeracionFiscalConfiguration.cs` — line-for-line mirror of
  `NumeracionComprobanteConfiguration`: PK, `IdTenant` non-key, `ProximoNumero`
  `HasDefaultValue(1L)`, both index names hand-written, composite FK mirrored from the sibling.
  *(design.md:171, fact 2:22-27)*
- [x] 1.16 Modify `Configuraciones/EmpresaConfiguration.cs` — `id_condicion_fiscal` property +
  simple `HasOne` + named index. *(design.md:167)*
- [x] 1.17 Modify `Configuraciones/PuntoVentaConfiguration.cs` — `numero_fiscal` + CHECK + named
  filtered unique index. *(design.md:168)*
- [x] 1.18 Modify `Configuraciones/ComprobanteVentaConfiguration.cs` — 4 properties (`jsonb`
  converter for `observaciones_fiscales`, the `Auditoria` precedent), 2 CHECKs, the partial index.
  *(design.md:169)*
- [x] 1.19 Modify `WaysDbContext.cs` — `DbSet<CertificadoFiscal>` (exposed in `IWaysDbContext`),
  `DbSet<NumeracionFiscal>` **not** exposed (sibling criterion), `AplicarFiltroDeTenantEnNumeracionFiscal`,
  `RechazarEscriturasDeNumeracionFiscal`. **DEVIATION (registered, not silent)**: the two enum
  registrations use `npgsql.MapEnum<T>()` in `WaysDbContextFactory.cs`/`DependencyInjection.cs`
  (task 1.20), never `HasPostgresEnum` in `OnModelCreating` — `WaysDbContext.cs:210-213`'s own
  comment states this is the project's established convention for every prior enum
  (`estado_usuario`/`estado_tenant`/…): declaring an enum in both places would generate the type
  twice in the migration. `design.md:172`'s "two HasPostgresEnum registrations" line does not
  match the codebase's actual mechanism. *(design.md:172)*
- [x] 1.20 Modify `WaysDbContextFactory.cs` and `DependencyInjection.cs` —
  `MapEnum<ResultadoFiscal>`/`MapEnum<AmbienteFiscal>` in both builders. Also added to the test
  fixture's own three `MapEnum` blocks (`WaysApiFixture.cs`) — not listed in design.md's file
  table but required for the shared test host to boot against the new schema (same convention
  every prior stage's migration followed).
- [x] 1.21 Modify `InicializadorDeBaseDeDatos.cs` — three seed nets: `CodigoAfip` field on
  `TiposComprobanteBase`/`CondicionesFiscalesBase`/`AlicuotasIvaBase`, each net tested
  **independently**. *(proposal.md:672-676)*
- [x] 1.22 Modify `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` — 2 exact-name `23505` branches
  (`ux_puntos_venta_numero_fiscal`, `ux_certificados_fiscales_activo`) + 8 exact-name `23514`
  branches (CHECKs 1-8), each its own named domain error. *(proposal.md:680-682)*
- [x] 1.23 Modify `docs/09-multi-tenancy.md` — `certificados_fiscales`'s documented scoping
  deviation (`id_empresa NOT NULL`) vs. the catálogo shape. *(proposal.md decision 5)*
- [x] 1.24 Modify `docs/10-modelo-de-datos.md` — scoping table, §4-adjacent subsections, "Estado
  (Etapa 19a)" header **OPENED** (closes at slice 5, regla 19).
- [x] 1.25 [P] Domain unit: enum-order test — C# member index ↔ `pg_enum.enumsortorder`, both
  enums. *(target 1)*
- [x] 1.26 **[S]** Migration-source scan: zero `ALTER TYPE … ADD VALUE` in the file. *(target 2)*
- [x] 1.27 [P] `pg_indexes` by-definition comparison — all 8 new index definitions (8
  sub-mutations, count = 8). *(target 3, design.md:147-161)*
- [x] 1.28 [P] Raw-insert CHECK 1 boundary — `numero_fiscal = 0` and `= 100000` ⇒ `23514`.
  *(target 4)*
- [x] 1.29 [P] Raw-insert CHECK 2 — four conjunct-killing writes: CAE without expiry; `rechazado`
  with a CAE; `aprobado` without a CAE; `cae` set with `resultado_fiscal` NULL. *(target 5)*
- [x] 1.30 [P] Raw-insert CHECK 3 — a 13-digit and an alphanumeric CAE. *(target 6)*
- [x] 1.31 [P] Raw-insert CHECK 4 — equal `vigencia_hasta`/`vigencia_desde`. *(target 7)*
- [x] 1.32 [P] Raw-insert CHECK 5 — a 10-digit CUIT. *(target 8)*
- [x] 1.33 [P] Raw-insert CHECK 6 — three conjunct-killing writes: 11-byte nonce, 15-byte tag,
  empty ciphertext. *(target 9)*
- [x] 1.34 [P] Raw-insert CHECK 7 — `proximo_numero = 0` must succeed, `100000000` must fail.
  *(target 10)* **CORRECTION (registered, mutation-proof-tests rule 2 "run it, don't reason it")**:
  the CHECK itself is `proximo_numero BETWEEN 1 AND 99999999 AND (ultimo_autorizado_arca IS NULL
  OR ultimo_autorizado_arca BETWEEN 0 AND 99999999)` — `proximo_numero = 0` cannot succeed under
  that range (1 is the floor); "0 is legal" is exclusively true of `ultimo_autorizado_arca`
  ("serie sin usar", design.md's own row 10 wording). Tested both real behaviors:
  `UnUltimoAutorizadoArcaEnCeroEsLegalYNoViolaLaCheckDeRango` (succeeds) and
  `UnProximoNumeroEnCeroVIOLALaCheckDeRango` (fails, the literal wording's contradiction with the
  CHECK, confirmed by running it) plus the `100000000` over-range kill.
- [x] 1.35 [P] Raw-insert CHECK 8 — either half (`ultimo_autorizado_arca`/`sincronizado_en`)
  written alone. *(target 11)*
- [x] 1.36 [P] Duplicate-write test on `ux_puntos_venta_numero_fiscal` ⇒ `23505`; two `NULL`
  fiscal numbers still accepted (the filter's own kill). *(target 12)*
- [x] 1.37 [P] Duplicate-write test on `ux_certificados_fiscales_activo` ⇒ `23505`; a
  soft-deleted twin and an inactive twin must both be accepted. *(target 13)*
- [x] 1.38 [P] RLS cross-tenant read+write pair on `certificados_fiscales` via `ways_app`.
  *(target 14)*
- [x] 1.39 [P] RLS cross-tenant read+write pair on `numeraciones_fiscales` via `ways_app`.
  *(target 15)*
- [x] 1.40 [P] Delete `AplicarFiltroDeTenantEnNumeracionFiscal`, confirm a tenant-B context reads
  tenant A's counter through EF (mutation applied, red, reverted, green). *(target 16)*
- [x] 1.41 [P] Delete `RechazarEscriturasDeNumeracionFiscal`, confirm `SaveChangesAsync` over a
  tracked `NumeracionFiscal` throws. *(target 17)*
- [x] 1.42 [P] Each migration data statement (DS1/DS2/DS3) tested independently on an
  already-migrated DB — deleting one alone fails only its own test. *(target 18)*
- [x] 1.43 [P] Each seed net tested independently on a fresh DB — deleting one field alone fails
  only its own test. *(target 19)*
- [x] 1.44 [P] Assert `Exento`/`No gravado` remain `codigo_afip NULL` after DS3. *(target 20)*
- [x] 1.45 **[S]** `ix_empresas_condicion_fiscal` definition test — simple, not `(id_tenant,
  id_condicion_fiscal)`. *(target 21)*
- [x] 1.46 **[S]** `Up → Down → Up` clean, `has-pending-model-changes` clean at each leg,
  pre-state comparison of the three catalogues' `codigo_afip`. *(target 22)*
- [x] 1.47 **[S]** `NumeracionFiscalConfiguration`'s explicit `HasDatabaseName` definition test on
  both indexes. *(target 23)*
- [x] 1.48 Non-regression: full domain/application/integration suite green;
  `src/Ways.Application/Ventas/ServicioDeVentas.cs` untouched this slice (reasserted at slice 5).
- [x] 1.49 GATE GUARD — exactly one migration file `FiscalArcaEtapa19a`; `has-pending-model-changes`
  clean; zero `ALTER TYPE ADD VALUE`; index count = 8; CHECK count = 8 — all **by definition**.
  *(proposal.md §I criteria 1-4; Binding Verify Criteria 1-4)*
- [x] 1.50 Mutation evidence recorded in the PR body for targets 1-23 (**S** rows 2, 3, 21, 22, 23
  record the file/state/definition assertion, not a runtime failure). See "Work Unit Evidence"
  table below.
- [ ] 1.51 [ ] `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 1.52 [ ] Open PR #1 `feat/stage19a-slice1-schema-fiscal`, merge to `main` after a clean
  `judgment-day` round.

### Work Unit Evidence

| Evidence | Value |
|---|---|
| Mode | Standard (no `strict_tdd` config found; `mutation-proof-tests` v1.1 discipline followed instead — see per-target evidence below) |
| Focused test command | `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~FiscalSchemaTests\|FullyQualifiedName~ManejadorDeErroresFiscalTests"` → **63/63 passed** (0 failed), against a real Postgres 17 Testcontainer (Docker confirmed available in this environment) |
| Runtime harness | Testcontainers Postgres 17 via `WaysApiFixture`, `ways_app` role (RLS-scoped, non-superuser connection) for every cross-tenant/CHECK/UNIQUE test; three ad-hoc throwaway databases created/dropped via raw `CREATE DATABASE`/`DROP DATABASE ... WITH (FORCE)` for the data-statement (target 18), seed-net (target 19), and Up→Down→Up (target 22) tests — real migrations applied via `IMigrator`, not simulated |
| `dotnet build --no-incremental` | `src/Ways.Api`, `src/Ways.Infrastructure`, `tests/Ways.IntegrationTests`, `tests/Ways.Application.Tests` all built clean (0 warnings, 0 errors) after every production edit in this slice |
| `dotnet ef migrations has-pending-model-changes` | Clean — confirmed via CLI (`No changes have been made to the model since the last migration.`) after the hand-edited migration, and re-confirmed at runtime via `Database.HasPendingModelChanges()` in target 22's Up/Up-after-Down legs |
| Full suite (non-regression, task 1.48) | `Ways.Application.Tests`: **297/297 passed**. `Ways.IntegrationTests` (full suite, all pre-existing + new fiscal tests): **1674/1674 passed**, 0 failed, 11 m 46 s — real Postgres 17 Testcontainer. First run surfaced 16 real failures (`PendingModelChangesWarning` in 7 files whose own throwaway-DB `MapEnum` lists predate this slice's two new enums; a `42804` type mismatch in 14 more files with the same gap touching `comprobantes_venta`; a `42703 column "id_condicion_fiscal" does not exist` in `CostoCongeladoTests`/`CuentaCorrienteProveedorBackfillTests`, whose pre-Etapa-19a throwaway databases now diverge from the EF model's `empresas`/`puntos_venta` shape — the FIRST such divergence since Etapa 1). All fixed (21 test files touched: `MapEnum<ResultadoFiscal>`/`MapEnum<AmbienteFiscal>` added to every hand-curated builder; `empresas`/`puntos_venta` writes switched to raw SQL in the two pre-migration seeding helpers, mirroring the file's own established `comprobantes_venta`/`articulos` pattern for the exact same reason). Re-run clean per rule 17 (isolated re-run with the fix applied) |
| Rollback boundary | `dotnet ef migrations remove` / `Down()` — both `CREATE TYPE`s (`ambiente_fiscal`, `resultado_fiscal`) drop cleanly (zero `ALTER TYPE ADD VALUE` anywhere in the file, confirmed by target 2's source scan); no operational row is modified except the three catalogues' `codigo_afip`, reverted by the exact value `Up()` set (doubly-guarded `WHERE codigo = … AND codigo_afip = …`); confirmed empirically by target 22's Up→Down→Up test against a real throwaway database |

**Deviations registered (not silent):**
1. Task 1.19's `design.md:172` line "two `HasPostgresEnum` registrations" does not match the codebase's actual, uniform convention (`WaysDbContext.cs:210-213`'s own comment): every enum in this project is registered exclusively via `npgsql.MapEnum<T>()` in `WaysDbContextFactory.cs`/`DependencyInjection.cs` (task 1.20), never via `HasPostgresEnum` in `OnModelCreating` — declaring it in both places would emit the `CREATE TYPE` twice. Followed the established convention instead of the design line.
2. Target 10 / task 1.34's literal wording ("`proximo_numero = 0` must succeed") contradicts CHECK 7's own range (`proximo_numero BETWEEN 1 AND 99999999`) — confirmed by actually running the mutation (mutation-proof-tests rule 2: "run it, don't reason it"). The "0 is legal" clause applies exclusively to `ultimo_autorizado_arca` ("serie sin usar"). Both real behaviors are tested; see the task 1.34 note above.
3. `NO_RESP`'s `codigo_afip` mapping (proposal.md decision 11 flags this as "the one uncertainty" but never states the numeric value): mapped to **15** (RG 5616 "IVA No Alcanzado", the closest real ARCA condition to an unregistered/non-categorized receptor). This value is never read by any runtime emission decision in this slice — the slice-5 rejection of a `NO_RESP` receptor checks `condiciones_fiscales.Codigo`, not `CodigoAfip` — and is explicitly flagged in both `InicializadorDeBaseDeDatos.cs` and `docs/10-modelo-de-datos.md` for confirmation against `FEParamGetCondicionIvaReceptor` in 19b.
4. `WaysApiFixture.cs`'s three `MapEnum` blocks (test host DI) needed the two new enum registrations too — not listed in design.md's file table (which only names production `WaysDbContextFactory.cs`/`DependencyInjection.cs`) but required for the shared integration-test host to boot against the new schema, same convention every prior stage's slice-1 migration followed for this fixture.

---

## Slice 2: SobreSoap + TRA/CMS + ClienteWsaa + caché TA + certificado de prueba + fixtures WSAA (PR 2)

**Branch**: `feat/stage19a-slice2-wsaa`. **Start**: PR 1 merged (test host only). **Finish**:
`SobreSoap` + `GeneradorDeTra` + `FirmanteCms` (`SignedCms`) + `ClienteWsaa` + the in-memory TA
cache with single-flight + `CertificadoDePrueba` (runtime-generated, D7) + the WSAA fixtures +
`REVISION.md`; the `LoginCms` envelope golden matches byte-for-byte; TA margin boundary and fault
taxonomy tests green. **No consumer exists yet** — `ClienteWsaa` gets its first caller in slice 5.
**Rollback**: `git revert`; no file here has a consumer until slice 3 wires `ClienteWsaa` into the
fixture harness. **Skills required**: `mutation-proof-tests` v1.1 (targets 24-36; no guarded-`UPDATE`
conjunct set this slice), `dto-contract-honesty` (`TicketDeAcceso`/`ClaveDeTicket` carry no key
material), `work-unit-commits`.

**T3 (design.md:643-647)**: the WSAA fault codes (`500/501/502/600/601/602`) are the **proposal's**
numbering, not verified against the specification's symbolic wire strings
(`ns1:cms.sign.invalid`, `ns1:coe.alreadyAuthenticated`, siblings). The taxonomy table and fixtures
transcribe the proposal's numbering; confirming the exact wire strings is a **19b** task.

**T4 (design.md:648-651)**: the goldens can only be as true as the manual transcription. No test in
19a can detect a transcription error in the `LoginCms`/TRA fixtures; 19b's first task is the
fixture-vs-reality diff.

- [ ] 2.1 Create `src/Ways.Application/Fiscal/{IClienteWsaa,IRepositorioDeTicketDeAcceso}.cs` +
  `Contratos.cs` (`TicketDeAcceso`, `ClaveDeTicket`) — no key material in any port contract.
  *(design.md:245-252, 371)*
- [ ] 2.2 Create `src/Ways.Infrastructure/Fiscal/SobreSoap.cs` — pure static `Construir`/
  `AccionDe`/`Leer`, `XDeclaration` + `SaveOptions.DisableFormatting`, the only file in `src/`
  naming SOAP. *(design.md D2:66, 176-210, 375)*
- [ ] 2.3 Create `src/Ways.Infrastructure/Fiscal/GeneradorDeTra.cs` — puro salvo
  `IRelojDelSistema`, `uniqueId` = unix seconds ⊕ `Interlocked` tiebreak, `Ventana` = 10 min.
  *(design.md:212-224, 376)*
- [ ] 2.4 Create `src/Ways.Infrastructure/Fiscal/FirmanteCms.cs` — `SignedCms` + `CmsSigner`
  (SHA-256, `EndCertOnly`), BCL only. *(design.md:226-236, 376)*
- [ ] 2.5 Create `src/Ways.Infrastructure/Fiscal/ClienteWsaa.cs` — implements `IClienteWsaa`, calls
  `SobreSoap.Construir` + `HttpClient`, maps WSAA fault codes 500/501/502/600/601/602 to domain
  codes. *(design.md:377, error taxonomy 309-320)*
- [ ] 2.6 Create `src/Ways.Infrastructure/Fiscal/RepositorioEnMemoriaDeTicketDeAcceso.cs` —
  `ConcurrentDictionary` + per-key `SemaphoreSlim` single-flight, `MargenDeSeguridad` = 10 min
  absolute (not a TTL percentage). *(design.md D8:72, 378)*
- [ ] 2.7 Create `tests/**/Fiscal/CertificadoDePrueba.cs` — `CertificateRequest` self-signed,
  PKCS#12 round trip before signing (D7), **no key material committed**. *(design.md D7:71, 389)*
- [ ] 2.8 Create `tests/**/Fiscal/Fixtures/**` WSAA — `LoginTicketRequest`/`Response` goldens,
  fault codes, `REVISION.md` pinning `manual-desarrollador-ARCA-COMPG-v4-0.pdf` rev. 15/01/2025 +
  `Especificacion_Tecnica_WSAA_1.2.2.pdf`. *(proposal.md decision 8, design.md:388)*
- [ ] 2.9 Modify DI registration — `IClienteWsaa`/`IRepositorioDeTicketDeAcceso` registered (test
  host wiring only; no production caller yet).
- [ ] 2.10 [P] TRA golden — element names/order (`uniqueId`, `generationTime`, `expirationTime`,
  `service`) byte-for-byte under `RelojFijo`. *(target 24)*
- [ ] 2.11 [P] `generationTime = Ahora − 10 min`, `expirationTime = Ahora + 10 min` from
  `IRelojDelSistema`, not `DateTimeOffset.UtcNow`. *(target 25)*
- [ ] 2.12 [P] `uniqueId` tiebreak — two TRAs generated in the same clock tick must differ
  (`Interlocked`). *(target 26)*
- [ ] 2.13 [P] `CmsSigner` structure test — digest OID SHA-256, certificate count = 1
  (`EndCertOnly`). *(target 27)*
- [ ] 2.14 [P] `LoginCms` envelope golden — namespace URI, `soapenv` prefix, `in0` element,
  `SOAPAction: ""` — byte-for-byte. *(target 28)*
- [ ] 2.15 **[S]** `rg` scan — `SobreSoap` is the only SOAP-naming file in `src/`. *(target 29)*
- [ ] 2.16 [P] Every golden — `SaveOptions.DisableFormatting` + `XDeclaration` present, no
  indentation. *(target 30)*
- [ ] 2.17 [P] TA cache hit — a second emission within TTL issues zero extra `LoginCms` (spy
  count). *(target 31)*
- [ ] 2.18 [P] `MargenDeSeguridad` boundary pair under `RelojQueAvanza` — at `Expiracion − Margen −
  1s` cached, at `Expiracion − Margen` a new `LoginCms`. *(target 32)*
- [ ] 2.19 [P] Per-key single-flight — N concurrent cold asks ⇒ exactly one `LoginCms`.
  *(target 33)*
- [ ] 2.20 [P] WSAA fault taxonomy — one test per code (500/501/502/600/601/602), asserting the
  domain code, not the HTTP status alone. *(target 34)*
- [ ] 2.21 **[S]** Repository scan — no PEM/PFX/private-key material under `src/` or `tests/`.
  *(target 35)*
- [ ] 2.22 [P] D7 signing test — PKCS#12 re-load before signing, recorded as a platform-conditional
  kill (Windows). *(target 36)*
- [ ] 2.23 Non-regression: full suite green, nothing wired to production DI yet.
- [ ] 2.24 GATE GUARD — repository-wide PEM/PFX/private-key scan clean (reasserted, verify
  criterion 7); zero new migrations this slice.
- [ ] 2.25 Mutation evidence recorded in the PR body for targets 24-36 (**S** rows 29, 35 record
  the file/state assertion).
- [ ] 2.26 [ ] `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 2.27 [ ] Open PR #2 `feat/stage19a-slice2-wsaa`, merge to `main` after a clean `judgment-day`
  round.

---

## Slice 3: ClienteWsfe + mapper + máquina CAE + taxonomía (PR 3)

**Branch**: `feat/stage19a-slice3-wsfe-y-cae`. **Start**: PR 2 merged (needs its fixture harness).
**Finish**: `ClienteWsfe` (`FECAESolicitar`/`FECompConsultar`/`FECompUltimoAutorizado`/
`FEParamGet*`), the request mapper, `ComposicionDeTotalesFiscales`, `MaquinaDeEstadosCae` +
`PermisoDeSolicitud`, the three response fixtures + the error taxonomy (WSFE half), backoff +
circuit breaker. **Still no caller** — the emission use case arrives in slice 5. **Rollback**:
`git revert`; nothing calls `ClienteWsfe` yet. **Budget note**: pre-authorized split `3a`/`3b`
above if this slice overflows. **Skills required**: `mutation-proof-tests` v1.1 (targets 37-51; no
guarded-`UPDATE` conjunct set this slice), `work-unit-commits`.

**T4 (design.md:648-651)**, reasserted here: the `FECAESolicitar` request golden and the three
response fixtures are only as true as the manual transcription — no test in 19a can catch a
transcription error; 19b's first task is the fixture-vs-reality diff.

- [ ] 3.1 Create `src/Ways.Application/Fiscal/IClienteWsfe.cs` — `SolicitarCaeAsync`/
  `ConsultarAsync`/`UltimoAutorizadoAsync`/`ParametrosAsync`. Modify `Contratos.cs` —
  `SolicitudDeCae`, `RespuestaCae`, `ClaveDeSerie`. *(design.md:254-260, 371)*
- [ ] 3.2 Create `src/Ways.Domain/Fiscal/MaquinaDeEstadosCae.cs` — pure, DB-free,
  `PermisoDeSolicitud` (internal constructor, D4), `EsTerminal`, `Decidir`, `Mapear`.
  *(design.md D4:68, 270-282, 365)*
- [ ] 3.3 Create `src/Ways.Application/Fiscal/ComposicionDeTotalesFiscales.cs` — `GROUP BY
  id_alicuota_iva` over the frozen per-line snapshot, `ImpOpEx`/`ImpTotConc` per D11.
  *(design.md D11:75, 373)*
- [ ] 3.4 Create `src/Ways.Infrastructure/Fiscal/ClienteWsfe.cs` — the SOAP request mapper (money
  `"0.00"` `InvariantCulture`, `CbteFch` `yyyyMMdd`, `MonId = "PES"`, `MonCotiz = 1`, optional
  elements omitted, never empty), backoff + circuit breaker. *(design.md D2/D3:66-67, 300-320,
  377)*
- [ ] 3.5 Create the WSFE fixtures — `FECAESolicitar` request golden + three response fixtures
  (approved / approved-with-observations / rejected), `FECompConsultar` found/not-found,
  `FECompUltimoAutorizado` head + empty series (`0`), error taxonomy `10016` +
  `Errors[]`/`Observaciones[]`. *(proposal.md decision 8, design.md:388-389)*
- [ ] 3.6 [P] `FECAESolicitar` envelope golden — namespace, `SOAPAction`, `Auth`/`FeCabReq`/
  `FeDetReq` order, byte-for-byte. *(target 37)*
- [ ] 3.7 [P] Money/date/currency formatting golden — `InvariantCulture`, `yyyyMMdd`, `PES`/`1`.
  *(target 38)*
- [ ] 3.8 [P] Optional elements omitted, never emitted empty — `Concepto = 1` invoice golden.
  *(target 39)*
- [ ] 3.9 [P] Mixed-invoice test — `Iva[]` excludes `Exento`/`No gravado`, exactly two entries.
  *(target 40)*
- [ ] 3.10 [P] Mixed-invoice test — `ImpOpEx` ← exento, `ImpTotConc` ← no gravado, distinct
  amounts. *(target 41)*
- [ ] 3.11 [P] `GROUP BY` test — two lines of 21% collapse to one entry with summed
  `BaseImp`/`Importe`. *(target 42)*
- [ ] 3.12 [P] `ImpTotal` exact-sum assertion — drop-a-term mutation. *(target 43)*
- [ ] 3.13 [P] D11 bucketing test — the `0%` alícuota lands in `Iva[]` with code 3, not `ImpOpEx`.
  *(target 44)*
- [ ] 3.14 [P] `alicuota_sin_mapeo_afip` throw — a seeded NULL-coded alícuota raises, not
  invoiced. *(target 45)*
- [ ] 3.15 [P] Three response states test — the observed approval writes a CAE **and** persists
  `observaciones_fiscales` (two kills). *(target 46)*
- [ ] 3.16 [P] `EsTerminal` transition table — both approvals terminal. *(target 47)*
- [ ] 3.17 [P] `10016` fixture — `proximo_numero` unchanged, `409` raised, no auto-advance (D13).
  *(target 48)*
- [ ] 3.18 [P] WSFE `Errors[]` `600` — TA invalidated + retried exactly once (call log).
  *(target 49)*
- [ ] 3.19 [P] Backoff + circuit breaker bounds — attempt-count test + open-circuit zero-requests
  test. *(target 50)*
- [ ] 3.20 [P] `FECompUltimoAutorizado` empty series maps to `0`, not `null`/`1`. *(target 51)*
- [ ] 3.21 Non-regression: full suite green, no production caller wired yet.
- [ ] 3.22 Mutation evidence recorded in the PR body for targets 37-51.
- [ ] 3.23 [ ] `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 3.24 [ ] Open PR #3 `feat/stage19a-slice3-wsfe-y-cae`, merge to `main` after a clean
  `judgment-day` round.

---

## Slice 4: AsignadorDeNumeroFiscal + reconciliación + cifrado + policy/ABM (PR 4)

**Branch**: `feat/stage19a-slice4-numeracion-y-certificados`. **Start**: PR 1 merged (independent
of slices 2-3, may interleave). **Finish**: `AsignadorDeNumeroFiscal` with invariant I1 + its
concurrency and `pg_locks` test, D13 reconciliation, `CifradoDeClavesFiscales` (AES-GCM + AAD +
rotation), `ServicioDeCertificados`, the `AdministracionFiscal` policy and its three ABM routes.
**Rollback**: `git revert` — `numeraciones_fiscales` is empty in production (nothing has been
emitted), `certificados_fiscales` has no row until an owner uploads one, the policy registration is
one line. **Skills required**: `mutation-proof-tests` v1.1 rule 3 — **U1, U3, U4 transcribed below,
up front, before any test is written** (Reconciliación 4); `dto-contract-honesty` — the exposure
clause on `CertificadoFiscalDto` (`clave_privada_cifrada`/`nonce`/`tag_autenticacion`/
`certificado_pem` absent from every DTO, matched by property name, rule 1); `work-unit-commits`.

**T2 (design.md:638-642)**: I1's operator-release path ("released only when it is the top of its
series, by an explicit operator action") is **NOT shipped in 19a**. This slice ships only the
enforceable half — the number stays bound to its `pendiente` comprobante and is never silently
reused; the release route is registered for **19c**, alongside the durable queue that would drain
the same rows.

**Guard enumeration (mutation-proof-tests rule 3 v1.1, design.md:420-433)**:
- **U1** `UPDATE numeraciones_fiscales SET proximo_numero = proximo_numero + 1 WHERE …` — conjuncts
  (a) `id_punto_venta = $1` (b) `codigo_afip = $2`. Kills: (a) a sibling PV of the same tenant keeps
  its own `proximo_numero` untouched; (b) a sibling `codigo_afip` on the **same** PV (`FA`=1 vs
  `FB`=6) untouched.
- **U3** `UPDATE numeraciones_fiscales SET ultimo_autorizado_arca, sincronizado_en WHERE …` —
  conjuncts (a) `id_punto_venta` (b) `codigo_afip`. Same sibling-pair kill; the neighbour's
  `ultimo_autorizado_arca` stays `NULL`.
- **U4** `UPDATE certificados_fiscales SET activo = false … WHERE …` (rotation **and**
  deactivation) — conjuncts (a) `id_tenant` (b) `id_empresa` (c) `ambiente` (d) `activo` (e)
  `deleted_at IS NULL`. Five kills: (a) `ways_app` under tenant B ⇒ 0 rows; (b) a sibling empresa's
  active certificate stays active; (c) the **homologación** certificate stays active while
  **producción** rotates; (d) an already-inactive row untouched (affected-row count, not final
  state); (e) a soft-deleted twin neither resurrected nor counted.

- [ ] 4.1 Create `src/Ways.Application/Fiscal/AsignadorDeNumeroFiscal.cs` — `AsegurarContadorAsync`
  (`INSERT … ON CONFLICT DO NOTHING`) + `AsignarSiguienteAsync` (`UPDATE … RETURNING`), raw ADO on
  the caller's connection, discipline **opposite** to `AsignadorDeNumeroComprobante` (D1, proposal
  decision 13). *(design.md:284-307, 372)* — implements U1
- [ ] 4.2 Same file: reconciliation against `FECompUltimoAutorizado` — writes **only**
  `ultimo_autorizado_arca` + `sincronizado_en`, divergence raises `409
  numeracion_fiscal_desincronizada`, **never** auto-heals `proximo_numero` (D13).
  *(design.md D13:77)* — implements U3
- [ ] 4.3 Create `src/Ways.Infrastructure/Fiscal/CifradoDeClavesFiscales.cs` — AES-256-GCM, AAD =
  `UTF8("v1|"+idTenant+"|"+idEmpresa+"|"+ambiente+"|"+huellaSha256)` **excluding**
  `id_clave_maestra` (D5), key versioning via `id_clave_maestra`, `ZeroMemory` in a `finally`.
  *(design.md D5:69, 379)*
- [ ] 4.4 Same file: master-key lookup — `Ways:Fiscal:ClaveMaestraActual` +
  `Ways:Fiscal:ClavesMaestras:<id>`; missing/short/absent ⇒ `503 clave_maestra_ausente` on the ABM /
  `409 certificado_fiscal_ausente` on emission, **never** a plaintext fallback or generated-on-boot
  key (D6). *(design.md D6:70)*
- [ ] 4.5 Create `src/Ways.Application/Fiscal/IAlmacenDeClavesFiscales.cs`. Modify `Contratos.cs` —
  `CertificadoFiscalDto` with **no** key-material property. *(design.md:262-267, 371)*
- [ ] 4.6 Create `src/Ways.Application/Fiscal/ServicioDeCertificados.cs` — register/list (never
  returning key material)/deactivate; rotation = deactivate+activate inside one transaction.
  *(proposal.md §707, design.md file changes)* — implements U4
- [ ] 4.7 Create `src/Ways.Api/Endpoints/FiscalEndpoints.cs` (certificate ABM routes only this
  slice) — `POST`/`GET`/`DELETE /api/fiscal/certificados`, `PUT
  /api/fiscal/empresas/{id}/condicion-fiscal`, `PUT
  /api/fiscal/puntos-venta/{id}/numero-fiscal`, all under `AdministracionFiscal`. *(proposal.md
  API surface 705-710)*
- [ ] 4.8 Modify `src/Ways.Api/Seguridad/Politicas.cs` — **+1** exact policy `AdministracionFiscal`
  (Admin only). 11 → **12**. *(proposal.md §684, design.md fact 7:54-56, Binding Verify Criterion
  11)*
- [ ] 4.9 [P] U1 conjunct (a) `id_punto_venta` — sibling-PV test (rule 12c). *(target 52)*
- [ ] 4.10 [P] U1 conjunct (b) `codigo_afip` — sibling-type test on the same PV. *(target 53)*
- [ ] 4.11 [P] I1 test — a `rechazado` emission does **not** advance the series: number stays
  bound, `proximo_numero` consistent, no hole. *(target 54)*
- [ ] 4.12 **[S]** D1 lock proof — `pg_locks` polled from a second connection: `numeraciones_fiscales`
  is the **first and only** existing-row lock of the fiscal transaction (both halves asserted, rule
  13). *(target 55)*
- [ ] 4.13 [P] U3 conjuncts (a)(b) — sibling pair, the neighbour's `ultimo_autorizado_arca` stays
  `NULL`. *(target 56)*
- [ ] 4.14 [P] AAD four-component tamper test — one per component (tenant, empresa, ambiente,
  huella), each failing authentication. *(target 57)*
- [ ] 4.15 [P] AAD **excludes** `id_clave_maestra` — rotation test: a re-encrypted row must still
  decrypt. *(target 58)*
- [ ] 4.16 [P] No-plaintext-fallback test — missing/short master key ⇒ the named error, **nothing**
  written. *(target 59)*
- [ ] 4.17 **[S]** `CryptographicOperations.ZeroMemory` structural assertion — `UsarCertificadoAsync`
  clears its buffer in a `finally`. *(target 60)*
- [ ] 4.18 [P] U4 conjuncts (a)-(e) — five kills: cross-tenant `ways_app`; sibling empresa; sibling
  ambiente; already-inactive row (affected-row count); soft-deleted twin. *(target 61)*
- [ ] 4.19 [P] Certificate DTO exposure clause — recursive property-**name** assertion, no
  `ClavePrivadaCifrada`/`Nonce`/`TagAutenticacion`/`CertificadoPem`/`ClaveMaestra` property anywhere
  in the serialized response. *(target 62)*
- [ ] 4.20 [P] `AdministracionFiscal` role matrix — Admin 200, Supervisor/Vendedor/Root 403;
  `Politicas.cs` count 11 → 12. *(target 63)*
- [ ] 4.21 Non-regression: full suite green, `ServicioDeVentas.cs` untouched.
- [ ] 4.22 GATE GUARD — zero new migrations this slice; `has-pending-model-changes` clean.
- [ ] 4.23 Mutation evidence recorded in the PR body for targets 52-63 (**S** rows 55, 60 record
  the assertion, not a runtime failure).
- [ ] 4.24 [ ] `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 4.25 [ ] Open PR #4 `feat/stage19a-slice4-numeracion-y-certificados`, merge to `main` after a
  clean `judgment-day` round.

---

## Slice 5: Emisión end-to-end contra mocks + QR + doc 11 (PR 5)

**Branch**: `feat/stage19a-slice5-emision-y-qr`. **Start**: PR 1, 2, 3, 4 merged. **Finish**:
`ServicioDeFacturacionFiscal` end-to-end against mocks (the four gates D10, the letter resolver's
first caller, invariants I2/I3/I4), `PayloadQrFiscal`, the two emission routes, docs 09/10 "Estado
(Etapa 19a)" header **CLOSED**, doc 11's Etapa 19a status block. **Rollback**: `git revert` removes
two routes; nothing else calls them. **Skills required**: `mutation-proof-tests` v1.1 rule 3 — **U2
transcribed below, up front**; `dto-contract-honesty` (the certificate/comprobante exposure clause
reasserted at the endpoint boundary); `work-unit-commits`.

**BINDING WARNING — T1 (design.md D12:76, T1:629-637)**: this slice's fiscal write plan is
`comprobante + items` **ONLY**. **NO** `movimientos_stock`, **NO** `pagos_comprobante`, **NO**
`movimientos_cuenta_corriente`, **NO** turno guard. Safe **only** because of invariant I4 — with no
certificate, no such row can exist in production. Target 75 is the **trip-wire** zero-rows test,
labelled as the **known 19c gap**, not a discovery. `FA`/`FB`/`FC` carry `afecta_stock = true` in
the catalogue — a real inconsistency named here rather than hidden. **Binding 19c contract**: the
fiscal emission gains the stock/payment/cuenta-corriente loops together with the screen that
supplies them, and target 75 must go **RED** then.

**T2 reasserted (design.md:638-642)**: no operator-release route ships in this slice either — it
remains a 19c deliverable.

**Guard enumeration (mutation-proof-tests rule 3 v1.1, design.md:420-433)**:
- **U2** `UPDATE comprobantes_venta SET cae…, resultado_fiscal… WHERE …` — conjuncts (a)
  `id_comprobante_venta = $` (b) `id_tenant = $` (c) `resultado_fiscal = 'pendiente'`. Kills: (a) a
  sibling `pendiente` comprobante of the same PV stays `pendiente`; (b) executed on `ways_app` under
  tenant B's context ⇒ 0 rows; (c) **two** kills — a below-the-confound direct call on an
  already-`aprobado` row ⇒ 0 rows (I3), **and** the TOCTOU race (two retries rendezvous, the loser
  re-evaluates under the lock and matches 0).

- [ ] 5.1 Create `src/Ways.Domain/Fiscal/PayloadQrFiscal.cs` — 13 RG 4291 fields + base64 +
  `https://www.afip.gob.ar/fe/qr/?p=` URL, `tipoCodAut = "E"` (synthetic `codAut`).
  *(design.md:366, proposal.md §75-76)*
- [ ] 5.2 Create `src/Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs` — the four gates in
  fixed order (D10, each its own named 409, **all** before any port is resolved):
  `empresa_sin_condicion_fiscal` → `punto_venta_sin_numero_fiscal` → `tipo_fiscal_invalido` →
  `condicion_fiscal_receptor_no_mapeada` (`NO_RESP` ⇒ 409) → `certificado_fiscal_ausente`.
  *(design.md D10:74, 324-330)*
- [ ] 5.3 Same file: `ResolverTipoFiscalAsync` — mirror image of the POS resolver, requires
  `Activo && Clase == Venta && EsFiscal`, does **not** read `AfectaStock`, never touches
  `ServicioDeVentas` (D9). *(design.md D9:73)*
- [ ] 5.4 Same file: call `ResolvedorDeLetraComprobante.Resolver(emisor, receptor)` — its **first**
  caller; call `ComposicionDeTotalesFiscales`. *(design.md:331-332, proposal.md §57-60)*
- [ ] 5.5 Same file: emission transaction — `EstrategiaSinReintento` `BEGIN`,
  `AsegurarContadorAsync` + `AsignarSiguienteAsync` (lock at position 0), `INSERT
  comprobantes_venta` (`pendiente`) + `INSERT items_comprobante_venta` **only** (D12/T1 above —
  zero stock/pagos/CC), the WSFE round trip under `TimeoutDeWsfe = 30s` with the lock held, guarded
  `UPDATE` (U2) on success/failure, conditional `UPDATE numeraciones_fiscales` (only on approval),
  `COMMIT`. *(design.md:334-347, D1:65)*
- [ ] 5.6 Create `POST /api/fiscal/comprobantes/{id}/reintentar` — I2 path: reads a `pendiente`
  comprobante via `ix_comprobantes_venta_fiscal_pendientes`,
  `MaquinaDeEstadosCae.Decidir(no-definitivo)` ⇒ `FECompConsultar` first; found ⇒ adopts the CAE
  (zero `FECAESolicitar`); not-found ⇒ same number, `FECAESolicitar`. *(design.md:350-356)*
- [ ] 5.7 Modify `src/Ways.Api/Endpoints/FiscalEndpoints.cs` — add the two emission routes `POST
  /api/fiscal/comprobantes` + `POST /api/fiscal/comprobantes/{id}/reintentar`, both under
  `OperacionDePos`. *(proposal.md API surface 710-711)*
- [ ] 5.8 Modify `src/Ways.Domain/Ventas/ResolvedorDeLetraComprobante.cs` **doc-comment only** — the
  *"dormant"* line updated to reflect its first caller; the rule itself does not change.
  *(design.md:369)*
- [ ] 5.9 Confirm `src/Ways.Application/Ventas/ServicioDeVentas.cs` is **UNMODIFIED** this whole
  sub-stage — zero edits, `:1162` unchanged (D9 fact 6). *(design.md fact 6:46-52, proposal.md
  decision 9)*
- [ ] 5.10 [P] I4 gate — the five gate paths each return their own 409, `HttpMessageHandler` spy
  records **zero** requests on all five, before any port resolved. *(target 64)*
- [ ] 5.11 [P] Each gate's own named 409 in order — four kills, one per gate, asserting the code.
  *(target 65)*
- [ ] 5.12 [P] I2 — `FECompConsultar` precedes on a non-definitive retry, exactly **one**
  `FECAESolicitar` across both attempts (call log). *(target 66)*
- [ ] 5.13 [P] I2 adoption — the *found* fixture: zero `FECAESolicitar`, CAE written locally.
  *(target 67)*
- [ ] 5.14 [P] U2 conjuncts (a)(b)(c) — four kills: sibling `pendiente`; cross-tenant `ways_app`;
  already-`aprobado` direct call (I3); TOCTOU rendezvous. *(target 68)*
- [ ] 5.15 **[S]** D4's `PermisoDeSolicitud` gate — structural assertion that
  `MaquinaDeEstadosCae` is the only producer (no public constructor path). *(target 69)*
- [ ] 5.16 [P] Letter resolver's first caller — RI→RI ⇒ `A`, RI→CF ⇒ `B`, end to end against the
  mocks. *(target 70)*
- [ ] 5.17 [P] `NO_RESP` receptor test — `409 condicion_fiscal_receptor_no_mapeada`, zero requests.
  *(target 71)*
- [ ] 5.18 [P] QR hand-computed vector — 13 fields, `tipoCodAut = "E"`, base64 + URL prefix.
  *(target 72)*
- [ ] 5.19 **[S]** `git diff --exit-code src/Ways.Application/Ventas/ServicioDeVentas.cs` clean
  **and** a live `POST /api/ventas` with `FA` ⇒ `400`. *(target 73)*
- [ ] 5.20 **[S]** `ContadorDeComandos` equality — a non-fiscal sale issues the **same** EF command
  count as `main`, zero extra SQL statements. *(target 74)*
- [ ] 5.21 **[S]** D12's declared gap — zero-rows assertion over `movimientos_stock` /
  `pagos_comprobante` / `movimientos_cuenta_corriente` after a fiscal emission, labelled as the
  **known 19c gap** (the T1 trip-wire). *(target 75)*
- [ ] 5.22 **[S]** Shipped-configuration scan — no real ARCA hostname (`wswhomo`/`servicios1`) as a
  default anywhere in `appsettings*.json` across the **cumulative** diff of all five slices.
  *(target 76, cross-slice)*
- [ ] 5.23 [P] Certificate/fiscal-comprobante DTO exposure reassertion at the endpoint boundary —
  the same recursive property-name scan as task 4.19, run against the live `POST
  /api/fiscal/comprobantes` response.
- [ ] 5.24 [P] Authorization matrix — fiscal emission under `OperacionDePos`: Vendedor 200, Root
  403; ABM routes still Admin-only (reconfirm slice 4).
- [ ] 5.25 Non-regression: full domain/application/integration suite green;
  `VentasCheckoutTests`/`VentasAnulacionTests`/`VentasAtomicidadYConcurrenciaTests` unedited and
  green.
- [ ] 5.26 Modify `docs/11-programa-post-paridad.md` — Etapa 19a status block (regla 19: the doc 11
  task lands in the **last** slice).
- [ ] 5.27 Modify `docs/09-multi-tenancy.md` / `docs/10-modelo-de-datos.md` — "Estado (Etapa 19a)"
  header **CLOSED** (regla 19, opened at slice 1 task 1.24).
- [ ] 5.28 GATE GUARD (whole sub-stage) — re-verify the full success-criteria checklist: exactly
  one migration (`has-pending-model-changes` clean), 8 indexes, 8 CHECKs, RLS `FORCE`d on both new
  tables, non-fiscal sale byte-identical, zero PEM/PFX/key material, zero real ARCA hostname as a
  default. *(proposal.md §792-823; Binding Verify Criteria 1-13)*
- [ ] 5.29 Mutation evidence recorded in the PR body for targets 64-76 (**S** rows 69, 73, 74, 75,
  76 record the file/state/definition assertion).
- [ ] 5.30 [ ] `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 5.31 [ ] Open PR #5 `feat/stage19a-slice5-emision-y-qr`, merge to `main` after a clean
  `judgment-day` round.
