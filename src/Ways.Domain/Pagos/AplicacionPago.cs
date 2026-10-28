using Ways.Domain.Common;

namespace Ways.Domain.Pagos;

/// <summary>
/// Aplicación de una parte de un <see cref="Pago"/> a una compra (doc 10 §5, tabla <c>aplicaciones_pago</c>). Hija
/// operativa de <c>pagos</c>: scope <c>id_tenant</c> únicamente. Un pago puede aplicarse a varias compras y una compra
/// recibir varios pagos; la parte del pago sin aplicar es un crédito a favor.
///
/// Rebanada E3-S1a: solo esquema y backfill. Ningún escritor la usa todavía y los lectores siguen derivando lo pagado de
/// <c>pagos.id_comprobante_compra</c>. Las invariantes («lo aplicado no supera al pago ni a la compra, la compra está
/// confirmada y el pago es de proveedor del mismo proveedor y empresa») las sostienen constraint triggers diferidos.
/// </summary>
public class AplicacionPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdPago { get; set; }

    public int IdComprobanteCompra { get; set; }

    /// <summary>Siempre <c>&gt; 0</c> (<c>ck_aplicaciones_pago_importe_positivo</c>).</summary>
    public decimal Importe { get; set; }

    public OrigenAplicacionPago Origen { get; set; }

    /// <summary>Usuario que hizo la aplicación (FK simple, igual que <see cref="Pago.IdEmpleado"/>).</summary>
    public int IdEmpleado { get; set; }

    public DateTimeOffset Fecha { get; set; }
}
