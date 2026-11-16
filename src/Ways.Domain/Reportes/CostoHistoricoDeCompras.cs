namespace Ways.Domain.Reportes;

/// <summary>Una línea de compra que escribió <c>articulos.costo_nominal</c>: confirmada, de un comprobante que no es nota de
/// crédito, con <c>actualiza_costo</c> y costo efectivo positivo. <see cref="CostoEfectivo"/> está en la misma base que el costo
/// nominal (con IVA, neto de descuento, impuesto interno incluido, por unidad).</summary>
public sealed record LineaQueEscribioCosto(
    int IdArticulo, DateTimeOffset FechaRecepcion, int IdComprobante, int Orden, decimal CostoEfectivo);

/// <summary>
/// Reconstruye el costo nominal que tenía un artículo en un instante pasado. No existe historial de costos, así que se deduce de las
/// compras que lo escribieron: gana la última línea (por recepción, comprobante y orden) anterior o igual al corte. Los artículos de
/// una misma familia comparten costo nominal, así que para un miembro cuenta la última línea de cualquiera de ellos. Es una
/// reconstrucción: no ve ediciones manuales del costo ni la pertenencia que tenía la familia en esa fecha.
/// </summary>
public static class CostoHistoricoDeCompras
{
    public static IReadOnlyDictionary<int, decimal> Resolver(
        IReadOnlyList<LineaQueEscribioCosto> lineas, DateTimeOffset corte, IEnumerable<int> idsArticulo,
        IReadOnlyDictionary<int, int> familiaPorArticulo)
    {
        var claveDe = (int idArticulo) => familiaPorArticulo.TryGetValue(idArticulo, out var familia)
            ? (Familia: (int?)familia, Articulo: (int?)null)
            : (Familia: null, Articulo: (int?)idArticulo);

        var ultimaPorClave = lineas
            .Where(l => l.FechaRecepcion <= corte)
            .GroupBy(l => claveDe(l.IdArticulo))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(l => l.FechaRecepcion).ThenByDescending(l => l.IdComprobante)
                    .ThenByDescending(l => l.Orden).First().CostoEfectivo);

        var resultado = new Dictionary<int, decimal>();
        foreach (var idArticulo in idsArticulo)
        {
            if (ultimaPorClave.TryGetValue(claveDe(idArticulo), out var costo))
            {
                resultado[idArticulo] = costo;
            }
        }

        return resultado;
    }
}
