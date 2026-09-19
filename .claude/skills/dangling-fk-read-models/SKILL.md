---
name: dangling-fk-read-models
description: "Trigger: new or modified listing, grid, report, export or read model that joins a soft-deletable catalog (áreas, categorías, marcas, grupos, proveedores, clientes, listas) or filters by \"sin <x>\" / \"id<X>\" / completeness. A FK pointing at a soft-deleted row is treated exactly like NULL — in the projection, every filter and every count."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when writing or changing any read path that resolves the NAME of a referenced row
(LEFT/INNER JOIN or navigation) or filters by the presence/absence of a FK. Born from
three identical judgment-day findings: stage 13 decision #12 (reposición: proveedor
soft-deleted mid-list), and two slices on 2026-09-19 (articles grid API: `sinProveedor`
excluded a row that displayed `proveedor: null`; articles report: `sinMarca` /
`soloIncompletos` missed rows displayed as "Sin asignar", and an INNER JOIN on a
soft-deleted área returned `total=1, items=[]`).

Root cause: every `DbSet` carries the global `BajaLogica` query filter, so a
soft-deleted row is INVISIBLE to joins and `Any()`, but the referencing article keeps
its non-null FK. Filters written against the raw FK and projections written against the
join then disagree.

## Hard Rules

1. **One definition of "effectively unassigned" per dimension**: FK is null OR the
   referenced row is not visible (`!db.X.Any(x => x.Id == a.IdX)`). Use it in the
   projection (name AND the id you return), every `sin<X>` filter, every completeness
   predicate (`soloIncompletos`), and any grouping bucket ("Sin proveedor").
2. **`id<X>` filters match only visible rows** (`a.IdX == id && db.X.Any(x => x.Id == id)`),
   so a deleted id matches nothing — consistent with rule 1.
3. **Never INNER JOIN a soft-deletable table in a listing whose total is counted before
   the join.** Use a LEFT JOIN (`into … DefaultIfEmpty()`), project null, and let rule 1
   classify the row. An INNER JOIN silently drops rows from `items` while `total` still
   counts them — and in exports it ships fewer rows than the count-first cap checked.
4. **Mandatory FKs are not exempt.** A required FK (e.g. `id_area`) can still dangle after
   a soft delete without usage guard; the row must still be returned.
5. **Test with a REAL soft delete of a real row**, never by inserting a bogus id: the
   fixture must leave the FK non-null and the target invisible. Since
   `GuardaDeReferencias` (áreas, categorías, marcas, grupos, medios de pago, listas de
   precio, proveedores), the DELETE endpoint REJECTS a referenced row with 409
   `<x>_en_uso` — so stamp `DeletedAt` directly on the existing row (that is the legacy
   data the read path must still survive). One test per dimension covering projection +
   `sin<X>` + `id<X>` + completeness.

## Decision Gate

| Situation | Action |
|---|---|
| Listing/report shows a referenced name | LEFT JOIN + rule 1 for its filters |
| "Sin X" / "solo incompletos" filter | Rule 1 predicate, never raw `IdX == null` alone |
| Count computed before a join | The join must be LEFT; assert `items.Count == total` on a small page |
| Reviewing such a read path | Soft-delete the referenced row in a test and check projection, filters and total agree |

## Note

If the referenced entity gains a usage guard on delete, keep these rules anyway:
existing data may already hold dangling FKs, and rows can still be deleted by paths
that bypass the guard.
