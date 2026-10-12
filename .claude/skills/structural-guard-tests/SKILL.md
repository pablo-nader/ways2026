---
name: structural-guard-tests
description: "Trigger: new tenant table or FK to tenants/empresas/familias, new RemoveRange or physical delete, new non-GET endpoint, new write path with an execution strategy, new writer of family-shared fields, or delegating any of those to a writer agent. The repo has guard tests that enumerate the whole codebase; a filtered test run misses them, so they are named in the task and run before the work is called done."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when a change adds one of the shapes below, and when writing the brief for an agent
that will. This skill comes from the combos feature (2026-10-11), where the same miss
happened three times in three slices:

- T1 added two tenant tables: `InventarioDeDependientesTests` failed on its golden file,
  which was outside the writer's edit surfaces.
- T2 added a replace-set with `RemoveRange`: `BajasEstructuralesTests` failed on its
  allow-list, also outside the surfaces.
- T3b added `POST /api/ofertas/resolver-con-combos`: `SuperficieDeAutorizacionTests` would
  have failed in CI. The writer's filtered integration run did not include it and reported
  green; a judge caught it by reading.

Each guard walks the whole codebase or the real endpoint table, so it fails for code that
its own author never ran it against.

## Hard Rules

1. **Know which guard a change wakes up.**

   | Change | Guard | What to update |
   |---|---|---|
   | New table with `id_tenant`, or a new FK to `tenants`, `empresas`, `familias` or another inventory anchor | `tests/Ways.Application.Tests/Persistencia/InventarioDeDependientesTests.cs` | The golden `Fixtures/inventario-de-dependientes.txt`, in sorted position |
   | New table with `id_tenant` | `tests/Ways.IntegrationTests/AislamientoDeTenantTests.cs` | Nothing: it fails if the migration forgot `HabilitarRlsDeTenant` |
   | `RemoveRange`, `Remove`, `ExecuteDelete` or `DELETE FROM` in `src/` | `tests/Ways.Application.Tests/Organizacion/BajasEstructuralesTests.cs` | `RemoveRangePermitidos`, only for a replace-set of detail rows |
   | New non-GET endpoint that is not catalog management | `tests/Ways.IntegrationTests/SuperficieDeAutorizacionTests.cs` | Its `Allowlist`, plus a positive test driving the route with the role that needs it |
   | New `CreateExecutionStrategy` / write transaction | `tests/Ways.Application.Tests/Abstracciones/EscriturasSinReintentoEstructuralesTests.cs` | See `ef-retry-safe-writes` |
   | New writer of family-shared fields or prices | `tests/Ways.Application.Tests/Familias/EscritoresDeFamiliasEstructuralesTests.cs` | The writer follows the lock protocol |

2. **A filtered run is not the verification of record for these shapes.** After the change,
   run the whole `tests/Ways.Application.Tests` project and, for a new endpoint, the
   integration filter `FullyQualifiedName~SuperficieDeAutorizacion`. For a new table also
   run `FullyQualifiedName~AislamientoDeTenant`.

3. **When delegating, the brief carries the guard.** Put the guard's file (and its golden or
   allow-list) in the writer's allowed edit surfaces and its command in `## Verification`.
   A writer that cannot edit the file can only report it, which costs a round trip.

4. **An allow-list entry is a decision, not a formality.** Add one only when the route or
   the delete really belongs to the exempt category, and say why in the same style as the
   neighbouring entries. An endpoint that changes catalog data stacks `GestionDeCatalogo`
   instead.

## Decision Gates

| Situation | Action |
|---|---|
| The guard fails and the change is of the exempt kind | Update the golden or allow-list in the same commit |
| The guard fails and the change is not of the exempt kind | Fix the code, not the list |
| Unsure which guards exist | `rg -l "EstructuralesTests|InventarioDeDependientes|SuperficieDeAutorizacion|AislamientoDeTenant" tests` |

## Output Contract

Name each guard the change touched, the list or golden line added and the reason, and the
observed result of the commands in rule 2.
