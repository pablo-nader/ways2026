using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

public class NumeroDeComprobanteDeCompraTests
{
    [Theory]
    [InlineData("3-12345", "00003-00012345")]
    [InlineData("0003-00012345", "00003-00012345")]
    [InlineData("00003-00012345", "00003-00012345")]
    [InlineData("  3 - 12345  ", "00003-00012345")]
    [InlineData("3 -12345", "00003-00012345")]
    [InlineData("99999-99999999", "99999-99999999")]
    [InlineData("1-1", "00001-00000001")]
    [InlineData("000000003-000000000012345", "00003-00012345")]
    [InlineData("000300012345", "00003-00012345")]
    [InlineData("0000300012345", "00003-00012345")]
    [InlineData("  000300012345  ", "00003-00012345")]
    public void UnNumeroInterpretableSeGuardaEnLaForma_PPPPP_NNNNNNNN(string entrada, string esperado)
    {
        Assert.True(NumeroDeComprobanteDeCompra.TryNormalizar(entrada, out var canonico));
        Assert.Equal(esperado, canonico);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A-12345")]
    [InlineData("3-12345A")]
    [InlineData("FA 3-12345")]
    [InlineData("3-12-345")]
    [InlineData("3--12345")]
    [InlineData("-12345")]
    [InlineData("3-")]
    [InlineData("-")]
    [InlineData("123456-1")]
    [InlineData("1-123456789")]
    [InlineData("0-1")]
    [InlineData("1-0")]
    [InlineData("00000-00000000")]
    [InlineData("3 3-12345")]
    [InlineData("3-123 45")]
    [InlineData("3.5-12345")]
    [InlineData("3/12345")]
    [InlineData("12345")]
    [InlineData("00012345")]
    [InlineData("00030001234")]
    [InlineData("00003000123456")]
    [InlineData("000000000000")]
    [InlineData("٠٠٠٣-٠٠٠١٢٣٤٥")]
    public void UnNumeroQueNoSePuedeInterpretarSeRechaza(string? entrada)
    {
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar(entrada, out var canonico));
        Assert.Equal(string.Empty, canonico);
    }

    [Fact]
    public void ElPuntoDeVentaAdmiteCincoDigitosSignificativosYNoSeis()
    {
        Assert.True(NumeroDeComprobanteDeCompra.TryNormalizar("12345-1", out _));
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("123456-1", out _));
        Assert.True(NumeroDeComprobanteDeCompra.TryNormalizar("0012345-1", out _));
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("0123456-1", out _));
    }

    [Fact]
    public void ElNumeroAdmiteOchoDigitosSignificativosYNoNueve()
    {
        Assert.True(NumeroDeComprobanteDeCompra.TryNormalizar("1-12345678", out _));
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("1-123456789", out _));
        Assert.True(NumeroDeComprobanteDeCompra.TryNormalizar("1-0012345678", out _));
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("1-0123456789", out _));
    }

    [Fact]
    public void ElCeroNoEsPuntoDeVentaNiNumero()
    {
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("0-5", out _));
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("5-0", out _));
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("00000-5", out _));
        Assert.False(NumeroDeComprobanteDeCompra.TryNormalizar("5-00000000", out _));
        Assert.True(NumeroDeComprobanteDeCompra.TryNormalizar("1-5", out _));
    }

    [Theory]
    [InlineData("0003-00012345", 3, 12345)]
    [InlineData("00003-00012345", 3, 12345)]
    [InlineData("3-12345", 3, 12345)]
    [InlineData("  3 - 12345 ", 3, 12345)]
    [InlineData("FA 0003-00012345", 3, 12345)]
    [InlineData("A-0001-00000007", 1, 7)]
    [InlineData("0003-00012345 (original)", 3, 12345)]
    [InlineData("12-34-56", 1234, 56)]
    [InlineData("0-0", 0, 0)]
    public void LosSegmentosSeLeenDeCualquierValorGuardadoPartiendoEnElUltimoGuion(
        string guardado, long puntoDeVenta, long numero)
    {
        Assert.True(NumeroDeComprobanteDeCompra.TryObtenerSegmentos(guardado, out var segmentos));
        Assert.Equal((puntoDeVenta, numero), segmentos);
    }

    [Fact]
    public void ElUltimoGuionSeparaElPuntoDeVentaDelNumero()
    {
        Assert.True(NumeroDeComprobanteDeCompra.TryObtenerSegmentos("1-2-3", out var segmentos));
        Assert.Equal((12L, 3L), segmentos);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("000300012345")]
    [InlineData("FA")]
    [InlineData("-12345")]
    [InlineData("0003-")]
    [InlineData("FA-12345")]
    [InlineData("0003-ABC")]
    [InlineData("-")]
    [InlineData("1234567890123456789-1")]
    [InlineData("1-1234567890123456789")]
    public void UnValorGuardadoSinGuionOSinDigitosEnUnLadoNoEsComparable(string? guardado)
    {
        Assert.False(NumeroDeComprobanteDeCompra.TryObtenerSegmentos(guardado, out var segmentos));
        Assert.Equal((0L, 0L), segmentos);
    }

    [Theory]
    [InlineData("3-12345", "0003-00012345")]
    [InlineData("00003-00012345", "3 - 12345")]
    public void ElNormalizadoYLosSegmentosDeUnMismoNumeroCoinciden(string a, string b)
    {
        Assert.True(NumeroDeComprobanteDeCompra.TryNormalizar(a, out var canonico));
        Assert.True(NumeroDeComprobanteDeCompra.TryObtenerSegmentos(canonico, out var deA));
        Assert.True(NumeroDeComprobanteDeCompra.TryObtenerSegmentos(b, out var deB));
        Assert.Equal(deA, deB);
    }
}
