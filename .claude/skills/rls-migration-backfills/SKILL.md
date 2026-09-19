---
name: rls-migration-backfills
description: "Trigger: migration backfill, data migration, UPDATE/INSERT in migrationBuilder.Sql, populate new column, tenant table with RLS. Backfills set the platform GUC and are tested under ways_app."
license: Apache-2.0
metadata:
  author: pablo-nader
  version: "1.0"
---

## Activation Contract

Load when an EF Core migration in `src/Ways.Infrastructure/Persistencia/Migraciones/` writes data (UPDATE/INSERT/DELETE) into any table enrolled with `HabilitarRlsDeTenant` (every tenant table in Ways).

## Hard Rules

- Every data-writing `migrationBuilder.Sql(...)` on a tenant table starts with `SET LOCAL app.acceso = 'plataforma';` INSIDE the same `Sql()` block. Tenant tables run under FORCE ROW LEVEL SECURITY and the app role is NOBYPASSRLS: without the GUC the statement touches 0 rows and reports success.
- Never rely on the API startup path (`InicializadorDeBaseDeDatos` with the platform-keyed context) setting the GUC: `dotnet ef database update` goes through `WaysDbContextFactory`, which registers no tenant interceptor. The migration must be correct on both paths.
- Keep the backfill idempotent (a WHERE that excludes already-filled rows) and run it AFTER the constraints it must satisfy.
- A backfill test that runs over `OwnerConnectionString` proves nothing about RLS (the owner bypasses it). At least one test runs the backfill over the NOBYPASSRLS `ways_app` connection without the tenant interceptor, and is mutation-proven: remove the `SET LOCAL` → the test goes red.
- Add a short Spanish comment in the `Sql()` block pointing to this rule, as `20260811033540_CostoCongeladoEnVentaEtapa9.cs` and `20260917025328_QuitarVueltoMaximo.cs` do.

## Decision Gates

| Situation | Action |
|---|---|
| Migration only changes schema (DDL) | Skill does not apply |
| Backfill on a tenant table | `SET LOCAL` in the same block + `ways_app` test |
| Backfill that must pick one row per tenant | GROUP BY `id_tenant`, and leave ambiguous tenants NULL rather than guessing |
| Backfill on a platform table without RLS | `SET LOCAL` optional; say why in the comment |

## Execution Steps

1. List every table the migration's `Sql()` blocks write to; check each for `HabilitarRlsDeTenant`.
2. Add the `SET LOCAL` line and the comment inside each data-writing block.
3. Write the backfill test over `ways_app` (see `WaysApiFixture`), then mutate the `SET LOCAL` away and confirm red.
4. Run the migration integration tests against real Postgres.

## Output Contract

Report the tables backfilled, confirm the `SET LOCAL` is in each block, and give the mutation evidence of the `ways_app` test.

## References

- `src/Ways.Infrastructure/Multitenancy/RlsMigrationBuilderExtensions.cs` — RLS policy (`app_es_plataforma() OR id_tenant = app_tenant_actual()`).
- `src/Ways.Infrastructure/Persistencia/WaysDbContextFactory.cs` — design-time/deploy factory without an interceptor.
- `.claude/skills/mutation-proof-tests/SKILL.md` — mutation evidence rules.
