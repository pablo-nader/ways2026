using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Línea de una <see cref="OrdenCompra"/> (proposal: Modelo de datos propuesto — §C). Child scope:
/// <c>id_tenant</c> únicamente, sin FK propia a <c>puntos_venta</c> — se deriva de la orden padre,
/// criterio verbatim de <c>ItemComprobanteCompraConfiguration.cs:12-14</c>. <c>EntidadBase</c>: SÍ,
/// como <see cref="ItemComprobanteCompra"/> — el replace-set del borrador las reescribe físicamente.
///
/// Deliberadamente SIN <c>CantidadRecibida</c> (proposal decisión 2/§F): la cantidad recibida
/// siempre se deriva del libro de recepción (<c>items_comprobante_compra</c> vía comprobantes
/// ligados confirmados), nunca se cachea acá.
/// </summary>
public class ItemOrdenCompra : EntidadTenant
{
    public int Id { get; set; }

    public int IdOrdenCompra { get; set; }

    /// <summary>Asignado por el servidor en cada replace-set (mismo criterio que
    /// <c>ItemComprobanteCompra.Orden</c>, slice 2) — nunca input de cliente.</summary>
    public int Orden { get; set; }

    /// <summary><c>NOT NULL</c>: una línea sin artículo no puede recibirse contra stock — mismo
    /// criterio que <c>ItemComprobanteCompra.IdArticulo</c> (proposal §C).</summary>
    public int IdArticulo { get; set; }

    /// <summary>Snapshot al momento del guardado del borrador — el proveedor lee un nombre.</summary>
    public required string Descripcion { get; set; }

    /// <summary><c>numeric(12,3)</c> (proposal §C, doc-10 principio 5) — <c>&gt; 0</c>
    /// (<c>ck_items_orden_compra_cantidad_positiva</c>).</summary>
    public decimal CantidadPedida { get; set; }

    /// <summary><c>numeric(14,4)</c>, NUNCA un hecho — intención de precio, nunca lo que
    /// finalmente factura el proveedor (proposal §C). <c>NULL</c> = no cotizado.
    /// <c>&gt;= 0</c> cuando presente (<c>ck_items_orden_compra_costo_no_negativo</c>): una línea
    /// de bonificación con costo estimado cero es real, mismo criterio que
    /// <c>ItemComprobanteCompra.CostoUnitario</c>.</summary>
    public decimal? CostoUnitarioEstimado { get; set; }
}
