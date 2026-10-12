using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Combos;

namespace Ways.Application.Combos;

/// <summary>Una línea de la venta tal como la ve la detección de combos. <see cref="PrecioLista"/> es el precio
/// vigente sin ofertas; <c>null</c> si no hay precio vigente. <see cref="DescuentoUnitarioDeOferta"/> es el que
/// resolvieron las ofertas para la línea.</summary>
public readonly record struct LineaDeVentaParaCombos(
    int IdArticulo,
    int? IdFamilia,
    UnidadVenta UnidadVenta,
    decimal Cantidad,
    decimal? PrecioLista,
    decimal DescuentoUnitarioDeOferta);

/// <summary>Lo que los combos le hicieron a una línea de la venta: <see cref="UnidadesConsumidas"/> (la cantidad de
/// la línea que entró a combos, en la unidad de venta del artículo: unidades enteras o kilos con hasta tres decimales),
/// <see cref="Ahorro"/> (importe de línea, a sumar al descuento) e <see cref="IdCombo"/>, el de menor id entre los
/// combos que consumieron cantidad de esa línea.</summary>
public readonly record struct ComboEnLinea(decimal UnidadesConsumidas, decimal Ahorro, int IdCombo);

/// <summary>Lo que la detección de combos decidió para un documento: <see cref="Lineas"/> por posición de línea,
/// <see cref="Combos"/> aplicados (veces y ahorro total de cada uno) y los <see cref="Candidatos"/> con los que se
/// detectó, para quien necesite datos del combo que el motor no devuelve (el código, por ejemplo).</summary>
public sealed record DeteccionDeCombosEnVenta(
    IReadOnlyDictionary<int, ComboEnLinea> Lineas,
    IReadOnlyList<ComboAplicado> Combos,
    IReadOnlyList<ComboCandidato> Candidatos)
{
    public static DeteccionDeCombosEnVenta Vacia { get; } = new(new Dictionary<int, ComboEnLinea>(), [], []);
}

/// <summary>
/// Puente entre las líneas de una venta y <see cref="ResolvedorDeCombos"/>. Puro y sin base de datos: decide qué
/// líneas entran a la detección y devuelve el resultado indexado por la posición de la línea en la venta.
/// </summary>
public static class CombosEnVenta
{
    /// <summary>
    /// Detecta combos en las líneas de una venta, en el orden dado. Quedan fuera de la detección, sin consumir ni
    /// ser consumidas: las líneas sin precio vigente y las con un descuento de oferta negativo (el motor supone
    /// descuentos de oferta no negativos). La unidad de venta de cada línea viaja al motor, que es quien decide con
    /// ella qué cantidades valen (<see cref="ResolvedorDeCombos"/>). Qué documentos detectan combos (no las notas de
    /// crédito, no lo congelado) lo decide quien llama. La clave del resultado es la posición de la línea en
    /// <paramref name="lineas"/>; solo figuran las líneas con cantidad consumida.
    /// </summary>
    public static IReadOnlyDictionary<int, ComboEnLinea> Resolver(
        IReadOnlyList<LineaDeVentaParaCombos> lineas, IReadOnlyList<ComboCandidato> combos) =>
        Detectar(lineas, combos).Lineas;

    /// <summary>Igual que <see cref="Resolver"/> pero devuelve también los combos aplicados.</summary>
    public static DeteccionDeCombosEnVenta Detectar(
        IReadOnlyList<LineaDeVentaParaCombos> lineas, IReadOnlyList<ComboCandidato> combos)
    {
        if (combos.Count == 0)
        {
            return DeteccionDeCombosEnVenta.Vacia;
        }

        var entrada = new List<LineaParaCombos>(lineas.Count);
        for (var i = 0; i < lineas.Count; i++)
        {
            var linea = lineas[i];
            if (linea.PrecioLista is not { } precioLista || linea.DescuentoUnitarioDeOferta < 0m)
            {
                continue;
            }

            entrada.Add(new LineaParaCombos(
                i, linea.IdArticulo, linea.IdFamilia, linea.Cantidad, precioLista, linea.DescuentoUnitarioDeOferta,
                linea.UnidadVenta));
        }

        var resolucion = ResolvedorDeCombos.Resolver(entrada, combos);

        return new DeteccionDeCombosEnVenta(
            resolucion.Lineas.ToDictionary(
                c => c.Indice,
                c => new ComboEnLinea(c.UnidadesConsumidas, c.AhorroDeCombo, c.IdsDeCombo.Min())),
            resolucion.Combos,
            combos);
    }

    /// <summary>
    /// Camino único de la detección con base de datos, compartido por la venta online y por la vista previa del
    /// carrito: cuesta UNA consulta (<see cref="ServicioDeCombos.CargarCandidatosParaVentaAsync"/>, candidatos
    /// pre-filtrados por los artículos y familias de las líneas) y ninguna si todas las líneas son de artículos por
    /// unidad y el documento suma menos de dos unidades: un combo pide al menos dos componentes, o uno solo con
    /// cantidad dos o más, y de una línea por unidad cada componente toma al menos una unidad entera. Con una línea
    /// pesable esa cuenta no vale (0,400 kg completan dos componentes de 0,200) y la consulta se paga. Es solo una
    /// optimización: qué líneas entran a la detección lo decide <see cref="Detectar"/>. <paramref name="idEmpresa"/>
    /// <c>null</c> deja solo los combos de todo el tenant.
    /// </summary>
    public static async Task<DeteccionDeCombosEnVenta> DetectarAsync(
        IWaysDbContext db, int? idEmpresa, IReadOnlyList<LineaDeVentaParaCombos> lineas, CancellationToken ct = default)
    {
        if (lineas.All(l => l.UnidadVenta == UnidadVenta.Unidad)
            && lineas.Sum(l => l.Cantidad) < ReglaDeCombos.CantidadMinimaDeUnComponenteUnico)
        {
            return DeteccionDeCombosEnVenta.Vacia;
        }

        var candidatos = await ServicioDeCombos.CargarCandidatosParaVentaAsync(
            db,
            idEmpresa,
            [.. lineas.Select(l => l.IdArticulo).Distinct()],
            [.. lineas.Where(l => l.IdFamilia.HasValue).Select(l => l.IdFamilia!.Value).Distinct()],
            ct);

        return Detectar(lineas, candidatos);
    }
}
