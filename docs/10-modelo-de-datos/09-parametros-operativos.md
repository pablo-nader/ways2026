> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 9. Parámetros operativos

## 9. Parámetros operativos

> **Estado (Etapa 1):** tabla, RLS y API (`GET`/`PUT`, resolución punto de venta > empresa >
> default) implementadas — ver ADR-13 de `design.md`. El editor de `Ways.Web` pide el id de
> empresa a mano en lugar de un selector: `ITenantActual` todavía no carga una "empresa
> actual" en la sesión (ADR-10, deferred) y no existe un endpoint para listar las empresas del
> tenant — en esta etapa cada tenant tiene una sola empresa (la que crea el aprovisionamiento),
> así que sigue siendo una UX razonable hasta que la selección de empresa/punto de venta
> llegue con las etapas operativas.

Los números mágicos del legacy ($10 de tolerancia, $20 de vuelto máximo, $5 por
operación de recarga) se vuelven configuración:

```sql
parametros (                  -- [operativa a nivel punto de venta, con fallback a empresa]
    id_parametro, id_punto_venta NULL,       -- NULL = default de la empresa
    clave citext, valor jsonb
);
-- tolerancia_pago, importe_adicional_recarga, slots_tickets_espera…
-- importe_envio: importe de la línea "Envío" de una venta con envío (decimal, default 0)
-- redondeo_sugerencia_precio: múltiplo al que se redondea el precio sugerido (decimal, default 100)
```

> **`vuelto_maximo` eliminado (decisión del dueño, 2026-09-16):** dejó de ser consumido por
> cualquier validador (reemplazado por la regla de billetes formables, PRs #205/#211) y se
> eliminó del registro `ParametroConocido` y de cualquier fila remanente en `parametros`
> (migración de datos `QuitarVueltoMaximo`, sin cambio de esquema).

---
