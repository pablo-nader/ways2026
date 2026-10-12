using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Application.Ventas;

/// <summary>
/// Los documentos que precian cada línea con la lista (presupuestos, remitos) no tienen dónde guardar el importe que
/// tipea el cajero para un artículo por importe (<see cref="UnidadVenta.Importe"/>). En vez de venderlo a un precio de
/// lista que no corresponde, o de fallar con "sin precio vigente", los rechazan con un error propio.
/// </summary>
public static class ArticulosPorImporte
{
    /// <param name="documento">Con artículo y género, para el mensaje: "un presupuesto", "un remito".</param>
    public static void RechazarEn(IEnumerable<Articulo> articulos, string documento)
    {
        if (articulos.FirstOrDefault(a => a.UnidadVenta == UnidadVenta.Importe) is { } porImporte)
        {
            throw new ErrorDominio(
                "articulo_por_importe_no_admitido",
                $"El artículo {porImporte.CodigoInterno} se vende por importe: no se puede incluir en {documento}.",
                400);
        }
    }
}
