---
name: db-error-backstops
description: "Trigger: new unique index, unique constraint, foreign key, migration, EF Core write path, 23505, 23503, duplicate check. Every new Postgres constraint ships with its error-translation backstop and SQLSTATE-asserted race test."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when adding or modifying: a unique index/constraint, a foreign key, an EF Core entity write path, or an endpoint that inserts/updates rows in this repo (Ways).

## Hard Rules

- Every NEW unique index reachable from a client write path MUST get a `23505` → domain 409 mapping in `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` (constraint-name match, per-family domain code). Prefer extending the existing dictionary/prefix mapping over adding hardcoded cases.
- Every NEW FK whose referenced id comes from client input MUST be either pre-validated in the service (tenant-scoped existence check) or covered by the generic `23503` → 400 `referencia_invalida` mapping. Silent fall-through to 500 is a defect.
- Availability pre-checks (`ExigirDisponibilidadAsync`-style) are best-effort UX only. The constraint + backstop is the contract; never treat the pre-check as the protection (TOCTOU races are normal operation).
- Cross-tenant pre-checks CANNOT use `IgnoreQueryFilters(["Tenant"])` — RLS still filters at the DB layer. Use the platform-keyed `IWaysDbContext` (`ClavesDeContexto.Plataforma`), as `ServicioDeUsuarios`/`ServicioDeAutenticacion` do.
- Tests for constraint behavior MUST assert the specific SQLSTATE (`42501`, `23505`, `23503`) or the translated domain code — never just the exception type. RLS-blocked UPDATE/DELETE yields 0 rows, NOT an exception; assert the mechanism actually in play.
- At least one race-style integration test per new backstop family (two concurrent creates → exactly one 201 + one 409).

## Decision Gates

| Situation | Action |
|---|---|
| New unique index on a tenant table | 23505 mapping + pre-check on request context + race test |
| New unique index enforcing a GLOBAL rule (e.g. mail) | Pre-check via platform-keyed context + 23505 mapping + race test |
| New FK fed by client input | Service-level ownership/existence validation, 23503 mapping as backstop |
| Constraint only written by platform seed | 23505 mapping optional; document why if omitted |

## Execution Steps

1. List every constraint the migration adds; classify per the gates table.
2. Add/extend mappings in `ManejadorDeErrores` before writing endpoint code.
3. Write the race/SQLSTATE tests in the same work unit as the constraint.
4. Run the full integration suite against real Postgres before committing.

## Output Contract

The PR diff shows, for each new constraint: its mapping (or documented exemption), its test asserting the right SQLSTATE/domain code, and no path where a normal concurrent operation surfaces a 500.

## References

- `references/incidents.md` — the two judgment-day findings that created this skill.
