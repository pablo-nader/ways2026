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
parametros (                  -- [operativa a nivel sucursal o punto de venta, con fallback a empresa]
    id_parametro, id_empresa, id_punto_venta NULL, id_sucursal NULL,
    --   ambos NULL = default de la empresa; CHECK ck_parametros_un_solo_alcance: a lo sumo uno de los dos
    --   FK compuestas: (id_punto_venta, id_tenant) → puntos_venta y (id_sucursal, id_empresa, id_tenant) → sucursales
    clave citext, valor jsonb
);
-- Únicos parciales (filas vivas): ux_parametros_empresa (id_punto_venta e id_sucursal NULL),
--   ux_parametros_punto_venta y ux_parametros_sucursal; los tres traducen su 23505 a 409 `parametro_duplicado`.
-- Cada clave pertenece a UN alcance (`ParametroConocido.Alcance`):
--   sucursal: zona_horaria, lotes_habilitado, dias_alerta_vencimiento, dias_rotacion, dias_cobertura_objetivo
--     resuelven sucursal > empresa > default; las filas por punto de venta NO cuentan, y una consulta por
--     punto de venta usa la sucursal de esa caja (las cajas de un local comparten zona, lotes y días);
--   caja: el resto (tolerancia_pago, importe_envio…) resuelve punto de venta > empresa > default.
-- PUT: una clave de sucursal con idPuntoVenta es 400 `parametro_de_sucursal`, una de caja con idSucursal es 400
--   `parametro_de_caja`, los dos ids son 400 `parametro_alcance_ambiguo` y una sucursal de otra empresa es 400
--   `sucursal_no_pertenece_a_la_empresa`.
-- La fusión de dos sucursales deja al destino con sus filas y le pasa las del origen donde no tiene la clave.
-- tolerancia_pago, importe_adicional_recarga, slots_tickets_espera…
-- importe_envio: importe de la línea "Envío" de una venta con envío (decimal, default 0; de 0 a
--   999999999999.99 con hasta 2 decimales, 400 `parametro_importe_envio_invalido`)
-- redondeo_sugerencia_precio: múltiplo al que se redondea el precio sugerido (decimal, default 100)
-- tolerancia_redondeo_pago_proveedor: diferencia máxima que se perdona/absorbe al cerrar en cero una compra o un pago de
--   proveedor (decimal, default 500, de 0 a 999999999999.99 con hasta 2 decimales, 400 `parametro_tolerancia_redondeo_invalida`;
--   se resuelve con el punto de venta de la compra; no es la `tolerancia_pago` de ventas; ver §5, E4)
```

> **`vuelto_maximo` eliminado (decisión del dueño, 2026-09-16):** dejó de ser consumido por
> cualquier validador (reemplazado por la regla de billetes formables, PRs #205/#211) y se
> eliminó del registro `ParametroConocido` y de cualquier fila remanente en `parametros`
> (migración de datos `QuitarVueltoMaximo`, sin cambio de esquema).

---
