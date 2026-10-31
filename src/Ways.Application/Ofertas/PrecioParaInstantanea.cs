namespace Ways.Application.Ofertas;

/// <summary>
/// Un precio resuelto tal como viaja en la instantánea offline (precio plano, escalón o celda de familia).
/// <see cref="PrecioFinal"/>, <see cref="DescuentoUnitario"/> y <see cref="Aplicadas"/> son los campos que leen los POS
/// ya instalados, y solo llevan las ofertas comunes. <see cref="AplicadasCompletas"/> son todas las ofertas aplicadas en
/// el orden del servidor, incluidas las "solo múltiplos" (con <see cref="OfertaAplicadaDto.MultiploDe"/>); es
/// <c>null</c> cuando ninguna lo es, y entonces los campos de siempre ya son la resolución entera.
/// </summary>
public readonly record struct PrecioParaInstantanea(
    decimal PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicadaDto> Aplicadas,
    IReadOnlyList<OfertaAplicadaDto>? AplicadasCompletas)
{
    /// <summary>
    /// Separa las ofertas "solo múltiplos" de una resolución. Un POS sin soporte para ellas cobraría su descuento
    /// unitario en todas las unidades, también en las que no completan un grupo: con los campos de siempre limitados a
    /// las ofertas comunes, ese POS cobra el precio original en las unidades de la oferta "solo múltiplos" (nunca menos
    /// de lo que cobra el servidor) y la auditoría de discrepancias lo registra. El descuento común lleva el mismo tope
    /// del motor: la suma de los descuentos, entre cero y el precio original.
    /// </summary>
    public static PrecioParaInstantanea Separar(
        decimal precioOriginal, decimal precioFinal, decimal descuentoUnitario, IReadOnlyList<OfertaAplicadaDto> aplicadas)
    {
        if (!aplicadas.Any(a => a.MultiploDe is not null))
        {
            return new PrecioParaInstantanea(precioFinal, descuentoUnitario, aplicadas, null);
        }

        var comunes = aplicadas.Where(a => a.MultiploDe is null).ToList();
        var descuentoComun = Math.Clamp(comunes.Sum(a => a.DescuentoUnitario), 0m, precioOriginal);

        return new PrecioParaInstantanea(precioOriginal - descuentoComun, descuentoComun, comunes, aplicadas);
    }
}
