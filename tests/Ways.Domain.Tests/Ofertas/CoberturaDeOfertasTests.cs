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
    public void LaFamiliaCuentaLosGruposSobreElDocumentoYElRestoCaeEnLasUltimasLineas()
    {
        var aplicada = new OfertaAplicada(1, "familia", 250m, MultiploDe: 2m, PorFamilia: true);
        OfertaAplicada[] aplicadas = [aplicada];

        // 2 + 2 + 1 = 5 unidades de la familia: dos grupos (4 unidades), la última línea queda sin descuento.
        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
        [
            new LineaConOfertas(2m, 4000m, aplicadas),
            new LineaConOfertas(2m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
        ]);

        Assert.Equal([500m, 500m, 0m], descuentos);
    }

    [Fact]
    public void LaFamiliaArmaGruposConUnidadesDeLineasDistintas()
    {
        var aplicada = new OfertaAplicada(1, "familia", 250m, MultiploDe: 2m, PorFamilia: true);
        OfertaAplicada[] aplicadas = [aplicada];

        // 1 + 1 + 1 = 3: un grupo con las dos primeras líneas, la tercera paga el precio original.
        var descuentos = CoberturaDeOfertas.DescuentosDeLinea(
        [
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
            new LineaConOfertas(1m, 4000m, aplicadas),
        ]);

        Assert.Equal([250m, 250m, 0m], descuentos);
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
