using System.Globalization;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

public class ReglasDeLibroDeTesoreriaTests
{
    private static readonly DateTimeOffset Desde = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnRangoDeExactamente366DiasSeAceptaYUnSegundoMasNo()
    {
        ReglasDeLibroDeTesoreria.ValidarRango(Desde, Desde.AddDays(366));

        var error = Assert.Throws<ErrorDominio>(() => ReglasDeLibroDeTesoreria.ValidarRango(Desde, Desde.AddDays(366).AddSeconds(1)));
        Assert.Equal("rango_demasiado_amplio", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnHastaAnteriorADesdeEsInvalidoYUnRangoDeUnInstanteSeAcepta()
    {
        ReglasDeLibroDeTesoreria.ValidarRango(Desde, Desde);

        var error = Assert.Throws<ErrorDominio>(() => ReglasDeLibroDeTesoreria.ValidarRango(Desde, Desde.AddTicks(-1)));
        Assert.Equal("rango_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("0", "10")]
    [InlineData("0", "0")]
    [InlineData("10", "10")]
    [InlineData("-10", "10")]
    [InlineData("9.99", "10")]
    [InlineData("-9.99", "10")]
    public void ElCeroYLaDiferenciaDentroDeLaToleranciaIncluidoElLimiteExactoSeOcultan(string diferencia, string tolerancia)
    {
        Assert.Null(ReglasDeLibroDeTesoreria.DiferenciaDeArqueoVisible(Dec(diferencia), Dec(tolerancia)));
    }

    [Theory]
    [InlineData("10.01", "10")]
    [InlineData("-10.01", "10")]
    [InlineData("-1500", "10")]
    [InlineData("1300", "10")]
    [InlineData("0.01", "0")]
    [InlineData("-0.01", "0")]
    public void UnaDiferenciaQueSuperaLaToleranciaSeMuestraConSuSigno(string diferencia, string tolerancia)
    {
        Assert.Equal(Dec(diferencia), ReglasDeLibroDeTesoreria.DiferenciaDeArqueoVisible(Dec(diferencia), Dec(tolerancia)));
    }

    [Fact]
    public void ElMedioEfectivoSeReconocePorComportamientoOPorElAnclaFijadaEnElTurno()
    {
        Assert.True(ReglasDeLibroDeTesoreria.EsMedioEfectivo(ComportamientoMedioPago.Efectivo, 1, null));
        Assert.True(ReglasDeLibroDeTesoreria.EsMedioEfectivo(ComportamientoMedioPago.Electronico, 7, 7));
        Assert.False(ReglasDeLibroDeTesoreria.EsMedioEfectivo(ComportamientoMedioPago.Electronico, 7, 8));
        Assert.False(ReglasDeLibroDeTesoreria.EsMedioEfectivo(null, 7, null));
    }

    [Fact]
    public void UnaLineaVaAlEfectivoALaCuentaDeLaEmpresaOQuedaSinCuentaPeroNuncaSeDescarta()
    {
        Assert.Equal(new DestinoDeLinea(DestinoDeLineaDePago.Efectivo, null), ReglasDeLibroDeTesoreria.RutearLineaDePago(true, 5));
        Assert.Equal(new DestinoDeLinea(DestinoDeLineaDePago.Cuenta, 5), ReglasDeLibroDeTesoreria.RutearLineaDePago(false, 5));
        Assert.Equal(new DestinoDeLinea(DestinoDeLineaDePago.SinCuenta, null), ReglasDeLibroDeTesoreria.RutearLineaDePago(false, null));
    }

    [Fact]
    public void ElDesgloseSumaCobroMenosComisionYRetencionesPorMedioYPorCuenta()
    {
        var filas = new[]
        {
            new FilaDeCuentaDeCaja(10, 1, TipoMovimientoCuenta.Cobro, 1000m, 0m),
            new FilaDeCuentaDeCaja(10, 1, TipoMovimientoCuenta.Comision, 0m, 30m),
            new FilaDeCuentaDeCaja(10, 1, TipoMovimientoCuenta.RetencionIibb, 0m, 5m),
            new FilaDeCuentaDeCaja(10, 1, TipoMovimientoCuenta.RetencionIva, 0m, 2m),
            new FilaDeCuentaDeCaja(10, 2, TipoMovimientoCuenta.Cobro, 500m, 0m),
            new FilaDeCuentaDeCaja(20, 3, TipoMovimientoCuenta.Cobro, 200m, 0m),
            new FilaDeCuentaDeCaja(20, 3, TipoMovimientoCuenta.Ajuste, 999m, 0m)
        };

        var desglose = ReglasDeLibroDeTesoreria.ArmarDesglose(filas);

        Assert.Equal([10, 20], desglose.Select(d => d.IdCuentaTesoreria));
        var primera = desglose[0];
        Assert.Equal(963m + 500m, primera.Neto);
        Assert.Equal(new DesgloseDeMedio(1, 1000m, 30m, 7m, 963m), primera.Medios[0]);
        Assert.Equal(new DesgloseDeMedio(2, 500m, 0m, 0m, 500m), primera.Medios[1]);
        Assert.Equal(200m, desglose[1].Neto);
    }

    [Fact]
    public void UnBrutoNegativoInvierteLosLadosSinPerderElSigno()
    {
        var filas = new[]
        {
            new FilaDeCuentaDeCaja(10, 1, TipoMovimientoCuenta.Cobro, 0m, 100m),
            new FilaDeCuentaDeCaja(10, 1, TipoMovimientoCuenta.Comision, 3m, 0m)
        };

        var medio = Assert.Single(ReglasDeLibroDeTesoreria.ArmarDesglose(filas)[0].Medios);

        Assert.Equal(new DesgloseDeMedio(1, -100m, -3m, 0m, -97m), medio);
    }

    private static decimal Dec(string texto) => decimal.Parse(texto, CultureInfo.InvariantCulture);
}
