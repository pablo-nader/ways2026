namespace Ways.Domain.Reportes;

/// <summary>Una fila de stock de un artículo en una sucursal, ya sumada desde el ledger hasta el instante de corte.</summary>
public sealed record StockDeSucursal(int IdArticulo, int IdSucursal, decimal Cantidad);

/// <summary>Lo que hace falta de un artículo para valorarlo: su área (si tiene) y el costo nominal vigente (<c>null</c> si no
/// tiene costo).</summary>
public sealed record ArticuloAValorar(int IdArticulo, int? IdArea, decimal? CostoActual);

public sealed record MercaderiaPorArea(int? IdArea, decimal Costo, decimal Venta);

/// <summary>Mercadería valorada a un instante. <see cref="SinCosto"/> y <see cref="SinPrecio"/> cuentan artículos con stock
/// positivo que no se pudieron valorar (nunca se valoran en cero). <see cref="ConCostoEstimado"/> cuenta los valorados con el costo
/// actual por falta de una compra anterior al corte, y <see cref="ValorCostoEstimado"/> es lo que pesan dentro de
/// <see cref="Costo"/>.</summary>
public sealed record MercaderiaValorada(
    decimal Costo, decimal Venta, int SinCosto, int SinPrecio, int ConCostoEstimado, decimal ValorCostoEstimado,
    IReadOnlyList<MercaderiaPorArea> PorArea);

/// <summary>
/// Aritmética pura del reporte de valoración, testeable sin base de datos como <c>PoliticaDeRoles</c>. Solo cuenta stock positivo:
/// cada par artículo-sucursal se acota a cero antes de sumar, así que un saldo negativo en un local no resta valor del otro.
/// </summary>
public static class CalculoDeValoracion
{
    /// <summary>Suma las filas de cada par (artículo, sucursal), acota cada par a <c>max(0, x)</c> y suma los pares de cada
    /// artículo. Los artículos sin stock positivo no figuran en el resultado.</summary>
    public static IReadOnlyDictionary<int, decimal> StockPositivoPorArticulo(IEnumerable<StockDeSucursal> filas) =>
        filas
            .GroupBy(f => (f.IdArticulo, f.IdSucursal))
            .Select(g => (g.Key.IdArticulo, Cantidad: Math.Max(0m, g.Sum(f => f.Cantidad))))
            .GroupBy(p => p.IdArticulo)
            .Select(g => (IdArticulo: g.Key, Cantidad: g.Sum(p => p.Cantidad)))
            .Where(a => a.Cantidad > 0m)
            .ToDictionary(a => a.IdArticulo, a => a.Cantidad);

    /// <summary>
    /// Valora el stock positivo de cada artículo con ficha. Un artículo sin ficha en <paramref name="articulos"/> (dado de baja) no
    /// se valora. <paramref name="costoDeCompra"/> es el costo de la última compra anterior al corte; <c>null</c> significa que
    /// el punto es el actual y se usa el costo nominal vigente sin marcarlo como estimado. Con historial pedido y sin compra del
    /// artículo, se usa el costo actual y el artículo cuenta como estimado. <paramref name="precios"/> da el precio de venta al
    /// corte (<c>null</c> o ausente es un artículo sin precio). Un área que no está en <paramref name="areasVisibles"/> se agrupa
    /// igual que un artículo sin área.
    /// </summary>
    public static MercaderiaValorada Valorar(
        IReadOnlyDictionary<int, decimal> stockPorArticulo,
        IReadOnlyDictionary<int, ArticuloAValorar> articulos,
        IReadOnlyDictionary<int, decimal>? costoDeCompra,
        IReadOnlyDictionary<int, decimal?> precios,
        IReadOnlySet<int> areasVisibles)
    {
        var costo = 0m;
        var venta = 0m;
        var sinCosto = 0;
        var sinPrecio = 0;
        var conCostoEstimado = 0;
        var valorEstimado = 0m;
        var porArea = new Dictionary<int, (decimal Costo, decimal Venta)>();
        var sinArea = (Costo: 0m, Venta: 0m);
        var haySinArea = false;

        foreach (var (idArticulo, cantidad) in stockPorArticulo)
        {
            if (!articulos.TryGetValue(idArticulo, out var articulo))
            {
                continue;
            }

            decimal? costoUnitario;
            var estimado = false;
            if (costoDeCompra is null)
            {
                costoUnitario = articulo.CostoActual;
            }
            else if (costoDeCompra.TryGetValue(idArticulo, out var deCompra))
            {
                costoUnitario = deCompra;
            }
            else
            {
                costoUnitario = articulo.CostoActual;
                estimado = costoUnitario is not null;
            }

            var costoDelArticulo = 0m;
            if (costoUnitario is { } unitario)
            {
                costoDelArticulo = cantidad * unitario;
                costo += costoDelArticulo;
                if (estimado)
                {
                    conCostoEstimado++;
                    valorEstimado += costoDelArticulo;
                }
            }
            else
            {
                sinCosto++;
            }

            var ventaDelArticulo = 0m;
            if (precios.GetValueOrDefault(idArticulo) is { } precio)
            {
                ventaDelArticulo = cantidad * precio;
                venta += ventaDelArticulo;
            }
            else
            {
                sinPrecio++;
            }

            if (articulo.IdArea is { } idArea && areasVisibles.Contains(idArea))
            {
                var acumulado = porArea.GetValueOrDefault(idArea);
                porArea[idArea] = (acumulado.Costo + costoDelArticulo, acumulado.Venta + ventaDelArticulo);
            }
            else
            {
                sinArea = (sinArea.Costo + costoDelArticulo, sinArea.Venta + ventaDelArticulo);
                haySinArea = true;
            }
        }

        var areas = porArea
            .OrderBy(a => a.Key)
            .Select(a => new MercaderiaPorArea(a.Key, a.Value.Costo, a.Value.Venta))
            .ToList();
        if (haySinArea)
        {
            areas.Add(new MercaderiaPorArea(null, sinArea.Costo, sinArea.Venta));
        }

        return new MercaderiaValorada(costo, venta, sinCosto, sinPrecio, conCostoEstimado, valorEstimado, areas);
    }

    public static decimal Dinero(decimal efectivo, decimal cuentas) => efectivo + cuentas;

    public static decimal TotalACosto(MercaderiaValorada mercaderia, decimal dinero) => mercaderia.Costo + dinero;

    public static decimal TotalAVenta(MercaderiaValorada mercaderia, decimal dinero) => mercaderia.Venta + dinero;
}
