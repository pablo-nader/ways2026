using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// <see cref="EscalonesDeCantidad.ConstruirGrillaDeFamilia"/> — la grilla (cantidad de familia ×
/// cantidad propia) que le permite al dispositivo offline cobrar una oferta de familia cuando la
/// cantidad de la familia viene de OTROS artículos del carrito. Pura, sin base de datos.
/// </summary>
public class GrillaDeFamiliaTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);
    private const int DiaSemana = 1;
    private const int IdFamilia = 5;
    private const decimal Lista = 2000m;

    private static LineaAResolver CrearLinea(decimal cantidad = 1m) =>
        new(1, null, [], 1, cantidad, Lista, Fecha, Hora, DiaSemana, IdFamilia, cantidad);

    private static OfertaCandidata DeFamilia(
        int id, decimal cantidadMinima, decimal precioUnitario, int idFamilia = IdFamilia,
        DateOnly? fechaDesde = null) =>
        new(id, $"familia {cantidadMinima}", 0, false, AlcanceDeOferta.DeFamilia(idFamilia),
            BeneficioDeOferta.DePrecioUnitario(precioUnitario), cantidadMinima, fechaDesde, null, null, null,
            new HashSet<int>(), new HashSet<int>());

    private static OfertaCandidata DeArticulo(int id, decimal cantidadMinima, decimal precioUnitario) =>
        new(id, $"articulo {cantidadMinima}", 0, false, AlcanceDeOferta.DeArticulo(1),
            BeneficioDeOferta.DePrecioUnitario(precioUnitario), cantidadMinima, null, null, null, null,
            new HashSet<int>(), new HashSet<int>());

    private static (decimal Familia, decimal Propia, decimal Descuento)[] Resumen(IEnumerable<CeldaDeFamilia> grilla) =>
        grilla.Select(c => (c.CantidadDeFamiliaDesde, c.CantidadDesde, c.Resolucion.DescuentoUnitario)).ToArray();

    [Fact]
    public void SoloUnaOfertaDeFamiliaGeneraUnaCeldaEnSuUmbral()
    {
        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(CrearLinea(), [DeFamilia(1, 2m, 1750m)]);

        Assert.Equal([(2m, 1m, 250m)], Resumen(grilla));
        Assert.Equal([1], grilla.Single().Resolucion.Aplicadas.Select(a => a.IdOferta));
    }

    [Fact]
    public void UnaOfertaDeArticuloAgregaUnaColumnaPropiaYLaCeldaLaResuelveConAmbasOfertas()
    {
        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(
            CrearLinea(), [DeFamilia(1, 2m, 1750m), DeArticulo(2, 6m, 1400m)]);

        Assert.Equal([(2m, 1m, 250m), (2m, 6m, 600m)], Resumen(grilla));
        Assert.Equal([2], grilla[1].Resolucion.Aplicadas.Select(a => a.IdOferta));
    }

    [Fact]
    public void DosOfertasDeFamiliaGeneranUnaCeldaPorUmbralDeFamilia()
    {
        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(
            CrearLinea(), [DeFamilia(1, 4m, 1500m), DeFamilia(2, 2m, 1750m)]);

        Assert.Equal([(2m, 1m, 250m), (4m, 1m, 500m)], Resumen(grilla));
    }

    [Fact]
    public void SinOfertaDeFamiliaConUmbralLaGrillaEstaVacia()
    {
        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(
            CrearLinea(), [DeArticulo(1, 3m, 1800m), DeFamilia(2, 1m, 1900m)]);

        Assert.Empty(grilla);
    }

    [Fact]
    public void UnaOfertaDeFamiliaFueraDeSuVentanaODeOtraFamiliaNoAportaUmbral()
    {
        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(
            CrearLinea(),
            [DeFamilia(1, 2m, 1750m, fechaDesde: Fecha.AddDays(1)), DeFamilia(2, 3m, 1700m, idFamilia: 99)]);

        Assert.Empty(grilla);
    }

    // ---- exactitud ---------------------------------------------------------------------------

    /// <summary>
    /// ESPEJO en C# de la regla del cliente (<c>elegirCeldaDeFamilia</c> y
    /// <c>elegirEscalon</c>, que <c>preciosVigentesOffline</c> aplica en ese orden, en
    /// <c>instantaneaOffline.ts</c>), escrito solo para este test: NO es la regla de producción, que
    /// vive en TypeScript. Si la regla del cliente cambia, hay que cambiar esta copia a mano.
    /// </summary>
    private static PrecioConOfertas ReglaDelCliente(
        CurvaDeCantidad curva, IReadOnlyList<CeldaDeFamilia> grilla, decimal cantidadPropia, decimal cantidadDeFamilia)
    {
        var candidatas = grilla.Where(c => c.CantidadDeFamiliaDesde <= cantidadDeFamilia).ToList();
        if (candidatas.Count == 0)
        {
            var escalon = curva.Escalones
                .Where(e => e.CantidadDesde <= cantidadPropia)
                .OrderByDescending(e => e.CantidadDesde)
                .Select(e => (EscalonResuelto?)e)
                .FirstOrDefault();
            return escalon?.Resolucion ?? curva.Base;
        }

        var umbralDeFamilia = candidatas.Max(c => c.CantidadDeFamiliaDesde);
        return candidatas
            .Where(c => c.CantidadDeFamiliaDesde == umbralDeFamilia && c.CantidadDesde <= cantidadPropia)
            .OrderByDescending(c => c.CantidadDesde)
            .First().Resolucion;
    }

    /// <summary>La curva legada más la grilla describen el motor real en TODO par (qL, qF) con
    /// 1 &lt;= qL &lt;= qF &lt;= 8, para un conjunto de ofertas que mezcla umbrales de familia y de
    /// artículo.</summary>
    [Fact]
    public void LaReglaSobreLaCurvaYLaGrillaCoincideConElMotorEnTodoParDeCantidades()
    {
        OfertaCandidata[] candidatas =
        [
            DeFamilia(1, 2m, 1750m),
            DeFamilia(2, 4m, 1500m),
            DeArticulo(3, 3m, 1700m),
            DeArticulo(4, 6m, 1400m),
        ];
        var linea = CrearLinea();
        var curva = EscalonesDeCantidad.Construir(linea, candidatas);
        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(linea, candidatas);

        for (var cantidadDeFamilia = 1; cantidadDeFamilia <= 8; cantidadDeFamilia++)
        {
            for (var cantidadPropia = 1; cantidadPropia <= cantidadDeFamilia; cantidadPropia++)
            {
                var esperado = ResolvedorDeOfertas.Resolver(
                    linea with { Cantidad = cantidadPropia, CantidadDeFamilia = cantidadDeFamilia }, candidatas);

                var obtenido = ReglaDelCliente(curva, grilla, cantidadPropia, cantidadDeFamilia);

                Assert.True(
                    esperado.DescuentoUnitario == obtenido.DescuentoUnitario &&
                    esperado.Aplicadas.Select(a => a.IdOferta).SequenceEqual(obtenido.Aplicadas.Select(a => a.IdOferta)),
                    $"qL={cantidadPropia} qF={cantidadDeFamilia}: esperado {esperado.DescuentoUnitario}, obtenido {obtenido.DescuentoUnitario}");
            }
        }
    }
}
