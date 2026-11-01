using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

public class TerminalPointMercadoPagoTests
{
    [Theory]
    [InlineData("PAX_A910__8701")]
    [InlineData("NEWLAND_N950__N950NCB123")]
    [InlineData("  PAX_A910__8701  ")]
    public void UnaTerminalConLaFormaTipoSerieSeAcepta(string id) =>
        Assert.Equal(id.Trim(), TerminalPointMercadoPago.Normalizar(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PAX_A910_8701")]
    [InlineData("__8701")]
    [InlineData("PAX_A910__")]
    [InlineData("pax_a910__8701")]
    [InlineData("PAX_A910__87 01")]
    [InlineData("PAX_A910__87-01")]
    [InlineData("PAX_A910__87\n01")]
    [InlineData("PAX A910__8701")]
    public void UnIdentificadorQueNoTieneLaFormaSeRechaza(string? id) =>
        Assert.Null(TerminalPointMercadoPago.Normalizar(id));

    [Fact]
    public void ElLargoMaximoEsInclusivo()
    {
        var justo = "A__" + new string('b', TerminalPointMercadoPago.LargoMaximo - 3);
        Assert.Equal(TerminalPointMercadoPago.LargoMaximo, justo.Length);
        Assert.Equal(justo, TerminalPointMercadoPago.Normalizar(justo));
        Assert.Null(TerminalPointMercadoPago.Normalizar(justo + "b"));
    }

    [Theory]
    [InlineData("PDV", true)]
    [InlineData("pdv", true)]
    [InlineData(" PDV ", true)]
    [InlineData("STANDALONE", false)]
    [InlineData("UNDEFINED", false)]
    [InlineData("SELF_SERVICE", false)]
    [InlineData(null, false)]
    public void SoloPdvCuentaComoModoPdv(string? modo, bool esperado) =>
        Assert.Equal(esperado, TerminalPointMercadoPago.EstaEnPdv(modo));
}
