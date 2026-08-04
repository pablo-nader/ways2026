using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Un pago de un <see cref="ComprobanteVenta"/> — una fila por medio (doc 10 §4, design: Table
/// Shapes — write path A). Child scope: <c>id_tenant</c> únicamente, sin <c>id_punto_venta</c>
/// propio, mismo criterio que <see cref="ItemComprobanteVenta"/>. Con auditoría completa
/// (<see cref="EntidadTenant"/>).
///
/// Su clave alterna <c>(Id, IdTenant)</c> es la que <see cref="CuentaCorriente.MovimientoCuentaCorriente"/>
/// referencia vía <c>id_pago_comprobante</c> (design: Table Shapes — write path A, "referenced
/// by the CC movimiento") cuando el medio de este pago es cuenta corriente.
/// </summary>
public class PagoComprobante : EntidadTenant
{
    public int Id { get; set; }

    public int IdComprobanteVenta { get; set; }

    public int IdMedioPago { get; set; }

    public decimal Importe { get; set; }

    /// <summary>Cupón/nro de operación — obligatorio cuando <c>medios_pago.requiere_referencia
    /// = true</c> (<c>ValidadorDePagos</c>, rechazo <c>referencia_de_pago_requerida</c>).</summary>
    public string? Referencia { get; set; }

    /// <summary>Solo medios que admiten vuelto (<c>medios_pago.admite_vuelto</c>) — validado
    /// server-side, nunca confiado del cliente tal cual (design decisión 3).</summary>
    public decimal Vuelto { get; set; }
}
