namespace Ways.Domain.Articulos;

/// <summary>
/// La composición del costo de un artículo: lo que se carga para llegar a su <c>costo_nominal</c>. En una familia que
/// permite desglosar el costo (<see cref="Familia.PermiteDesglosarCosto"/>) es propia de cada miembro; las cinco
/// columnas son las de <see cref="ValoresCompartidosDeFamilia.ColumnasDeLaComposicion"/>. La igualdad es por valor.
/// </summary>
public sealed record ComposicionDelCosto(
    decimal? CostoLista,
    CostoListaPor CostoListaPor,
    bool CostoListaIncluyeIva,
    decimal? DescuentoProveedor,
    decimal ImpuestoInterno)
{
    public static ComposicionDelCosto De(Articulo articulo) => new(
        articulo.CostoLista,
        articulo.CostoListaPor,
        articulo.CostoListaIncluyeIva,
        articulo.DescuentoProveedor,
        articulo.ImpuestoInterno);
}
