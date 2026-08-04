using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// stage-5-pos-ventas, Slice 2 (task 2.11, design decisión 7, spec: codigos-barra / Scan
/// Resolution Rule) — exhaustivo: boundary de 6/7/13 dígitos, sintaxis <c>N*codigo</c>,
/// cantidad vacía/0, entrada basura. Pura, sin base de datos: <see cref="ParserDeEscaneo"/> no
/// sabe si el código existe, solo a qué columna apunta.
/// </summary>
public class ParserDeEscaneoTests
{
    // ---- boundary de longitud (regla I.2: < 7 -> codigo_interno, >= 7 -> codigos_barra) ------

    [Fact]
    public void UnCodigoDeSeisDigitosResuelvePorCodigoInterno()
    {
        var resultado = ParserDeEscaneo.Parsear("123456");

        Assert.Equal(ObjetivoDeEscaneo.CodigoInterno, resultado.Objetivo);
        Assert.Equal("123456", resultado.Codigo);
    }

    [Fact]
    public void UnCodigoDeSieteDigitosResuelvePorCodigosBarra()
    {
        var resultado = ParserDeEscaneo.Parsear("1234567");

        Assert.Equal(ObjetivoDeEscaneo.CodigoBarra, resultado.Objetivo);
        Assert.Equal("1234567", resultado.Codigo);
    }

    [Fact]
    public void UnCodigoDeTreceDigitosResuelvePorCodigosBarra()
    {
        var resultado = ParserDeEscaneo.Parsear("7790001234567");

        Assert.Equal(ObjetivoDeEscaneo.CodigoBarra, resultado.Objetivo);
        Assert.Equal("7790001234567", resultado.Codigo);
    }

    [Fact]
    public void UnCodigoDeUnSoloDigitoResuelvePorCodigoInterno()
    {
        var resultado = ParserDeEscaneo.Parsear("4");

        Assert.Equal(ObjetivoDeEscaneo.CodigoInterno, resultado.Objetivo);
    }

    // ---- sintaxis N*codigo ---------------------------------------------------------------

    [Fact]
    public void UnPrefijoDeCantidadCargaLaCantidadIndicada()
    {
        var resultado = ParserDeEscaneo.Parsear("3*7790001234567");

        Assert.Equal(3m, resultado.Cantidad);
        Assert.Equal("7790001234567", resultado.Codigo);
        Assert.Equal(ObjetivoDeEscaneo.CodigoBarra, resultado.Objetivo);
    }

    [Fact]
    public void UnPrefijoDeCantidadConDecimalesSePreserva()
    {
        var resultado = ParserDeEscaneo.Parsear("1.5*42");

        Assert.Equal(1.5m, resultado.Cantidad);
        Assert.Equal("42", resultado.Codigo);
    }

    [Fact]
    public void UnPrefijoDeCantidadAplicaTambienAUnCodigoInterno()
    {
        var resultado = ParserDeEscaneo.Parsear("2*42");

        Assert.Equal(2m, resultado.Cantidad);
        Assert.Equal(ObjetivoDeEscaneo.CodigoInterno, resultado.Objetivo);
    }

    [Fact]
    public void ConEspaciosAlrededorDelAsteriscoSeIgnoranAlTrimear()
    {
        var resultado = ParserDeEscaneo.Parsear(" 3 * 7790001234567 ");

        Assert.Equal(3m, resultado.Cantidad);
        Assert.Equal("7790001234567", resultado.Codigo);
    }

    // ---- cantidad vacía/0 default a 1 -----------------------------------------------------

    [Fact]
    public void SinAsteriscoLaCantidadEsUno()
    {
        var resultado = ParserDeEscaneo.Parsear("42");

        Assert.Equal(1m, resultado.Cantidad);
    }

    [Fact]
    public void UnPrefijoVacioAntesDelAsteriscoDefaultAUno()
    {
        var resultado = ParserDeEscaneo.Parsear("*7790001234567");

        Assert.Equal(1m, resultado.Cantidad);
        Assert.Equal("7790001234567", resultado.Codigo);
    }

    [Fact]
    public void UnPrefijoCeroDefaultAUno()
    {
        var resultado = ParserDeEscaneo.Parsear("0*7790001234567");

        Assert.Equal(1m, resultado.Cantidad);
    }

    [Fact]
    public void UnPrefijoNegativoDefaultAUno()
    {
        var resultado = ParserDeEscaneo.Parsear("-3*7790001234567");

        Assert.Equal(1m, resultado.Cantidad);
    }

    // ---- entrada basura: nunca revienta, cae a un código plano ---------------------------

    [Fact]
    public void UnPrefijoNoNumericoDefaultAUnoYElCodigoEsElRestoDeLaEntrada()
    {
        var resultado = ParserDeEscaneo.Parsear("abc*123");

        Assert.Equal(1m, resultado.Cantidad);
        Assert.Equal("123", resultado.Codigo);
        Assert.Equal(ObjetivoDeEscaneo.CodigoInterno, resultado.Objetivo);
    }

    [Fact]
    public void UnaEntradaSinAsteriscoNiDigitosSeUsaTalCualComoCodigo()
    {
        var resultado = ParserDeEscaneo.Parsear("basura");

        Assert.Equal(1m, resultado.Cantidad);
        Assert.Equal("basura", resultado.Codigo);
        Assert.Equal(ObjetivoDeEscaneo.CodigoInterno, resultado.Objetivo);
    }

    // ---- validación de entrada -------------------------------------------------------------

    [Fact]
    public void UnaEntradaVaciaLanzaErrorDeDominio()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ParserDeEscaneo.Parsear(""));
        Assert.Equal("escaneo_invalido", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnaEntradaSoloDeEspaciosLanzaErrorDeDominio()
    {
        Assert.Throws<ErrorDominio>(() => ParserDeEscaneo.Parsear("   "));
    }

    [Fact]
    public void UnaEntradaNulaLanzaErrorDeDominio()
    {
        Assert.Throws<ErrorDominio>(() => ParserDeEscaneo.Parsear(null));
    }

    [Fact]
    public void UnAsteriscoSinCodigoDespuesLanzaErrorDeDominio()
    {
        Assert.Throws<ErrorDominio>(() => ParserDeEscaneo.Parsear("3*"));
    }
}
