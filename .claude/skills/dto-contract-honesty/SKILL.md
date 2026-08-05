---
name: dto-contract-honesty
description: "Trigger: new or modified request DTO, contract record, SolicitudDe* type, API endpoint accepting a body. Every accepted field has exactly one fate — used/persisted or rejected — never accepted-and-dropped."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when adding or modifying any request DTO / contract record consumed by an API
endpoint (`SolicitudDe*`, `Contratos.cs`, web `tipos.ts` mirrors). Born from two
identical judgment-day findings in consecutive stages: `SolicitudDeCierre.Observaciones`
(stage 6) and `SolicitudDePagoACuenta.Observaciones` (stage 7) — both accepted by the
contract, both silently discarded by the service, both shipped unnoticed because no
test exercised the field.

## Hard Rules

1. **Every field a DTO accepts has exactly one fate.** It is either read and
   used/persisted by the service, or it does not exist on the contract. A field the
   handler never reads is silent data loss wearing an API's clothes.

2. **The field's fate ships with a test in the same PR.** For persisted fields: a
   round-trip assertion (send it, read it back). For derived/ignored-by-design cases:
   the field must not exist — delete it rather than documenting that it does nothing.

3. **Adding a field to a record signature is not implementing it.** Threading a value
   through to `EjecutarX` and persisting it are the implementation; the signature is
   just the promise. Grep the service for every DTO property name before calling a
   contract done.

## Decision Gate

| Situation | Action |
|---|---|
| New request DTO field | Trace it to its use/persistence point; add the round-trip test in the same commit |
| Design pins a record signature with a field | The apply task includes wiring it, not just declaring it |
| Reviewing a contract | `rg` each property name in the consuming service — zero hits on any = finding |

## Verification

Before committing a DTO change: for each property, name the line where the service
reads it. Any property without one is either dead (delete it) or unimplemented
(implement it now).
