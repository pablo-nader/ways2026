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
    public void SinConfigurarElBackoffDeLaRenovacionEsDeQuinceMinutos()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), new OpcionesDeMercadoPago().BackoffDeRenovacion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void UnBackoffNoPositivoVuelveAlPorDefecto(int minutos)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(15), new OpcionesDeMercadoPago { MinutosDeBackoffDeRenovacion = minutos }.BackoffDeRenovacion);
    }

    [Fact]
    public void ElBackoffConfiguradoSeRespeta()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(3), new OpcionesDeMercadoPago { MinutosDeBackoffDeRenovacion = 3 }.BackoffDeRenovacion);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-14, true)]
    [InlineData(-15, false)]
    [InlineData(-16, false)]
    [InlineData(5, false)]
    public void UnIntentoFallidoFrenaElSiguienteSoloHastaQuePaseElBackoffYUnoFechadoEnElFuturoNoFrena(int minutosDesdeAhora, bool esperado)
    {
        var backoff = TimeSpan.FromMinutes(15);

        Assert.Equal(
            esperado,
            ServicioDeRenovacionDeConexionesMercadoPago.EstaEnBackoff(Ahora.AddMinutes(minutosDesdeAhora), Ahora, backoff));
    }

    [Fact]
    public void SinIntentoPreviamenteAnotadoNoHayBackoff()
    {
        Assert.False(ServicioDeRenovacionDeConexionesMercadoPago.EstaEnBackoff(null, Ahora, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void ElLimiteDelBackoffEsExclusivoYUnTickAntesTodaviaFrena()
    {
        var backoff = TimeSpan.FromMinutes(15);
        var intento = Ahora - backoff;

        Assert.False(ServicioDeRenovacionDeConexionesMercadoPago.EstaEnBackoff(intento, Ahora, backoff));
        Assert.True(ServicioDeRenovacionDeConexionesMercadoPago.EstaEnBackoff(intento.AddTicks(1), Ahora, backoff));
    }

    [Fact]
    public void ElTokenDelVendedorNoImprimeElAccessTokenAlConvertirseEnTexto()
    {
        var token = new TokenDelVendedorMercadoPago(3, 99, "APP_USR-secreto", Ahora);

        Assert.DoesNotContain("APP_USR-secreto", token.ToString());
    }
}
