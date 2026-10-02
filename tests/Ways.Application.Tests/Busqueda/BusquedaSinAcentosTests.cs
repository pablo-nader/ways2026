using Ways.Application.Busqueda;

namespace Ways.Application.Tests.Busqueda;

public class BusquedaSinAcentosTests
{
    [Theory]
    [InlineData("jose", "%jose%")]
    [InlineData("José Ñandú", "%José Ñandú%")]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"c\d", @"%c\\d%")]
    [InlineData(@"\%_", @"%\\\%\_%")]
    [InlineData("", "%%")]
    public void PatronDeContieneEscapaPorcentajeGuionBajoYBarraInvertida(string termino, string esperado) =>
        Assert.Equal(esperado, BusquedaSinAcentos.PatronDeContiene(termino));

    [Fact]
    public void CoincideFueraDeUnaConsultaEfFallaEnVezDeDevolverUnaRespuestaEngañosa() =>
        Assert.Throws<NotSupportedException>(() => BusquedaSinAcentos.Coincide("José", "%jose%"));
}
