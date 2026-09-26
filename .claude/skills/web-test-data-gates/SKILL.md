---
name: web-test-data-gates
description: "Trigger: Ways.Web component test that awaits findBy*/getBy* on a control fed by a fetch, selectOptions, toHaveValue on a select, asserting on mock.calls after a render, intermittent \"Value X not found in options\", flaky Vitest suite, a test that passes alone but fails in the full run. Wait for the DATA to arrive, never for the element that renders before it."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when writing or reviewing a `src/Ways.Web` component test that drives a screen whose
controls are populated by an async fetch — every `Remito`/`Presupuesto`/`OrdenDeCompra`-style
editor, every filter bar backed by `/puntos-venta`, `/proveedores`, `/clientes`, and any test
that asserts on `apiGetMock.mock.calls` after rendering. Born from the 2026-09-26 flakiness
hunt: five sites across `Remito.test.tsx`, `Presupuesto.test.tsx`, `OrdenDeCompra.test.tsx`,
`Reposicion.test.tsx` and `Vencimientos.test.tsx` failed at random under CPU load, all falling
into one of two variants of the same root class: the test waits for something that is not the
data it then uses.

## The defect

Two distinct variants share that one root class.

**Variant 1 — element rendered before its data, gated by a disabled flag.**
`Remito.tsx`, `Presupuesto.tsx` and `OrdenDeCompra.tsx` render the control **before** its data
exists — an empty, disabled `<select>` holding only `Elegir…`, with
`disabled={... || !referenciaOk}`. So `findByLabelText('Proveedor')` resolves on the FIRST
render and proves nothing about the options. Whichever microtask wins the race decides whether
the next line works:

```tsx
// ✗ pasa en aislamiento, falla bajo carga con "Value 4 not found in options"
await userEvent.selectOptions(await screen.findByLabelText('Proveedor'), '4')

// ✓ espera al dato, no al elemento — `referenciaOk` es lo que habilita el select
const proveedor = await screen.findByLabelText('Proveedor')
await waitFor(() => expect(proveedor).toBeEnabled())
await userEvent.selectOptions(proveedor, '4')
```

**Variant 2 — asserting on `mock.calls` before the chained effect fires.**
`Reposicion.tsx` and `Vencimientos.tsx` render the `<select>` only after `puntosVenta` loads (no
`referenciaOk` flag on this control), so `findByLabelText` alone is a correct gate for the select
itself. Their real defect was elsewhere: reading `apiGetMock.mock.calls` in the same tick,
before the effect chained to the *previous* response — the one that fires the report request —
had actually run:

```tsx
// ✗ lee la lista de llamadas en el mismo tick: el efecto encadenado todavía no corrió
await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
expect(apiGetMock.mock.calls.some((c) => (c[0] as string).includes('idPuntoVenta=11'))).toBe(true)

// ✓ la llamada encadenada se espera, no se lee de inmediato
await waitFor(() => {
  const llamadas = apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith('/reportes/stock/vencimientos?'))
  expect(llamadas.some((c) => (c[0] as string).includes('idPuntoVenta=11'))).toBe(true)
})
```

A test that only fails under load is not "flaky infrastructure": it is a missing wait. The full
suite is this project's only merge gate (there is no CI workflow running tests on PRs), so a
random red per run is a real hole, not noise.

## Hard Rules

- NEVER act on a control's data right after awaiting only the control's existence. Await a
  second signal that proves the data landed: `toBeEnabled()`, the expected `toHaveValue(...)`,
  or `findByRole('option', { name })`.
- One `waitFor(() => expect(control).toBeEnabled())` covers every control gated by the same
  `referenciaOk`-style flag — gate once, then interact freely. Only holds when the sibling
  controls' `disabled` expressions are provably identical; if they differ, gate each one.
- A `<select>` whose option is not loaded yet reports value `''`, so `toHaveValue('4')` on a
  precargado select is ALSO a race: wrap it in `waitFor`, never a bare `expect`.
- Assertions on `apiGetMock.mock.calls` for a request fired by an effect that reacts to a
  previous response go inside `waitFor` — the call list is empty in the same tick.
- Preferred when authoring the COMPONENT: do not render the control until its data exists
  (`condiciones === null ? <Cargando/> : <select>…`, as in `AltaRapidaProveedor.tsx`). Then
  `findByLabelText` alone is a correct gate and no second wait is needed.
- NEVER "fix" one of these by raising `testTimeout`, adding a retry, or marking the test flaky.

## Decision Gates

| Situation | Action |
|---|---|
| Control is rendered always, disabled while loading | `await waitFor(() => expect(control).toBeEnabled())` before interacting |
| Control is rendered only after its data (`x === null ? … : <select>`) | `findBy*` alone is enough — no extra wait |
| Reading a `<select>` value precargado desde `location.state`, alone | `await waitFor(() => expect(select).toHaveValue(v))` |
| Reading that same value when the test then reads a sibling select fed by an independent fetch | `await waitFor(() => expect(select).toBeEnabled())` — a bare `toHaveValue` only proves its own select's data landed, not the sibling's |
| Asserting a request triggered by an effect chained to another response | Put the `mock.calls` lookup inside `waitFor` |
| Element only exists when the data is non-empty (`resultados.length > 0 && <listbox>`) | `findByRole` alone is enough |

## Execution Steps

1. For every `userEvent` call in the test, ask: does this line depend on data that arrived
   after the element? If yes, add the gate above it.
2. Prove the gate has teeth: delay or withhold the fetch fixture and confirm the test fails
   without the gate (`mutation-proof-tests` discipline).
3. Reproduce flakiness on purpose instead of re-running until green: two concurrent
   `npx vitest run` in `src/Ways.Web` saturate the box and surface these races reliably.

## Output Contract

No test in the diff awaits an element as a proxy for its data. Two concurrent full-suite runs
come back green.

## References

- `src/Ways.Web/src/paginas/OrdenDeCompra.test.tsx` — reference gate for two selects sharing one
  `referenciaOk`.
- `src/Ways.Web/src/paginas/Reposicion.test.tsx` — reference for the `mock.calls`-inside-`waitFor`
  variant.
- `src/Ways.Web/src/paginas/articulos/AltaRapidaProveedor.tsx` — the component shape that makes
  the extra wait unnecessary.
