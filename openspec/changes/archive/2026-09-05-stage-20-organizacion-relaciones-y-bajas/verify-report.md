```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:dd355fd02c737d74458f45eb64824673aa38dc1cf7b7933fdb733e48945c9fd2
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 19/19
scenarios: 73/73
test_command: dotnet test tests/Ways.IntegrationTests/Ways.IntegrationTests.csproj
test_exit_code: 0
test_output_hash: sha256:48a669cd674f82ba7ac8c7c9af9876b63c3f7e33f748626f2e736398b41d8975
build_command: dotnet build Ways.slnx
build_exit_code: 0
build_output_hash: sha256:100df004fdac2c6b0fde9c54ea6e7ec29ead1bb52627ba268bfa6fe71e3c5fc3
```

# Verify Report: stage-20-organizacion-relaciones-y-bajas

**Change**: `stage-20-organizacion-relaciones-y-bajas` · **Mode**: hybrid (openspec file + Engram,
Engram owned by the orchestrator) · **HEAD verified**: `858e9589ed657ce2a7e533bb00d6db75e99552db`
(`main`) · **Stage base**: `22af91af6c52222895bd6b63166ef3aae708282c` (the commit before slice 1's
first commit, i.e. `5f0018f^`). **PRs verified against `git log`**: #165 (`5f0018f`), #167, #169,
#171, #173 (`858e958`) — all five merge commits present on `main`, in the planned order 1-2-3-4-5.

**Evidence discipline**: every suite in section 3 was executed for this report against this exact
tree. No number there is copied from `state.yaml` or `tasks.md`; where a re-measured number equals a
recorded one, that agreement is stated as a re-measurement, not as a citation.

## Verdict: **PASS WITH WARNINGS**

**0 CRITICAL · 3 WARNING · 9 SUGGESTION.**

No CRITICAL finding exists against any of the four binding zero-schema criteria, against any of the
19 requirements / 73 scenarios of the three specs, against the 116-line task ledger, or against the
declared design amendments. All three WARNINGs are bookkeeping or comment-style items; none requires
touching shipped behaviour, re-opening the DB gate, or unwinding a merged PR.

---

## 1. The binding zero-schema criteria (`state.yaml:45-51`, `db_gate_approval`)

| # | Criterion | Verdict | Evidence produced for this report |
|---|---|---|---|
| a | No new file under `src/Ways.Infrastructure/Persistencia/Migraciones/`; the last migration is still `20260822002214_FiscalArcaEtapa19a.cs` | **PASS** | Directory listing: the highest-timestamped pair is `20260822002214_FiscalArcaEtapa19a.cs` / `.Designer.cs`; nothing follows it. `git diff 22af91a..858e958 --name-only` lists **zero** paths under `Migraciones/` |
| b | `dotnet ef migrations has-pending-model-changes` clean | **PASS** | Run at HEAD: `dotnet ef migrations has-pending-model-changes --project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure` returns *"No changes have been made to the model since the last migration."*, exit `0`. The `--startup-project src/Ways.Api` form the brief suggested fails with *"Ways.Api doesn't reference Microsoft.EntityFrameworkCore.Design"* — that package lives in `Ways.Infrastructure.csproj:10`, which also owns `WaysDbContextFactory`. The invocation above is the one this repository supports; recorded here because `tasks.md` never wrote the flags down |
| c | `InicializadorDeBaseDeDatos.cs`, `Politicas.cs`, `ManejadorDeErrores.cs` untouched across the whole stage | **PASS** | Filtering `git diff 22af91a..858e958 --name-only` for `Migraciones`, `InicializadorDeBaseDeDatos`, `Politicas.cs` and `ManejadorDeErrores` returns **no output**. The complete non-`openspec/` stage diff is 66 files (13 721 insertions, 495 deletions) and none of the three appears in it |
| d | Zero physical deletes over `tenants`, `empresas`, `puntos_venta`, `usuarios` | **PASS** | Two independent proofs. (1) My own scan of `src/`: `ExecuteDelete` 0 matches; `\.Remove\(` 0 matches; `DELETE\s+FROM` outside `Migraciones/` 0 matches; `RemoveRange\(` exactly 6 hits, all detail-set replacements (`ItemsRemito`, `ItemsPresupuesto`, `OfertasListas`, `ItemsOrdenCompra`, `ArticulosEmpresas`, `ItemsComprobanteCompra`), none an organization table, none in the stage diff. (2) The trip-wire as code: `BajasEstructuralesTests.NingunCaminoDeProduccionBorraFisicamenteFilasDeOrganizacion` (`tests/Ways.Application.Tests/Organizacion/BajasEstructuralesTests.cs:84`) walks every `*.cs` under `src/`, asserts the two `ExecuteDelete*` substrings empty, asserts the three anchored `Remove(` receiver patterns empty, asserts `DELETE FROM` absent from **all** files (migrations included), and freezes the six `RemoveRange` receivers by name (`:30-38`) with an explicit disjointness assertion against the four organization sets (`:118`). It passed in this run |

**Additional criteria from `tasks.md:146-176`, re-checked at HEAD:**

- **V7** — `IWaysDbContext` gained exactly one member (`IModel Model { get; }`); scanning `src/` and
  `tests/` for `: IWaysDbContext` returns **zero** implementers, so no implementation changed. PASS
- **V10** — `InspectorDeUso` was inert in slice 3 and is now legitimately called by exactly the two
  services slice 4 introduces (`ServicioDeOrganizacion.cs:698`, `ServicioDeUsuarios.cs:419`). PASS
  for its scoped meaning (zero callers *in the slice-3 diff*).
- **V13** — zero DDL anywhere in the stage diff outside the guard's generated read-only `SELECT`;
  confirmed by (a) and (c) plus the absence of any `.sql` file in the diff. PASS
- **V5/V6** — `Politicas.cs` and `ManejadorDeErrores.cs` untouched; the three `MapDelete`s reuse the
  policy of the group they already belong to (`OrganizacionEndpoints.cs:49, 73, 102-107`), the
  punto-de-venta route declaring `GestionDeOrganizacion` per-route because its group has none. PASS

**The gate does not reopen.** Nothing in the stage proposes DDL, a migration, a data statement or a
seed change.

---

## 2. Task ledger

`tasks.md` carries **116** numbered task lines. **114 are `[x]`. Two are `[ ]`: `5.11` and `5.12`.**

Both are the orchestrator-owned closing steps of slice 5, and both are **provably complete** from
evidence outside `tasks.md`:

- `5.11` (`judgment-day` to a clean round) — two full rounds are recorded *in `tasks.md` itself*
  (`:1923-1994` round 1, eight findings fixed, 20 mutations; `:1997-2038` round 2, FINAL, five
  findings plus one records entry, seven mutations, zero survivors) and mirrored in
  `state.yaml:1135-1249`. The corresponding commits are on `main`: `7442504`, `a370599`, `87aa3b3`,
  `6b4f678`.
- `5.12` (open PR 5 and merge) — PR **#173** is merged as `858e958`, which is the verified HEAD.

Spot-check of twelve `[x]` tasks against the tree (all **confirmed**):

| Task | Claim | Confirmed at |
|---|---|---|
| 1.1 | `TenantListado` gains three counts, `EmpresaListado` gains `NombreTenant`, `PuntoVentaListado` gains two names | `Organizacion/Contratos.cs:22-29, 39-45, 52-64` |
| 1.2 | `UsuarioListado` gains `int? IdTenant` and `string? NombreTenant` | `Usuarios/Contratos.cs:28-38` |
| 1.4 / 1.5 | Owner names as correlated scalar subqueries, each with its own `DeletedAt == null` | `ServicioDeOrganizacion.cs:284-294` (empresa), `:435-454` (punto de venta) |
| 3.1 | `IWaysDbContext` gains exactly one member | one added `IModel Model { get; }`, zero implementers |
| 3.3 | Carve-outs are exactly two, each with a written reason | `InventarioDeDependientes.cs:160-184`; asserted by `LosCarveOutsSonExactamenteAuditoriaYNumeracionCliente` |
| 3.6 | Identifiers validated with `\A...\z`, never `^...$` | `InspectorDeUso.cs:76-84` |
| 3.8 | `InspectorDeUso` registered in DI | `Ways.Application/DependencyInjection.cs:96` |
| 3.14 | OD4 — no rendered branch emits `deleted_at` | Scanning `InspectorDeUso.cs` and `InventarioDeDependientes.cs` for `deleted_at`/`DeletedAt` returns only two doc-comment lines (`InspectorDeUso.cs:44`, `:64`) and zero code |
| 4.9 | Three `MapDelete`, zero new policies | `OrganizacionEndpoints.cs:49-54, 73-78, 102-108`; allowlisted in `SuperficieDeAutorizacionTests` |
| 4.34 / 4.35 | Zero physical deletes plus disjoint lock sets, asserted structurally | `BajasEstructuralesTests.cs:84`, `:152` |
| 5.1 / 5.2 | `eliminarTenant/Empresa/PuntoVenta` plus the `codigo`-to-copy module | `src/Ways.Web/src/api/organizacion.ts`, `src/Ways.Web/src/api/bajas.ts:37-83` |
| 5.9 | Etapa 20 note in docs 09 and 10 | `docs/09-multi-tenancy.md:216`, `docs/10-modelo-de-datos.md:1254` |

**Verdict: PASS with WARNING-1** — the two unticked boxes are a bookkeeping gap, not incomplete work.

---

## 3. Runtime evidence — every suite run for this report, at HEAD `858e958`

The integration suite was run **once and alone** against the Docker daemon (server `29.6.2`), with no
other suite executing concurrently, per the binding discipline. **Zero transport-abort flakes and
zero re-runs were needed**, so no per-class isolation re-run was required.

| Command | Exit | Result |
|---|---|---|
| `dotnet build Ways.slnx` | `0` | Clean, 0 errors. 2 pre-existing `NU1903` advisories, both `SSH.NET 2024.1.0` on `Ways.IntegrationTests.csproj` (`GHSA-q939-rpr3-3284`), each reported twice (restore + build) |
| `dotnet ef migrations has-pending-model-changes --project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure` | `0` | *"No changes have been made to the model since the last migration."* |
| `dotnet test tests/Ways.Domain.Tests/Ways.Domain.Tests.csproj` | `0` | `Con error: 0, Superado: 545, Omitido: 0, Total: 545, Duración: 70 ms` |
| `dotnet test tests/Ways.Application.Tests/Ways.Application.Tests.csproj` | `0` | `Con error: 0, Superado: 434, Omitido: 0, Total: 434, Duración: 1 s` |
| `dotnet test tests/Ways.IntegrationTests/Ways.IntegrationTests.csproj` | `0` | `Con error: 0, Superado: 1780, Omitido: 0, Total: 1780, Duración: 12 m 55 s`. Trx at `tests/Ways.IntegrationTests/TestResults/verify-stage20.trx` |
| `npm --prefix src/Ways.Web run test` | `0` | `Test Files 65 passed (65)`, `Tests 1102 passed (1102)`, `Duration 26.99s`. One benign jsdom notice: *"Not implemented: navigation to another Document"* (the R2-4 route probe) |
| `npm --prefix src/Ways.Web run build` | `0` | `built in 510ms`. Only the pre-existing 500 kB chunk-size advisory |
| `npm --prefix src/Ways.Web run lint` | `0` | 5 warnings, all pre-existing `react(only-export-components)`: `ResumenSaldoDeProveedor.tsx:12`, `PanelDeCambio.tsx:10`, `ConsultaPrecios.tsx:59`, `AuthContext.tsx:13`, `Auditoria.tsx:24`. **No new warning** |

Every one of these re-measurements **matches the apply phase's recorded figures exactly**
(545 / 434 / 1780 / 65 files / 1102 tests / 5 lint warnings / 2 NU1903 advisories). The apply agents'
suite claims are therefore independently confirmed rather than trusted.

---

## 4. Spec compliance matrix — 19 requirements, 73 scenarios

Abbreviations: `BDO` = `tests/Ways.IntegrationTests/BajasDeOrganizacionTests.cs` · `IDU` =
`tests/Ways.IntegrationTests/InspectorDeUsoEjecucionTests.cs` · `PDO` =
`tests/Ways.IntegrationTests/ProyeccionDeOrganizacionTests.cs` · `BET` =
`tests/Ways.Application.Tests/Organizacion/BajasEstructuralesTests.cs` · `IDT` =
`tests/Ways.Application.Tests/Persistencia/InventarioDeDependientesTests.cs`.
Every test named below was **green in this report's run**.

### 4.1 `bajas-de-organizacion` — 12 requirements, 41 scenarios

| Req | Scenario | Verdict | Proof |
|---|---|---|---|
| BO-R1 | A deleted row survives in the database | PASS | `BDO:374 UnTenantReciennAprovisionadoSeDaDeBajaYSusFilasSiguenEnLaBase` |
| BO-R1 | The deleted row is invisible to every normal read | PASS | `BDO:441 LaCascadaNoTocaElRestoDeLaPlantillaNiDejaHuerfanosVisibles` |
| BO-R1 | A second deletion of the same row is a clean 404 | PASS | `BDO:1553 UnaSegundaBajaDelMismoIdEs404YNoPisaElInstante` |
| BO-R1 | The repository contains no physical delete over the four tables | PASS | `BET:84` plus my own `src/` scan (section 1d) |
| BO-R2 | A freshly provisioned tenant is pristine and deletable | PASS | `BDO:374`; `IDU:95 UnTenantReciennAprovisionadoEstaPristinoEnLasCuatroAnclas` |
| BO-R2 | A dependent created at exactly the anchor instant does not block | PASS | `BDO:1090 UnDependienteEnElInstanteDelAnclaNoBloqueaYUnTickDespuesSi` (RelojFijo boundary pair) |
| BO-R2 | A dependent created one tick after the anchor blocks | PASS | `BDO:1090`, other half; mutation U7b (strict `>` mutated to `>=`) killed |
| BO-R2 | Breaking the single-clock-reading property is detected | PASS | `BDO:374` is the N4 regression; mutation N4 (provisioning reads the clock twice) is recorded killed against it |
| BO-R3 | One article makes the tenant, its empresa and its punto de venta undeletable | **PASS via Reconciliación 11** | `BDO:762 UnSoloArticuloDelClienteBloqueaLaBajaDelTenant` (409 `tenant_en_uso`), `BDO:792 LaFilaDeDisponibilidadDeUnArticuloBloqueaLaBajaDeSuEmpresa` (409 `empresa_en_uso`), `BDO:826 UnArticuloNoBloqueaAlPuntoDeVentaYUnaFilaDeStockSi`. No article-shaped row hangs off a punto de venta (`articulos` is tenant-wide); the PV blocks on its own smallest customer datum, one `stock` row. Usage propagates UP the hierarchy, never down — the slice-3 amendment in its own words. Spec text deliberately byte-identical |
| BO-R3 | A catalog-only tenant with thousands of rows and zero sales is not deletable | PASS | Subsumed a fortiori by `BDO:762`: **one** article already refuses, so 3 000 do |
| BO-R3 | A second punto de venta blocks its empresa | PASS | `BDO:1301` seeds a second PV of the same empresa at `:1337` and asserts `empresa_en_uso` at `:1356-1358` |
| BO-R4 | A secondary FK to the same principal is discovered | PASS | `BDO:1133 LasFksSecundariasYLasDeNombreNoConvencionalTambienBloquean` (`id_punto_venta_destino`) |
| BO-R4 | Non-conventional FK property names are discovered | PASS | `BDO:1133` (`id_empleado_cierre`) |
| BO-R4 | A referencing type added by a future stage is covered without editing the guard | PASS | Mechanism `InventarioDeDependientes.cs:209-214` (FK walk, tenant-scope walk and PV bridge, none hand-written). Trip-wires `IDT:181 N3_ElInventarioCoincideConElGoldenVersionado` (109-line checked-in golden) and `IDT:269 N5_TodaTablaDeAlcanceDelAnclaApareceEnSuInventario` |
| BO-R5 | The completeness test covers all four principals | PASS | `IDT:61 N1_ConstruirNoTiraParaNingunaDeLasCuatroAnclas`, `IDT:86 N1_NingunaFkSeCaeEnSilencioYLosCarveOutsNoEjecutan` |
| BO-R5 | An unclassifiable type turns the build red, naming it | **PASS via Reconciliación 4** | The literal "fails the build on an unclassified type" is a tautology against a total classifier and was replaced, on the record, by N1 (mechanical-impossibility throws that name the CLR type and the branch origin), N2, **N3 (the golden — the actual trip-wire)** and N5. `IDT:181` and `IDT:269` both fail *naming* the missing table or pair; mutations M15/M16/M25 killed them by name |
| BO-R5 | A type claimed by two buckets turns the build red | **PASS via Reconciliación 4** | Two-bucket membership is unrepresentable: the classifier is a total ordered cascade (carve-out, then `Marcado`, then `SinMarca`, `InventarioDeDependientes.cs:477-506`), so the assertion is structural, and `IDT:326 NingunCarveOutAportaRamaParaNingunaAncla` fixes the carve-out/executable disjointness that would otherwise be the only overlap |
| BO-R5 | An untimestamped dependent blocks on mere existence | PASS | `BDO:826` — one `stock` row refuses the punto de venta |
| BO-R5 | An untimestamped type cannot be evaluated with the timestamp rule | PASS | `IDT:142 N2_UsaAnclaEquivaleAEntidadBaseConColumnaCreatedAt`; mutation U8 (force the anchor conjunct onto a `SinMarca` branch) died with Postgres `42703` — `stock` has no `created_at` |
| BO-R6 | Audit rows alone do not block | PASS | `BDO:888 SoloFilasDeAuditoriaNoBloqueanLaBajaYElRastroSigueResolviendo` |
| BO-R6 | The provisioning counter row alone does not block | PASS | `BDO:934 ElContadorDeNumeracionDeClientesNoBloqueaLaBaja` |
| BO-R6 | The carve-out list is asserted to have exactly two members | PASS | `IDT:312 LosCarveOutsSonExactamenteAuditoriaYNumeracionCliente`; source `InventarioDeDependientes.cs:180-184` |
| BO-R7 | A deleted article still blocks its tenant | PASS | `BDO:958 UnArticuloDadoDeBajaIgualBloqueaLaBajaDelTenant`. OD4 honoured — zero `deleted_at` conjuncts in the guard, with the reversible knob documented at `InspectorDeUso.cs:43-44` |
| BO-R7 | Row-level security still applies to the guard | PASS | `BDO:1630 ElGuardVeTodoLoDeSuTenantYNadaDeOtroSobreLaConexionDeAplicacion` (real `ways_app` non-superuser connection) |
| BO-R8 | A shared catalog row does not block an empresa | PASS | `BDO:1192 UnaFilaDeCatalogoCompartidoNoBloqueaALaEmpresaYUnaPropiaSi` (both directions) |
| BO-R9 | No orphan remains visible after a tenant is deleted | PASS | `BDO:441` |
| BO-R9 | Parent and children share one deletion instant | PASS | `BDO:405 LaCascadaDeUnTenantEstampaElMismoInstanteEnLosCuatroYDejaElEstadoEnBaja` — instant equality, not non-null |
| BO-R9 | The rest of the provisioning template survives | PASS | `BDO:441` reads `areas`, `medios_pago`, `listas_precio`, `clientes` and `numeraciones_clientes` with filters ignored |
| BO-R9 | Deleting an empresa cascades only to its puntos de venta | PASS | `BDO:985 LaBajaDeUnaEmpresaSoloArrastraSusPropiosPuntosDeVenta`; source `ServicioDeOrganizacion.cs:402-412` |
| BO-R9 | An already-deleted child keeps its original deletion instant | PASS | `BDO:493 UnHijoYaDadoDeBajaConservaSuInstanteOriginalCuandoCaeElTenant`; mechanism `ServicioDeOrganizacion.cs:229-231` — the cascade reads under the ambient `"BajaLogica"` filter, so a deleted child is never re-stamped (S3) |
| BO-R9 | The cascade never runs over used data | PASS | `BDO:712 UnaBajaRechazadaPorElGuardNoDejaNingunRastro`; source ordering `ServicioDeOrganizacion.cs:208-216` — the guard throws before `momento` is read |
| BO-R10 | Deleting a tenant's only empresa is refused | PASS | `BDO:1312-1316` (`ultima_empresa_del_tenant`); the tenant itself stays deletable per `BDO:374` |
| BO-R10 | Deleting one of two empresas succeeds | PASS | `BDO:1234 LosMinimosDisparanEnSuCondicionExactaYGananSobreElVeredictoDeUso` — the first of two is deleted successfully before the survivor re-triggers the minimum |
| BO-R10 | Deleting an empresa's only punto de venta is refused | PASS | `BDO:1319-1321` (`ultimo_punto_venta_de_la_empresa`) |
| BO-R10 | Already-deleted siblings do not count towards the minimum | PASS | `BDO:1234` (S2); mechanism `ServicioDeOrganizacion.cs:379` and `:529` — the `CountAsync` runs under the ambient filter |
| BO-R10 | The structural minimum wins over the usage verdict | PASS | `BDO:1234` (S6); source ordering `ServicioDeOrganizacion.cs:379-398` — minimum first, guard second |
| BO-R11 | Each code fires on its exact condition and no other | PASS | `BDO:1301 CadaUnoDeLosSeisCodigosDisparaSoloEnSuPropiaCondicion` — six fixtures, four through the API, `empresa_en_uso` and `punto_venta_en_uso` below the API per OD5 |
| BO-R11 | An unlabelled blocking table still yields the exact code | PASS | `BDO:1376 UnaTablaSinEtiquetaRindeElCodigoExactoYDegradaSoloElMensaje`; unit half `BET:320` |
| BO-R11 | The web maps copy from the code | PASS | `src/Ways.Web/src/api/bajas.ts:79-83` selects on `error.codigo`; `bajas.test.ts` *"los seis códigos rinden seis copias distintas"* and *"cambiar el mensaje no cambia la copia"*; mutation MS4 (select off `error.message`) killed 3 unit tests plus the four screen suites |
| BO-R12 | An out-of-scope delete is indistinguishable from a non-existent id | PASS | `BDO:1514 UnaBajaFueraDeAlcanceEs404YNuncaFiltraElUso` |
| BO-R12 | An out-of-scope used entity does not leak its usage | PASS | `BDO:1514`, heavy-usage arm. UI half: `Empresas.test.tsx:414` and `bajas.ts:52` — the server message is deliberately not appended |

### 4.2 `tenant-organization` (delta) — 5 requirements, 21 scenarios

| Req | Scenario | Verdict | Proof |
|---|---|---|---|
| TO-R1 | The empresas listing shows the tenant name | PASS | `PDO:410 LosListadosDeEmpresasYPuntosDeVentaLlevanLosNombresDeSusDuenios`; web `Empresas.test.tsx:104` *"rinde el NOMBRE del tenant en la columna, nunca el id"* |
| TO-R1 | The puntos de venta listing shows both owner names | PASS | `PDO:410`; web `PuntosVenta.tsx:445` and `PuntosVenta.test.tsx:129` |
| TO-R1 | Listing owner names costs one round trip | PASS | `PDO:333 CadaListadoCuestaExactamenteUnaIdaALaBase` — `ContadorDeComandos` = 1 for empresas |
| TO-R1 | No raw owner id is displayed | PASS | `Empresas.test.tsx:118`, `PuntosVenta.test.tsx:129`, `Usuarios.test.tsx:347`. Read through **Reconciliación 10** for the orphan filter *option* label `— (tenant 7)`, which is the D13 anomaly's handle rather than an owner identity; no **cell** presents an id |
| TO-R2 | Counts reflect the surviving children | PASS | `PDO:472 LosContadoresDelTenantCuentanSoloHijosVivosYNuncaAlPersonalDePlataforma`; web `Tenants.tsx:382-384` |
| TO-R2 | Logically deleted children are not counted | PASS | `PDO:472`; mutation M3 (`IgnoreQueryFilters` on the correlated `Count`) killed |
| TO-R2 | Platform staff are counted under no tenant | PASS | `PDO:472`; mutation M3b (drop the tenant correlation) killed |
| TO-R2 | Counting costs no extra round trip | PASS | `PDO:333` — tenants listing = 1 command |
| TO-R3 | Filtering empresas by tenant narrows the list | PASS | `Empresas.test.tsx:141` *"elegir un tenant angosta las filas SIN pedir nada más a la API"* |
| TO-R3 | Filtering puntos de venta by empresa narrows the list | PASS | `PuntosVenta.test.tsx:201` *"el filtro de empresa angosta las filas sin pedir nada más a la API"* |
| TO-R3 | A tenant admin's filter offers only their own tenant | PASS | `Empresas.test.tsx:232`, `PuntosVenta.test.tsx:222`, `Usuarios.test.tsx:270` — S5, options derive from the already-loaded rows |
| TO-R3 | Clearing the filter restores the full loaded list | PASS | `Empresas.test.tsx:153` |
| TO-R4 | A platform actor deletes a pristine tenant | PASS | `BDO:374` — 204 through `DELETE /api/plataforma/tenants/{id}` |
| TO-R4 | A tenant admin cannot reach the tenant delete route | PASS | `BDO:1674 LasPoliciesYLasTransicionesDeEstadoSeComportanIgualQueAntes` asserts `403`; surface half is the `SuperficieDeAutorizacionTests` allowlist entry |
| TO-R4 | A vendedor cannot delete a punto de venta they can list | PASS | `BDO:1674` — the same vendedor gets `200` on `GET /api/puntos-venta` and `403` on the `DELETE`; plus `CadaRutaSinPolicyDeGrupoApilaSuPolicyExigida`, the only walker that catches dropping the per-route `RequireAuthorization` |
| TO-R4 | No new policy exists after the change | PASS | `Politicas.cs` is absent from `git diff 22af91a..858e958 --name-only` (section 1c) |
| TO-R4 | Suspension and reactivation are unchanged | PASS | `BDO:1674` — neither reads nor writes `deleted_at` |
| TO-R5 | A deleted tenant carries both markers | PASS | `BDO:405`; source `ServicioDeOrganizacion.cs:242-243` — `Marcar` and `Estado = Baja` in the same `SaveChangesAsync` |
| TO-R5 | A deleted tenant's user cannot log in and gets a clean 403 | **PASS via Reconciliación 1 (OD6)** | `BDO:1578 ElAdminDeUnTenantDadoDeBajaRecibe401YElDeUnoSuspendido403`. The property the scenario protects (cannot log in, cleanly, no crash, no 500) holds; only the code differs — the login lookup runs under `"BajaLogica"` with no `IgnoreQueryFilters`, so the cascade-deleted admin is simply not found and gets `401 credenciales_invalidas`. The `403 tenant_suspendido` branch stays reachable for a **suspended** tenant and is asserted unchanged in the same test. Spec text deliberately byte-identical |
| TO-R5 | Reactivation cannot resurrect a deleted tenant | PASS | `BDO:1674` — `404` (S4), with the pre-existing `409 tenant_dado_de_baja` preserved as an unreachable backstop |
| TO-R5 | Suspension never writes Baja | PASS | `BDO:1674` — `estado = 'suspendido'`, `deleted_at IS NULL` |

### 4.3 `usuarios-tenant-scoping` (delta) — 2 requirements, 11 scenarios

| Req | Scenario | Verdict | Proof |
|---|---|---|---|
| UT-R1 | A tenant account shows its tenant name | PASS | `PDO:745 ElListadoDeUsuariosLlevaElTenantDeCadaCuentaYNuncaFabricaLaEtiquetaPlataforma`; web `Usuarios.test.tsx:170` |
| UT-R1 | Platform staff render as Plataforma | **PASS via Reconciliación 9** | The API sends `idTenant = null, nombreTenant = null` and never the literal (`PDO:745`; mutation M6 appending `?? "Plataforma"` killed); the web supplies the copy (`Usuarios.test.tsx:181`). The spec's `iff` holds for the platform-vs-tenant distinction and is deliberately false for the D13 orphan (non-null `IdTenant`, null name), asserted by `PDO:837 UnaCuentaCuyoTenantFueDadoDeBajaNoTraeNombreDeTenantEnNingunoDeLosTresCaminos` and rendered distinctly by `Usuarios.test.tsx:196`. Spec text deliberately byte-identical |
| UT-R1 | The tenant column costs no extra round trip | **PASS via Reconciliación 8** | `PDO:333` asserts `1, 1, 1, 2` — the three organization listings cost 1; `GET /api/usuarios` costs 2 because it is the **only paginated** listing and its `CountAsync` predates this change. The projection adds **zero** commands on all four endpoints; had it added one, usuarios would be 3. Mutation M1 (resolve the name from a second query) moved a listing 1 to 2 and killed the test. The pagination was not changed to make a sentence true |
| UT-R1 | A tenant admin never enumerates another tenant's name | PASS | `PDO:795 UnAdminDeTenantSoloVeSusCuentasYNuncaElNombreDeOtroTenant`; web `Usuarios.test.tsx:270` and `:575` *"un admin de tenant nunca pide el universo de tenants"* |
| UT-R2 | A usuario who has sold cannot be deleted | PASS | `BDO:1448 LaBajaDeUsuarioCorreElGuardDespuesDePoliticaDeRolesYNuncaEnSuLugar`, assertion 2 — `409 usuario_en_uso` and no `deleted_at` written |
| UT-R2 | A never-used usuario is still deletable | PASS | `BDO:1448`, assertion 1 — deletion succeeds and the audit row is written |
| UT-R2 | The provisioned admin is pristine until it operates | PASS | `BDO:1448`, assertions 1 then 2 on the same account before and after opening a shift |
| UT-R2 | Role policy is evaluated before the usage guard | PASS | `BDO:1448`, assertion 3 (Root target with heavy usage yields the `PoliticaDeRoles` error); structural half `BET:247 LaBajaDeUsuarioCorreSuGuardBajoElLockYDentroDeLaTransaccion` asserts the exact marker order |
| UT-R2 | Self-deletion is still forbidden regardless of usage | PASS | `BDO:1448`, assertion 4; `ServicioDeUsuarios.cs:391-392` keeps `ValidarPuedeIntervenirSobre` first and untouched |
| UT-R2 | An out-of-scope target stays a 404, never a usage disclosure | PASS | `BDO:1514`; `BDO:1448` assertion 5 |
| UT-R2 | Audit rows referencing the usuario do not block | PASS | `BDO:888` — the `Auditoria` carve-out, with the trail still resolving afterwards |

**Totals: 19/19 requirements PASS, 73/73 scenarios PASS.** Six scenarios pass *through* a recorded
Reconciliación (1, 4 twice, 8, 9, 10, 11). None is a miss, and in every case the spec text was
deliberately left byte-identical rather than edited mid-flight.

---

## 5. Design conformance, including the declared amendments

| Design element | Verdict | Evidence |
|---|---|---|
| **Three sources of the dependent set** (design declared one; the amendment declares three) | **PASS — amendment implemented and documented** | `InventarioDeDependientes.cs:209-214`: `GetReferencingForeignKeys()`, then `AgregarRamasDeAlcanceDeTenant` (`:259`, Tenant anchor only, `EntidadTenant` intersected with the `id_tenant` scope column, the same reflection idiom as `WaysDbContext.AplicarFiltroDeTenant`), then `AgregarRamasPuenteadasPorPuntoDeVenta` (`:347`, Empresa anchor only). The three-source contract is stated in the class doc-comment (`:116-133`), so the deviation from design's single source is declared, not smuggled |
| **The `puntos_venta` bridge** | **PASS** | `PuenteDeUso` record (`:55-59`); the Empresa anchor re-emits every executable branch of the PuntoVenta anchor bridged by `puntos_venta`, as one statement rather than N. The golden grew by exactly 17 lines of the form `Empresa \| <hoja> via puntos_venta \| <columnas> \| <balde>`, field shape unchanged. Behavioural proof: `IDU:173 UnTurnoDeCajaEnSuPuntoDeVentaBloqueaLaBajaDeLaEmpresa` and `IDU:217 ElTurnoDeOtroTenantNoBloqueaLaBajaDeLaEmpresaPorElPuente` |
| **`FabricaDeEstrategiaSinReintento` for the four bajas** | **PASS** | Three organization deletions at `ServicioDeOrganizacion.cs:619-620` (`EnUnaTransaccionDeBajaAsync`), asserted per method by `BET:195 LasTresBajasDeOrganizacionCorrenBajoLaEstrategiaSinReintento`, which also proves the wrapper is not an alias of the retryable one (`:214-217`). Usuario deletion at `ServicioDeUsuarios.cs:400`, asserted by `BET:247` including `Assert.DoesNotContain("CreateExecutionStrategy")`. Behavioural half: `BDO:1932 UnaFallaTransitoriaSobreElRastroNoSeReintentaYNoDuplicaNiFalsificaNada`. The retryable `EnUnaTransaccionAsync` (`:588`) is deliberately left on the four idempotent UPDATE paths |
| **`RamaDeUso.Esquema` and `RamaDeUso.Puente`** | **PASS** | `:78-84`. `Esquema` exists because the walk is pure and never sees `IModel` (`:65-66`). `Etiqueta` (`:102`) composes `<hoja> via <puente>` off the single `SeparadorDePuente` constant (`:92`), which `EtiquetasDeTablas.DescribirBloqueo` parses — the R2-6 root fix. `BET:341 LaCopiaDelBloqueoSaleDeLaRamaQueDisparoYNoDelConjuntoDeRamas` ties producer and parser in one assertion and exercises the mixed leaf (`parametros`) in both directions |
| **`InventarioCompleto` and `PropiedadesDeAncla`** | **PASS** | `:200-234` — the complete inventory including carve-outs, deterministically sorted by table, then columns, then bridge; it is the golden's source. `:243-248` defines the positional contract for `valoresDeClave`, resolved **by name** at both call sites (`ServicioDeOrganizacion.cs:688-696`, `ServicioDeUsuarios.cs:464-467`) so a future composite key cannot bind a value to the wrong parameter in silence |
| **D10 — tenant deletion is the only writer of `EstadoTenant.Baja`** | **PASS** | `ServicioDeOrganizacion.cs:242-243`, one `SaveChangesAsync`; suspension and reactivation still refuse to touch it (`BDO:1674`) |
| **D11 — `pg_advisory_xact_lock(idTenant, -20)`, disjoint from the POS total order** | **PASS** | `ServicioDeOrganizacion.cs:642-654`, raw ADO through `Database.OpenConnectionAsync` so the RLS GUC interceptor still fires. Disjointness asserted structurally by `BET:152 LasBajasDeOrganizacionSoloTocanTablasDeOrganizacion`, which also requires each deletion to register audit |
| **OD4 — a soft-deleted dependent still blocks** | **PASS** | Zero `deleted_at` conjuncts in the guard; `BDO:958` |
| **OD5 — empresa/PV deletion ships latent** | **PASS, honoured exactly** | No API-level test exists for `empresa_en_uso` or `punto_venta_en_uso`; both are proven below the API with a hand-seeded second empresa/PV (`BDO:1332-1363`). No creation endpoint was added |
| **OD6 — cascade-deleted admin gets 401** | **PASS** | `BDO:1578`, two codes, two assertions |
| **`db-error-backstops` structurally N/A** | **PASS** | `ManejadorDeErrores.cs` untouched. The deletion is an `UPDATE ... SET deleted_at`, against which `DeleteBehavior.Restrict` contributes nothing, so no SQLSTATE can fire and there is no branch to classify |

---

## 6. Delivered known limitations — stated as accepted, NOT as failures

These were decided, recorded and re-confirmed before delivery. None is a defect and none blocks
archive.

1. **OD5 latency — empresa and punto de venta deletion ships LATENT.** Ways has no endpoint that
   creates a second empresa or punto de venta (`AprovisionamientoEndpoints` exposes only
   `POST /api/plataforma/tenants`; `OrganizacionEndpoints` has no `POST`), so the structural minimum
   fires on **every** empresa/PV delete attempt through the API, and `empresa_en_uso` /
   `punto_venta_en_uso` are reachable only below the API layer. The code is correct and tested there.
   Same shape as `EstadoTenant.Baja`, which shipped in stage 1 and waited until this stage for its
   writer. **Owed to the owner at delivery (task 5.12).**
2. **R1 — a sale between the guard and the commit.** The advisory lock serializes deletion against
   deletion, not deletion against sale; under READ COMMITTED a sale, a shift or a comprobante can
   commit between the guard's `EXISTS` and the deletion's commit. Failure mode: a soft-deleted punto
   de venta or usuario with a later operation hanging off it. Closing it would put an administration
   lock on the POS hot path (the stage-19a D1 lesson). Recovery is a one-line
   `UPDATE ... SET deleted_at = NULL`, because B1 destroys nothing.
3. **T6 — FK index coverage is *reported*, not guaranteed.**
   `BDO:1736 CadaRamaDelInspectorTieneIndiceDeSoporteOQuedaReportada` reads `pg_indexes` and freezes
   an empty expected-uncovered set, so it is a trip-wire rather than a report nobody reads — but
   adding a missing index would be DDL, which the zero-schema gate forbids.
4. **Ambiguous commit under no-retry.** On a commit whose ACK is lost the deletion HAS succeeded and
   the operator receives a generic `500 error_interno` — a false negative, not merely an unabsorbed
   transient. Mitigated at the copy layer: `bajas.ts:59-61` renders *"No se pudo confirmar el
   resultado: verificá el listado antes de reintentar."* This is the accepted
   `AnularAsync`/`AjustarAsync` profile the repository already uses.
5. **The slice-5 modality scope is screen-inert, not document-modal.** Every control **inside** the
   four root screens is behind `bloqueado` (verified control-by-control by both judges and by the
   `MR1a-*` and `MR2-1` mutations). It is **not** modal at document level: (a) `Layout.tsx` renders
   about 25 `NavLink`s plus "Salir" outside any screen's `bloqueado`, so the operator can navigate
   away or log out with the DELETE undecided — a real escape, pre-existing, whose fix is a
   cross-cutting `inert`/portal change outside this stage; (b) the 409 banner renders as a sibling of
   the `aria-modal` dialog with no `role="alert"`, so `aria-modal` hides it from exactly the users it
   was added for; (c) focus is lost during the write and not restored on rejection, which jsdom
   cannot observe. All three are recorded in `tasks.md:1850-1879` as residuals rather than closed.
6. **The stage-2 backfill over-block on pre-existing tenants.**
   `InicializadorDeBaseDeDatos.cs:584` stamps the stage-2 backfill's `listas_precio` and `clientes`
   rows for PRE-EXISTING tenants with a later startup instant than those tenants' own `created_at`,
   so such a tenant is permanently blocked by `clientes` even with zero customer data. It is
   fail-**safe** (over-block), the discriminator is deliberately unchanged, and the 409 names the
   blocking table so the operator sees `clientes`. `InicializadorDeBaseDeDatos.cs` is untouched by
   criterion (c).

---

## 7. Findings

### CRITICAL — none

No finding blocks archive.

### WARNING

**W1 — `tasks.md` still shows `5.11` and `5.12` as `[ ]` although both are provably complete.**
Bookkeeping only: judgment-day rounds 1 and 2 for slice 5 are recorded in `tasks.md:1923-2038` and
`state.yaml:1135-1249`, and PR #173 is merged as the verified HEAD `858e958`. The strict
`sdd-verify` default treats any unchecked task as CRITICAL; it is downgraded here **because the
evidence that the work happened is external to the file and independently verifiable** (`git log`,
five merge commits, two recorded correction rounds with their mutation tables). *Action before
archive: tick 5.11 and 5.12 in `tasks.md` as part of the verify+archive commit.* No code change.

The same class, in the same file family: **`state.yaml`'s `phases.apply.status` is still
`in_progress`** although all five slices are merged. The change's own
`known_trap_from_explore` requires each phase entry to stay accurate, so this should be
corrected to `done` alongside the two task ticks. Bookkeeping only; the `notes` body of that
entry is already complete and accurate through slice 5's round 2.

**W2 — production doc-comments carry judgment-day round and finding ids, against the CLAUDE.md
no-changelog-comments rule.** Confirmed present at HEAD: `ServicioDeOrganizacion.cs:246`, `:593`,
`:722`; `ServicioDeUsuarios.cs:395`, `:408`, `:457`; `InspectorDeUso.cs`;
`InventarioDeDependientes.cs`; `EtiquetasDeTablas.cs`; `PayloadDeAuditoria.cs`. The change recorded
this itself as slice-5 carried input 6 and never closed it. Documentation-only; the technical content
of every one of these comments is accurate. *Action: rewrite to present intent when next touched*,
which is already the recorded instruction.

**W3 — the working tree carries `.claude/skills/` changes that are not part of this change.**
`git status` at verify time shows `M .claude/skills/react-async-state/SKILL.md` and
`?? .claude/skills/ef-retry-safe-writes/`. Neither appears in the stage diff `22af91a..858e958`, and
both post-date the session-start snapshot. They are most likely the skills-loop output CLAUDE.md
mandates, but they are **uncommitted and unreviewed**. *Action: the orchestrator's verify+archive
commit must decide about them explicitly rather than sweeping them in.* Also present and unrelated:
untracked `ventas-2026-dashboard.html`.

### SUGGESTION — carry-forwards the stage deliberately did not close

Every item below is recorded by the change itself; none blocks archive. Listed so `sdd-archive` can
register them.

**S1 — the physical-delete scan is narrower than its own record claims.** `BajasEstructuralesTests`
widened the three `Remove(` anchors but left `RemoveRange(` on `db\.(\w+)\.RemoveRange\(`, so
`dbPlataforma.Usuarios.RemoveRange(` would pass; a receiver without `db`/`Db` in its name
(`context.X.Remove(`) also escapes; and `LeerFuentesDeProduccion` enumerates `*.cs` only, so the
"`DELETE FROM` across ALL files" claim excludes a future `.sql` resource. **Zero matching call sites
and zero `.sql` files exist today** — my own independent scan in section 1d confirms it — so the
property holds and only the trip-wire's future coverage is narrow. Widen when next touching the file.

**S2 — `LIMIT 1` label attribution is plan-dependent** when the same leaf matches both its direct and
its bridged branch (a `parametros` row at empresa level AND one at a PV of that empresa, both after
the anchor). The 409 **code** and the verdict are unaffected; only the location phrase can point at
the wrong level. Below-API only (OD5). Closing it needs an `ORDER BY` on a branch-rank column.

**S3 — `estadoAnterior` is read from the identity map, not refreshed under the lock.** The
in-transaction re-read (`ServicioDeOrganizacion.cs:206`, `ServicioDeUsuarios.cs:417`) is an existence
check; EF returns the tracked instance without overwriting scalars. A concurrent
`SuspenderTenantAsync` between the pre-read and the lock makes the `tenant.baja` audit row record
`anterior.estado = "activo"` while the row read `suspendido`. Deletion, atomicity and the
404-on-lost-race are unaffected. Fix with `AsNoTracking` or `Entry(...).Reload()` when touched.

**S4 — the R2-2 race test forces its rendezvous sequentially** (the cascade completes inside
`TransactionStartedAsync`, before the loser reaches the lock). It proves the re-read and the 404; it
does **not** exercise advisory-lock contention, which is asserted structurally only.

**S5 — a platform-readable audit surface does not exist.** The `tenant.baja` row persists in tenant X
for forensics and export and is readable at the database, but `GET /api/auditoria` is Admin-only
(`Politicas.LecturaDeAuditoria`) and the cascade has just deleted every admin of that tenant.
`Politicas.cs` stays untouched by decision (criterion V5). **REOPEN** — the first time platform needs
a deleted tenant's trail without going to the database.

**S6 — six screens keep the native `confirm()`**: `Articulos`, `Clientes`, `Categorias`, `Ofertas`,
`PaginaCatalogo`, `Proveedores`. Out of stage scope; listed for the rule-10 sweep that adopts the
shared `ConfirmacionDeBaja` gate.

**S7 — slice-5 coverage gaps, not defects.** The per-screen `disparador` capture and the R2-5
form-close are mutated and asserted only on the shared component and on `Tenants`; the `Empresas`,
`PuntosVenta` and `Usuarios` wiring has no own kill. The `tabindex="-1"` written on the `Box` heading
is never removed. `disparadorDeLaPuerta` is nulled on cancel but not on success. `errorAlta` is
browser-unreachable, and `confirmarBaja`/`accion` still clear it while `pedirBaja`/`cancelarBaja`
preserve it.

**S8 — the retry double-`Add` class pre-exists outside this stage**, in
`ServicioDePrecios.AbrirNuevoPrecioAsync` (`:123`, `:226`, `:229`, `:240`) and
`ServicioDeUsuarios.CrearAsync` (`:201`, `:207`, `:215`, `:219`). Not touched by stage 20.
*Chip `task_29095520`.*

**S9 — `SSH.NET 2024.1.0` carries `NU1903` (`GHSA-q939-rpr3-3284`, high severity)** on
`tests/Ways.IntegrationTests.csproj`. Pre-existing, test-only, unchanged by this stage; the build is
otherwise clean. *Chip `task_869978db`.*

---

## 8. Summary

| Dimension | Verdict |
|---|---|
| Binding zero-schema criteria (a)-(d) plus V5/V6/V7/V10/V13 | **PASS** — all re-measured at HEAD, none inferred |
| 3 specs / 19 requirements / 73 scenarios | **PASS** — 73/73, six scenarios read through Reconciliaciones 1, 4, 8, 9, 10 and 11 |
| Task ledger (116 lines) | **PASS with W1** — 114 `[x]`; 5.11 and 5.12 complete in fact, unticked in the file |
| Design conformance and declared amendments | **PASS** — three sources, `puntos_venta` bridge, `FabricaDeEstrategiaSinReintento`, `RamaDeUso.Esquema`/`Puente`, `InventarioCompleto`/`PropiedadesDeAncla`, all present and tested |
| Runtime suites (8 commands) | **PASS** — all exit `0`; 545 / 434 / 1780 / 65 files with 1102 tests; integration run once and alone, zero flakes |
| Delivered known limitations | Section 6 — accepted, not failures |
| Carry-forwards for archive to register | Nine, section 7 |

**Recommendation: proceed to `sdd-archive`.** No CRITICAL blocks archival. W1 is a one-line
`tasks.md` correction the archive commit should carry; W2 and W3 need no code change and no
re-opening of the DB gate.
