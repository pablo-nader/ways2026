using Ways.Domain.Common;

namespace Ways.Domain.Pagos;

/// <summary>
/// Línea por medio de pago de un <see cref="Pago"/> (doc 10 §5, tabla <c>items_pago</c>). Hija operativa:
/// scope <c>id_tenant</c> únicamente, igual que <see cref="Ways.Domain.Ventas.PagoComprobante"/>.
///
/// Rebanadas E2-S1, E2-S3a y E2-S3b: el servicio escribe un ítem por línea del pago en la misma transacción que
/// la cabecera, que ya no guarda el medio (vive solo acá) y lleva el total. La invariante «la suma de
/// los ítems vivos es el importe del pago» la sostiene un constraint trigger diferido de la base.
/// </summary>
public class ItemPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdPago { get; set; }

    public int IdMedioPago { get; set; }

    /// <summary>Siempre <c>&gt; 0</c> (<c>ck_items_pago_importe_positivo</c>).</summary>
    public decimal Importe { get; set; }

    /// <summary>Cupón o número de operación, hasta 100 caracteres (<c>ck_items_pago_referencia_longitud</c>).</summary>
    public string? Referencia { get; set; }
}
