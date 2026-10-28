using Ways.Domain.Gastos;

namespace Ways.Application.Caja;

/// <summary>Única fuente del lado del servidor de las etiquetas en español de <see cref="CategoriaGasto"/>:
/// las usan el export del libro de tesorería y el orden por concepto de <c>GET /api/reportes/tesoreria</c>.
/// Espejo de <c>CATEGORIAS_GASTO</c> en <c>Ways.Web/src/api/tipos.ts</c>; que no se desfasen lo cuidan
/// <c>EtiquetasDeCategoriaDeGastoTests</c> (backend) y el test de <c>CATEGORIAS_GASTO</c> en
/// <c>tipos.test.ts</c> (web), que fijan la misma lista literal.</summary>
public static class EtiquetasDeCategoriaDeGasto
{
    public static readonly IReadOnlyDictionary<CategoriaGasto, string> Todas =
        new Dictionary<CategoriaGasto, string>
        {
            [CategoriaGasto.Proveedor] = "Proveedores",
            [CategoriaGasto.Sueldos] = "Sueldos",
            [CategoriaGasto.Viaticos] = "Viáticos",
            [CategoriaGasto.Impuestos] = "Impuestos",
            [CategoriaGasto.Servicios] = "Servicios",
            [CategoriaGasto.Otros] = "Otros",
        };

    public static string De(CategoriaGasto categoria) =>
        Todas.TryGetValue(categoria, out var etiqueta) ? etiqueta : categoria.ToString();
}
