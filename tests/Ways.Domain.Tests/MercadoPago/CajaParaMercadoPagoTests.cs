using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

public class CajaParaMercadoPagoTests
{
    [Theory]
    [InlineData(1, "WAYSPV1")]
    [InlineData(2_147_483_647, "WAYSPV2147483647")]
    public void ElExternalIdSonLetrasYDigitosYCabeEnElLimite(int idPuntoVenta, string esperado)
    {
        var externalId = CajaParaMercadoPago.ExternalIdDe(idPuntoVenta);

        Assert.Equal(esperado, externalId);
        Assert.All(externalId, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.True(externalId.Length <= CajaParaMercadoPago.LargoMaximoExternalId);
    }

    [Fact]
    public void ElExternalIdDeUnaCajaNoChocaConElDeUnaSucursal() =>
        Assert.NotEqual(LocalParaMercadoPago.ExternalIdDe(7), CajaParaMercadoPago.ExternalIdDe(7));

    [Theory]
    [InlineData("Caja 1", "Caja 1")]
    [InlineData("Caja Ñandú #1 (centro)", "Caja Nandu 1 centro")]
    [InlineData("Caja. 1, Centro", "Caja 1 Centro")]
    [InlineData("Caja_1 - Centro", "Caja_1 - Centro")]
    [InlineData("###", "")]
    [InlineData("   ", "")]
    public void ElNombreSoloConservaLetrasDigitosEspacioGuionYGuionBajo(string entrada, string esperado) =>
        Assert.Equal(esperado, CajaParaMercadoPago.NombreDe(entrada));

    [Fact]
    public void ElNombreSeCortaEnElLargoDeMercadoPagoSinEspacioAlFinal()
    {
        var nombre = CajaParaMercadoPago.NombreDe(new string('a', 44) + " " + new string('b', 20));

        Assert.True(nombre.Length <= CajaParaMercadoPago.LargoMaximoNombre);
        Assert.Equal(new string('a', 44), nombre);
    }
}
