using Ways.Domain.Articulos;

namespace Ways.Domain.Tests.Articulos;

public class ReglaDeGtinTests
{
    [Theory]
    [InlineData("7790895000782")]
    [InlineData("4006381333931")]
    [InlineData("96385074")]
    [InlineData("036000291452")]
    [InlineData("10012345678902")]
    public void UnGtinConLargoYDigitoVerificadorCorrectosNoTieneMotivo(string codigo)
    {
        Assert.Null(ReglaDeGtin.MotivoDeNoEstandar(codigo));
    }

    [Theory]
    [InlineData("7790895000783")]
    [InlineData("96385075")]
    [InlineData("036000291453")]
    [InlineData("10012345678903")]
    public void UnDigitoVerificadorIncorrectoSeInformaComoPosibleErrorDeTipeo(string codigo)
    {
        var motivo = ReglaDeGtin.MotivoDeNoEstandar(codigo);

        Assert.NotNull(motivo);
        Assert.Contains("dígito verificador", motivo);
    }

    [Theory]
    [InlineData("77908950A0782")]
    [InlineData("ABC-123")]
    [InlineData("7790 895000782")]
    public void UnCodigoConCaracteresQueNoSonDigitosSeInformaComoNoEstandar(string codigo)
    {
        var motivo = ReglaDeGtin.MotivoDeNoEstandar(codigo);

        Assert.NotNull(motivo);
        Assert.Contains("no son dígitos", motivo);
    }

    [Theory]
    [InlineData("1", "1 dígito ")]
    [InlineData("1234567", "7 dígitos ")]
    [InlineData("123456789", "9 dígitos ")]
    [InlineData("779089500078200", "15 dígitos ")]
    public void UnLargoFueraDe8_12_13_14SeInformaConElLargoRecibido(string codigo, string esperado)
    {
        var motivo = ReglaDeGtin.MotivoDeNoEstandar(codigo);

        Assert.NotNull(motivo);
        Assert.Contains(esperado, motivo);
        Assert.Contains("8, 12, 13 o 14", motivo);
    }


    [Fact]
    public void ElVacioNoSeEvalua()
    {
        Assert.Null(ReglaDeGtin.MotivoDeNoEstandar(string.Empty));
    }
}
