namespace Ways.Domain.Articulos;

/// <summary>
/// Qué cantidades admite un artículo según su <see cref="UnidadVenta"/>: <see cref="UnidadVenta.Unidad"/>
/// solo números enteros; <see cref="UnidadVenta.Peso"/> hasta <see cref="DecimalesMaximosDePeso"/>
/// decimales. Regla pura, sin base de datos. Mira solo la granularidad: que la cantidad sea mayor a
/// cero (o negativa, en un movimiento de signo inverso) lo decide cada llamador.
/// </summary>
public static class ReglaDeCantidadDeVenta
{
    public const int DecimalesMaximosDePeso = 3;

    public static bool EsCantidadAdmitida(UnidadVenta unidadVenta, decimal cantidad) => unidadVenta switch
    {
        UnidadVenta.Unidad => decimal.Truncate(cantidad) == cantidad,
        UnidadVenta.Peso => decimal.Round(cantidad, DecimalesMaximosDePeso, MidpointRounding.AwayFromZero) == cantidad,
        _ => throw new ArgumentOutOfRangeException(nameof(unidadVenta), unidadVenta, "Unidad de venta desconocida.")
    };
}
