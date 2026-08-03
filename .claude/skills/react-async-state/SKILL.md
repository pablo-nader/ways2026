---
name: react-async-state
description: "Trigger: React screen with async fetches/saves, form editing, useState + await, ABM screens in Ways.Web, stale response, double submit, loading flags. Every async response that mutates screen state ships with token/generation gating and a full-window disabled state."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when writing or modifying any `src/Ways.Web` screen (or component) that combines
React state with async operations: fetch-then-set flows, form save flows, per-row
editors, cached sub-resource loads. Born from stage-3 Slice 5 (`Articulos.tsx`),
where five judgment-day rounds each found a new variant of the same defect class.

## Hard Rules

1. **Functional updaters build from `prev`, never from closure state.**
   `setX((prev) => ({ ...prev[k], ...patch }))` — NEVER `{ ...leerDeEstado(k), ...patch }`.
   A helper that reads component state inside an updater reintroduces the stale
   snapshot the updater exists to avoid (multiple dispatches in one async invocation
   silently clobber each other; `finally` runs even after a `catch`'s `return`).

2. **Every async response that mutates screen state is token-gated.**
   Keep a `useRef` token (or generation counter). The fetch captures the token at
   start; every state application after every `await` checks
   `ref.current === token` first. This applies to READS (edit-fetch) **and to the
   WRITER'S OWN response** (`guardar` must capture and check its own token too).

3. **Every action that supersedes an in-flight operation invalidates the token.**
   Open-for-edit, new-blank-form, cancel, save, delete — all of them bump the token
   BEFORE changing what is on screen. Document the invalidation contract on the ref,
   not just one pairwise case.

4. **`finally` blocks that reset shared flags are token-gated too.**
   An unconditional `finally { setOcupado(false) }` from a stale operation re-enables
   the buttons of a NEWER in-flight operation → double-submit window (duplicate POST).

5. **Disabled state covers the full window, per entity.**
   From click until the post-write refresh has landed: inputs AND submit stay inert
   (`guardando` + `refrescando` per-row/per-entity flags, not one page-level boolean).
   A page-level busy flag lies as soon as two entities can be operated in sequence.
   Do not reset a superseded form's busy flag before its replacement is actually
   mounted (an async open resets it in its token-gated post-await block, not before).

6. **A committed write is never reported as a failure.**
   Isolate the post-write refresh from the write's try/catch. Refresh failure gets a
   distinct, reassuring message ("se guardó, pero no se pudo actualizar la vista"),
   and a swallowing loader must rethrow (opt-in flag) when the caller needs to
   distinguish. Shared loaders called concurrently need a generation guard so the
   last-STARTED call wins, not the last-RESOLVED.

7. **No silent catch-to-empty for required reference data.**
   `.catch(() => setCatalogo([]))` on data a required field depends on must also
   surface a visible aviso — and the aviso's copy must match real enforcement
   (if it says submission is blocked, `disabled` must actually block it).

8. **Key per-entity subtrees by entity id.**
   `key={entidad.id ?? 'nuevo'}` on the form/editor subtree so switching entities
   remounts and resets caches, drafts and suggestions. Caches keyed by a shared
   dimension (e.g. lista id) leak across entities without this.

9. **Block supersede-during-write; don't token-reconcile it.**
   While a WRITE is outstanding, disable every action that could supersede it
   (open-other-row, new, delete — not just the submit button). Trying to make
   supersede-during-save safe via token reconciliation mutated the bug across
   four consecutive review rounds (stale finally, same-row re-open, failed
   supersede leaving a resubmittable create, delete resurrection). Blocking the
   window kills the whole class; tokens remain for READ staleness only.
   Handlers also get a first-line re-entrancy guard (`if (ocupado) return`) —
   a same-tick double click beats the `disabled` attribute re-render.
   Rethrows that signal failure to a caller are generation-gated like setters.

## Decision Gates

| Situation | Action |
|---|---|
| New async fetch whose response calls a state setter | Token/generation guard before EVERY setter after EVERY await |
| New save flow | Own token captured + gated finally + full-window disabled state |
| Post-write refresh added | Separate try/catch + distinguishable message |
| Helper reads state inside a functional updater | Rewrite to use `prev` |
| Aviso copy claims a block | Wire the actual `disabled` enforcement in the same commit |

## Verification

Before committing, re-trace on paper at minimum: A-in-flight → open B; A-in-flight →
new/cancel; plain success (UI must still update!); plain failure (error must still
render!); post-write refresh failure. `tsc -b`, `oxlint`, `vite build` clean.
