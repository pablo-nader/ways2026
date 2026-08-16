# Proposal: Stage 14 — Auditoría y trazabilidad de operaciones sensibles

## Intent

doc-11:201-203 asks for one thing: **quién, cuándo y qué** on the sensitive operations —
price changes, comprobante anulaciones, stock adjustments, role/permission changes, cuenta
corriente reliquidaciones — behind **one filtrable, exportable query**.

`explore.md` proves the system is much closer than the doc assumes, and much emptier exactly
where it matters:

| Domain | Actor today | Verdict |
|---|---|---|
| Stock (`movimientos_stock`) | `id_empleado` on **every** row, `motivo` + `observaciones` | Traced. The gap is a unified query, not capture |
| Cuenta corriente (`movimientos_cuenta_corriente`) | `id_empleado` + `detalle` JSON | Traced. Same |
| **Precios** | **none** | **Zero trace of who.** The flagship gap |
| **Cambios de rol** (`ServicioDeUsuarios.ActualizarAsync:144-148`) | **none** | **Zero actor, and the old value is overwritten** |
| **Anulación de un comprobante 100% servicio sin CC** | **none** | `MarcarAnuladoAsync` bumps `estado`/`updated_at`; the reversal ledgers that would carry the actor are **not written** because there is nothing to reverse |

The argument doc-11:205-209 makes for sequencing this stage is the one that governs its
design: **what is not audited before this stage is not reconstructible after it.** Every day
this ships later is a day of blind history that no later stage can recover. That is why the
first pass is measured by *coverage of the write paths*, not by the beauty of the screen.

`IContextoDeUsuario` is already injected in **all six** candidate services
(`explore.md` §"Contexto de usuario disponible"). The gap is never "I don't have the user";
it is "I have the user and I don't persist him".

## Scope

### In Scope

- **One append-only table, `auditoria`** — tenant-scoped, `id_punto_venta` nullable, with an
  open `accion` catalog and two `jsonb` payloads. One migration (decision 1, gate section).
- **One writer, `ServicioDeAuditoria`, in the caller's transaction** — an operation that
  cannot be audited **does not happen** (decision 4).
- **Twelve audited actions across six services** — the five domains doc-11 names, resolved
  into concrete write paths, plus the user-lifecycle operations the doc does not name and that
  are unambiguously sensitive (decision 5).
- **A per-action payload contract**: `valor_anterior` / `valor_nuevo`, key-subset rule,
  snake_case keys mirroring the schema, and an explicit **secret denylist** (decisions 2, 7).
- **`GET /api/auditoria`** — paginated, filtered by date range, `accion`, actor, entidad +
  `id_entidad`, and punto de venta; plus its **`/export` sibling** over the stage-11
  `TablaExportable` / `GuardaDeTope` contract, reused verbatim.
- **A new policy, `LecturaDeAuditoria`, Admin-only** (decision 6).
- **`Auditoria.tsx`** — the filter screen, following the `Vencimientos.tsx` / `Reposicion.tsx`
  shape, with the download button.
- **doc 10 gains the `auditoria` table** in §6-adjacent position with its "Estado (Etapa 14)"
  annotation, written from inside slice 1 (the stage-12 task-1.17 discipline).

### Out of Scope

- **Retention, TTL, partitioning or archival of any kind** — decided explicitly as *no policy
  in this stage*, with the tripwire and the honest cost of deferring named (decision 3).
- **Migrating `movimientos_cuenta_corriente.detalle` from `text` to `jsonb`**, or re-serializing
  its existing PascalCase payloads. Rewriting rows that already mean something is the opposite
  of what an audit stage should do (decision 7).
- **Auditing the checkout emission path.** A sale being *emitted* is not a sensitive
  exception; doc-11 names *anulaciones*, not emisiones. `ServicioDeVentas`' checkout
  transaction is **not touched** (decision 4).
- **`stock.transferencia`** — a transfer has an origin *and* a destination punto de venta; a
  single `id_punto_venta` column cannot express it faithfully, it destroys no value, and both
  halves are already actor-stamped (decision 5).
- **Ajuste manual de cuenta corriente, gastos, movimientos de caja/tesorería, arqueos** —
  already actor-stamped ledgers, not named by the doc. Registered as the first extension.
- **Platform-level events** (root provisioning tenants, edits over accounts whose
  `usuarios.id_tenant IS NULL`). `auditoria.id_tenant` is `NOT NULL` by design; a platform
  audit log is a different product (decision 1).
- **Immutability enforced at the database** (revoked UPDATE/DELETE grants, triggers, rules) —
  a deliberate non-decision with its reason recorded in the gate section.
- **jsonb-key filtering / GIN index** — filters in v1 are columnar only.
- **Retroactive backfill.** Nothing before the deploy can be reconstructed; that is precisely
  doc-11's argument for not delaying further, and it is stated in the spec rather than implied.
- **A per-entity "historial" tab inside each editor screen** — the API supports it
  (`entidad` + `id_entidad` filter); the UI for it is a follow-up.
- **The owner's reserved items** (comisiones formula, Supervisor margin, `OperacionDePos` read
  model, cierre de caja por rol, export branding) and every open carryover
  (`articulos_empresas` replace-set gap, importe CHECK micro-gate, `ways_owner` superuser).
  Untouched.

## Capabilities

### New Capabilities

- **`auditoria-de-operaciones`** — owns the stage end to end: the `auditoria` row's meaning
  and immutability, the action catalog and its `entidad` / `id_entidad` convention, the
  payload contract (key-subset rule, snake_case, denylist), the same-transaction /
  fail-closed rule, the coverage requirement for each of the twelve actions, the query with
  its filters and pagination, the export sibling, and the `LecturaDeAuditoria` policy.

Following the stage-11/12/13 precedent (a new capability owns its surface end to end rather
than smearing one sentence across nine specs), **every "operation X writes exactly one audit
row" requirement lives here**, not in the audited domain's spec.

### Modified Capabilities

Only where the audit changes a question the capability *already claims to answer*:

- **`precios`** — ADDED: a price change is **attributable**. Today "Price History Never
  Overwrites" gives a perfect history of *what* with no trace of *who*; the write path now
  records the actor in the same transaction and **fails rather than losing the attribution**.
- **`comprobantes-venta`** — ADDED: an anulación is attributable **regardless of the
  comprobante's composition**. Named explicitly because the hole is exactly the composition
  the reversal ledgers do not cover (100% servicio, sin cuenta corriente).
- **`comprobantes-compra`** — ADDED: same statement over `ServicioDeCompras.AnularAsync`.

**Not modified**: `stock`, `conteo-de-inventario`, `lotes-y-vencimientos` (decomiso),
`reliquidacion-a-precio-del-dia`, `ajustes-de-cuenta-corriente`, `usuarios-y-login`,
`usuarios-tenant-scoping`, `operacion-de-pos`, `reportes-de-gestion`,
`exportacion-de-reportes` (this stage is a **consumer** of the export contract, not an
amender), `tablero`. Their behaviour is unchanged; their new audit row is owned by the new
capability.

## Approach

**One table, one writer, twelve call sites, one screen.**

The audit row never stores an arithmetic truth. Quantities live in `movimientos_stock`,
balances in `movimientos_cuenta_corriente`, amounts in `precios` — and they stay there. What
`auditoria` adds is the **actor stamp plus a pointer** (`entidad` + `id_entidad`, and the
ledger row id inside the payload). That framing is what makes the single-table approach safe:
there is **no duplicated source of truth, only a duplicated actor stamp** — which is the
entire point of the stage — so the two stores cannot disagree about a number, because the
number lives in exactly one of them.

Everything else follows from that: the writer joins the caller's existing transaction, the
payloads are small and per-action documented, and the query is a plain filtered listing over
one table with three indexes.

## Autonomous decisions

Under delegated technical authority, conservative and reversible bias. Each records context,
options with tradeoffs, the decision, and **what it costs to reverse it**.

---

### 1 — One generic table `auditoria`, `id_tenant NOT NULL`, `id_punto_venta` nullable. Not per-domain tables, not a UNION view.

**Context.** `explore.md` offers three approaches. doc-11:214 states the fork verbatim:
"tabla única de auditoría genérica versus registro por dominio".

**Options.**

| Option | Pro | Contra |
|---|---|---|
| **Single generic table** | One query, one export, one RLS policy, one gate; one write helper replicated across call sites; a new stage adds an action with **zero DDL** | `jsonb` without a strong per-action schema; cross-action queries must reach into the payload |
| Per-domain ledgers extended | Each shape stays typed; maximum reuse of what exists | **Does not deliver the doc's mandate** — a unified query becomes a five-way UNION; and the two real gaps (`precios`, `usuarios`) still need new storage, so the saving is illusory |
| New table for the 2 gaps + SQL view UNIONing the ledgers | Least new writing | The view is brittle against every ledger's schema; pagination and `COUNT(*)` over a UNION are awkward and slow; and the anulación-sin-líneas hole **stays open** unless anulación also writes the new table — which collapses this option into the first |

**Decision.** **Single generic table.** Two sub-decisions the explore's minimal model left
implicit, both challenged and resolved here:

- **`id_tenant integer NOT NULL`, carrying the tenant of the *audited subject*, not of the
  actor.** When a root/platform actor edits a tenant's user, the row belongs to **the tenant
  that must be able to see it** — "root changed my admin's role" is that tenant's audit, not
  the platform's. The rejected alternative (nullable `id_tenant` for platform events) makes
  every row invisible to every tenant and every query NULL-aware, to build a **platform** audit
  log — a different product, explicitly out of scope. Consequence, stated rather than hidden:
  operations whose subject has no tenant (`usuarios.id_tenant IS NULL`, platform accounts) are
  **not in pass 1** and are listed in Out of Scope.
- **`id_punto_venta integer NULL`, one table, not two.** A price change and a role change are
  tenant-wide; an anulación and a stock ajuste are per-PV. Splitting into
  `auditoria` + `auditoria_global` would double the DDL, the RLS, the query, the export and
  the screen to avoid **one nullable integer** — and would put the split exactly where the
  product wants a single chronological list. Precedent: `movimientos_tesoreria.id_turno_caja`
  is nullable for the same class of reason.

**Cost of reversing.** Splitting later is a table-copy migration with a mechanical
`WHERE id_punto_venta IS NULL` predicate over rows nobody has to reinterpret. Going the other
way (merging two tables that drifted apart) requires reconciling two `accion` catalogs. The
cheap direction is the one taken.

---

### 2 — The payload is a **bounded, per-action documented field set**, never a full row dump. `valor_anterior` ⊆ `valor_nuevo` by key.

**Context.** doc-11:215 asks "cuánto detalle se guarda (valor anterior y nuevo, o solo el
hecho)". `explore.md` question 2 sharpens it: full values or the relevant delta.

**The decisive argument is security, not size.** A generic "serialize the entity before and
after" writer over `usuarios` puts **`hash_password` into the audit table** on the first role
change — an Admin-readable, append-only, never-deleted copy of every password hash in the
tenant. A full-row convention makes that the *default* behaviour and a code review the only
thing standing between the system and it. A per-action field list makes it impossible.

Two secondary arguments: a full dump breaks meaning every time the audited table gains a
column, and an audit row exists to be **read by a human**, who does not want 24 unchanged
fields around the one that moved.

**Decision.** Per action, a **named, documented field set**, with three binding rules:

- **Key-subset rule**: every key in `valor_anterior` MUST be present in `valor_nuevo`. The
  reverse does not hold — `valor_nuevo` also carries the operation's own metadata (the ledger
  row id, `motivo`, `observaciones`). This makes a before/after diff mechanically legible and
  is a testable invariant, not a convention.
- **`valor_anterior IS NULL`** exactly when there was no prior state (first price of an
  articulo, `usuario.alta`) or the action is a pure fact (`usuario.password`).
- **Denylist, binding**: never `hash_password`, never a token, never a session artifact,
  never a full entity serialization. Asserted by test on the `usuarios` actions.

The pass-1 payload contract:

| `accion` | `entidad` / `id_entidad` | `id_punto_venta` | `valor_anterior` | `valor_nuevo` |
|---|---|---|---|---|
| `precio.cambio` | `articulo` / `id_articulo` | NULL | `{id_lista_precio, monto, vigente_desde}` or NULL | same keys, new values |
| `venta.anulacion` | `comprobante_venta` / id | PV del comprobante | `{estado:"emitido"}` | `{estado:"anulado"}` |
| `compra.anulacion` | `comprobante_compra` / id | PV del comprobante | `{estado:<previo>}` | `{estado:"anulado"}` |
| `stock.ajuste` | `articulo` / `id_articulo` | PV | `{cantidad}` | `{cantidad, id_movimiento_stock, observaciones}` |
| `stock.decomiso` | `articulo` / `id_articulo` | PV | `{cantidad}` | `{cantidad, id_movimiento_stock, observaciones, id_lote}` |
| `stock.conteo` | `articulo` / `id_articulo` | PV | `{cantidad}` | `{cantidad, id_movimiento_stock, observaciones}` |
| `cc.reliquidacion` | `cliente` / `id_cliente` | PV | `{saldo}` | `{saldo, id_movimiento, consumos_actualizados, diferencia}` |
| `usuario.alta` | `usuario` / `id_usuario` | NULL | NULL | `{usuario, mail, id_rol, estado}` |
| `usuario.actualizacion` | `usuario` / `id_usuario` | NULL | `{usuario, mail, id_rol, estado}` | same keys |
| `usuario.baja` | `usuario` / `id_usuario` | NULL | `{estado}` | `{estado:"eliminado"}` |
| `usuario.desbloqueo` | `usuario` / `id_usuario` | NULL | `{estado:"bloqueado"}` | `{estado:"activo"}` |
| `usuario.password` | `usuario` / `id_usuario` | NULL | NULL | `{por_el_propio_usuario}` |

`entidad` / `id_entidad` always name **the aggregate a human searches by** (the articulo, the
comprobante, the usuario, the cliente) — never the ledger row, whose id travels inside the
payload. That is what makes "show me everything that happened to this articulo" one indexed
query.

**Cost of reversing.** Widening a payload later is additive and needs no migration: old rows
keep the narrower key set, and the key-subset rule still holds within each row. Narrowing —
i.e. discovering after the fact that a hash was being written — is a data-destruction task on
an append-only table. The irreversible direction is the one refused.

---

### 3 — **No retention policy, no partitioning, no TTL in this stage.** Decided, priced, and tripwired — not left open.

**Context.** doc-11:215 lists "política de retención y tamaño de la tabla" as open;
`explore.md` names unbounded growth as the stage's first risk.

**Priced honestly.** The audited operations are **exceptions**, not traffic: price changes,
anulaciones, ajustes, role changes, reliquidaciones. A busy single-PV tenant plausibly writes
hundreds per month; ten thousand rows a year, with a ~200-byte payload, is **single-digit
megabytes** — three orders of magnitude below anything Postgres notices, and far below
`movimientos_stock`, which this system already writes one row per sold line into and never
worried about.

Against that: a retention policy needs a scheduler (nothing in `src` runs outside a request —
stage 13's decision 2 established this), a deletion rule that an auditor would accept, and an
archival destination. That is a stage, not a slice, and building it now would be **the most
expensive form of speculation**: infrastructure for a problem whose existence is unproven.

**The honest cost of deferring, stated because it is not zero.** Postgres cannot convert a
populated ordinary table into a partitioned one in place; a future `PARTITION BY RANGE
(creado_el)` means creating the partitioned table and moving the rows. That cost is
**proportional to the volume at conversion time** — trivial at ten thousand rows, real at
fifty million. Deferring is therefore only safe **if someone is watching**.

**Decision.** **No retention, no partitioning.** Watched by a named, measurable tripwire:

1. Any single tenant exceeds **5 million** `auditoria` rows, **or** the table exceeds **5 GB**,
   at which point partitioning is designed before it is expensive.
2. Any stage acquires a scheduler/job runner for its own reasons — the marginal cost of a
   retention job collapses at that moment (stage 13's decision-2 pattern; Etapa 16 is the
   realistic candidate).
3. A legal/compliance retention requirement arrives from a real customer, which would fix the
   number this stage has no basis to invent.

Two cheap properties are shipped now precisely so the future decision stays cheap: the table
is **strictly append-only** and `creado_el` is **monotonic and indexed**, so both a
`DELETE WHERE creado_el < X` and a range-partition conversion are mechanical.

**Cost of reversing.** Adding retention later is additive. Adding it *now*, wrongly, deletes
history that doc-11:207 says is unrecoverable — the exact failure this stage exists to
prevent.

---

### 4 — **Same transaction, fail-closed.** And the checkout hot path is untouched, by scope rather than by technique.

**Context.** doc-11:216 poses "misma transacción (consistente pero acoplada) versus diferida".
`explore.md` frames the checkout's 16-query guard as the tension.

**The tension mostly dissolves under inspection, and one factual correction is owed.**
`explore.md` states that an `INSERT ... RETURNING` via `ExecuteScalarAsync` counts against the
guard. It does not. `ContadorDeComandos`
(`tests/Ways.IntegrationTests/VentasCheckoutTests.cs:864-882`) overrides **only**
`ReaderExecuting` / `ReaderExecutingAsync`; `ExecuteNonQueryAsync` **and** `ExecuteScalarAsync`
are both invisible to it, exactly as the doc-comments at `ServicioDeVentas.cs:1045-1054` and
`1080-1083` state ("`RETURNING` vía `ExecuteScalarAsync` … invisible al guard").

**But the guard is not the reason the checkout is safe.** The real reason is scope:
**emitting a sale is not a sensitive exception.** doc-11:202 names *anulaciones*, not
emisiones — and an emission is already fully attributable through
`comprobantes_venta.id_empleado`. So this stage writes **nothing** in the checkout
transaction. The invisible-INSERT technique is recorded as the escape hatch for the day an
audited action does land on a budgeted path; it is not used here.

**Deferred writing, priced.** A queue or outbox needs its own table, a drainer (no scheduler
exists), retry/poison handling, and ordering guarantees — and it introduces the one failure
mode an audit log must not have: **the business commit succeeds and the audit row is lost**,
leaving an operation that happened and nobody performed. That is precisely the state doc-11
describes as unrecoverable.

**Decision.** **Same transaction, always. Fail-closed: an operation that cannot be audited
does not happen.**

The consequence is stated rather than hidden: a defect in the audit writer takes down price
changes, anulaciones and role changes. It is accepted because (a) the writer is a single
INSERT with no external dependency, no network call and no serialization of untrusted input;
(b) the alternative — best-effort auditing — produces a log that is *sometimes* complete,
which for an audit log is worse than no log, because it cannot be relied on in the one
conversation it exists for.

**Mechanism** is left to `sdd-design`, bounded by two constraints: it MUST enlist in the
caller's existing transaction (EF `SaveChangesAsync` where the caller is EF —
`ServicioDePrecios`, `ServicioDeUsuarios`; a raw statement on the caller's
`DbConnection`/`DbTransaction` where it is ADO — the anulación paths), and it MUST NOT add a
round trip to any path under a query-count guard.

**Cost of reversing.** Moving to deferred writing later means adding an outbox and changing
the writer's call shape — the twelve call sites and the payload contract survive untouched.
Moving from deferred to transactional would mean explaining the gap in the history.

---

### 5 — Pass 1 = **twelve actions across six services**: the five domains doc-11 names, plus the user lifecycle. `transferencia` is excluded on a structural ground.

**Context.** doc-11:217 leaves "qué operaciones entran en la primera pasada" open;
`explore.md` question 5 asks whether the user-lifecycle paths join the five.

**Decision.** The list in decision 2's table. The reasoning per family:

- **`precio.cambio`** (`ServicioDePrecios.AbrirNuevoPrecioAsync`) — the flagship gap. Every
  path that opens a price row writes one audit row; a price change that touches a predecessor
  (the close/reopen dance at lines 167-183) is **one** operation and therefore **one** row.
- **`venta.anulacion` / `compra.anulacion`** — the named gap. Written unconditionally,
  **including** the 100%-servicio-sin-CC comprobante, which is the only case with no trace
  whatsoever today. That case is a spec scenario, not an afterthought.
- **`stock.ajuste` / `stock.decomiso` / `stock.conteo`** — the three **discretionary** stock
  paths: the operator states a quantity or a total of their choosing. That is the classic
  shrinkage-concealment vector, and it is exactly what "ajustes de stock" means in doc-11:202.
  A zero-difference conteo writes no ledger row (`conteo-de-inventario` spec, line 78) and
  therefore **no audit row either** — the audit follows the operation's effect, and this limit
  is recorded rather than discovered.
- **`stock.transferencia` — excluded**, and not merely for economy: a transfer has an origin
  **and** a destination punto de venta, which a single `id_punto_venta` column cannot express
  without lying. It also destroys no value (both halves stay inside the tenant) and both rows
  are already actor-stamped. Reopening it means deciding whether a transfer is one row or two —
  a modelling question that deserves its own decision, not a silent default.
- **`cc.reliquidacion`** — named by the doc; already actor-stamped, joined so the unified query
  is actually unified.
- **The user lifecycle (`alta`, `actualizacion`, `baja`, `desbloqueo`, `password`)** — doc-11
  says "cambios de rol y permisos", and the honest observation is that `ActualizarAsync`
  changes rol, estado, usuario **and** mail in one call: auditing "only the rol" would mean
  reading the same object and recording one of its four mutable fields. `alta` is included
  because **creating an admin account is the single most sensitive operation in the system**
  and costs one more call in a file already being touched; `password` records **the fact
  only** (decision 2's denylist).

**Explicitly not in pass 1**, each with its reason, so nothing disappears by silence: stock
transferencia (above); ajuste manual de cuenta corriente, gastos, movimientos de caja and
tesorería, arqueos (already actor-stamped ledgers, not named by the doc); parámetros
operativos and the stage-13 mínimos (configuration, not operations — recorded as the most
likely first extension, since "who lowered the mínimo" is a real question); catálogo ABM
(articulos, clientes, proveedores — high volume, low sensitivity); platform provisioning
(decision 1).

**Cost of reversing.** Adding an action is a constant plus a call site plus a spec scenario —
**zero DDL** (decision 8). Removing one leaves rows in an append-only table whose `accion` no
longer has a writer, which is harmless and self-documenting (the `motivo_stock` reserved-value
precedent, doc-10:551-553).

---

### 6 — Reading the audit log is **Admin-only, under a new named policy `LecturaDeAuditoria`**. Not `LecturaDeReportes`, not `GestionDeCatalogo`.

**Context.** `explore.md` question 6; unexplored until now.

**Options.**

| Option | Verdict |
|---|---|
| `LecturaDeReportes` (Supervisor + Admin) | **Rejected.** The log records what Supervisors do — reliquidaciones and CC ajustes are *theirs* (`SupervisionDeCuentaCorriente`). A supervised actor who can read the supervision log defeats the control |
| `GestionDeCatalogo` (Admin) reused | Rejected as semantic drift: it is the catalog/parameter ABM gate. Reading an audit log is not managing a catalog, and reusing it makes a future divergence a migration of intent |
| **A new `LecturaDeAuditoria`, Admin-only** | **Chosen** |

**Decision.** `Politicas.LecturaDeAuditoria` — Admin only, exactly the shape of
`LecturaDeRentabilidad`, which exists for the same reason (a read sensitive enough to earn its
own name rather than ride an existing gate). **Not stacked** over `LecturaDeReportes`: ASP.NET
composes with AND and Admin ∈ Supervisor+Admin, so stacking would be a no-op that suggests a
relationship that does not exist. Root is outside, same criterion as `GestionDeCatalogo`
("root administra tenants, no opera ninguno").

Two consequences, both testable: **a Supervisor receives 403** on `/api/auditoria` and its
export; and within a tenant an Admin sees **every punto de venta** (the PV is a filter, not a
boundary) — an Admin is already tenant-wide, and a per-PV audit boundary would make
cross-PV movement invisible to the only person who can act on it.

**Cost of reversing.** Widening to Supervisor later is one claim in one policy registration.
Narrowing after a Supervisor has been reading the log is a conversation, not a code change.

---

### 7 — `jsonb`, with **snake_case keys mirroring the schema**. The existing `detalle text` precedent is **not** migrated.

**Context.** `explore.md` §"Patrones de schema" and question 7: the only precedent
(`ServicioDeReliquidacion.cs:121`) serializes with C#'s default PascalCase into a `text`
column. Confirmed: **no `JsonSerializerOptions` with a naming policy exists anywhere in
`src/`** — the PascalCase is a default, not a decision.

**Decision, in two parts.**

- **`jsonb`, not `text`**, for the two new columns. `jsonb` validates the document at write
  time (a malformed payload fails immediately rather than at read, years later), and it keeps
  the door open to `->>` filtering and GIN indexing without a migration. The cost — key-order
  and whitespace normalization — is irrelevant for a document nobody diffs byte-wise.
- **snake_case keys mirroring the audited table's column names**, via a dedicated
  `JsonSerializerOptions` (`JsonNamingPolicy.SnakeCaseLower`) owned by the audit writer and
  **not** registered globally. The keys are Spanish domain names because they *are* the schema
  (`id_lista_precio`, `id_rol`, `cantidad`) — doc-10's naming convention applies to data stored
  in the database, and a payload that reads `IdListaPrecio` inside a column next to
  `id_punto_venta` is inconsistent for no reason. Scoping the options to the writer keeps every
  HTTP contract in the repo byte-identical.
- **`movimientos_cuenta_corriente.detalle` is not touched** — not its type, not its existing
  rows, not its serialization. Rewriting the meaning of rows that already exist is exactly what
  an audit stage must not do, and there is no reader that would benefit.

**Cost of reversing.** A naming change later applies only to rows written after it, leaving a
log with two conventions and a cutoff date to remember — cheap to avoid now, annoying forever
if gotten wrong. `jsonb` → `text` is a trivial cast; `text` → `jsonb` is a validating rewrite
that fails on the first malformed historical row.

---

### 8 — `accion` is **`text` with a non-empty CHECK and an application-owned catalog**. Not a Postgres enum.

**Context.** The repo has native enums (`motivo_stock`, `tipo_movimiento_cc`,
`estado_turno`), so an enum would look like the house style.

**Two decisive arguments against.** doc-10's principle 4 restricts native enums to *state
machines* ("los padrones son datos, no enums"), and `accion` is a growing catalog, not a state
machine. More concretely: stage 12 proved that `ALTER TYPE ... ADD VALUE` is **irreversible** —
Postgres cannot drop an enum value — and cannot be used in the same transaction that adds it.
An audit action catalog is expected to grow **with every future stage**; making each new action
an irreversible migration is the worst possible trade for a column nobody joins on.

**Decision.** `accion text NOT NULL` with `ck_auditoria_accion_no_vacia`. The authoritative
catalog is a C# constant set (`AccionAuditada`) in Domain, unit-tested, with the naming
convention `<dominio>.<operacion>` fixed by spec. The database stays permissive; the
application stays strict. `entidad` follows the same rule.

**Cost of reversing.** Tightening later (a CHECK with an explicit value list, or a lookup
table) is one additive migration over data that already satisfies it. Loosening an enum is not
possible at all.

---

## Modelo de datos propuesto

> **DB CHANGE GATE — this section is the contract.** It states the complete model at table
> level. Anything `sdd-apply` writes that is not here is a scope violation that reopens the
> gate. On implementation, **doc 10 is updated** with the new table, following the
> "Estado (Etapa N)" annotation convention already used there.

**Gate verdict proposed: ONE migration**, named `AuditoriaEtapa14`. PostgreSQL 17.
**One new table. No existing table, column, index, constraint, enum or type is altered or
dropped by this stage.** No data statement over existing rows.

### A. New table — `auditoria`

**Scoping category (doc 09): operativa** (`id_tenant` + `id_punto_venta`) — with
`id_punto_venta` **nullable**, which is the one deviation from the category's default shape and
is justified rather than assumed: doc-09:86 defines operativa as "nace en un punto de venta y
no se comparte jamás", which holds for anulaciones, stock and reliquidación; the tenant-wide
events (precio, usuario) touch tables that doc-09 classifies as *tenant-wide*
(`precios`, doc-09:85) and *global-ish* (`usuarios`, whose `id_tenant` is nullable for platform
staff). A single chronological log spanning both is the product requirement (decision 1), so
the row is operativa with a nullable PV rather than two tables. It is **not** catálogo: it
carries no `id_empresa`.

It inherits **no** `EntidadBase` columns: an immutable fact has no `updated_at` and no soft
delete — the same criterion `movimientos_stock` and `movimientos_cuenta_corriente` already
apply.

```sql
auditoria (                          -- [operativa — id_punto_venta NULL en eventos tenant-wide]
    id_auditoria    bigint      GENERATED BY DEFAULT AS IDENTITY,
    id_tenant       integer     NOT NULL,
    id_punto_venta  integer     NULL,      -- NULL: precio.*, usuario.* (tenant-wide)
    id_actor        integer     NOT NULL,  -- usuarios.id_usuario, siempre del contexto autenticado
    accion          text        NOT NULL,  -- '<dominio>.<operacion>', catálogo en la aplicación
    entidad         text        NOT NULL,  -- 'articulo' | 'comprobante_venta' | 'usuario' | 'cliente' | ...
    id_entidad      integer     NOT NULL,  -- el agregado que un humano busca, nunca la fila del ledger
    valor_anterior  jsonb       NULL,      -- NULL si no había estado previo o la acción es un hecho puro
    valor_nuevo     jsonb       NOT NULL,
    creado_el       timestamptz NOT NULL,  -- IRelojDelSistema, sin DEFAULT (ver abajo)
    CONSTRAINT pk_auditoria PRIMARY KEY (id_auditoria)
);
```

| Element | Name | Definition |
|---|---|---|
| PK | `pk_auditoria` | `(id_auditoria)` — `bigint`, `GENERATED BY DEFAULT AS IDENTITY` (repo convention, EF's `IdentityByDefaultColumn`; the explore's `ALWAYS` is corrected to match `lotes`) |
| FK | `fk_auditoria_tenant` | `(id_tenant) → tenants(id_tenant)` `ON DELETE RESTRICT` |
| FK | `fk_auditoria_punto_venta` | `(id_punto_venta, id_tenant) → puntos_venta(id_punto_venta, id_tenant)` `ON DELETE RESTRICT`. **MATCH SIMPLE** (the default): with `id_punto_venta` NULL the constraint is not checked — tenant integrity comes from `fk_auditoria_tenant`, which is why both FKs exist |
| FK | `fk_auditoria_actor` | `(id_actor) → usuarios(id_usuario)` `ON DELETE RESTRICT` — **simple, not composite**, for the documented reason at doc-10:563-567: a composite FK would require an alternate key forcing `id_tenant NOT NULL` on `usuarios`, breaking the platform-staff NULL sentinel. Same criterion as `id_empleado` |
| CHECK | `ck_auditoria_accion_no_vacia` | `length(btrim(accion)) > 0` (decision 8) |
| CHECK | `ck_auditoria_entidad_no_vacia` | `length(btrim(entidad)) > 0` |
| Index | `ix_auditoria_tenant_creado` | `(id_tenant, creado_el DESC)` — the listing's driving path; also serves the RLS predicate and any future `creado_el` range delete/partition |
| Index | `ix_auditoria_entidad` | `(id_tenant, entidad, id_entidad)` — "everything that happened to this articulo/usuario/comprobante" |
| Index | `ix_auditoria_actor` | `(id_tenant, id_actor, creado_el DESC)` — the "filter by actor" filter, which is in the spec and therefore not speculative; also FK support |
| RLS | `auditoria_tenant` | `migrationBuilder.HabilitarRlsDeTenant("auditoria")` → `ENABLE` + `FORCE ROW LEVEL SECURITY` + `USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())`. **Standard policy, no deviation** |

**Column notes where the explore's tentative model was challenged:**

- **`bigint` PK, kept.** Four extra bytes per row against an `ALTER TABLE` rewrite under an
  exclusive lock if `integer` ever overflows. The cheap-to-reverse direction.
- **`creado_el` has NO `DEFAULT now()`** (the explore proposed one). The repo's single time
  source is `IRelojDelSistema` — a DB default would let an audit row's timestamp disagree with
  the business transaction's clock and would silently defeat `RelojFijo` in tests. `NOT NULL`
  without a default forces the value to come from the caller, as `movimientos_stock.creado_el`
  already does.
- **`valor_nuevo NOT NULL`** (the explore had it nullable). Every audited action has an
  after-state or a fact to state; a row with both payloads NULL would be an audit entry that
  says nothing. `valor_anterior` stays nullable and carries real meaning when NULL
  (decision 2).
- **No FK on `id_entidad`** — it is polymorphic by construction; a FK is impossible, not
  omitted. Referential coherence of the pointer is an application invariant with a test.

### B. Error backstops (`db-error-backstops` APPLIES)

| New constraint | Client-input reachable? | Backstop |
|---|---|---|
| `fk_auditoria_actor` | **No** — `id_actor` is always `contexto.UsuarioId`, server-derived, never a request field (same rule as `id_empleado`, doc-10:566-567). Additionally, `usuarios` is **soft-deleted**, so the referenced row is never physically removed | Covered anyway by the existing generic `23503` → `400 referencia_invalida` prefix mapping (`ManejadorDeErrores.cs:224`, matches any `fk_` constraint). **No new mapping needed**; the exemption is documented here per the skill's gate table, and an integration test asserts the SQLSTATE/domain code path rather than assuming it |
| `fk_auditoria_tenant`, `fk_auditoria_punto_venta` | No — both derive from the session/subject | Same generic `fk_` mapping |
| `ck_auditoria_accion_no_vacia`, `ck_auditoria_entidad_no_vacia` | No — both come from the application-owned catalog | Unreachable from client input by construction; guarded by the constant set + unit test |
| **New unique index** | **none** | See non-decisions |

**No `23505` family is introduced by this stage** — there is no new unique index, therefore no
duplicate-race test family is required. Stated explicitly so the absence reads as a decision.

### C. Deliberate non-decisions (gate-relevant)

- **No unique constraint of any kind.** Two identical audited operations one second apart are
  legitimate history; uniqueness would refuse a true fact.
- **No `PARTITION BY RANGE (creado_el)`, no retention job, no TTL** (decision 3), with the
  conversion cost and the tripwire recorded there.
- **No GIN index on `valor_anterior` / `valor_nuevo`.** v1 filters are columnar only; a GIN
  index costs write amplification and maintenance for a query nobody has asked for. It is the
  obvious first addition **if** payload search becomes a real filter.
- **No index on `accion`.** It is a low-cardinality filter that composes with
  `ix_auditoria_tenant_creado`; adding it speculatively is a migration for an unmeasured gain
  (the stage-13 gate criterion, applied verbatim).
- **No database-level immutability** — no `REVOKE UPDATE, DELETE`, no rule, no trigger. Two
  reasons: the application ships **no** UPDATE or DELETE path over `auditoria` (the writer is
  INSERT-only and the entity exposes no mutator), and the repo's known `ways_owner`-as-superuser
  weakness would make a GRANT-based rule theatre rather than protection. Recorded as the honest
  residue, and as the correct fix **if and when** that carryover is addressed.
- **No `EntidadBase` columns** (`created_at`/`updated_at`/`deleted_at`) — an immutable fact has
  no update and no soft delete; `movimientos_stock` is the precedent.
- **No `id_empresa`** — operativa tables do not carry it (doc-09:86).
- **No changes to `precios`, `usuarios`, `comprobantes_venta`, `comprobantes_compra`,
  `movimientos_stock` or `movimientos_cuenta_corriente`.** In particular, **no `id_actor`
  column is added to `precios`** — the external table is the whole point of decision 1, and
  altering the hottest history table of the catalog is a cost this stage does not need to pay.

### Model summary for the gate

| Object | Change |
|---|---|
| `auditoria` | **NEW** — 10 columns, 1 PK, 3 FKs, 2 CHECKs, 3 indexes, RLS estándar |
| `precios` / `usuarios` / `comprobantes_venta` / `comprobantes_compra` | **NONE** — read only |
| `movimientos_stock` / `movimientos_cuenta_corriente` | **NONE** — read only; their ids travel inside the payload |
| Enums / types | **NONE** — no `ALTER TYPE`, nothing irreversible (decision 8) |
| Data statements | **NONE** — no backfill; history starts at deploy |
| Migrations | **ONE** (`AuditoriaEtapa14`) |

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Auditoria/` | New | `Auditoria` entity (immutable, no `EntidadBase`), `AccionAuditada` catalog, payload builder — unit-testable without a DB (`PoliticaDeRoles` pattern) |
| `src/Ways.Infrastructure/Migrations/` | New | `AuditoriaEtapa14` — the single migration of the gate section |
| `src/Ways.Infrastructure/Persistence/` | Modified | `AuditoriaConfiguration`, `DbSet`, RLS via `HabilitarRlsDeTenant` |
| `src/Ways.Application/Auditoria/ServicioDeAuditoria.cs` | New | The writer (same transaction, fail-closed) + the query/read model |
| `src/Ways.Application/Precios/ServicioDePrecios.cs` | Modified | `precio.cambio` call site inside the existing transaction (lines 92-198) |
| `src/Ways.Application/Usuarios/ServicioDeUsuarios.cs` | Modified | 5 call sites (`alta`, `actualizacion`, `baja`, `desbloqueo`, `password`) |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modified | `venta.anulacion` in `EjecutarAnulacionAsync` only. **Checkout untouched** (decision 4) |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modified | `compra.anulacion` |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modified | `ajuste`, `decomiso`, `conteo`. `TransferirAsync` untouched (decision 5) |
| `src/Ways.Application/CuentaCorriente/ServicioDeReliquidacion.cs` | Modified | `cc.reliquidacion` |
| `src/Ways.Application/Exportacion/` | Unmodified | Consumed as-is: `TablaExportable`, `GuardaDeTope`, `OpcionesDeExportacion.TopeDeFilas` |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modified | `LecturaDeAuditoria` (Admin) |
| `src/Ways.Api/Endpoints/AuditoriaEndpoints.cs` | New | `GET /api/auditoria`, `GET /api/auditoria/export` |
| `src/Ways.Web/src/paginas/Auditoria.tsx` | New | Filter screen + download (`Vencimientos.tsx` / `Reposicion.tsx` shape) |
| `docs/10-modelo-de-datos.md` | Modified | The `auditoria` table + "Estado (Etapa 14)" annotation, from inside slice 1 |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | **Unmodified** | The generic `fk_`/`23503` mapping already covers this stage (gate §B) |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **A defect in the audit writer takes down price changes / anulaciones / role changes** — the accepted cost of fail-closed | Med | The writer is one INSERT, no external dependency, no untrusted input; integration-tested per call site before any call site ships (slice 1 delivers the writer alone) |
| **Payload drift** — each domain inventing its own key names, making the log unreadable | High if unmanaged | The per-action table in decision 2 is a spec contract with a scenario per action; the key-subset rule is a testable invariant, not a convention |
| **A secret reaching the log** (`hash_password` on a user change) | Low, catastrophic | Per-action field lists instead of entity dumps (decision 2), plus an explicit denylist test on the `usuarios` actions |
| **Unbounded growth** | Low near-term, real long-term | Decision 3: no policy, a measured tripwire, and two cheap properties that keep the future conversion mechanical. The cost of deferring is stated, not hidden |
| **Duplicated actor stamp diverging from the ledger** (stock, CC) | Low | `auditoria` never stores the arithmetic — only the stamp and a pointer. The number lives in exactly one place, so the two cannot disagree about it |
| **A write path silently missing its audit row** (the failure the stage exists to prevent) | Med | Coverage is a spec scenario per action, not a code review; the 100%-servicio-sin-CC anulación is an explicit test |
| **The checkout budget eroding by accident** | Low | No checkout write path is touched; the existing `Assert.Equal(16, …)` guard remains untouched and green as a success criterion |
| **Scope creep into retention infrastructure or platform auditing** | Med | Both refused in writing with named reopen conditions (decisions 3 and 1) |
| **A Supervisor reading the log that records Supervisors** | Low | Admin-only policy asserted by a 403 test (decision 6) |

## Rollback Plan

**Per slice.** Slices 2-7 are additive code over an unchanged schema: reverting one removes
call sites or a read surface and leaves the table intact and consistent.

**Slice 1 (the only irreversible-ish one).** Rolling back means dropping `auditoria` — a
single `DROP TABLE` with **no dependent object**: no other table references it, no enum value
was added (decision 8 — this is the concrete payoff of refusing the enum, since stage 12's
`motivo_stock` values can never be removed), no existing column was altered, no row of any
existing table was rewritten. The rollback destroys only audit rows written after deploy;
**nothing of business value is lost** because the table holds no arithmetic truth.

**Runtime kill switch.** There is none by design, and that is deliberate: a switchable audit
log is an audit log that was off when it mattered. The operational lever, if the writer ever
misbehaves in production, is reverting the call-site slice — which is why the call sites are
split across slices 2-4 by domain rather than landing in one commit.

**Whole stage.** Revert the code, drop the table. **No irreversible database artifact of any
kind** — no enum value, no altered column, no rewritten row.

## Dependencies

- **Etapa 11** — `TablaExportable` / `IExportadorDeTabla` / `GuardaDeTope` / the `/export`
  sibling house standard, consumed verbatim (doc-11:211 names this dependency).
- **Etapas 5/7/8/12** — the three existing ledgers whose ids the payloads point at, and whose
  actor stamps prove the pattern.
- **Etapa 1** — `usuarios`, `PoliticaDeRoles`, `RolConocido`, and the policy composition
  pattern `LecturaDeRentabilidad` established.
- **Etapa 12/13** — the filter-screen shape (`Vencimientos.tsx`, `Reposicion.tsx`) and the
  `web-descriptor-tests` discipline.
- **`IContextoDeUsuario`** — already injected in all six services; **no new wiring**.
- No new NuGet package. No new web dependency. No scheduler, no queue, no outbox.

## Success Criteria

- [ ] Exactly **one** migration file ships; `dotnet ef migrations has-pending-model-changes` is
      clean afterwards, and the only DDL is the one described in the gate section.
- [ ] RLS proven on `auditoria`: a tenant reading with another tenant's GUC sees **zero rows**;
      an INSERT with a foreign `id_tenant` is refused (`42501`), asserted by SQLSTATE.
- [ ] Each of the **twelve** actions writes **exactly one** row per operation — including the
      price change that also closes a predecessor row (one operation, one row).
- [ ] **Anulación of a 100%-servicio comprobante with no cuenta corriente** produces an audit
      row naming the actor. This is the stage's flagship scenario.
- [ ] A **zero-difference conteo** writes no ledger row **and** no audit row (recorded limit).
- [ ] A forced failure of the audit INSERT **rolls back the business operation** — the price
      is not changed, the comprobante is not anulado (fail-closed, decision 4).
- [ ] `valor_anterior`'s key set is a **subset** of `valor_nuevo`'s, for every action that has
      both — asserted generically over the catalog.
- [ ] **No `usuarios` audit payload contains `hash_password`** or any secret — explicit test.
- [ ] All jsonb keys are snake_case, asserted on at least one payload per domain.
- [ ] A **Supervisor receives 403** on `GET /api/auditoria` and on its export; a Vendedor too;
      an Admin sees rows from **every** punto de venta of the tenant.
- [ ] The filter set (date range, `accion`, actor, `entidad` + `id_entidad`, punto de venta)
      each returns the expected subset, including `id_punto_venta IS NULL` rows under "todos".
- [ ] The export's rows equal the JSON endpoint's for identical filters (the stage-11 binding
      invariant), and the export **refuses rather than truncates** at `TopeDeFilas`.
- [ ] The checkout's query-count guard still asserts **16** and is untouched (decision 4).
- [ ] `movimientos_cuenta_corriente.detalle` is unchanged in type, content and serialization.
- [ ] doc 10 carries the `auditoria` table with its "Estado (Etapa 14)" annotation.
- [ ] Domain / Application / Integration / vitest suites green; descriptor tests for
      `Auditoria.tsx` (`web-descriptor-tests`).

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one judgment-day round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage14-slice1-tabla-auditoria` | Migration + entity + EF config + RLS + `AccionAuditada` catalog + `ServicioDeAuditoria` writer (no call site yet) + RLS/SQLSTATE tests + doc 10 | ~420 |
| 2 | `feat/stage14-slice2-precios-usuarios` | `precio.cambio` + the five `usuario.*` call sites (EF transactions) + denylist test | ~330 |
| 3 | `feat/stage14-slice3-anulaciones` | `venta.anulacion` + `compra.anulacion` (ADO transactions) + the 100%-servicio-sin-CC scenario + checkout-guard regression | ~320 |
| 4 | `feat/stage14-slice4-stock-cc` | `stock.ajuste` / `stock.decomiso` / `stock.conteo` + `cc.reliquidacion` + zero-difference conteo scenario | ~300 |
| 5 | `feat/stage14-slice5-consulta` | `LecturaDeAuditoria` policy + `GET /api/auditoria` (filters, pagination) + authorization tests | ~380 |
| 6 | `feat/stage14-slice6-export` | `/api/auditoria/export` sibling + `TablaExportable` mapper + cap/parity tests | ~240 |
| 7 | `feat/stage14-slice7-web` | `Auditoria.tsx` (filters, before/after detail, download) + descriptor tests | ~360 |

Merge order `1 → 2 → 3 → 4 → 5 → 6 → 7`. Slices 2, 3 and 4 touch disjoint services and are
foldable in parallel once slice 1 lands; 5 and 6 depend only on 1.

**Pre-approved degradation** (stage-12 decision-11 pattern), in priority order:

1. **If slice 1 overflows** — split at the schema/service boundary: `1a` (migration, entity,
   EF config, RLS + its tests) and `1b` (catalog, writer, writer tests). The migration must not
   ship in a slice that might be dropped, so this split is *pre-authorized*, not optional.
2. **If slice 7 overflows** — ship the screen with the filters, the list and the download, and
   **drop the before/after detail panel**; the payload still reaches the operator through the
   export. A documented reduction, never a silent one.
3. **Never degraded**: coverage of the twelve actions and the fail-closed rule. A partially
   covered audit log is worse than none (decision 4), so a coverage slice is split, never
   trimmed.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~2 350 lines** across 7 slices.
- **Decision needed before apply: No** — `auto-chain` + `stacked-to-main` already resolved in
  `state.yaml`.
- **Chained PRs recommended: Yes.** `chain_strategy: stacked-to-main`.
- **400-line budget risk: Medium.** Slices 1 and 5 sit closest to the cap; split points are
  pre-identified above and at the query/authorization boundary for slice 5.
- **`size:exception` anticipated: No** — slice 1's migration is small (one table) and its
  pre-authorized `1a`/`1b` split keeps it under budget without an exception.

## Deferred / adjacent (recorded, not in scope)

- **Retention / partitioning / archival** — decision 3, with three named tripwires.
- **Platform-level audit** (root provisioning, edits over `usuarios.id_tenant IS NULL`) —
  decision 1; a nullable `id_tenant` would be the design, and it is a different product.
- **`stock.transferencia`** — decision 5; needs a modelling call on origin/destination first.
- **Parámetros operativos and stage-13 mínimos** — "who lowered the mínimo" is a real question
  and this is the most likely first extension of the catalog; it costs one constant and one
  call site, **zero DDL** (decision 8).
- **Catálogo ABM (articulos, clientes, proveedores)** — high volume, low sensitivity; would
  change the growth profile and reopen decision 3 sooner.
- **Ajuste manual de CC, gastos, movimientos de caja/tesorería, arqueos** — already
  actor-stamped; joining them is additive.
- **Per-entity "historial" tab inside each editor** — the API already supports it via
  `entidad` + `id_entidad`; only the UI is missing.
- **jsonb payload search + GIN index** — gate §C.
- **Database-level immutability** (`REVOKE UPDATE, DELETE`) — gate §C; correct **after** the
  `ways_owner`-superuser carryover is addressed, theatre before.
- **Migrating `movimientos_cuenta_corriente.detalle` to `jsonb`/snake_case** — decision 7;
  refused because it rewrites the meaning of existing rows.
- **Push notification of audit events** ("alert me when someone changes a price") — inherits
  stage 13's decision-2 tripwires; the query built here is the payload any future channel sends.
- **Carryovers untouched**: `articulos_empresas` replace-set concurrency gap, the importe CHECK
  micro-gate, the containment/import-boundary lint rule, `stage-13b-conteo-por-planilla`.

## Proposal question round

Each records the assumption taken, so a correction is cheap. **None of these blocks
spec/design**; all are recorded for the owner.

1. **Is a single generic audit table the right shape, rather than per-domain registers?**
   Assumed **yes** (decision 1). The doc asks for *one* filtrable query; five tables make that
   a UNION, and the two real gaps need new storage anyway.
2. **Should the log record the before/after values, or only the fact?** Assumed **both, but
   bounded per action** (decision 2). *This is the most product-weight call of the stage*: a
   full row dump would put password hashes into an Admin-readable, never-deleted table on the
   first role change.
3. **Do we accept that an operation which cannot be audited fails?** Assumed **yes**
   (decision 4). The alternative is a log that is *sometimes* complete, which cannot be relied
   on in the one conversation it exists for. The cost is stated: a writer defect blocks price
   changes and anulaciones.
4. **Is "no retention policy" acceptable, with a tripwire instead of a number?** Assumed
   **yes** (decision 3). The honest residue: converting a populated table to partitioned later
   costs proportionally to its size, so the tripwire is the mitigation, not an afterthought.
5. **Does the first pass include the user lifecycle (alta, baja, desbloqueo, password) beyond
   "cambios de rol"?** Assumed **yes** (decision 5) — `ActualizarAsync` changes four fields in
   one call, and creating an admin account is the most sensitive operation in the system.
6. **Is the audit log Admin-only?** Assumed **yes**, under a new `LecturaDeAuditoria`
   (decision 6). A Supervisor reading the log that records Supervisors defeats the control.
7. **Should stock transferencias be audited in this pass?** Assumed **no** (decision 5) — a
   transfer has two puntos de venta and one column cannot express that honestly; both halves
   are already actor-stamped.
8. **Is it acceptable that history starts at deploy, with no backfill?** Assumed **yes** — it
   is structurally impossible to reconstruct, and it is doc-11:207's own argument for not
   delaying this stage further.
