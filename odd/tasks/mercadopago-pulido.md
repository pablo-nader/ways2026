# Mercado Pago — polish of the connection (E1/E2 follow-ups)

## Objective

Close the follow-ups that judgment-day left open on the Mercado Pago connection (E1: #555 #558 #560) and on the seller
token / API client (E2 T1: #563), before E3 builds charges on top of them. No new feature.

## Why

They are small, independent of the owner's sucursales stack (which blocks E2 T2–T6), and some of them are traps for E3:
a stale 401 that marks a healthy connection as `requiere_reconexion` would cut charges in the middle of a sale.

## Scope

- T1 backend (`src/Ways.Api|Application|Infrastructure` Mercado Pago code, tests, one migration, doc 10 §07).
- T2 web (`src/Ways.Web/src/paginas/organizacion/*MercadoPago*`, `src/Ways.Web/src/api/mercadoPago.ts`, tests).
- Out of scope: E2 T2–T6, new endpoints beyond what a fix needs, splitting the generic 502 of the code exchange (kept as
  a follow-up: it needs Mercado Pago's real error catalogue).

## Tasks

- [x] T1 — Backend follow-ups. Route: delegated (several non-trivial files). Done 2026-10-31.
  1. A non-JSON 2xx from `/oauth/token` (`ClienteOAuthMercadoPago`) answers a typed error, never an unhandled
     `NotSupportedException`/`JsonException`.
  2. Record who disconnected: `conexiones_mercadopago.id_usuario_desconecto` (nullable, same FK pattern as
     `id_usuario_conecto`), set by the disconnect endpoint; doc 10 §07.
  3. A `requiere_reconexion` connection can be disconnected (→ `desconectada`), so the Admin can clear it without
     reconnecting.
  4. The `Down()` of migration `RenovacionDeConexionesMercadoPago` works when `requiere_reconexion` rows exist (they
     become `desconectada` before the old CHECK comes back).
  5. A stale 401 (the guarded UPDATE touched no row because a renewal already rotated the token) no longer answers
     `mercadopago_requiere_reconexion` on a healthy connection: retry once with the fresh token, same idempotency key.
  6. An unexpected exception during the pre-use renewal still records `fecha_ultimo_intento_renovacion`
     (best-effort, separate write), so the backoff applies.
  7. The callback comment says exactly what it guarantees; `ResultadoDeCarrera` evaluated once in the callback catch.
- [x] T2 — Web follow-ups (stacked on T1). Route: delegated (several non-trivial files). Done 2026-10-31.
  1. Going back from Mercado Pago with the browser's Back (bfcache) never leaves the card stuck in "Conectando…".
  2. If the re-read after connect/disconnect fails, the card shows an error, not the old state.
  3. The `#mercadopago=completar…` fragment is cleared on landing even when the Admin section does not mount (non-Admin,
     or the section is not rendered).
  4. An already-open disconnect confirmation honors `bloqueado` while an exchange runs.
  5. A `requiere_reconexion` card offers "Desconectar" (uses T1.3).

## Acceptance criteria

Every item above has a test that fails without the fix (mutation evidence recorded for the guards), the structural guard
tests pass, and judgment-day returns APPROVED for each PR.

## Checks

- `dotnet build Ways.slnx -c Release`; `Ways.Domain.Tests`; `Ways.Application.Tests` (full, structural guards);
  integration filter `MercadoPago|UpDownUp|Cobertura|AislamientoDeTenant|SuperficieDeAutorizacion|InventarioDeDependientes`
  (full suite left to CI, never two suites at once).
- Web: `npm run build`, oxlint, vitest.

## Delivery strategy

`ask-on-risk`, chain `stacked-to-main`. T1 branch `pnader/mercadopago-pulido-t1` from `pnader/mercadopago-e2-t1`
(ca490203, PR #563); T2 stacks on T1. Forecast ≈ 300 + 200 authored lines.

## Progress

- 2026-10-31: document created; branch `pnader/mercadopago-pulido-t1` from ca490203.

- 2026-10-31: T1 done (writer). Model change (T1): `conexiones_mercadopago.id_usuario_desconecto int NULL` + FK
  `fk_conexiones_mercadopago_usuario_desconecto` (RESTRICT, to `usuarios`) + `ix_conexiones_mercadopago_usuario_desconecto`;
  migration `UsuarioQueDesconectoMercadoPago` (DDL only, no backfill); doc 10 §07 updated; inventory golden
  (`inventario-de-dependientes.txt`) gets the `Usuario | conexiones_mercadopago | id_usuario_desconecto | marcado` line.

### T1 evidence

- Build `dotnet build Ways.slnx -c Release`: OK. `Ways.Domain.Tests`: 2110 passed. `Ways.Application.Tests` (full, with structural
  guards): 1618 passed. Integration filter (`MercadoPago|UpDownUp|Cobertura|AislamientoDeTenant|SuperficieDeAutorizacion|
  InventarioDeDependientes`): 133 passed.
- Item 1: `ClienteOAuthMercadoPago` reads the 2xx body as bytes and deserializes it; `ReadFromJsonAsync` (and `ReadAsStringAsync`) throw
  `InvalidOperationException` on an unknown charset in `Content-Type`, which escaped untyped. RED observed (InvalidOperationException),
  GREEN with `mercadopago_intercambio_fallido`; no new public code.
- Item 2/3: disconnect is still one guarded `UPDATE` (no lock, no tracked entity); the guard is now `activa OR requiere_reconexion`
  and it sets `id_usuario_desconecto`. Tests in `MercadoPagoConexionTests`.
- Item 4: `Down()` runs `SqlDeBajaDeRequiereReconexion` (`SET LOCAL app.acceso = 'plataforma'` + UPDATE) after dropping the three CHECKs and
  before restoring the two-value one. `MercadoPagoMigracionTests`: UpDownUp with rows in the three states, and the SQL block under
  `ways_app` (0 rows without the GUC, 1 with it, 0 on rerun).
- Item 5: `ClienteApiMercadoPago` retries once when the guarded mark changed no row and the re-read connection is active with a different
  expiry; same key, same body, new token. Unit tests (5) + integration test with a renewal committed between the read and the 401.
- Item 6: `ProveedorDeTokenMercadoPago` calls `ServicioDeRenovacion....RegistrarIntentoAparteAsync` (now public; own scope, swallows and logs
  its own failure) when a non-`ErrorDominio` exception escapes the pre-use renewal. Integration test: attempt recorded, 3 later calls do not
  hit the token endpoint.
- Item 7: callback comment states the exact guarantee; the `catch` evaluates `ResultadoDeCarrera` once (`is { } carrera`). Behavior-preserving
  refactor: covered by the two existing backstop tests (no new test can distinguish one evaluation from two).

### T1 mutation evidence

- Item 3: guard reduced to `Activa` only -> `UnaConexionQueRequiereReconexionSePuedeDesconectar...` fails; reverted.
- Item 4: removing `migrationBuilder.Sql(SqlDeBajaDeRequiereReconexion)` from `Down()` -> UpDownUp fails with 23514
  `ck_conexiones_mercadopago_estado`; making the GUC end as `'tenant'` -> the `ways_app` test fails (expected 1, actual 0); reverted.
- Item 5: dropping `|| yaReintentado` -> third request, `UnSegundo401Tras...` fails; dropping the expiry comparison -> `...SinUnTokenMasNuevo...`
  fails; testing only `yaReintentado` (ignoring the changed-row result) -> `...QueSiCambioLaFilaNuncaReintenta` fails; passing
  `yaReintentado = true` always -> the integration retry test fails with `requiere_reconexion`; reverted.
- Item 6: guard replaced with `if (false)` -> `UnaExcepcionInesperadaDeLaRenovacionAnotaElIntento...` fails (`FechaUltimoIntentoRenovacion` null); reverted.

### T1 decisions and residuals

- A re-read that fails with `mercadopago_no_disponible` after a stale 401 propagates as 503 instead of `requiere_reconexion` (the connection may
  still be healthy; only `requiere_reconexion`/`no_conectada` keep today's answer).
- The `Down()` of `UsuarioQueDesconectoMercadoPago` drops the column, so who disconnected is lost on rollback (expected).
- Residual: the "separate UPDATE fails -> only logged" branch of item 6 has no test (needs a fault injected into a fresh scope).
- Residual: `id_usuario_desconecto` has no CHECK tying it to `estado = 'desconectada'`; rows disconnected before this migration stay NULL.

## Next step

Judgment-day over the T2 slice, then PR (stacked on T1) with auto-merge and Auto-fix.

## Judgment-day T1 verdict

- 2026-10-31: **APPROVED** in round one, no CRITICAL. Shared point (B WARNING, A SUGGESTION "pre-existing"): a 2xx body cut
  mid-read in `ClienteOAuthMercadoPago`; not reachable, since `PostAsJsonAsync` buffers the body (`ResponseContentRead`), so a cut
  surfaces at the send, already mapped. Suspect (B only): disconnect also closes an older `requiere_reconexion` row that coexists with
  a newer `activa` one, giving it the same actor and date; follow-up (close the old row when reconnecting). Suggestions: retry skipped
  when the empresa reconnected in between (new `IdConexion`) or the expiry did not change; both fail safe (`requiere_reconexion`).

### T2 evidence

- `npm run build`: OK. `npx oxlint src/paginas/organizacion`: no output (clean). `npx vitest run` (full): the first run had 1 failure
  in an unrelated file (a `Yerba nueva` field assertion, line ~2108); a second full run was entirely green (flaky, not touched here).
- Item 1: `ConexionMercadoPago` listens to `pageshow`; with `persisted` and a connect in flight (`navegandoRef`) it frees `ocupado` and
  re-reads (new generation). A non-persisted `pageshow` changes nothing (test).
- Item 2: a failed read now clears `estado`, so the card shows the read error (with a "Reintentar" button) instead of the stale state.
- Item 3: `Empresas.tsx` calls `descartarRetornoDeMercadoPago()` (new `fragmentoMercadoPago.ts`, which also hosts `limpiarFragmento`)
  when the user is known and is not Admin. For Admin the section always mounts once the list settles (`cargada`) and consumes it. A hash
  without a `mercadopago` param is left alone.
- Item 4: the confirmation gets `ocupado={inerte}` (so `bloqueado` disables its confirm button; label "Completando la conexión…").
- Item 5: a `requiere_reconexion` card renders "Desconectar" next to the reconnect form, same confirmation and same DELETE.

### T2 mutation evidence

- Item 1: listener not registered -> `una restauración libera la tarjeta y vuelve a leer el estado` fails; reverted.
- Item 2: `setEstado(null)` in the read catch removed -> `si la lectura que sigue a desconectar falla...` fails; reverted.
- Item 3: the effect in `Empresas.tsx` neutered -> `un usuario que no es Admin no deja el code...` fails; reverted.
- Item 4: `ocupado={ocupado}` instead of `inerte` -> `ConexionMercadoPago.test.tsx` fails; reverted.
- Item 5: button not rendered -> the two reconexión tests fail.

### T2 decisions and residuals

- `Empresas.tsx`/`Empresas.test.tsx` live in `src/Ways.Web/src/paginas/`, not in `paginas/organizacion/` (the allowed list had the wrong path).
- No `|| bloqueado` guard in `confirmarDesconexion`: the confirm button is disabled, so no test can kill it (dead guard not shipped).
- The Admin-with-unmounted-section case cannot happen today (`esAdmin && cargada` always becomes true); only the non-Admin path needs the cleanup.
- Residual: `Reintentar` and the "Completando la conexión…" label are small additions beyond the five items.
