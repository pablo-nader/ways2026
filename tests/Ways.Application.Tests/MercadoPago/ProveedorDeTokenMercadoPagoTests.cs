using Ways.Application.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

public class ProveedorDeTokenMercadoPagoTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Margen = TimeSpan.FromDays(1);

    [Fact]
    public void SinConfigurarElMargenEsDeUnDiaYEsDistintoDeLaVentanaDeRenovacionEnSegundoPlano()
    {
        var opciones = new OpcionesDeMercadoPago();

        Assert.Equal(TimeSpan.FromDays(1), opciones.MargenDelToken);
        Assert.NotEqual(opciones.VentanaDeRenovacion, opciones.MargenDelToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void UnMargenNoPositivoVuelveAlPorDefecto(int horas)
    {
        Assert.Equal(TimeSpan.FromHours(24), new OpcionesDeMercadoPago { HorasDeMargenDelToken = horas }.MargenDelToken);
    }

    [Fact]
    public void ElMargenConfiguradoSeRespeta()
    {
        Assert.Equal(TimeSpan.FromHours(6), new OpcionesDeMercadoPago { HorasDeMargenDelToken = 6 }.MargenDelToken);
    }

    [Theory]
    [InlineData(-60, true)]
    [InlineData(0, true)]
    [InlineData(1439, true)]
    [InlineData(1440, true)]
    [InlineData(1441, false)]
    [InlineData(60 * 24 * 20, false)]
    public void UnTokenQueVenceDentroDelMargenOYaVencioNecesitaRenovarseAntesDeUsarse(int minutosHastaElVencimiento, bool esperado)
    {
        Assert.Equal(
            esperado,
            ProveedorDeTokenMercadoPago.NecesitaRenovarse(Ahora.AddMinutes(minutosHastaElVencimiento), Ahora, Margen));
    }

    [Fact]
    public void ElLimiteDelMargenEsInclusivoYUnTickDespuesYaNoRenueva()
    {
        var limite = Ahora + Margen;

        Assert.True(ProveedorDeTokenMercadoPago.NecesitaRenovarse(limite, Ahora, Margen));
        Assert.False(ProveedorDeTokenMercadoPago.NecesitaRenovarse(limite.AddTicks(1), Ahora, Margen));
    }

    [Theory]
    [InlineData(-10, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(600, true)]
    public void UnTokenVencidoOPorVencerEnMenosDeLaHolguraNuncaSeEntrega(int minutosHastaElVencimiento, bool esperado)
    {
        Assert.Equal(esperado, ProveedorDeTokenMercadoPago.EsUsable(Ahora.AddMinutes(minutosHastaElVencimiento), Ahora));
    }

    [Fact]
    public void ElLimiteDeLaHolguraNoEsUsableYUnTickDespuesSi()
    {
        var limite = Ahora + ProveedorDeTokenMercadoPago.HolguraDeUso;

        Assert.False(ProveedorDeTokenMercadoPago.EsUsable(limite, Ahora));
        Assert.True(ProveedorDeTokenMercadoPago.EsUsable(limite.AddTicks(1), Ahora));
    }

    [Fact]
    public void ElTokenDelVendedorNoImprimeElAccessTokenAlConvertirseEnTexto()
    {
        var token = new TokenDelVendedorMercadoPago(3, 99, "APP_USR-secreto", Ahora);

        Assert.DoesNotContain("APP_USR-secreto", token.ToString());
    }
}
