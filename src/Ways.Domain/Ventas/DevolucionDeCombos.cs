namespace Ways.Domain.Ventas;

/// <summary>Un item de la venta original que consumió un combo (<c>id_combo</c> no nulo), con lo que se
/// cobró por él: <see cref="Total"/> ya incluye la parte del combo y cualquier ajuste manual.</summary>
public readonly record struct ItemVendidoConCombo(
    int IdItem, int Orden, int IdArticulo, decimal Cantidad, decimal PrecioUnitario, decimal Total, int IdCombo,
    int? IdOferta, int IdListaPrecio, int IdAlicuotaIva, decimal PorcentajeIva);

/// <summary>Una línea que la nota de crédito devuelve: el artículo y las unidades (positivas).</summary>
public readonly record struct LineaDevuelta(int IdArticulo, decimal Cantidad);

/// <summary>Las unidades de una línea devuelta que salen de UN item de la venta original, con el importe
/// que se devuelve por ellas (<see cref="Credito"/>, positivo).</summary>
public readonly record struct TramoDeDevolucion(ItemVendidoConCombo Origen, decimal Cantidad, decimal Credito);

/// <summary>
/// Reparte las unidades que devuelve una nota de crédito entre los items de la venta original que consumieron un
/// combo, para devolver lo que se cobró y no lo que cuesta hoy. Pura, sin base de datos.
///
/// <para>Para cada línea devuelta, en el orden en que viene, toma unidades de los items con el mismo artículo en
/// orden de <see cref="ItemVendidoConCombo.Orden"/> (y de <see cref="ItemVendidoConCombo.IdItem"/> si empatan),
/// hasta agotar las unidades de cada uno. Las unidades que ningún item de combo cubre (más que las vendidas en
/// combo, o de un artículo que no estuvo en un combo) no figuran en ningún tramo: las precia quien llama, como
/// siempre.</para>
///
/// <para>Importe: se acredita por unidades acumuladas. El crédito de una toma es
/// <c>round(Total × (unidades ya tomadas + unidades de esta toma) / Cantidad)</c> en centavos
/// (<see cref="MidpointRounding.AwayFromZero"/>) menos lo ya acreditado de ese item. El consumo se acumula entre las
/// líneas del mismo documento: devolver TODAS las unidades de un item acredita exactamente su <c>Total</c>, y como el
/// redondeo es monótono ninguna toma resulta negativa ni la suma supera el <c>Total</c>, sea cual sea el reparto en
/// líneas.</para>
///
/// <para>No lleva cuenta de lo devuelto por notas de crédito anteriores de la misma venta: el tope es por documento.</para>
/// </summary>
public static class DevolucionDeCombos
{
    public static IReadOnlyList<IReadOnlyList<TramoDeDevolucion>> Repartir(
        IReadOnlyList<ItemVendidoConCombo> vendidos, IReadOnlyList<LineaDevuelta> devueltas)
    {
        var restantes = new Dictionary<int, decimal>();
        var devueltoPorItem = new Dictionary<int, decimal>();
        var tomadasPorItem = new Dictionary<int, decimal>();
        var vendidosPorArticulo = vendidos
            .Where(v => v.Cantidad > 0m)
            .OrderBy(v => v.Orden)
            .ThenBy(v => v.IdItem)
            .ToLookup(v => v.IdArticulo);

        foreach (var vendido in vendidosPorArticulo.SelectMany(g => g))
        {
            restantes[vendido.IdItem] = vendido.Cantidad;
            devueltoPorItem[vendido.IdItem] = 0m;
            tomadasPorItem[vendido.IdItem] = 0m;
        }

        var resultado = new List<IReadOnlyList<TramoDeDevolucion>>(devueltas.Count);

        foreach (var devuelta in devueltas)
        {
            var tramos = new List<TramoDeDevolucion>();
            var faltan = devuelta.Cantidad;

            foreach (var vendido in vendidosPorArticulo[devuelta.IdArticulo])
            {
                if (faltan <= 0m)
                {
                    break;
                }

                var disponibles = restantes[vendido.IdItem];
                if (disponibles <= 0m)
                {
                    continue;
                }

                var unidades = Math.Min(faltan, disponibles);
                var tomadas = tomadasPorItem[vendido.IdItem] + unidades;
                var credito = Math.Round(vendido.Total * tomadas / vendido.Cantidad, 2, MidpointRounding.AwayFromZero)
                    - devueltoPorItem[vendido.IdItem];

                restantes[vendido.IdItem] = disponibles - unidades;
                tomadasPorItem[vendido.IdItem] = tomadas;
                devueltoPorItem[vendido.IdItem] += credito;
                faltan -= unidades;
                tramos.Add(new TramoDeDevolucion(vendido, unidades, credito));
            }

            resultado.Add(tramos);
        }

        return resultado;
    }
}
