---
name: mutation-proof-tests
description: "Trigger: writing a test whose PURPOSE is to prove one specific clause — an RLS/tenant predicate, a WHERE filter, a timezone/bucketing expression, a validation guard, a fallback branch. The test ships only with recorded mutation evidence: mutate the clause, watch the test fail, revert."
license: Apache-2.0
metadata:
  author: ways-project
  version: "1.0"
---

## Activation Contract

Load when a test exists to prove that a SPECIFIC line or clause does its job — not
general behavior coverage. Typical markers: a tenant/RLS predicate (`id_tenant = $n`),
a soft-delete or estado filter, a timezone/`date_trunc` expression, a platform-mode
`SET LOCAL`, a hardening guard (null check, `HasIanaId`), an error-classifier arm.

Born from three same-class incidents in one program:
1. Stage 9: the naive multi-tenant backfill test passed with `SET LOCAL app.acceso`
   deleted (fixture migrates as the testcontainer superuser — RLS never applied).
2. Stage 10 slice 2: the cross-tenant test passed with `AND cv.id_tenant = $2`
   deleted (real RLS + an empresa-scoped PV list masked the predicate).
3. Stage 10 slice 2: the timezone test passed with `timezone($1, ...)` deleted
   (single-day range → the C# WHERE boundary alone determined the result).

The defect class: **the asserted outcome is overdetermined** — other layers
(RLS, scoping, C# pre-filters, closed UI paths) produce the same observable result,
so deleting the clause under test changes nothing the test can see.

## Hard Rules

1. **Name the clause.** Before writing the test, state (in the test name or a one-line
   comment) WHICH exact clause it exists to prove. If you cannot name one, this skill
   does not apply — it is ordinary behavior coverage.

2. **Run the mutation, don't reason it.** Temporarily delete/neuter the named clause,
   run the test, confirm it FAILS, revert, confirm it passes. "It would obviously fail"
   has been wrong three times in this repo. Record the evidence in the PR body or the
   review notes (mutation applied → exact failing test → reverted → green).

3. **Kill the confounds, not the layers.** If the test passes under mutation, do not
   weaken the other layers (never disable RLS, never bypass scoping) — instead route
   the test BELOW the confound: call the component directly with hand-built inputs the
   upper layers would never produce (e.g. a PV list containing another tenant's id),
   or widen the data so the clause alone decides the outcome (multi-day range so the
   bucket LABEL, not row admission, carries the assertion).

4. **Assert the discriminating value.** Prefer asserting the value only the clause can
   produce (the bucket label, the affected-row count, the SQLSTATE) over asserting
   absence/presence that a confound can also explain.

5. **Superuser fixtures lie.** Any test that "proves" an RLS-dependent behavior while
   running on `ways_owner` (testcontainer superuser) proves nothing — use the
   `ways_app` connection (NOSUPERUSER NOBYPASSRLS) at statement level, per the
   stage-9 precedent.

## Decision Gate

| Situation | Action |
|---|---|
| Test exists to prove a predicate/filter/guard/expression | Mutation evidence recorded before commit |
| Test passes with the clause deleted | Re-route below the confound (rule 3) — never call it done |
| RLS-related assertion | ways_app connection, statement-level, row counts |
| Cannot name the clause under test | Ordinary coverage — this skill does not apply |
