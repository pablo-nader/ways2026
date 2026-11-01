using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>Ofertas "solo múltiplos": el beneficio alcanza a <c>N × floor(q / N)</c> unidades y el resto paga el precio
/// original. Los totales salen del camino real: <see cref="ResolvedorDeOfertas"/> → <see cref="CoberturaDeOfertas"/> →
/// <see cref="CalculadorDeTotales"/>.</summary>
public class CoberturaDeOfertasTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);

    private static LineaAResolver Linea(
        decimal cantidad, int idArticulo = 1, decimal precio = 4000m, int? idFamilia = null,
        decimal? cantidadDeFamilia = null) =>
        new(idArticulo, null, [], 1, cantidad, precio, Fecha, Hora, 1, idFamilia, cantidadDeFamilia ?? cantidad);

    private static OfertaCandidata Oferta(
        BeneficioDeOferta beneficio, decimal? cantidadMinima = 2m, bool soloMultiplos = true, int id = 1,
        AlcanceDeOferta? alcance = null, bool acumulable = false, int prioridad = 0) =>
        new(
            id, "2x7500", prioridad, acumulable, alcance ?? AlcanceDeOferta.DeArticulo(1), beneficio, cantidadMinima,
            null, null, null, null, new HashSet<int>(), new HashSet<int>(), soloMultiplos);

    private static decimal TotalDeLinea(decimal cantidad, params OfertaCandidata[] candidatas)
    {
        var resuelto = ResolvedorDeOfertas.Resolver(Linea(cantidad), candidatas);
        var descuento = CoberturaDeOfertas.DescuentoDeLinea(resuelto, cantidad);

        return CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(cantidad, resuelto.PrecioOriginal, resuelto.DescuentoUnitario,
                DescuentoDeOfertaDeLinea: descuento)]).Total;
    }

    [Theory]
    [InlineData(1, 4000)]
    [InlineData(2, 7500)]
    [InlineData(3, 11500)]
    [InlineData(4, 15000)]
    [InlineData(5, 19000)]
    public void SoloMultiplosCobraElPrecioDeOfertaSoloEnLosGruposCompletos(decimal cantidad, decimal total)
    {
        Assert.Equal(total, TotalDeLinea(cantidad, Oferta(BeneficioDeOferta.DePrecioUnitario(3750m))));
    }

    [Theory]
    [InlineData(2, 7500)]
    [InlineData(3, 11250)]
    public void SinSoloMultiplosTodasLasUnidadesLlevanLaOferta(decimal cantidad, decimal total)
    {
        Assert.Equal(
            total, TotalDeLinea(cantidad, Oferta(BeneficioDeOferta.DePrecioUnitario(3750m), soloMultiplos: false)));
    }

    [Fact]
    public void SoloMultiplosValeParaUnPorcentaje()
    {
        // 10 % de 4000 = 400 por unidad, en 2 de las 3 unidades.
        Assert.Equal(11200m, TotalDeLinea(3m, Oferta(BeneficioDeOferta.DePorcentaje(10m))));
    }

    [Fact]
    public void SoloMultiplosValeParaUnImporteFijo()
    {
        // 300 por unidad en 6 de las 7 unidades (cantidad mínima 3).
        Assert.Equal(26200m, TotalDeLinea(7m, Oferta(BeneficioDeOferta.DeImporteFijo(300m), cantidadMinima: 3m)));
    }

    [Fact]
    public void UnaAcumulableComunSeSumaATodasLasUnidadesYLaSoloMultiplosSoloALasDelGrupo()
    {
        var base2x = Oferta(BeneficioDeOferta.DePrecioUnitario(3750m));
        var acumulable = Oferta(BeneficioDeOferta.DeImporteFijo(100m), cantidadMinima: null, soloMultiplos: false, id: 2,
            acumulable: true);

        // 3 × 4000 − (2 × 250) − (3 × 100).
        Assert.Equal(11200m, TotalDeLinea(3m, base2x, acumulable));
    }

    [Fact]
    public void ElDescuentoDeCadaUnidadNoSuperaElPrecioOriginal()
    {
        var resuelto = new PrecioConOfertas(
            100m, 0m, 100m,
            [new OfertaAplicada(1, "a", 80m, MultiploDe: 2m), new OfertaAplicada(2, "b", 50m)]);

        // Dos unidades cubiertas por las dos (tope 100 cada una) y una solo por la común (50).
        Assert.Equal(250m, CoberturaDeOfertas.DescuentoDeLinea(resuelto, 3m));
    }

    [Fact]
    public void SinSoloMultiplosElDescuentoEsUnidadesPorDescuentoUnitario()
    {
        var resuelto = new PrecioConOfertas(1000m, 850m, 150m, [new OfertaAplicada(1, "a", 150m)]);

        Assert.Equal(450m, CoberturaDeOfertas.DescuentoDeLinea(resuelto, 3m));
        Assert.False(CoberturaDeOfertas.TieneSoloMultiplos(resuelto.Aplicadas));
    }

    [Fact]
    public void ElResolvedorInformaElMultiploYSiCuentaPorFamilia()
    {
        var resuelto = ResolvedorDeOfertas.Resolver(
            Linea(2m, idFamilia: 7),
            [Oferta(BeneficioDeOferta.DePrecioUnitario(3750m), alcance: AlcanceDeOferta.DeFamilia(7))]);

        var aplicada = Assert.Single(resuelto.Aplicadas);
        Assert.Equal(2m, aplicada.MultiploDe);
        Assert.True(aplicada.PorFamilia);
        Assert.Equal(250m, resuelto.DescuentoUnitario);
    }

    [Fact]
    public void UnaOfertaComunNoInformaMultiplo()
    {
        var resuelto = ResolvedorDeOfertas.Resolver(
            Linea(2m), [Oferta(BeneficioDeOferta.DePrecioUnitario(3750m), soloMultiplos: false)]);

        Assert.Null(Assert.Single(resuelto.Aplicadas).MultiploDe);
    }

    [Fact]
    public void UnaOfertaComunDeFamiliaNoSeInformaPorFamilia()
    {
        var resuelto = ResolvedorDeOfertas.Resolver(
            Linea(2m, idFamilia: 7),
            [Oferta(BeneficioDeOferta.DePrecioUnitario(3750m), alcance: AlcanceDeOferta.DeFamilia(7), soloMultiplos: false)]);

        var aplicada = Assert.Single(resuelto.Aplicadas);
        Assert.Null(aplicada.MultiploDe);
        Assert.False(aplicada.PorFamilia);
    }

    [Fact]
    public void LaFamiliaCuentaLosGruposSobreElDocumentoYLosRepartePorUnidades()
    {
        var aplicada = new OfertaAplicada(1, "familia", 250m, MultiploDe: 2m, PorFamilia: true);
        OfertaAplicada[] aplicadas = [aplicada];

        // 2 + 2 + 1 = 5 unidades de la familia: dos grupos (4 unidades, 1000 de descuento), 200 por unidad de línea.
        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
        [
            new LineaConOfertas(2m, 4000m, aplicadas),
            new LineaConOfertas(2m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
        ]);

        Assert.Equal([400m, 400m, 200m], descuentos);
    }

    /// <summary>Cláusula: el resto del reparto. 1 + 1 + 1 = 3: un grupo, 500 de descuento; cada línea lleva su parte
    /// truncada al peso (166) y la última el resto, así el total del documento es el de un grupo (11500).</summary>
    [Fact]
    public void LaFamiliaRepartePesosEnterosYElRestoVaALaUltimaLinea()
    {
        var aplicada = new OfertaAplicada(1, "familia", 250m, MultiploDe: 2m, PorFamilia: true);
        OfertaAplicada[] aplicadas = [aplicada];

        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
        [
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
        ]);

        Assert.Equal([166m, 166m, 168m], descuentos);
        Assert.Equal(
            11500m,
            CalculadorDeTotales.Calcular(
                [.. descuentos.Select(d => new LineaParaCalcular(1m, 4000m, 250m, DescuentoDeOfertaDeLinea: d))]).Total);
    }

    /// <summary>Cláusula: el reparto proporcional coincide con la clave con la que la oferta de familia ganó la base
    /// (<see cref="ResolvedorDeOfertas.ClaveDeLinea"/>). Tres artículos de la familia, una unidad de cada uno: la "solo
    /// múltiplos" de familia vale 250 × 2 / 3 = 166,67 por unidad y le gana a la común de 150 del tercero. Repartida en
    /// el orden de las líneas, el tercero se quedaba sin descuento (menos que la común que perdió); proporcional, ninguna
    /// línea queda debajo de la común que le ganó.</summary>
    [Fact]
    public void NingunaLineaDeFamiliaQuedaDebajoDeLaComunALaQueLeGano()
    {
        var deFamilia = Oferta(BeneficioDeOferta.DePrecioUnitario(3750m), alcance: AlcanceDeOferta.DeFamilia(7));
        var comunDelTercero = Oferta(
            BeneficioDeOferta.DePrecioUnitario(3850m), cantidadMinima: null, soloMultiplos: false, id: 2,
            alcance: AlcanceDeOferta.DeArticulo(3));
        OfertaCandidata[] candidatas = [deFamilia, comunDelTercero];

        var lineas = new[] { 1, 2, 3 }
            .Select(id => ResolvedorDeOfertas.Resolver(Linea(1m, idArticulo: id, idFamilia: 7, cantidadDeFamilia: 3m), candidatas))
            .ToList();
        Assert.All(lineas, l => Assert.Equal(1, Assert.Single(l.Aplicadas).IdOferta));

        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
            [.. lineas.Select(l => new LineaConOfertas(1m, l.PrecioOriginal, l.Aplicadas))]);

        Assert.All(descuentos, d => Assert.True(d > 150m, $"una línea quedó con {d}, debajo de la común de 150"));
        Assert.Equal(500m, descuentos.Sum());
    }

    /// <summary>Cláusula: el tope del resto. 50 centavos por unidad en grupos de 2, 1 + 1 + 1: un peso de descuento,
    /// ninguna parte llega al peso y el resto no puede cubrir dos unidades de la última línea, que tiene una: medio peso
    /// a la última y medio a la anterior.</summary>
    [Fact]
    public void ElRestoNoCubreMasUnidadesDeLasQueTieneLaLinea()
    {
        OfertaAplicada[] aplicadas = [new OfertaAplicada(1, "familia", 0.5m, MultiploDe: 2m, PorFamilia: true)];

        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
        [
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
        ]);

        Assert.Equal([0m, 0.5m, 0.5m], descuentos);
    }

    /// <summary>Cláusula: el tope por unidad con una cobertura fraccionaria. 3000 de familia en grupos de 2 más una
    /// acumulable de 2000 a 4000, 1 + 1 + 1: cada línea cubre 2/3 de unidad a 4000 (tope) y 1/3 a 2000.</summary>
    [Fact]
    public void ConLaParteDeFamiliaFraccionariaElTopeSigueSiendoPorUnidad()
    {
        OfertaAplicada[] aplicadas =
            [new OfertaAplicada(1, "familia", 3000m, MultiploDe: 2m, PorFamilia: true), new OfertaAplicada(2, "acumulable", 2000m)];

        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
        [
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
        ]);

        Assert.Equal([3333.333333m, 3333.333333m, 3333.333333m], descuentos);
    }

    [Fact]
    public void UnaOfertaSoloMultiplosDeArticuloCuentaSusGruposPorLinea()
    {
        OfertaAplicada[] aplicadas = [new OfertaAplicada(1, "articulo", 250m, MultiploDe: 2m)];

        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
        [
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(3m, 4000m, aplicadas),
        ]);

        Assert.Equal([0m, 500m], descuentos);
    }

    [Fact]
    public void LasUnidadesNegativasNoCubrenNada()
    {
        var resuelto = new PrecioConOfertas(
            4000m, 3750m, 250m, [new OfertaAplicada(1, "a", 250m, MultiploDe: 2m)]);

        Assert.Equal(0m, CoberturaDeOfertas.DescuentoDeLinea(resuelto, -2m));
    }

    [Fact]
    public void SoloMultiplosExigeCantidadMinima()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.ValidarSoloMultiplos(true, null));

        Assert.Equal("solo_multiplos_sin_cantidad_minima", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void SoloMultiplosConCantidadMinimaOApagadoEsValido()
    {
        ReglaDeOfertas.ValidarSoloMultiplos(true, 2m);
        ReglaDeOfertas.ValidarSoloMultiplos(false, null);
    }
}
