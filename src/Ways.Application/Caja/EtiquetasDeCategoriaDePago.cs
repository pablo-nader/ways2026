using Ways.Domain.Pagos;

namespace Ways.Application.Caja;

/// <summary>Única fuente del lado del servidor de las etiquetas en español de <see cref="CategoriaPago"/>:
/// las usa el detalle de un pago del libro de tesorería.
/// Espejo de <c>CATEGORIAS_PAGO</c> en <c>Ways.Web/src/api/tipos.ts</c>; que no se desfasen lo cuidan
/// <c>EtiquetasDeCategoriaDePagoTests</c> (backend) y el test de <c>CATEGORIAS_PAGO</c> en
/// <c>tipos.test.ts</c> (web), que fijan la misma lista literal.</summary>
public static class EtiquetasDeCategoriaDePago
{
    public static readonly IReadOnlyDictionary<CategoriaPago, string> Todas =
        new Dictionary<CategoriaPago, string>
        {
            [CategoriaPago.Proveedor] = "Proveedores",
            [CategoriaPago.Sueldos] = "Sueldos",
            [CategoriaPago.Viaticos] = "Viáticos",
            [CategoriaPago.Impuestos] = "Impuestos",
            [CategoriaPago.Servicios] = "Servicios",
            [CategoriaPago.Otros] = "Otros",
        };

    public static string De(CategoriaPago categoria) =>
        Todas.TryGetValue(categoria, out var etiqueta) ? etiqueta : categoria.ToString();
}
