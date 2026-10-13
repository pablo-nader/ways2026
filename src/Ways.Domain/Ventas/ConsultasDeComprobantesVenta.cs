using System.Linq.Expressions;

namespace Ways.Domain.Ventas;

/// <summary>
/// Predicado compartido "no es un espejo fiscal". Un espejo fiscal es la fila de <c>comprobantes_venta</c>
/// que representa la factura de una venta (<c>id_venta_origen</c> apunta a la venta); el ticket sigue siendo
/// el documento económico, así que todo lector que agregue o liste ventas excluye los espejos por acá. Los
/// lectores que sí necesitan ver las facturas (el libro IVA ventas) no lo usan.
/// </summary>
public static class ConsultasDeComprobantesVenta
{
    /// <summary>La misma cláusula como expresión, para consultas armadas con <c>join</c> o <c>where</c>
    /// sobre un tipo anónimo.</summary>
    public static readonly Expression<Func<ComprobanteVenta, bool>> NoEsEspejoFiscal =
        c => c.IdVentaOrigen == null;

    public static IQueryable<ComprobanteVenta> SinEspejosFiscales(this IQueryable<ComprobanteVenta> consulta) =>
        consulta.Where(NoEsEspejoFiscal);
}
