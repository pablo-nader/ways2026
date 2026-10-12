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

/// <summary>Lo que los combos le hicieron a una línea de la venta: <see cref="UnidadesConsumidas"/>,
/// <see cref="Ahorro"/> (importe de línea, a sumar al descuento) e <see cref="IdCombo"/>, el de menor id entre los
/// combos que consumieron unidades de esa línea.</summary>
public readonly record struct ComboEnLinea(int UnidadesConsumidas, decimal Ahorro, int IdCombo);

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
    /// ser consumidas: las líneas de un artículo cuya unidad de venta no es <see cref="UnidadVenta.Unidad"/> (la
    /// unidad de venta decide, nunca la cantidad: 2 kg de un artículo pesable no son 2 unidades), las sin precio
    /// vigente y las con un descuento de oferta negativo (el motor supone descuentos de oferta no negativos). Qué
    /// documentos detectan combos (no las notas de crédito, no lo congelado) lo decide quien llama. La clave del
    /// resultado es la posición de la línea en <paramref name="lineas"/>; solo figuran las líneas con unidades
    /// consumidas.
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
            if (linea.UnidadVenta != UnidadVenta.Unidad
                || linea.PrecioLista is not { } precioLista
                || linea.DescuentoUnitarioDeOferta < 0m)
            {
                continue;
            }

            entrada.Add(new LineaParaCombos(
                i, linea.IdArticulo, linea.IdFamilia, linea.Cantidad, precioLista, linea.DescuentoUnitarioDeOferta));
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
    /// pre-filtrados por los artículos y familias de las líneas detectables) y ninguna si el documento suma menos
    /// unidades que el mínimo de un combo, porque con menos no puede completarse ninguno. Es solo una optimización:
    /// qué líneas entran a la detección lo decide <see cref="Detectar"/>. <paramref name="idEmpresa"/> <c>null</c>
    /// deja solo los combos de todo el tenant.
    /// </summary>
    public static async Task<DeteccionDeCombosEnVenta> DetectarAsync(
        IWaysDbContext db, int? idEmpresa, IReadOnlyList<LineaDeVentaParaCombos> lineas, CancellationToken ct = default)
    {
        if (lineas.Sum(l => l.Cantidad) < ReglaDeCombos.UnidadesMinimas)
        {
            return DeteccionDeCombosEnVenta.Vacia;
        }

        var detectables = lineas.Where(l => l.UnidadVenta == UnidadVenta.Unidad).ToList();
        var candidatos = await ServicioDeCombos.CargarCandidatosParaVentaAsync(
            db,
            idEmpresa,
            [.. detectables.Select(l => l.IdArticulo).Distinct()],
            [.. detectables.Where(l => l.IdFamilia.HasValue).Select(l => l.IdFamilia!.Value).Distinct()],
            ct);

        return Detectar(lineas, candidatos);
    }
}
