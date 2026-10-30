using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Aplicación de una parte de un cobro (recibo <c>RC</c> de pago a cuenta) a una venta en cuenta corriente (doc 10 §8,
/// tabla <c>aplicaciones_cobro</c>). Hija operativa de <c>comprobantes_venta</c> por los dos lados: scope <c>id_tenant</c>
/// únicamente. Un recibo puede aplicarse a varias ventas y una venta recibir varios recibos; la parte del recibo sin
/// aplicar queda a favor del cliente. Aplicar no mueve <c>clientes.saldo</c> ni escribe movimientos de cuenta corriente:
/// el saldo ya bajó cuando se registró el recibo.
///
/// Las invariantes («lo aplicado no supera lo cobrado del recibo ni la parte en cuenta corriente de la venta, el recibo es
/// un <c>RC</c> y la venta no, los dos son del mismo cliente y ninguno está anulado») las sostienen constraint triggers
/// diferidos, y <see cref="ReglaDeAplicacionDeCobro"/> las valida antes en el servicio con los mismos códigos.
/// </summary>
public class AplicacionCobro : EntidadTenant
{
    public int Id { get; set; }

    /// <summary>El recibo <c>RC</c> cuyo cobro se aplica.</summary>
    public int IdComprobanteCobro { get; set; }

    /// <summary>La venta a la que se aplica.</summary>
    public int IdComprobanteVenta { get; set; }

    /// <summary>Siempre <c>&gt; 0</c> (<c>ck_aplicaciones_cobro_importe_positivo</c>).</summary>
    public decimal Importe { get; set; }

    /// <summary>Usuario que hizo la aplicación (FK simple, igual que <see cref="ComprobanteVenta.IdEmpleado"/>).</summary>
    public int IdEmpleado { get; set; }

    public DateTimeOffset Fecha { get; set; }
}
