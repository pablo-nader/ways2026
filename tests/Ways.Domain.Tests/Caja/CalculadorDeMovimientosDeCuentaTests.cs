using Ways.Domain.Caja;

namespace Ways.Domain.Tests.Caja;

public class CalculadorDeMovimientosDeCuentaTests
{
    private static readonly PorcentajesDeCobro SinPorcentajes = new(0m, 0m, 0m, 0m);

    private static decimal EfectoNeto(IEnumerable<ComponenteDeMovimientoDeCuenta> componentes) =>
        componentes.Sum(c => c.Ingreso - c.Egreso);

    private static decimal D(string valor) => decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture);

    private static decimal Redondear(decimal valor) => Math.Round(valor, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public void UnBrutoPositivoConLosCuatroPorcentajesGeneraElCobroYCuatroEgresos()
    {
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(10000m, new PorcentajesDeCobro(4.5m, 1.5m, 0.5m, 2m));

        Assert.Equal(
            [
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.Cobro, 10000m, 0m, 10000m, null),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.Comision, 0m, 450m, 10000m, 4.5m),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.RetencionIibb, 0m, 150m, 10000m, 1.5m),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.RetencionIva, 0m, 50m, 10000m, 0.5m),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.RetencionGanancias, 0m, 200m, 10000m, 2m)
            ],
            componentes);
        Assert.Equal(9150m, EfectoNeto(componentes));
    }

    [Fact]
    public void SinPorcentajesSoloHayCobro()
    {
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(1234.56m, SinPorcentajes);

        var cobro = Assert.Single(componentes);
        Assert.Equal(new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.Cobro, 1234.56m, 0m, 1234.56m, null), cobro);
    }

    [Fact]
    public void UnBrutoCeroNoGeneraComponentesAunConPorcentajes()
    {
        Assert.Empty(CalculadorDeMovimientosDeCuenta.Calcular(0m, new PorcentajesDeCobro(4.5m, 1m, 1m, 1m)));
    }

    [Fact]
    public void UnBrutoNegativoPoneElCobroEnEgresoYLaComisionYLasRetencionesEnIngreso()
    {
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(-1000m, new PorcentajesDeCobro(5m, 2m, 1m, 3m));

        Assert.Equal(
            [
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.Cobro, 0m, 1000m, -1000m, null),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.Comision, 50m, 0m, -1000m, 5m),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.RetencionIibb, 20m, 0m, -1000m, 2m),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.RetencionIva, 10m, 0m, -1000m, 1m),
                new ComponenteDeMovimientoDeCuenta(TipoMovimientoCuenta.RetencionGanancias, 30m, 0m, -1000m, 3m)
            ],
            componentes);
        Assert.Equal(-890m, EfectoNeto(componentes));
    }

    [Theory]
    [InlineData("100.49", "0.1", "0.10")]
    [InlineData("5", "1", "0.05")]
    [InlineData("0.50", "1", "0.01")]
    [InlineData("1.50", "1", "0.02")]
    [InlineData("2.50", "1", "0.03")]
    [InlineData("0.49", "1", "0.00")]
    public void LaComisionPositivaSeRedondeaADosDecimalesConElPuntoMedioHaciaAfueraDelCero(
        string bruto, string porcentaje, string esperado)
    {
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(
            D(bruto), new PorcentajesDeCobro(D(porcentaje), 0m, 0m, 0m));

        var comision = componentes.SingleOrDefault(c => c.Tipo == TipoMovimientoCuenta.Comision);
        Assert.Equal(D(esperado), comision.Egreso);
        Assert.Equal(0m, comision.Ingreso);
    }

    [Theory]
    [InlineData("-0.50", "0.01")]
    [InlineData("-1.50", "0.02")]
    [InlineData("-2.50", "0.03")]
    public void LaComisionNegativaSeRedondeaConElPuntoMedioHaciaAfueraDelCeroYEntraPorIngreso(
        string bruto, string esperado)
    {
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(
            D(bruto), new PorcentajesDeCobro(1m, 0m, 0m, 0m));

        var comision = Assert.Single(componentes, c => c.Tipo == TipoMovimientoCuenta.Comision);
        Assert.Equal(D(esperado), comision.Ingreso);
        Assert.Equal(0m, comision.Egreso);
    }

    [Fact]
    public void CadaComponenteSeRedondeaPorSeparadoYNoSobreLaSuma()
    {
        // 0.50 x 1% = 0.005 -> 0.01 por componente: tres componentes suman 0.03; redondear la suma (0.015) daría 0.02.
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(0.50m, new PorcentajesDeCobro(0m, 1m, 1m, 1m));

        var retenciones = componentes.Where(c => c.Tipo != TipoMovimientoCuenta.Cobro).ToList();
        Assert.Equal(3, retenciones.Count);
        Assert.All(retenciones, c => Assert.Equal(0.01m, c.Egreso));
    }

    [Fact]
    public void LosPorcentajesQueSumanCienDejanElEfectoNetoEnCero()
    {
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(800m, new PorcentajesDeCobro(40m, 30m, 20m, 10m));

        Assert.Equal(5, componentes.Count);
        Assert.Equal(0m, EfectoNeto(componentes));
    }

    [Fact]
    public void UnComponenteQueRedondeaACeroSeOmite()
    {
        // 0.10 x 1% = 0.001 -> 0.00: no hay fila; la comision de 50% (0.05) si.
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(0.10m, new PorcentajesDeCobro(50m, 1m, 0m, 0m));

        Assert.Equal(
            [TipoMovimientoCuenta.Cobro, TipoMovimientoCuenta.Comision],
            componentes.Select(c => c.Tipo));
        Assert.Equal(0.05m, componentes[1].Egreso);
    }

    [Fact]
    public void UnBrutoNegativoTinyConComponenteQueRedondeaACeroSoloDejaElCobro()
    {
        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(-0.10m, new PorcentajesDeCobro(1m, 0m, 0m, 0m));

        var cobro = Assert.Single(componentes);
        Assert.Equal(-0.10m, cobro.Ingreso - cobro.Egreso);
    }

    [Theory]
    [InlineData("1234.57", "3.333", "1.111", "0.777", "2.5")]
    [InlineData("-987.65", "6.5", "0.001", "21", "0.25")]
    [InlineData("0.99", "99.999", "0", "0", "0")]
    public void ElEfectoNetoEsElBrutoMenosLaSumaDeLosComponentesRedondeados(
        string bruto, string comision, string iibb, string iva, string ganancias)
    {
        var porcentajes = new PorcentajesDeCobro(
            D(comision), D(iibb), D(iva), D(ganancias));
        var brutoDecimal = D(bruto);

        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(brutoDecimal, porcentajes);

        var sumaRedondeada =
            Redondear(brutoDecimal * porcentajes.Comision / 100m) + Redondear(brutoDecimal * porcentajes.RetencionIibb / 100m)
            + Redondear(brutoDecimal * porcentajes.RetencionIva / 100m)
            + Redondear(brutoDecimal * porcentajes.RetencionGanancias / 100m);
        Assert.Equal(brutoDecimal - sumaRedondeada, EfectoNeto(componentes));
    }

    [Theory]
    [InlineData("321.45")]
    [InlineData("-321.45")]
    public void CadaComponenteTieneUnSoloLadoNoNegativoYLaBaseEsElBrutoConSigno(string bruto)
    {
        var brutoDecimal = D(bruto);

        var componentes = CalculadorDeMovimientosDeCuenta.Calcular(brutoDecimal, new PorcentajesDeCobro(3.3m, 1.1m, 0.7m, 2.2m));

        Assert.Equal(5, componentes.Count);
        Assert.All(componentes, c =>
        {
            Assert.True(c.Ingreso == 0m || c.Egreso == 0m);
            Assert.True(c.Ingreso >= 0m && c.Egreso >= 0m);
            Assert.Equal(brutoDecimal, c.BaseBruta);
            Assert.Equal(c.Tipo != TipoMovimientoCuenta.Cobro, c.PorcentajeAplicado is not null);
        });
    }
}
