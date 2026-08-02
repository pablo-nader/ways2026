# Incidents that created this skill

Both found by judgment-day dual review, same root pattern, two slices apart.

## Slice 2 (2026-08-01) — usuarios

- CRITICAL: the global `mail` uniqueness pre-check ran on the tenant-filtered context, so a cross-tenant collision skipped the 409 path and surfaced the raw 23505 as a 500 — which also created a cross-tenant enumeration oracle (409 vs 500 distinguishable).
- Follow-up in the same slice: `ux_usuarios_usuario` initially had no backstop while `ux_usuarios_mail` did (inconsistent hardening); a later round also proved the pre-check could not see cross-tenant rows at all under RLS (`IgnoreQueryFilters` does not bypass DB-level policies) and had to move to the platform-keyed context.

## Slice 3 (2026-08-02) — catalog machine

- WARNING (confirmed by both judges): none of the ~10 new unique indexes (`ux_{tabla}_nombre_*`, fiscal catalog codes, `ux_parametros_*`) nor the new composite FKs (`fk_*_empresa`, `fk_categorias_padre`, `fk_parametros_punto_venta`) had 23505/23503 translations — every normal create/create race or invalid client-supplied reference surfaced as a generic 500.

## Lesson

The backstop is not hardening; it is part of the definition of done for any constraint. Judge A stated it explicitly: "this is exactly the class of recurring mistake CLAUDE.md's skills-loop rule asks to be captured."
