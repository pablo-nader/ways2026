using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// stage-5-pos-ventas, Slice 2 (task 2.10, design: API Surface — <c>PPPP-NNNNNNNN</c>).
/// </summary>
public class NumeroDeComprobanteTests
{
    [Fact]
    public void RellenaConCerosLosDosSegmentos()
    {
        Assert.Equal("0001-00000001", NumeroDeComprobante.Formatear(1, 1));
    }

    [Fact]
    public void UnPuntoDeVentaDeDosDigitosSeRellenaAcuatro()
    {
        Assert.Equal("0042-00000007", NumeroDeComprobante.Formatear(42, 7));
    }

    [Fact]
    public void UnNumeroDeVariosDigitosSeRellenaAOcho()
    {
        Assert.Equal("0001-00012345", NumeroDeComprobante.Formatear(1, 12345));
    }

    [Fact]
    public void ElLimiteExactoDeCuatroDigitosNoAgregaCeros()
    {
        Assert.Equal("9999-00000001", NumeroDeComprobante.Formatear(9999, 1));
    }

    [Fact]
    public void ElLimiteExactoDeOchoDigitosNoAgregaCeros()
    {
        Assert.Equal("0001-99999999", NumeroDeComprobante.Formatear(1, 99999999L));
    }

    /// <summary>Design's Open Questions: sin bound superior — un id o número que excedan
    /// 4/8 dígitos imprimen más dígitos en vez de truncarse (harmless mientras TX/NCX no
    /// sean fiscales).</summary>
    [Fact]
    public void UnPuntoDeVentaDeMasDeCuatroDigitosNoSeTrunca()
    {
        Assert.Equal("10000-00000001", NumeroDeComprobante.Formatear(10000, 1));
    }

    [Fact]
    public void UnNumeroDeMasDeOchoDigitosNoSeTrunca()
    {
        Assert.Equal("0001-100000000", NumeroDeComprobante.Formatear(1, 100000000L));
    }

    [Fact]
    public void ElPuntoDeVentaCeroEsPosibleParaElInputAunqueElAsignadorNuncaLoUse()
    {
        Assert.Equal("0000-00000001", NumeroDeComprobante.Formatear(0, 1));
    }
}
