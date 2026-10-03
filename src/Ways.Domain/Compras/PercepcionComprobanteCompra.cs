using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Percepción que el proveedor le factura a la empresa en un <see cref="ComprobanteCompra"/>
/// (IIBB o IVA). Child scope (<c>id_tenant</c> únicamente), mismo criterio que
/// <see cref="AlicuotaComprobanteCompra"/>: se reemplaza completa junto con los ítems en cada
/// guardado del borrador. Una por <see cref="Tipo"/> y comprobante; la jurisdicción de IIBB va a
/// sumar una columna cuando haya más de una. <see cref="Importe"/> es lo que dice la factura y
/// suma al total del comprobante; <see cref="BaseImponible"/> y <see cref="Alicuota"/> son
/// informativas y no se recalculan.
/// </summary>
public class PercepcionComprobanteCompra : EntidadTenant
{
    public int Id { get; set; }

    public int IdComprobanteCompra { get; set; }

    /// <summary><see cref="TiposDePercepcion"/>: <c>iibb</c> o <c>iva</c>.</summary>
    public required string Tipo { get; set; }

    public decimal BaseImponible { get; set; }

    public decimal Alicuota { get; set; }

    public decimal Importe { get; set; }
}
