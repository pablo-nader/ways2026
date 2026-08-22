using Ways.Application.Fiscal;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice3 (tasks 3.9-3.14, design.md: Totals Composition, decisión 11, targets 40-45):
/// <see cref="ComposicionDeTotalesFiscales"/> sobre un snapshot congelado por línea — el bucketing
/// es por <c>CodigoAfip IS NULL</c> + nombre, NUNCA por porcentaje (el mutante de la D11: 0%,
/// Exento y No gravado comparten porcentaje 0.00).
/// </summary>
public class ComposicionDeTotalesFiscalesTests
{
    // 21% (código 5): línea de 121.00 con IVA incluido → neto 100.00, iva 21.00.
    private static readonly LineaFiscal Linea21A = new(1, "21%", 5, 21m, 121.00m);
    private static readonly LineaFiscal Linea21B = new(1, "21%", 5, 21m, 60.50m); // neto 50.00 iva 10.50
    private static readonly LineaFiscal Linea10_5 = new(2, "10.5%", 4, 10.5m, 110.50m); // neto 100.00 iva 10.50
    private static readonly LineaFiscal LineaExento = new(3, "Exento", null, 0m, 50.00m);
    private static readonly LineaFiscal LineaNoGravado = new(4, "No gravado", null, 0m, 30.00m);
    private static readonly LineaFiscal Linea0Porciento = new(5, "0%", 3, 0m, 25.00m); // neto 25.00 iva 0.00

    [Fact]
    public void UnaFacturaMixtaExcluyeExentoYNoGravadoDeIva()
    {
        var totales = ComposicionDeTotalesFiscales.Componer(
            [Linea21A, Linea10_5, LineaExento, LineaNoGravado]);

        Assert.Equal(2, totales.Iva.Count); // target 40
    }

    [Fact]
    public void ImpOpExRecibeElExentoEImpTotConcElNoGravadoConMontosDistintos()
    {
        var totales = ComposicionDeTotalesFiscales.Componer(
            [Linea21A, Linea10_5, LineaExento, LineaNoGravado]);

        Assert.Equal(50.00m, totales.ImpOpEx); // target 41
        Assert.Equal(30.00m, totales.ImpTotConc);
        Assert.NotEqual(totales.ImpOpEx, totales.ImpTotConc);
    }

    [Fact]
    public void DosLineasDeLaMismaAlicuotaColapsanEnUnaEntradaConLosMontosSumados()
    {
        var totales = ComposicionDeTotalesFiscales.Componer([Linea21A, Linea21B]);

        var entrada = Assert.Single(totales.Iva); // target 42
        Assert.Equal((short)5, entrada.Id);
        Assert.Equal(150.00m, entrada.BaseImp); // 100.00 + 50.00
        Assert.Equal(31.50m, entrada.Importe); // 21.00 + 10.50
    }

    [Fact]
    public void ImpTotalEsLaSumaExactaDeLosCincoTerminos()
    {
        var totales = ComposicionDeTotalesFiscales.Componer(
            [Linea21A, Linea10_5, LineaExento, LineaNoGravado]);

        var sumaEsperada = totales.ImpNeto + totales.ImpIVA + totales.ImpOpEx + totales.ImpTotConc + totales.ImpTrib;
        Assert.Equal(sumaEsperada, totales.ImpTotal); // target 43

        // Pin explícito de los valores, no solo de la identidad algebraica (rule 3: un mutante que
        // rompa AMBOS lados por igual sobreviviría a un assert puramente relativo).
        Assert.Equal(311.50m, totales.ImpTotal); // 121.00 + 110.50 + 50.00 + 30.00
    }

    [Fact]
    public void El0PorCientoVaAIvaConCodigo3_NoAImpOpEx()
    {
        var totales = ComposicionDeTotalesFiscales.Componer([Linea0Porciento]);

        var entrada = Assert.Single(totales.Iva); // target 44
        Assert.Equal((short)3, entrada.Id);
        Assert.Equal(25.00m, entrada.BaseImp);
        Assert.Equal(0.00m, entrada.Importe);
        Assert.Equal(0.00m, totales.ImpOpEx);
    }

    [Fact]
    public void UnaAlicuotaNullCodedSinMapeoConocidoLanzaEnVezDeFacturar()
    {
        var lineaSinMapeo = new LineaFiscal(6, "Percepción especial", null, 0m, 10.00m);

        var error = Assert.Throws<ErrorDominio>(
            () => ComposicionDeTotalesFiscales.Componer([lineaSinMapeo])); // target 45

        Assert.Equal("alicuota_sin_mapeo_afip", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }
}
