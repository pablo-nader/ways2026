using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Un artículo con <see cref="Articulo.DisponibleVenta"/> en <c>false</c> es de uso interno: se compra y se stockea,
/// pero ninguna línea de venta puede llevarlo. Pura, sin base de datos, para que los documentos de venta
/// (venta, presupuesto, remito) compartan una sola regla y un solo código de error.
/// </summary>
public static class ReglaDeArticuloVendible
{
    public const string CodigoDeError = "articulo_no_vendible";

    public static bool EsVendible(Articulo articulo) => articulo.DisponibleVenta;

    /// <summary>Rechaza con <c>409 articulo_no_vendible</c> el primer artículo no vendible, en el orden recibido.</summary>
    public static void Exigir(IEnumerable<Articulo> articulos)
    {
        foreach (var articulo in articulos)
        {
            if (!EsVendible(articulo))
            {
                throw ErrorDominio.Conflicto(
                    CodigoDeError,
                    $"El artículo {articulo.CodigoInterno} no está disponible a la venta.");
            }
        }
    }
}
