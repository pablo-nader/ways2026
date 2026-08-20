using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Línea de un <see cref="Remito"/> (proposal: Modelo de datos propuesto — §F). Child scope:
/// <c>id_tenant</c> únicamente, sin FK propia a <c>puntos_venta</c> — se deriva del remito
/// padre, mismo criterio que <see cref="ItemPresupuesto"/>/<c>ItemComprobanteVentaConfiguration.cs:13-15</c>.
/// <c>EntidadBase</c>: SÍ, mismo razonamiento que <see cref="ItemPresupuesto"/>.
///
/// A diferencia de <see cref="ItemPresupuesto"/>, esta línea SÍ congela costo y lote (proposal
/// §F): la mercadería efectivamente sale por este write site (slice 5), así que
/// <see cref="CostoUnitario"/>/<see cref="CostoEsEstimado"/> y <see cref="IdLote"/> viajan
/// desde acá — un costo es irrecuperable una vez que la mercadería salió (el argumento de la
/// etapa 9, aplicado), y el FEFO resuelto y congelado es el mismo criterio que la línea de
/// venta.
/// </summary>
public class ItemRemito : EntidadTenant
{
    public int Id { get; set; }

    public int IdRemito { get; set; }

    /// <summary>Asignado por el servidor en cada replace-set (mismo criterio que
    /// <c>ItemPresupuesto.Orden</c>/<c>ItemComprobanteVenta.Orden</c>) — nunca input de
    /// cliente.</summary>
    public int Orden { get; set; }

    /// <summary><c>NOT NULL</c> (proposal §F): un remito entrega mercadería, nunca un
    /// servicio.</summary>
    public int IdArticulo { get; set; }

    public required string Descripcion { get; set; }

    /// <summary><c>numeric(12,3)</c> (proposal §F, doc-10 principio 5) — <c>&gt; 0</c>
    /// (<c>ck_items_remito_cantidad_positiva</c>).</summary>
    public decimal Cantidad { get; set; }

    public decimal PrecioUnitario { get; set; }

    public decimal Descuento { get; set; }

    public decimal Total { get; set; }

    public int IdListaPrecio { get; set; }

    public int? IdOferta { get; set; }

    public int IdAlicuotaIva { get; set; }

    public decimal PorcentajeIva { get; set; }

    /// <summary>Congelado al SALIR la mercadería (etapa 9, aplicado — proposal §F): <c>NULL</c>
    /// hasta que <c>emitir</c> (slice 5) lo escribe, nunca re-derivado después.
    /// <c>ck_items_remito_costo_no_negativo</c> lo respalda a nivel esquema.</summary>
    public decimal? CostoUnitario { get; set; }

    /// <summary><c>ck_items_remito_estimado_con_costo</c>: una marca "estimado" sin costo es
    /// irrepresentable, mismo criterio que <c>ItemComprobanteVenta.CostoEsEstimado</c>.</summary>
    public bool CostoEsEstimado { get; set; }

    /// <summary>FEFO resuelto y congelado al <c>emitir</c> (proposal §F) — <c>NULL</c> para un
    /// artículo que no controla lote, poblado para uno lot-effective (invariante cruzado entre
    /// tablas, probado con un test de integración dedicado, mismo criterio que
    /// <c>MovimientoStock.IdLote</c>).</summary>
    public int? IdLote { get; set; }
}
