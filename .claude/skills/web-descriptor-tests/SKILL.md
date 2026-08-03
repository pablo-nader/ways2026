---
name: web-descriptor-tests
description: "Trigger: Ways.Web descriptor, aAlta, aValores, opcionesDesdeListado, visibleSi, mapping helper, catalog form field, PaginaCatalogo. Every new/changed descriptor or pure mapping helper ships colocated Vitest unit tests in the same PR — smoke-only is not done."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when a Ways.Web (`src/Ways.Web`) change adds or edits: a `DescriptorDeCatalogo` (`aAlta`/`aValores`/`opcionesDesdeListado`/`visibleSi`) in `src/api/catalogos.ts` or a sibling descriptor file, any other pure mapping/formatting helper consumed by a page, or the shared field-rendering logic in `PaginaCatalogo.tsx` (or its future siblings).

## Hard Rules

- Every NEW or CHANGED `aAlta`/`aValores` pair MUST get unit tests covering: type coercion (string → number/boolean), the `''` → `null` conversion, and any conditional field forced to `null`/default under a specific mode (mirror the `descriptorListasPrecio` Fija/Derivada pattern).
- Every NEW or CHANGED `opcionesDesdeListado` MUST get unit tests covering: the active/inactive filter, any type/mode filter, self-exclusion when editing (`idActual` set) vs. inclusion when creating (`idActual` null), and the `{ valor, etiqueta }` mapping shape.
- Every NEW or CHANGED `visibleSi` predicate, or shared conditional-rendering logic in `PaginaCatalogo.tsx`, MUST get a component test (render + `@testing-library/user-event`) asserting the field appears/disappears, not just a unit test of the predicate in isolation.
- A slice that ships only a smoke test (page renders, no crash) for new descriptor/mapping logic is NOT done — judgment-day will flag it (see slice 6 round 2 finding that created this skill).
- Tests are colocated: `foo.ts` → `foo.test.ts` next to it (e.g. `src/api/catalogos.test.ts`, `src/paginas/PaginaCatalogo.test.tsx`), not in a separate `__tests__` tree.

## Decision Gates

| Situation | Action |
|---|---|
| New pure function (`aAlta`, `aValores`, formatter, mapper) | Unit test, no DOM — build minimal fixtures matching the real `tipos.ts` shape |
| New/changed `opcionesDesdeListado` filter logic | Unit test covering every filter predicate + the mapping |
| New/changed `visibleSi` or shared select fallback/orphan-option logic | Component test via `render` + `userEvent`, mock `../api/cliente` with `vi.mock` |
| Static `opciones` field vs. `opcionesDesdeListado` field | Never conflate: fallback/orphan-option lookups against `items` only apply to `opcionesDesdeListado` fields |

## Execution Steps

1. Identify every descriptor/mapping function touched by the slice.
2. Write/extend the colocated `*.test.ts`/`*.test.tsx` file before marking the slice done.
3. Run `npm run test` (or `npm run test:watch` while iterating) in `src/Ways.Web` — infra lives in `vite.config.ts` (`test` block, jsdom environment) and `src/test/setup.ts` (jest-dom matchers, RTL `cleanup`).
4. Run `npm run lint` (oxlint) and `npm run build` (`tsc -b && vite build`) — both must stay green.

## Output Contract

The PR diff shows, for each new/changed descriptor or mapping helper: a colocated test file exercising its branches (not just a happy path), and green `npm run test` / `npm run lint` / `npm run build` in the summary.

## References

- `src/Ways.Web/src/api/catalogos.test.ts` — reference pattern for `aAlta`/`aValores`/`opcionesDesdeListado`.
- `src/Ways.Web/src/paginas/PaginaCatalogo.test.tsx` — reference pattern for `visibleSi` and the scoped orphan-option fallback.
