---
name: update-404-first
description: "Trigger: new or modified update/delete endpoint (PUT, PATCH, DELETE) or Actualizar*/Eliminar* service method that validates a body or pre-checks uniqueness. A missing, soft-deleted or other-tenant id answers 404 before any payload validation or duplicate pre-check — never 400/409 for a row that does not exist."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when writing or changing an update/delete service method (`ActualizarAsync`,
`EliminarAsync`, …) or the endpoint that calls it. This skill comes from three
judgment-day findings with the same shape:

- `ServicioDeOfertas.ActualizarAsync`
- `ServicioDeArticulos.ActualizarAsync`
- `ServicioDeFamilias.ActualizarAsync` (`PUT /api/familias/{id}`)

Each one validated the body or ran a name pre-check before checking that the row
exists. So an unknown id answered 400 or 409 instead of 404, and no test pinned the
precedence.

## Hard Rules

1. **404 is the first observable outcome for a row that does not exist.** That covers
   missing, soft-deleted and other-tenant ids. The 404 comes before body
   normalization, required-field checks and duplicate pre-checks.

2. **Use the repo idiom: an `AnyAsync` existence check as the FIRST statement.** Do not
   use a tracked read here. A tracked entity enters the identity map, and the read under
   the lock would then resolve against that stale instance (see `single-read-under-lock`).
   - The authoritative read stays inside the transaction, after the lock.
   - That read still answers 404 if the row vanished in between.

3. **Pin the precedence with tests.** Cover each of these cases against an unknown id:
   - an invalid body → 404;
   - a body whose name collides with another live row → 404;
   - the same two cases for a soft-deleted id and for an other-tenant id.

   A 404 test that sends a valid, non-colliding body proves nothing about precedence.

## Decision Gate

| Situation | Action |
|---|---|
| New `ActualizarAsync` or PUT | Order: existence check, payload validation, pre-checks, then the transaction with the locked read |
| Existing method validates before existence | Move the `AnyAsync` to the first statement and add the precedence tests |
| Reviewing a PUT or DELETE | Send an invalid body to an unknown id: any answer other than 404 is a finding |

## Verification

- Run `rg -n "Task<.*> ActualizarAsync|Task ActualizarAsync" src`. In each match, the first statement must be the existence check.
- Mutation: move the existence check below the validation. The precedence tests must go red.
