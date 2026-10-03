using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Desglose de IVA de un <see cref="ComprobanteCompra"/> por alícuota: el neto gravado y el IVA de
/// cada una, tal como los imprime el proveedor. Child scope (<c>id_tenant</c> únicamente), mismo
/// criterio que <see cref="ItemComprobanteCompra"/>: existe solo cuando el comprobante discrimina
/// IVA y se reemplaza completo junto con los ítems en cada guardado del borrador. Exento y no
/// gravado son filas de <c>alicuotas_iva</c> como cualquier otra: aparecen con IVA cero.
/// <see cref="Iva"/> es lo que se guarda y de donde se recomputan <c>iva_total</c> y <c>total</c>:
/// el calculado, o el impreso cuando el proveedor redondeó distinto.
/// </summary>
public class AlicuotaComprobanteCompra : EntidadTenant
{
    public int Id { get; set; }

    public int IdComprobanteCompra { get; set; }

    public int IdAlicuotaIva { get; set; }

    /// <summary>Snapshot del porcentaje de la alícuota al guardar.</summary>
    public decimal Porcentaje { get; set; }

    public decimal Neto { get; set; }

    public decimal Iva { get; set; }
}
