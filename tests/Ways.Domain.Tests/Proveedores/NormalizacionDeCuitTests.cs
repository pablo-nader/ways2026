using Ways.Domain.Proveedores;

namespace Ways.Domain.Tests.Proveedores;

public class NormalizacionDeCuitTests
{
    [Theory]
    [InlineData("20123456789")]
    [InlineData("20-12345678-9")]
    [InlineData("20 12345678 9")]
    [InlineData("20.12345678.9")]
    [InlineData("  20 - 12345678 - 9  ")]
    [InlineData("20\t12345678\t9")]
    public void LasFormasHabitualesDeTipearloDanLosMismosOnceDigitos(string texto) =>
        Assert.Equal("20123456789", NormalizacionDeCuit.Digitos(texto));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2012345678")]
    [InlineData("201234567890")]
    [InlineData("20-12345678")]
    public void SinOnceDigitosNoHayCuit(string? texto) =>
        Assert.Null(NormalizacionDeCuit.Digitos(texto));

    [Theory]
    [InlineData("20-12345678-9a")]
    [InlineData("CUIT 20123456789")]
    [InlineData("20/12345678/9")]
    [InlineData("２0123456789")]
    public void UnCaracterQueNoEsDigitoNiSeparadorInvalidaElCuit(string texto) =>
        Assert.Null(NormalizacionDeCuit.Digitos(texto));
}
