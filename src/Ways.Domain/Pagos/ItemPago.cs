using Ways.Domain.Common;

namespace Ways.Domain.Pagos;

/// <summary>
/// Línea por medio de pago de un <see cref="Pago"/> (doc 10 §5, tabla <c>items_pago</c>). Hija operativa:
/// scope <c>id_tenant</c> únicamente, igual que <see cref="Ways.Domain.Ventas.PagoComprobante"/>.
///
/// Escritura doble (rebanada E2-S1): hoy cada pago tiene exactamente un ítem que repite el medio y el
/// importe de su cabecera, y el servicio los escribe en la misma transacción. La invariante «la suma de los
/// ítems vivos es el importe del pago» la sostiene un constraint trigger diferido de la base.
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
