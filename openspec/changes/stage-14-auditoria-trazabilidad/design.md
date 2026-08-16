# Design: Stage 14 — Auditoría y trazabilidad de operaciones sensibles

## Technical Approach

**One table, one writer with two enlistment modes, eleven call sites, one query seen twice, one
screen.**

The proposal's `Modelo de datos propuesto` (lines 453-611) is the ratified gate contract and this
design **adds no DDL of any kind** — not a column, not an index, not a constraint. Everything below
is code over that exact table.

Three structural facts decide the shape.

1. **The repo has two write worlds and the writer must live in both without ever opening a
   transaction.** `ServicioDePrecios` and `ServicioDeUsuarios` persist through EF
   (`db.SaveChangesAsync`); the anulaciones, the discretionary stock paths and the reliquidación
   write raw ADO statements on `db.Database.CurrentTransaction`. Those are not two styles of the
   same thing: an EF caller **without** an explicit transaction (`ServicioDeUsuarios`, four of its
   five paths) has *no ambient transaction at all*, so a raw INSERT there would autocommit and
   produce exactly the two failures fail-closed exists to prevent — an audit row for an operation
   that then failed, or a committed operation whose audit row was lost. Hence **two modes, one
   contract** (decision 1): `Registrar(...)` **enqueues** an entity into the caller's own
   `SaveChangesAsync` (no I/O), `RegistrarAsync(conexion, transaccion, ...)` executes **one INSERT
   without `RETURNING`** on the caller's connection *and* transaction — the
   `EscriturasDeCuentaCorriente` / `InsertarMovimientoStockAsync` convention, verbatim.

2. **The actor and the clock are not parameters** (decision 2). Eleven call sites cannot forge,
   omit or mistype what they never pass: `id_actor` is always `contexto.UsuarioId` and `creado_el`
   is always `reloj.Ahora`, stamped inside the writer. This is also what makes `id_actor`
   unreachable from client input — the documented exemption of gate §B — structural instead of
   asserted.

3. **The audit row never re-reads and never re-derives.** Every before-image comes from a value the
   business transaction *already holds under its own lock or its own `RETURNING`*: the price's open
   row (one extra column on a `SELECT` that already runs under the advisory lock), the anulación's
   own `UPDATE ... WHERE estado = 'emitido'` (which becomes the authority for the punto de venta
   too, decision 8), the stock upsert's `RETURNING cantidad` (`anterior = nueva - delta`,
   decision 9), the cliente's `SELECT ... FOR UPDATE` in the reliquidación. **Zero additional
   round trips, zero re-reads that a concurrent writer could answer differently, no change to any
   lock order, and no change to any decide-then-commit boundary.**

The checkout is not opened by this stage at all. `VentasCheckoutTests`' `Assert.Equal(16, …)` is a
**non-regression criterion over unedited code**, not a test that has to be re-argued.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **Two enlistment modes, one contract.** `Registrar(...)` (sync, no I/O — `db.Auditoria.Add`, flushed by the caller's `SaveChangesAsync`) for EF callers; `RegistrarAsync(DbConnection, DbTransaction?, …)` (one `INSERT`, `ExecuteNonQueryAsync`, no `RETURNING`) for ADO callers. The writer **never** calls `BeginTransaction`, `SaveChanges` or `Commit` | One raw INSERT everywhere on `db.Database.GetDbConnection()`; one EF `Add` everywhere | A raw INSERT in `ServicioDeUsuarios` would run **outside any transaction** (four of its five paths have none — just a bare `SaveChangesAsync`), so either the audit row or the business change could commit alone. An EF `Add` in the ADO paths would need a `SaveChangesAsync` interleaved into a hand-ordered statement sequence, moving a flush inside pinned lock orders. Each mode is the one that is *already atomic* in its caller |
| 2 | **`id_actor` and `creado_el` are stamped by the writer, never passed.** `RegistroDeAuditoria` carries only tenant, PV, action, entity id and the two payloads | Pass `idActor`/`momento` from each call site (the `InsertarMovimientoStockAsync` shape) | Eleven call sites is eleven chances to pass the wrong user. Stamping centralises the gate-§B exemption ("`id_actor` is server-derived, never a request field") in one line, and makes `reloj.Ahora` the single time source — a `DateTimeOffset.UtcNow` mutation dies against one `RelojFijo` assertion instead of eleven |
| 3 | **`RegistroDeAuditoria` validates its own payload invariants in its constructor** (Domain, pure, no DB): key-subset (`valor_anterior` keys ⊆ `valor_nuevo` keys), `valor_nuevo` non-empty, denylist over keys | Validate in the writer; validate in tests only | The subset rule is the proposal's *testable invariant*, so it has to be enforced where it cannot be bypassed — an unconstructable illegal registro beats a rule someone must remember. Pure Domain means the whole invariant suite is xUnit facts with no container (`PoliticaDeRoles` pattern), and both modes inherit it because both take the same type |
| 4 | **`AccionAuditada` is a `sealed record (Accion, Entidad)` with 12 `static readonly` instances plus `Todas`** — the catalog fixes the **pair**, not just the verb | `const string` per action + a separate `entidad` argument; a native enum (refused by proposal decision 8) | With separate arguments a call site can pair `precio.cambio` with `entidad = "usuario"` and nothing notices. One record makes the pairing unforgeable, gives `Todas` for the generic catalog tests (naming convention `<dominio>.<operacion>`, non-empty, no duplicates), and keeps the DB permissive / the application strict |
| 5 | **The payload is `IReadOnlyDictionary<string, object?>` built by per-action static factories in `PayloadDeAuditoria` (Domain, pure). No factory accepts an entity** | 12 typed records; call sites building dictionaries inline | The security argument of proposal decision 2 becomes structural: there is **no overload that takes a `Usuario`**, so an entity dump is unrepresentable, not merely discouraged. 12 records would need 12 serializer shapes and would turn one generic subset-rule test into 12 hand-written ones. Inline dictionaries would reintroduce the drift risk the factories exist to kill — every key name in the system is written exactly once |
| 6 | **One `JsonSerializerOptions` in `SerializadorDeAuditoria`, shared by both modes:** `DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower` **(not `PropertyNamingPolicy`)** + `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)`. Not registered globally | `PropertyNamingPolicy` per proposal decision 7; enums as C# member names | For a dictionary payload `PropertyNamingPolicy` is a **no-op that looks like a decision** — STJ applies `DictionaryKeyPolicy` to dictionary keys. The enum converter is what makes `EstadoComprobante.Emitido` serialize as `"emitido"`, i.e. the *database label*, which is what "keys and values mirror the schema" actually means. One options instance for both modes makes EF-path/ADO-path divergence impossible |
| 7 | **`Auditoria` does not inherit `EntidadTenant`** (that base carries `EntidadBase`'s `created_at`/`updated_at`/`deleted_at`, which the gate forbids). It gets its own `AplicarFiltroDeTenantEnAuditoria`, cloned from `AplicarFiltroDeTenantEnMovimientoStock`, and `IdTenant` is written **explicitly** by the caller | Inherit `EntidadTenant` and let `EstamparTenant()` fill the tenant | `EstamparTenant()` *overwrites* `IdTenant` from the connection's `TenantActual` for non-platform sessions — which would silently convert proposal decision 1 ("the tenant of the audited **subject**") into "the tenant of the session". They coincide in 11 of 12 actions and differ exactly in the one that matters (root editing a tenant's user). Not inheriting is what keeps the subject's tenant authoritative |
| 8 | **`ServicioDeVentas.MarcarAnuladoAsync` returns `int?` via `RETURNING id_punto_venta`** instead of `bool` via `RETURNING id_comprobante_venta` | Read the PV from `comprobantePreLectura`; a second `SELECT` | The `UPDATE ... WHERE estado = 'emitido'` is already the single race-safe authority for "did the transition happen"; making it answer "and in which PV" costs **zero round trips** and refuses to trust the pre-read (taken with `AsNoTracking()` *before* the update, under a different filter set). Exact precedent: `ServicioDeTurnos.MarcarCerradoAsync` (`ServicioDeTurnos.cs:315-341`), which returns `id_punto_venta` from the same statement shape. `ServicioDeCompras.MarcarAnuladaAsync` **already** returns it — no change there |
| 9 | **Before-images are derived from the authoritative `RETURNING`, never from a second `SELECT`.** For stock: `cantidad_anterior = nueva − delta`, where `nueva` is `UpsertStockAsync`'s `RETURNING cantidad` | A `SELECT cantidad` before the upsert | The upsert is `cantidad = stock.cantidad + EXCLUDED.cantidad` under the row lock it takes itself, so `nueva − delta` is **exactly** the pre-image; a separate `SELECT` before the lock could read a value another transaction then changed, writing an audit row that never existed. Same criterion for the reliquidación (`saldo` from the `FOR UPDATE`, `saldo` new from `ActualizarSaldoClienteAsync`'s `RETURNING`) |
| 10 | **Fail-closed is proven by data, not by a test seam:** the probe is a session whose `contexto.UsuarioId` points at a non-existent usuario, so `fk_auditoria_actor` raises `23503` **inside the business transaction** | Introduce `IEscritorDeAuditoria` so a failing double can be injected | The seam would exist only for the test and would add an indirection to six services. The FK probe needs no production change, and it doubles as the gate-§B assertion that `23503`/`fk_` maps to `400 referencia_invalida` through the **existing** `ManejadorDeErrores.cs:224` prefix rule. Composed with the flagship scenario (100%-servicio comprobante, no lines, no CC) the audit INSERT is the *only* statement in the transaction touching `usuarios`, so nothing else can produce the failure |
| 11 | **`usuario.alta` gets an explicit transaction with two `SaveChangesAsync`** (`db.Precios`-style `CreateExecutionStrategy` + `BeginTransactionAsync`): user, flush, audit with the generated id, flush, commit | Fabricate the id from the sequence; an EF navigation | `id_entidad` is polymorphic and has no FK, so EF cannot fix it up; the id does not exist before the first flush. Two flushes in one transaction keep fail-closed exactly (the audit failure rolls back the user). This is the only call site that changes a caller's transaction structure, and it is stated rather than hidden |
| 12 | **Offset pagination (`PaginaDe<FilaDeAuditoria>`), ordered `creado_el DESC, id_auditoria DESC`** | Keyset over `(creado_el, id_auditoria)` | The repo has **zero** keyset precedent and seven `PaginaDe*` records; the web pager (`HistoricoDeCajas.tsx:100`) renders "Página N de M", which needs the `COUNT(*)` a cursor cannot give. `ix_auditoria_tenant_creado` drives either. The **tiebreaker is not cosmetic**: `creado_el` comes from one `reloj.Ahora` per operation, so rows written by the same operation tie *by construction* and under `RelojFijo` an entire fixture ties — without `id_auditoria DESC` pagination can duplicate and skip rows |
| 13 | **One `ConstruirQuery(filtros)`, two consumers; the export is the LISTING cap shape** (`CountAsync` → `GuardaDeTope.Exigir` → `Take(tope + 1)` → second `Exigir`), and it maps from the **same `FilaDeAuditoria`** the JSON returns | A redeclared export predicate; the aggregate cap shape | Stage-11 decision 7 verbatim (`ServicioDeVentas.ListarParaExportacionAsync:393-420`), and stage-13 decision 13's rule picks the shape: the listing shape is for row sets that **grow monotonically with time**, which is `auditoria`'s defining property. Sharing the row type makes "the export's rows equal the endpoint's" structural — the payload cell is `JsonSerializer.Serialize` of the very `JsonElement` the JSON returned |
| 14 | **The actor's name comes from a LEFT JOIN to `usuarios` with `IgnoreQueryFilters(["BajaLogica"])`**; `Actor` is `string?` and renders as the id when null | Inner join; `Include`; store the name in the payload | An inner join **deletes rows from the audit log**: a root actor is invisible to a tenant session (usuarios' own tenant filter + RLS) and a soft-deleted usuario is invisible to everyone — the two actors whose rows an auditor most wants. `IgnoreQueryFilters(["BajaLogica"])` alone (never the whole filter set — the `ServicioDeUsuarios.ListarAsync:44` warning) keeps a departed employee's name readable. Denormalising the name into the payload would freeze a value the ABM can still correct |
| 15 | **`accion` and `entidad` are NOT validated against the catalog on read.** An unknown value returns zero rows | 400 `accion_desconocida` | Proposal decision 5 says a retired action leaves rows whose `accion` has no writer, "harmless and self-documenting" — validating on read would make exactly those rows unqueryable by their own action. The catalog is authoritative for **writes**; the read surface is a filter over history, not a gate |
| 16 | **`idEntidad` requires `entidad` → `400 entidad_requerida`** | Accept `idEntidad` alone | `id_entidad` is polymorphic: alone it would silently mix articulo 7, usuario 7 and comprobante 7 into one answer. Accepting a filter that cannot mean what it says is `dto-contract-honesty` rule 1 wearing a query string |
| 17 | **`Auditoria.tsx` is `HistoricoDeCajas.tsx`'s filter+pager shape plus `Vencimientos.tsx`'s `BotonDeDescarga`**, with the before/after panel as an isolated, droppable component | A new screen shape; a modal per row | Both precedents already solve the two halves (filters object + generation-ref + offset pager; download button + `rutasDeExportacion`). Keeping the detail panel a separate component with its own props is what makes the proposal's **pre-approved degradation** (drop the panel if slice 7 overflows) a clean non-delivery instead of retracting shipped UI |

## Interfaces / Contracts

### Domain — pure, no database

```csharp
// Ways.Domain/Auditoria/AccionAuditada.cs — el catálogo fija el PAR (decisión 4).
public sealed record AccionAuditada(string Accion, string Entidad)
{
    public static readonly AccionAuditada PrecioCambio       = new("precio.cambio", "articulo");
    public static readonly AccionAuditada VentaAnulacion     = new("venta.anulacion", "comprobante_venta");
    public static readonly AccionAuditada CompraAnulacion    = new("compra.anulacion", "comprobante_compra");
    public static readonly AccionAuditada StockAjuste        = new("stock.ajuste", "articulo");
    public static readonly AccionAuditada StockDecomiso      = new("stock.decomiso", "articulo");
    public static readonly AccionAuditada StockConteo        = new("stock.conteo", "articulo");
    public static readonly AccionAuditada CcReliquidacion    = new("cc.reliquidacion", "cliente");
    public static readonly AccionAuditada UsuarioAlta        = new("usuario.alta", "usuario");
    public static readonly AccionAuditada UsuarioActualizacion = new("usuario.actualizacion", "usuario");
    public static readonly AccionAuditada UsuarioBaja        = new("usuario.baja", "usuario");
    public static readonly AccionAuditada UsuarioDesbloqueo  = new("usuario.desbloqueo", "usuario");
    public static readonly AccionAuditada UsuarioPassword    = new("usuario.password", "usuario");

    public static readonly IReadOnlyList<AccionAuditada> Todas = [ /* las 12 */ ];
}

// Ways.Domain/Auditoria/RegistroDeAuditoria.cs — invariantes en el constructor (decisión 3).
public sealed record RegistroDeAuditoria
{
    public RegistroDeAuditoria(
        int idTenant, int? idPuntoVenta, AccionAuditada accion, int idEntidad,
        IReadOnlyDictionary<string, object?>? valorAnterior,
        IReadOnlyDictionary<string, object?> valorNuevo);
    // 1. valorNuevo no vacío.
    // 2. REGLA DE SUBCONJUNTO: toda clave de valorAnterior está en valorNuevo (la inversa NO).
    // 3. DENYLIST sobre claves: ninguna contiene password / contrasena / hash / token / secret
    //    (case-insensitive). Backstop del hecho estructural de la decisión 5.
    // 4. Toda clave en snake_case (^[a-z][a-z0-9_]*$).
    // Violación ⇒ InvalidOperationException ("invariante de escritura violado"), nunca ErrorDominio:
    // no es un error del cliente, es un defecto de un call site.
}

// Ways.Domain/Auditoria/PayloadDeAuditoria.cs — una fábrica por acción. NINGUNA toma una entidad.
public static class PayloadDeAuditoria
{
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        CambioDePrecio(int idListaPrecio, decimal? montoAnterior, DateTimeOffset? vigenteDesdeAnterior,
                       decimal montoNuevo, DateTimeOffset vigenteDesdeNuevo);
    // … 11 más, una por acción, con la tabla de payloads del proposal como contrato.
}
```

### Application — the writer

```csharp
// Ways.Application/Auditoria/ServicioDeAuditoria.cs
public sealed class ServicioDeAuditoria(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    /// MUNDO EF — NO hace I/O: encola la fila en el MISMO SaveChangesAsync del llamador, así que
    /// es atómica con él tenga o no una transacción explícita abierta. Devuelve la entidad solo
    /// para que un test pueda inspeccionarla; ningún call site usa el valor.
    public Auditoria Registrar(RegistroDeAuditoria registro);

    /// MUNDO ADO — UN INSERT sin RETURNING sobre la conexión Y la transacción del llamador
    /// (convención de EscriturasDeCuentaCorriente/InsertarMovimientoStockAsync). `transaccion`
    /// null ⇒ InvalidOperationException: una fila de auditoría JAMÁS se escribe fuera de una
    /// transacción (el guard es el mutation target del fail-closed en el mundo ADO).
    public Task RegistrarAsync(
        DbConnection conexion, DbTransaction? transaccion, RegistroDeAuditoria registro, CancellationToken ct);
}
```

```sql
-- El único statement del mundo ADO. Cast explícito ::jsonb (Postgres no castea text→jsonb solo).
INSERT INTO auditoria
  (id_tenant, id_punto_venta, id_actor, accion, entidad, id_entidad, valor_anterior, valor_nuevo, creado_el)
VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8::jsonb, $9)
```

En el mundo EF la entidad mapea `ValorAnterior`/`ValorNuevo` como `string?`/`string` con
`HasColumnType("jsonb")`, serializados por **el mismo** `SerializadorDeAuditoria` — un solo lugar
produce el documento, así que los dos caminos no pueden divergir.

### Application — read model

```csharp
// Ways.Application/Auditoria/Contratos.cs — dto-contract-honesty: los 7 filtros se leen en
// ConstruirQuery; no hay campo aceptado y descartado.
public sealed record FiltrosDeAuditoria(
    DateTimeOffset? Desde, DateTimeOffset? Hasta, string? Accion, int? IdActor,
    string? Entidad, int? IdEntidad, int? IdPuntoVenta);

public sealed record FilaDeAuditoria(
    long IdAuditoria, DateTimeOffset CreadoEl, string Accion, string Entidad, int IdEntidad,
    int IdActor, string? Actor, int? IdPuntoVenta,
    JsonElement? ValorAnterior, JsonElement ValorNuevo);
//   Actor null ⇒ el nombre no es visible para esta sesión (actor de plataforma), NUNCA "sin actor":
//   IdActor siempre viaja y la pantalla lo muestra (decisión 14).

public sealed record PaginaDeAuditoria(IReadOnlyList<FilaDeAuditoria> Items, int Total, int Pagina, int Tamanio);

// ServicioDeConsultaDeAuditoria
public Task<PaginaDeAuditoria> ConsultarAsync(FiltrosDeAuditoria f, int pagina, int tamanio, CancellationToken ct);
public Task<IReadOnlyList<FilaDeAuditoria>> ConsultarParaExportacionAsync(FiltrosDeAuditoria f, int tope, CancellationToken ct);
```

```csharp
/// Cláusulas bajo prueba (mutation-proof-tests), en orden de daño si se pierden:
///   join LEFT sobre usuarios     → un inner join BORRA del log las filas de un actor root o
///                                  de un usuario dado de baja (decisión 14)
///   IgnoreQueryFilters(["BajaLogica"]) → sin él, el nombre del actor eliminado desaparece
///   ThenByDescending(IdAuditoria) → sin él, con creado_el empatado (RelojFijo, o dos filas de la
///                                  misma operación) la paginación duplica y saltea filas
///   cada `if (filtro is { } x)`  → un filtro ignorado devuelve de más, en silencio
private IQueryable<FilaCrudaDeAuditoria> ConstruirQuery(FiltrosDeAuditoria f) =>
    from a in db.Auditoria
    where (f.Desde  == null || a.CreadoEl >= f.Desde)
       && (f.Hasta  == null || a.CreadoEl <= f.Hasta)
       && (f.Accion == null || a.Accion == f.Accion)
       && (f.IdActor == null || a.IdActor == f.IdActor)
       && (f.Entidad == null || a.Entidad == f.Entidad)
       && (f.IdEntidad == null || a.IdEntidad == f.IdEntidad)
       && (f.IdPuntoVenta == null || a.IdPuntoVenta == f.IdPuntoVenta)
    join u in db.Usuarios.IgnoreQueryFilters(["BajaLogica"]) on a.IdActor equals u.Id into actores
    from u in actores.DefaultIfEmpty()
    orderby a.CreadoEl descending, a.IdAuditoria descending
    select new FilaCrudaDeAuditoria(a.IdAuditoria, a.CreadoEl, a.Accion, a.Entidad, a.IdEntidad,
                                    a.IdActor, u == null ? null : u.NombreUsuario, a.IdPuntoVenta,
                                    a.ValorAnterior, a.ValorNuevo);
```

`ConsultarAsync` = `CountAsync` + `Skip((pagina-1)*tamanio).Take(tamanio)`, con
`pagina = Math.Max(pagina, 1)` y `tamanio = Math.Clamp(tamanio, 1, 200)`
(`ServicioDeUsuarios.ListarAsync:35-36`). `ConsultarParaExportacionAsync` = `Contar → Exigir →
Take(tope+1) → Exigir` (decisión 13). El `jsonb` viaja como `string` desde EF y se parsea a
`JsonElement` **en memoria**, después de materializar.

### API surface

| Route | Policy | Notes |
|---|---|---|
| `GET /api/auditoria?desde&hasta&accion&idActor&entidad&idEntidad&idPuntoVenta&pagina&tamanio` | `LecturaDeAuditoria` | Admin-only, **sin apilar** sobre `LecturaDeReportes` (Admin ∈ Supervisor+Admin ⇒ apilar sería un no-op que sugiere una relación inexistente) |
| `GET /api/auditoria/export?…&formato=xlsx` | inherited by co-location | `desde`/`hasta` **obligatorios** (regla de la casa del export + nombre determinístico). `NombreDeArchivo.Construir("auditoria", alcance, desde, hasta)` |

```csharp
// Ways.Api/Seguridad/Politicas.cs — forma exacta de LecturaDeRentabilidad.
public const string LecturaDeAuditoria = "lectura_auditoria";
.AddPolicy(LecturaDeAuditoria, p => p.RequireAuthenticatedUser()
    .RequireClaim(ClaimsWays.RolId, ((int)RolConocido.Admin).ToString()));
```

Export columns (8, in order, header row asserted — `mutation-proof-tests` rule 8):
`Fecha · Acción · Entidad · Id entidad · Actor · Punto de venta · Valor anterior · Valor nuevo`.
`Actor` null ⇒ `"#<idActor>"`; `Punto de venta` null ⇒ celda vacía (evento tenant-wide);
los dos payloads son `Celda.Texto(JsonSerializer.Serialize(elemento))`.

## Call sites — where exactly, and what does not change

| # | Acción | Archivo : ancla | Modo | Inserción | Payload | Lo que NO cambia |
|---|---|---|---|---|---|---|
| 1 | `precio.cambio` | `Precios/ServicioDePrecios.cs:186` (justo antes de `db.Precios.Add`, dentro de la transacción abierta en :94) | EF | **Una** llamada por operación, después del cierre de la fila abierta y del re-cierre del predecesor — una operación, una fila | ant: `{id_lista_precio, monto, vigente_desde}` de `filaAbierta` (NULL si no había) · nuevo: los mismos con los valores nuevos | El advisory lock sigue siendo el primer statement (:96); `ahora` se sigue capturando post-lock (:101); el `SaveChangesAsync` (:197) y el commit (:198) no se mueven. **`BuscarFilaAbiertaAsync` (:584) suma `monto` a su `SELECT`** — una columna más en un statement que ya corre bajo el lock, cero round trips nuevos |
| 2 | `usuario.alta` | `Usuarios/ServicioDeUsuarios.cs:113-114` | EF | `CreateExecutionStrategy` + `BeginTransactionAsync`; `Add` → `SaveChanges` → `Registrar` con el id generado → `SaveChanges` → `Commit` (decisión 11) | ant: NULL · nuevo: `{usuario, mail, id_rol, estado}` | `PoliticaDeRoles`, la derivación de `idTenantDestino` y los chequeos de disponibilidad quedan **afuera** de la transacción, como hoy |
| 3 | `usuario.actualizacion` | `ServicioDeUsuarios.cs:143` (antes de mutar la entidad, con los valores viejos todavía en memoria) | EF | Encolado antes del `SaveChangesAsync` de :157 | ant: `{usuario, mail, id_rol, estado}` (valores pre-mutación) · nuevo: los mismos, post | El `estabaBloqueado`/`Desbloquear` de :142-155 y el orden de validaciones |
| 4 | `usuario.baja` | `ServicioDeUsuarios.cs:200` | EF | Antes del `SaveChangesAsync` de :202 | ant: `{deleted_at: null, estado}` · nuevo: `{deleted_at: <momento>, estado}` — **ver Open Questions**: `estado:"eliminado"` del proposal no es un valor de `EstadoUsuario` | La baja sigue siendo lógica |
| 5 | `usuario.desbloqueo` | `ServicioDeUsuarios.cs:188` | EF | Antes del `SaveChangesAsync` de :189 | ant: `{estado}` **real** · nuevo: `{estado}` post-`Desbloquear` | `Desbloquear` sigue corriendo aunque la cuenta ya esté activa |
| 6 | `usuario.password` | `ServicioDeUsuarios.cs:177` | EF | Antes del `SaveChangesAsync` de :178 | ant: NULL · nuevo: `{por_el_propio_usuario: usuario.Id == contexto.UsuarioId}` — **jamás** el hash | La regla "cualquiera cambia la propia" (:167) |
| 7 | `venta.anulacion` | `Ventas/ServicioDeVentas.cs:541` (después del guard `!seAnulo`, antes del paso 2) | ADO | `RegistrarAsync(conexion, transaccionCruda, …)` | ant: `{estado: EstadoComprobante.Emitido}` — **la misma constante que liga el `WHERE` del UPDATE**, así que no pueden divergir · nuevo: `{estado: Anulado}` | `MarcarAnuladoAsync` sigue siendo la única autoridad race-safe; el guard de turno sigue siendo el statement 0; el orden turnos_caja → clientes → ledger intacto. **Único cambio**: su `RETURNING` pasa a `id_punto_venta` y el método a `int?` (decisión 8) |
| 8 | `compra.anulacion` | `Compras/ServicioDeCompras.cs:522` (después del guard, antes del paso 2) | ADO | ídem | ant: `{estado: "confirmada"}` (el `WHERE` del UPDATE lo garantiza, :677) · nuevo: `{estado: "anulada"}` · `id_punto_venta` del `RETURNING` que **ya existe** | `MarcarAnuladaAsync` sin tocar; la regla invertida de gastos (:577) sin tocar |
| 9 | `stock.ajuste` | `Stock/ServicioDeStock.cs:101` (después del upsert agregado y del de lote, antes del commit) | ADO | ídem | ant: `{cantidad: nueva − delta}` · nuevo: `{cantidad: nueva, id_movimiento_stock, observaciones}` | El orden agregado → lote (lock order) intacto; sin rechazo de negatividad, como hoy |
| 10 | `stock.decomiso` | `ServicioDeStock.cs:276` (**después** de los dos rechazos de negatividad) | ADO | ídem | ant: `{cantidad}` · nuevo: `{cantidad, id_movimiento_stock, observaciones, id_lote}` (`id_lote` null = no lote-efectivo) | Los dos `409 stock_insuficiente_para_decomiso` siguen decidiendo antes; un decomiso rechazado no deja fila |
| 11 | `stock.conteo` | `ServicioDeStock.cs:743` (`EjecutarConteoAsync`, tras el chequeo de consistencia) **y** `:810` (`EjecutarConteoPorLoteAsync`, por lote con diferencia) | ADO | Una fila **por movimiento de ledger escrito** (ver Open Questions) | ant: `{cantidad}` · nuevo: `{cantidad, id_movimiento_stock, observaciones, id_lote}` | El early-return de delta cero (:721-727) intacto ⇒ **conteo sin diferencia = cero filas de ledger y cero de auditoría**; la fase adquisición/aplicación del conteo por lote intacta |
| 12 | `cc.reliquidacion` | `CuentaCorriente/ServicioDeReliquidacion.cs:130` (después del marcador y de su chequeo de rowcount, antes del commit) | ADO | ídem | ant: `{saldo}` del `SELECT … FOR UPDATE` (:174) · nuevo: `{saldo: nuevoSaldo, id_movimiento, consumos_actualizados: |ids|, diferencia: delta}` | Lock del cliente como primer statement; los dos no-ops (sin elegibles, delta cero) siguen comiteando **sin** escribir nada — y por lo tanto sin fila de auditoría; `detalle text` PascalCase intacto |

`ServicioDeStock.InsertarMovimientoStockAsync` pasa a `RETURNING id_movimiento_stock` /
`ExecuteScalarAsync` y devuelve `int` para alimentar los payloads 9-11; `TransferirAsync` ignora el
valor y su comportamiento queda byte-idéntico (**y sigue sin escribir auditoría** — decisión 5 del
proposal). La copia privada homónima de `ServicioDeVentas` **no se toca**.

**Sujeto sin tenant.** Cuando el usuario auditado tiene `IdTenant is null` (cuenta de plataforma),
`auditoria.id_tenant NOT NULL` no admite la fila: las acciones `usuario.*` **no escriben nada** y no
fallan. Es el Out of Scope del proposal ("platform-level events"), tratado como límite con test, no
como agujero. Root editando a un usuario **de un tenant** sí escribe: `id_tenant` = el del sujeto e
`id_actor` = root; la policy RLS lo admite por su rama `app_es_plataforma()`.

## Web composition — `Auditoria.tsx`

`HistoricoDeCajas.tsx` verbatim para filtros + paginado (objeto `FiltrosDeAuditoria` +
`filtrosDeAuditoriaVacios()`, `generacionRef` de `react-async-state` regla 2, `cambiarFiltro` que
resetea a página 1, `cambiarPagina(±1)` con `disabled` en los bordes) más el `BotonDeDescarga` de
`Vencimientos.tsx` apuntando a `rutasDeExportacion.auditoria(filtros)`.

- Filtros: rango de fechas, `accion` (select alimentado por el catálogo espejado en `tipos.ts`),
  actor (id), entidad + id de entidad, punto de venta (incluye **"Todos"**, que devuelve también
  las filas con `id_punto_venta` NULL) — la opción "Todos" es un filtro ausente, nunca `0`.
- Columnas: `Fecha · Acción · Entidad · #Id · Actor · PV · (detalle)`. `Actor` null ⇒ `#<idActor>`;
  PV null ⇒ `—` con `title="Evento de todo el tenant"`.
- **Panel de detalle** (`PanelDeCambio`, componente propio con `data-testid` por lado): fila
  expandible que muestra `valor_anterior` / `valor_nuevo` clave por clave, marcando las claves que
  cambiaron; una clave presente solo en `valor_nuevo` se muestra como "—→ valor". Helper puro
  `compararPayloads(anterior, nuevo)` con tests colocados (`web-descriptor-tests`).
- **Degradación pre-aprobada**: si la slice 7 desborda, se entrega sin `PanelDeCambio` (los payloads
  siguen llegando por el export). Reducción documentada, nunca silenciosa.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Auditoria/Auditoria.cs` | Create | Entidad inmutable, sin `EntidadBase`, sin mutadores |
| `src/Ways.Domain/Auditoria/AccionAuditada.cs` | Create | Catálogo de 12 pares + `Todas` |
| `src/Ways.Domain/Auditoria/RegistroDeAuditoria.cs` | Create | Invariantes en el constructor (subset, denylist, snake_case) |
| `src/Ways.Domain/Auditoria/PayloadDeAuditoria.cs` | Create | 12 fábricas; ninguna acepta una entidad |
| `src/Ways.Infrastructure/Persistencia/Migraciones/…_AuditoriaEtapa14.cs` | Create | **La única** migración — exactamente el §A del gate |
| `src/Ways.Infrastructure/Persistencia/Configuraciones/AuditoriaConfiguration.cs` | Create | Mapeo, `jsonb`, índices |
| `src/Ways.Infrastructure/Persistencia/WaysDbContext.cs` | Modify | `DbSet` + `AplicarFiltroDeTenantEnAuditoria` |
| `src/Ways.Application/Abstracciones/IWaysDbContext.cs` | Modify | `DbSet<Auditoria> Auditoria` |
| `src/Ways.Application/Auditoria/ServicioDeAuditoria.cs` | Create | El writer (dos modos) + `SerializadorDeAuditoria` |
| `src/Ways.Application/Auditoria/ServicioDeConsultaDeAuditoria.cs` | Create | `ConstruirQuery`, `ConsultarAsync`, `ConsultarParaExportacionAsync` |
| `src/Ways.Application/Auditoria/Contratos.cs` | Create | `FiltrosDeAuditoria`, `FilaDeAuditoria`, `PaginaDeAuditoria` |
| `src/Ways.Application/Exportacion/ExportacionDeAuditoria.cs` | Create | `De(IReadOnlyList<FilaDeAuditoria>, ctx, zona)` + 8 columnas |
| `src/Ways.Application/Precios/ServicioDePrecios.cs` | Modify | 1 call site + `monto` en `BuscarFilaAbiertaAsync` |
| `src/Ways.Application/Usuarios/ServicioDeUsuarios.cs` | Modify | 5 call sites + la transacción de `CrearAsync` |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modify | 1 call site + `MarcarAnuladoAsync → int?` |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modify | 1 call site |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modify | 4 call sites + `InsertarMovimientoStockAsync → int` |
| `src/Ways.Application/CuentaCorriente/ServicioDeReliquidacion.cs` | Modify | 1 call site |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modify | `LecturaDeAuditoria` |
| `src/Ways.Api/Endpoints/AuditoriaEndpoints.cs` | Create | 2 rutas |
| `src/Ways.Web/src/api/{tipos,auditoria}.ts` | Create/Modify | Espejos + `clienteDeAuditoria` + `rutasDeExportacion.auditoria` |
| `src/Ways.Web/src/paginas/Auditoria.tsx` (+ `.test.tsx`) | Create | Pantalla + `PanelDeCambio` + `compararPayloads` |
| `src/Ways.Web/src/App.tsx` · `componentes/Layout.tsx` | Modify | Una ruta + una línea de nav (visible solo para Admin) |
| `docs/10-modelo-de-datos.md` | Modify | La tabla `auditoria` + "Estado (Etapa 14)", desde adentro de la slice 1 |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | **Unmodified** | El mapeo genérico `fk_`/`23503` ya cubre la etapa (gate §B) |

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (sin DB) | `RegistroDeAuditoria`: subset OK / clave extra en `anterior` ⇒ throw / `anterior` null legal / `nuevo` vacío ⇒ throw / clave `hash_password` ⇒ throw / clave `PascalCase` ⇒ throw. `AccionAuditada.Todas`: 12 entradas, sin duplicados, `<dominio>.<operacion>`, `entidad` no vacía. **Genérico sobre las 12 fábricas**: ningún payload viola subset ni denylist ni snake_case | xUnit puro, patrón `PoliticaDeRoles`. Sin fixture, sin contenedor |
| Domain unit — serializador | `SerializadorDeAuditoria`: claves snake_case, enums como etiqueta de base (`"emitido"`, `"activo"`), `DateTimeOffset` ISO-8601, null explícito ≠ clave ausente | Un test por regla; mata la mutación `PropertyNamingPolicy` (no-op) |
| Integration — RLS (`ways_app`) | `SELECT` con el GUC de otro tenant ⇒ **0 filas** (row count, no excepción); `INSERT` con `id_tenant` ajeno ⇒ `42501` por SQLSTATE | `mutation-proof-tests` regla 5: conexión `ways_app` (NOSUPERUSER NOBYPASSRLS), a nivel statement |
| Integration — fail-closed (**el test insignia**) | Sesión con `contexto.UsuarioId` inexistente: (a) `AbrirNuevoPrecioAsync` ⇒ `400 referencia_invalida` **y `precios` sin fila nueva y la fila vieja todavía abierta**; (b) anulación de un comprobante **100% servicio sin CC** ⇒ mismo código **y `estado` sigue `emitido`** | Decisión 10. Prueba a la vez fail-closed, misma transacción y el mapeo `23503` del gate §B |
| Integration — cobertura, una por acción | 12 tests: la operación deja **exactamente una** fila (y `stock.conteo` por lote, N = lotes con diferencia), con `accion`, `entidad`, `id_entidad`, `id_actor`, `id_punto_venta` y **ambos payloads clave por clave** | Valores discriminantes distintos por fila y por columna (regla 6): ningún id repetido entre articulo/PV/actor/entidad |
| Integration — el caso insignia | Anulación de comprobante **100% servicio, sin cuenta corriente**: cero movimientos de stock, cero de CC, **una** fila de auditoría con el actor | El único rastro hoy es `updated_at` sin actor |
| Integration — límites registrados | Conteo sin diferencia ⇒ **cero** filas de ledger y cero de auditoría; reliquidación con delta cero ⇒ cero filas; `TransferirAsync` ⇒ **cero** filas de auditoría; edición de una cuenta de plataforma (`id_tenant` NULL) ⇒ cero filas y **200** | Cada límite es un scenario, no un descubrimiento |
| Integration — precio con predecesor | Reemplazo de una fila pendiente (cierra la pendiente **y** re-cierra el predecesor) ⇒ **una** fila de auditoría | Mata la mutación "una llamada por fila cerrada" |
| Integration — denylist real | `usuario.actualizacion` y `usuario.password` sobre una cuenta con hash conocido: el texto de los dos `jsonb` **no contiene** el hash ni la subcadena `password` como clave | Assert sobre el documento crudo leído de la base, no sobre el DTO |
| Integration — reloj | Todo con `RelojFijo(2026-08-14T12:00:00Z)`; `creado_el` de la fila de auditoría **igual exactamente** al instante fijo y al `creado_el` del movimiento de ledger hermano | Mediodía UTC: `hoy` estable en UTC y en `-03:00`, así que la aserción la carga el reloj, no el borde de día. Mata `DateTimeOffset.UtcNow` |
| Integration — consulta | Cada filtro devuelve su subconjunto con seeds asimétricos (fechas, acciones, actores, entidades y PVs **todos distintos**); "PV todos" incluye las filas con `id_punto_venta` NULL; orden como **secuencia**, no como conjunto; paginación con `creado_el` **empatado** en todas las filas (RelojFijo) ⇒ página 2 sin repetir ni saltear; `idEntidad` sin `entidad` ⇒ `400`; `accion` desconocida ⇒ 200 con 0 filas | `mutation-proof-tests` reglas 4/6 |
| Integration — actor | Fila de un actor **soft-deleted** ⇒ el nombre sigue apareciendo; fila de un actor root leída por un Admin de tenant ⇒ la fila **aparece** con `actor: null` e `idActor` presente | Mata el inner join y la pérdida del `IgnoreQueryFilters` |
| Integration — autorización | Supervisor ⇒ **403** en `/api/auditoria` y en `/export`; Vendedor ⇒ 403; Root ⇒ 403; Admin ⇒ 200 y ve filas de **todos** los PV de su tenant; Admin del tenant B nunca ve filas del tenant A | Un test por rol por ruta |
| Integration — export | Mismo query string en JSON y XLSX: **todas** las celdas de **todas** las filas + **la fila de encabezados completa en orden** (regla 8); celda de PV vacía en un evento tenant-wide; payload igual al del JSON; con `TopeDeFilas` bajado, **rechaza en vez de truncar** (y el segundo `Exigir` cubre la carrera) | Shape `ExportacionTests` de la etapa 11 |
| Integration — no-regresión del checkout | `VentasCheckoutTests` con su `Assert.Equal(16, …)` **sin editar**, y `git diff` sin `EjecutarTransaccionAsync` | Criterio de verify vinculante |
| Web (vitest) | `compararPayloads` (clave solo en nuevo, clave cambiada, clave igual, ambos null); filtros que resetean a página 1; respuesta desactualizada descartada (**promesa stale resuelta dentro de `act`**, regla 7); paginado deshabilitado en los bordes; `actor` null renderiza `#id` y PV null renderiza `—` | `web-descriptor-tests` + `react-async-state` |
| Exempt | Estética del panel de detalle más allá de sus testids — exención registrada, heredada de las etapas 12/13 | — |

## Mutation targets

`mutation-proof-tests`: nombrar la cláusula, aplicar la mutación, ver fallar el test nombrado,
revertir, dejar la evidencia (aplicada → test que falló → revertida → verde) en el cuerpo del PR.

| Slice | Cláusula | Mutación | Test que DEBE fallar |
|---|---|---|---|
| 1 | La regla de subconjunto en `RegistroDeAuditoria` | borrar el chequeo | fact de Domain con una clave extra en `valor_anterior` |
| 1 | La denylist de claves | borrar el chequeo | fact `hash_password` + el test de integración sobre `usuario.*` |
| 1 | `DictionaryKeyPolicy = SnakeCaseLower` | cambiarlo por `PropertyNamingPolicy` (no-op sobre un diccionario) | test de claves snake_case del serializador |
| 1 | `JsonStringEnumConverter(SnakeCaseLower)` | quitar la política del converter | `estado` serializa `"Emitido"` en vez de `"emitido"` |
| 1 | `transaccion is null ⇒ throw` en `RegistrarAsync` | borrar el guard | test que llama al writer sin transacción y espera la excepción |
| 1 | `HabilitarRlsDeTenant("auditoria")` en la migración | borrar la línea | test cross-tenant sobre `ways_app` (row count) y el `42501` del INSERT ajeno |
| 1 | `creado_el = reloj.Ahora` | `DateTimeOffset.UtcNow` | igualdad exacta contra `RelojFijo` |
| 1 | `id_actor = contexto.UsuarioId` | un literal / un parámetro del llamador | cobertura de cualquier acción (el actor esperado no aparece) |
| 2 | `db.Auditoria.Add` **antes** del `SaveChangesAsync` de precios | moverlo después del `CommitAsync` | **fail-closed de precios**: el precio queda cambiado con el INSERT roto |
| 2 | La transacción explícita de `CrearAsync` | volver a dos `SaveChangesAsync` sueltos | fail-closed de `usuario.alta` (usuario creado sin fila de auditoría) |
| 2 | La captura del payload **antes** de mutar la entidad (`ActualizarAsync`) | moverla después de las asignaciones | `valor_anterior` == `valor_nuevo` en el test de cobertura |
| 2 | `monto` en el `SELECT` de `BuscarFilaAbiertaAsync` | quitarlo / hardcodear 0 | payload de `precio.cambio` con el monto anterior real |
| 3 | `RETURNING id_punto_venta` de `MarcarAnuladoAsync` | volver a `id_comprobante_venta` + leer el PV del pre-read | anulación cuyo pre-read no coincide (el PV de la fila de auditoría) |
| 3 | `RegistrarAsync` dentro de la transacción de anulación | moverlo después de `CommitAsync` | **fail-closed de anulación** (100% servicio sin CC) |
| 3 | `EstadoComprobante.Emitido` como `valor_anterior` | reemplazarlo por un literal `"anulado"` | payload de `venta.anulacion` |
| 4 | `cantidad − delta` como before-image | usar `nueva` en los dos lados | cobertura de `stock.ajuste` (anterior ≠ nuevo, con delta ≠ 0) |
| 4 | El early-return de delta cero (`ServicioDeStock.cs:721`) | quitarlo | conteo sin diferencia: cero filas de ledger **y** cero de auditoría |
| 4 | El `saldo` del `FOR UPDATE` como before-image | releerlo después del UPDATE | `cc.reliquidacion`: `saldo` anterior ≠ nuevo con diferencia conocida |
| 5 | `ThenByDescending(a.IdAuditoria)` | borrarlo | paginación con `creado_el` empatado en todas las filas |
| 5 | `candidatos.DefaultIfEmpty()` (LEFT JOIN a usuarios) | inner join | fila de actor root leída por un Admin de tenant / actor soft-deleted |
| 5 | `IgnoreQueryFilters(["BajaLogica"])` | quitarlo | el nombre del actor eliminado desaparece |
| 5 | Cada `if (filtro …)` de `ConstruirQuery` | borrar uno | el test de ese filtro (seeds asimétricos: ninguna otra cláusula produce el mismo subconjunto) |
| 5 | `.RequireAuthorization(Politicas.LecturaDeAuditoria)` | borrar la línea | **Supervisor ⇒ 403** en `/api/auditoria` |
| 5 | El filtro de tenant/RLS de la consulta | leer con el GUC de otro tenant | test de aislamiento sobre `ways_app` (row count) + Admin del tenant B |
| 6 | `GuardaDeTope.Exigir` (los dos) | borrar el segundo | export con tope bajado: rechaza en vez de truncar |
| 6 | Los encabezados de `ExportacionDeAuditoria` | intercambiar `Valor anterior`/`Valor nuevo` | aserción de la fila de encabezados completa (regla 8) |
| 6 | `.RequireAuthorization` del `/export` | borrar la línea | Supervisor ⇒ 403 en el export |
| 7 | `compararPayloads` (clave solo en `nuevo`) | tratarla como "sin cambio" | test colocado del helper |
| — | **No-regresión**: `VentasCheckoutTests` `Assert.Equal(16, …)` | — | criterio de verify: el archivo no aparece en el diff de la etapa |

## Slicing (7 PRs, stacked-to-main — el plan del proposal, ratificado y re-alcanzado)

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1 | `feat/stage14-slice1-tabla-auditoria` | Migración `AuditoriaEtapa14` + entidad + `AuditoriaConfiguration` + `DbSet` + filtro de tenant propio + RLS + `AccionAuditada` + `RegistroDeAuditoria` + `PayloadDeAuditoria` + `SerializadorDeAuditoria` + los dos modos del writer + doc 10 | ~430 | Suite de Domain completa; RLS sobre `ways_app`; `42501`; guard de transacción null |
| 2 | `…slice2-precios-usuarios` | `precio.cambio` (+ `monto` en el SELECT) + los 5 `usuario.*` (+ la transacción de `CrearAsync`) | ~340 | Fail-closed de precios; denylist real; predecesor ⇒ una fila; cuenta de plataforma ⇒ cero filas |
| 3 | `…slice3-anulaciones` | `venta.anulacion` (+ `MarcarAnuladoAsync → int?`) + `compra.anulacion` | ~300 | **100% servicio sin CC**; fail-closed de anulación; guard de 16 sin tocar |
| 4 | `…slice4-stock-cc` | `stock.ajuste` / `decomiso` / `conteo` (simple y por lote) + `cc.reliquidacion` + `InsertarMovimientoStockAsync → int` | ~320 | Conteo sin diferencia; before-image derivada; transferencia sin filas |
| 5 | `…slice5-consulta` | `LecturaDeAuditoria` + `ConstruirQuery` + `ConsultarAsync` + `GET /api/auditoria` | ~380 | Los 7 filtros con seeds asimétricos; empate de `creado_el`; actor root/eliminado; 403 por rol; aislamiento de tenant |
| 6 | `…slice6-export` | `/api/auditoria/export` + `ExportacionDeAuditoria` + `rutasDeExportacion` | ~230 | Paridad JSON↔XLSX celda por celda + encabezados; rechazo en el tope |
| 7 | `…slice7-web` | `Auditoria.tsx` + `PanelDeCambio` + `compararPayloads` + cliente + ruta/nav + tests | ~360 | Descriptor tests; stale dentro de `act`; paginado; `actor` null |

Total ≈ **2 360**. `delivery_strategy: auto-chain`, `chain_strategy: stacked-to-main`, una ronda de
judgment-day por slice. Orden de merge `1 → 2 → 3 → 4 → 5 → 6 → 7`; después de la 1, las slices
**2, 3 y 4 tocan servicios disjuntos** y son plegables en paralelo; 5 y 6 solo dependen de la 1.

**Decision needed before apply: No** · **Chained PRs recommended: Yes** · **400-line budget risk:
Medium**.

**Degradación pre-aprobada**, en orden de prioridad:

1. **Si la slice 1 desborda** — split *pre-autorizado* en `1a` (migración + entidad + config + filtro
   + RLS + sus tests + doc 10) y `1b` (catálogo + payloads + serializador + writer + tests). La
   migración no puede viajar en una slice que podría caerse.
2. **Si la slice 5 desborda** — cortar en el límite consulta/autorización: `5a` (policy + ruta con
   filtros de fecha/acción/actor + 403s), `5b` (entidad/id_entidad/PV + paginación).
3. **Si la slice 7 desborda** — entregar la pantalla con filtros, listado y descarga y **dejar caer
   el panel de detalle** (`PanelDeCambio` + `compararPayloads`); el payload sigue llegando por el
   export. Reducción documentada, nunca silenciosa.
4. **Nunca se degrada**: la cobertura de las 12 acciones y la regla fail-closed. Una slice de
   cobertura se parte, jamás se recorta.

## Binding verify criteria

1. Exactamente **una** migración nueva, `AuditoriaEtapa14`, con el DDL del §A del gate y nada más;
   `dotnet ef migrations has-pending-model-changes` limpio. Cualquier DDL extra reabre el gate.
2. `VentasCheckoutTests` con su constante `16` **byte-idéntica**, y ningún archivo de
   `src/Ways.Application/Ventas/` fuera de las líneas de `EjecutarAnulacionAsync`/`MarcarAnuladoAsync`
   en el diff de la etapa.
3. `movimientos_cuenta_corriente.detalle` sin cambios de tipo, contenido ni serialización;
   `ManejadorDeErrores.cs` sin cambios.
4. Evidencia de mutación registrada en el cuerpo del PR para **cada** fila de la tabla de arriba que
   corresponda a esa slice.
5. Suites Domain / Application / Integration / vitest verdes; tests colocados para cada helper puro
   nuevo del web (`web-descriptor-tests`).

## Threat Matrix

N/A — esta etapa no toca ruteo, comandos de shell, subprocesos, automatización de VCS/PR,
clasificación de archivos ejecutables ni integración de procesos. Sus superficies de riesgo
(aislamiento por tenant, autorización, secretos en el payload) están cubiertas por la tabla de
mutation targets, que sí es vinculante.

## Open Questions

- [ ] **`usuario.baja` no puede llevar `{estado:"eliminado"}`**: `EstadoUsuario` es
      `Activo | Inactivo | Bloqueado` y `EliminarAsync` escribe `deleted_at`, no `estado`. El diseño
      usa `{deleted_at, estado}` en ambos lados. **`sdd-spec` corre en paralelo y muy probablemente
      transcriba la tabla del proposal verbatim** — hay que reconciliar en `sdd-tasks`.
- [ ] **`stock.conteo` escribe una fila por movimiento de ledger, no una por operación.** El conteo
      por lote (etapa 12) escribe N movimientos con N cantidades distintas; una sola fila obligaría a
      un `id_movimiento_stock` que a veces es null y a veces uno de N. El payload gana `id_lote`
      (null = no lote-efectivo, igual que decomiso). Misma reconciliación con `sdd-spec`.
- [ ] **`stock.ajuste` no lleva `id_lote` y `stock.decomiso` sí**, tal como la tabla del proposal.
      Es una asimetría heredada; agregarlo a ajuste sería aditivo y consistente. Recomendación:
      agregarlo. Decide `sdd-spec`.
- [ ] **La cuenta de plataforma (`usuarios.id_tenant IS NULL`) no genera fila de auditoría.** Es el
      Out of Scope del proposal, pero convierte la regla "toda operación auditada deja rastro" en
      "toda operación auditada **sobre un sujeto de tenant** deja rastro". Registrado como límite con
      test; el día que exista un log de plataforma, cambia.
- [ ] **`ServicioDeStock.InsertarMovimientoStockAsync` cambia de firma** (`Task` → `Task<int>`) y lo
      usa también `TransferirAsync`, que ignora el valor. Es la única ondulación fuera de los 11 call
      sites; alternativa (duplicar el INSERT crudo) es peor. Registrado para que verify no lo lea
      como violación de alcance.
- [ ] **El export no pagina y el listado sí.** Un rango de fechas amplio sobre un tenant ruidoso
      puede chocar contra `TopeDeFilas` (25.000 por default) — el export **rechaza**, no trunca, y el
      operador acota el rango. Es el contrato de la etapa 11, no una regresión.
