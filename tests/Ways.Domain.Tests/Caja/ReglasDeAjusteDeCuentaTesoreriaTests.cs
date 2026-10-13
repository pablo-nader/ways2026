using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

public class ReglasDeAjusteDeCuentaTesoreriaTests
{
    private static ErrorDominio Falla(Action accion) => Assert.Throws<ErrorDominio>(accion);

    [Fact]
    public void ElConceptoSeRecortaYSeDevuelve()
    {
        Assert.Equal("Conciliación de octubre", ReglasDeAjusteDeCuentaTesoreria.NormalizarConcepto("  Conciliación de octubre \t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnConceptoVacioOSoloEspaciosEsRequerido(string? concepto)
    {
        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.NormalizarConcepto(concepto));

        Assert.Equal("concepto_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElConceptoDeExactamenteElLargoMaximoSeAceptaYUnCaracterMasNo()
    {
        var justo = new string('a', ReglasDeAjusteDeCuentaTesoreria.LargoMaximoDeConcepto);
        Assert.Equal(justo, ReglasDeAjusteDeCuentaTesoreria.NormalizarConcepto(" " + justo + " "));

        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.NormalizarConcepto(justo + "a"));
        Assert.Equal("concepto_muy_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void SinImporteNiSaldoObjetivoSeRechaza()
    {
        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(null, null));

        Assert.Equal("importe_o_saldo_objetivo_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ConImporteYSaldoObjetivoSeRechaza()
    {
        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(10m, 100m));

        Assert.Equal("importe_y_saldo_objetivo_excluyentes", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ConImporteYSaldoObjetivoEnCeroSeRechazaPorLosDos()
    {
        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(0m, 0m));

        Assert.Equal("importe_y_saldo_objetivo_excluyentes", error.Codigo);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("10.001")]
    [InlineData("-0.005")]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    public void UnImporteInvalidoSeRechaza(string texto)
    {
        var importe = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);

        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(importe, null));

        Assert.Equal("importe_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("100.5")]
    [InlineData("-100.5")]
    [InlineData("0.01")]
    [InlineData("-0.01")]
    [InlineData("999999999999.99")]
    [InlineData("-999999999999.99")]
    public void UnImporteConSignoNoCeroDeHastaDosDecimalesSeAcepta(string texto)
    {
        var importe = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);

        ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(importe, null);
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    public void UnSaldoObjetivoInvalidoSeRechaza(string texto)
    {
        var saldo = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);

        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(null, saldo));

        Assert.Equal("saldo_objetivo_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-250.75")]
    [InlineData("1234.56")]
    public void UnSaldoObjetivoValidoSeAcepta(string texto)
    {
        var saldo = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);

        ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(null, saldo);
    }

    [Fact]
    public void UnImportePositivoEntraPorElLadoIngreso()
    {
        var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(saldoActual: 100m, importe: 25.50m, saldoObjetivo: null);

        Assert.Equal(25.50m, ajuste.Importe);
        Assert.Equal(25.50m, ajuste.Ingreso);
        Assert.Equal(0m, ajuste.Egreso);
        Assert.Equal(100m, ajuste.Inicio);
        Assert.Equal(125.50m, ajuste.Final);
    }

    [Fact]
    public void UnImporteNegativoSaleDeLaCuentaPorElLadoEgreso()
    {
        var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(saldoActual: 100m, importe: -30m, saldoObjetivo: null);

        Assert.Equal(-30m, ajuste.Importe);
        Assert.Equal(0m, ajuste.Ingreso);
        Assert.Equal(30m, ajuste.Egreso);
        Assert.Equal(70m, ajuste.Final);
    }

    [Fact]
    public void UnImporteNegativoPuedeDejarElSaldoEnNegativo()
    {
        var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(saldoActual: 10m, importe: -30m, saldoObjetivo: null);

        Assert.Equal(-20m, ajuste.Final);
    }

    [Fact]
    public void ElSaldoObjetivoMayorAlActualCalculaUnIngresoPorLaDiferencia()
    {
        var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(saldoActual: 100m, importe: null, saldoObjetivo: 142.25m);

        Assert.Equal(42.25m, ajuste.Importe);
        Assert.Equal(42.25m, ajuste.Ingreso);
        Assert.Equal(0m, ajuste.Egreso);
        Assert.Equal(142.25m, ajuste.Final);
    }

    [Fact]
    public void ElSaldoObjetivoMenorAlActualCalculaUnEgresoPorLaDiferencia()
    {
        var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(saldoActual: 100m, importe: null, saldoObjetivo: -5m);

        Assert.Equal(-105m, ajuste.Importe);
        Assert.Equal(0m, ajuste.Ingreso);
        Assert.Equal(105m, ajuste.Egreso);
        Assert.Equal(-5m, ajuste.Final);
    }

    [Fact]
    public void ElSaldoObjetivoIgualAlActualEsUnConflictoYNoEscribeNada()
    {
        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.Resolver(100m, null, 100m));

        Assert.Equal("ajuste_sin_diferencia", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public void UnSaldoResultanteFueraDeNumeric14_2SeRechaza()
    {
        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.Resolver(999_999_999_999m, 5m, null));

        Assert.Equal("saldo_resultante_fuera_de_rango", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnSaldoResultanteExactamenteEnElTopeSeAcepta()
    {
        var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(999_999_999_999m, 0.99m, null);

        Assert.Equal(999_999_999_999.99m, ajuste.Final);
    }

    [Fact]
    public void UnSaldoObjetivoNegativoDeLaDiferenciaMaximaFueraDeRangoSeRechaza()
    {
        var error = Falla(() => ReglasDeAjusteDeCuentaTesoreria.Resolver(-999_999_999_999m, -5m, null));

        Assert.Equal("saldo_resultante_fuera_de_rango", error.Codigo);
    }
}
