# Artículos por importe

## Objective

An article can be sold "by amount": the cashier types the amount instead of a quantity. Generic
departments ("Verdulería", "Panadería", "Fiambrería") are loaded as one article each and rung up
as 1 unit at the typed amount.

## Problem

`unidad_venta` only knows `Unidad` and `Peso`, and every sale line takes its price from the price
list. A shop that does not itemize a department has no way to charge "150 of fiambrería" short of
creating a 1-peso article and typing 150 units.

## Scope (requested by the owner, 2026-10-12)

- New `unidad_venta` value `Importe`.
- POS entry: `150*500` on a by-amount article with code 500 → quantity 1, unit price 150.
- By-amount articles never accumulate: `acumula_en_venta` is forced to false, and the checkbox is
  disabled and unchecked in the article form.
- A by-amount article can be assigned one POS shortcut key among F5, F6, F7, F8 (at most one
  article per key). Articles beyond those four are typed by code.

Out of scope: purchases and stock rules, the online store (by-amount articles are not sellable
there), reports. No rewrite of existing rows.

## Decisions (agent-made, recorded so the owner can overrule)

- D1 — The amount travels in its own line field (`importe`), accepted only for `Importe` articles
  and required for them, on the online sale, the offline sale and the price preview. The server
  still ignores client prices for every other article.
- D2 — A by-amount line has quantity exactly 1, takes no price-list price, no offer and no combo.
  A by-amount article needs no price in any list: `articulo_sin_precio_vigente` does not apply to
  it and the POS snapshot emits it without a price.
- D3 — The shortcut is a column on the article (`tecla_atajo`, values F5..F8, NULL = none), only
  valid on `Importe` articles, unique per tenant among live rows. It is an own field, not a
  family-shared one.
- D4 — Pressing the shortcut key uses the amount typed in the scan box (`150` then F5). An empty
  or invalid box shows a notice and adds nothing. Typing the bare code of a by-amount article
  (no `amount*`) also shows a notice.
- D5 — Requests are rejected, not coerced: `Importe` with `acumulaEnVenta = true`, a shortcut on a
  non-`Importe` article, or a by-amount article as a combo component answer 400. The combo guard
  only adds the `Importe` rejection: `odd/tasks/combos-pesables.md` (parallel branch) is making
  `Peso` a valid component and rewrites the same guards.
- D6 — The manual adjustment % still applies on a by-amount line. Quantity is not editable in the
  cart; the amount is corrected by removing the line and typing it again.

## Model summary

```
ALTER TYPE unidad_venta ADD VALUE 'importe';
articulos.tecla_atajo text NULL
  CHECK ck_articulos_tecla_atajo: tecla_atajo IN ('F5','F6','F7','F8')
  CHECK ck_articulos_tecla_atajo_solo_importe: tecla_atajo IS NULL OR unidad_venta = 'importe'
  CHECK ck_articulos_importe_no_acumula: unidad_venta <> 'importe' OR acumula_en_venta = false
  UNIQUE (id_tenant, tecla_atajo) WHERE deleted_at IS NULL AND tecla_atajo IS NOT NULL
```

The enum value is added in its own migration: Postgres forbids using a new enum value in the
transaction that adds it, and the CHECKs reference it.

## Tasks

- [ ] T1 — Server, catalog: enum value + migrations, `tecla_atajo`, the three invariants in the
  article and family writers (D5), `ReglaDeCantidadDeVenta` (`Importe` → exactly 1), combos reject
  by-amount components, contracts (article list/create/edit, scan, POS snapshot) expose
  `teclaAtajo`, 23505 backstop for the unique index, docs/10. Tests first.
  Route: delegated writer (2+ non-trivial files).
- [ ] T2 — Server, sale: `importe` on the sale line and on the preview line (D1, D2), scan
  resolution turns `N*code` into quantity 1 + amount N for a by-amount article, snapshot emits
  unpriced by-amount articles, offline sale accepts them. Tests first.
  Route: delegated writer.
- [ ] T3 — Web, back office: "Por importe" option, disabled/unchecked accumulate checkbox,
  shortcut selector, descriptor tests; the two-unit assumptions in `cantidadPorUnidad`, stock and
  the online cart. Route: delegated writer.
- [ ] T4 — Web, POS: cart line with amount, scan and offline mirror, F5–F8 handlers, preview,
  queued payload and synthetic ticket. Route: delegated writer.
- [ ] T5 — POS release (Ways.Desktop bump + tag), since POS web changes do not reach the tills
  without it.

Each PR slice passes judgment-day before it is opened.

## Acceptance criteria

- Article 500 "Fiambrería" by amount: `150*500` → one line, quantity 1, total 150. Typing it twice
  gives two lines.
- The same sale can mix by-amount lines with list-priced lines, online and offline.
- Saving a by-amount article with accumulate on, or a shortcut on a by-unit article, is rejected.
- Two live articles of a tenant cannot hold the same shortcut key (409, also under a race).
- F5–F8 never reload the POS page.

## Checks

- `dotnet test` for Domain.Tests and Application.Tests; integration tests for articles, families,
  scan, sales and the structural guards (run unfiltered at slice close).
- `npm test`, `tsc` and lint in `src/Ways.Web`.

## Delivery

Strategy: `auto-chain`, `stacked-to-main` (owner's standing preference). Forecast: well above 400
authored lines; one PR per task T1–T4, T5 as the release PR.

## Progress

- T1 — implemented in `0318a9f9` (delegated writer; trigger: 2+ non-trivial files). Not checked
  off yet: judgment-day in progress, and the unfiltered integration run of record predates the
  final migration fix.
  - Observed: Domain.Tests 1363/1363 (re-run by the parent), Application.Tests 859/859,
    IntegrationTests unfiltered 3338 passed / 1 failed (`FiscalSchemaTests` Up-Down-Up, caused by
    the first enum migration and fixed afterwards), then 68/68 on
    `FiscalSchemaTests|ArticulosPorImporte` after the fix. Full suite after the fix: pending (CI).
  - No RED-first evidence: the writer wrote rules before tests; mutation evidence recorded instead
    (10 mutants killed; the service pre-checks survive by design because the DB backstop answers
    the same status and code).
  - Review tier: `gentle-ai review assess` → high risk; RDD is off for this clone, so the
    independent check is judgment-day.
  - Writer choices beyond the brief: `AltaArticulo.AcumulaEnVenta` is now `bool?` (omitted →
    true, or false for `Importe`); on edit a null `teclaAtajo` clears the shortcut; `Importe`
    admits quantity 1 or −1; grouping an article that holds a shortcut into a family answers 409
    `articulo_con_tecla_atajo`.
