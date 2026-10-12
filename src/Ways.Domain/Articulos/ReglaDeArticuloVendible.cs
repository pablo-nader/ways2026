using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Un artículo con <see cref="Articulo.DisponibleVenta"/> en <c>false</c> es de uso interno: se compra y se stockea,
/// pero ninguna línea de venta puede llevarlo. Pura, sin base de datos, para que los documentos de venta
/// (venta, presupuesto, remito) compartan una sola regla y un solo código de error. Se exige al guardar el borrador y
/// otra vez cuando el documento se concreta (convertir un presupuesto, emitir un remito, facturar remitos): un borrador
/// viejo no la esquiva. Quedan fuera la venta que un dispositivo ya cobró offline y la devolución.
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
                    $"El artículo {articulo.Nombre} ({articulo.CodigoInterno}) no está disponible a la venta.");
            }
        }
    }
}
