using Ways.Application.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>Los valores por defecto del timeout de las llamadas a WSFE (8 s), de la firma de WSAA (30 s) y del presupuesto de un
/// lote (20 s); que un valor no positivo o NaN vuelve al por defecto en vez de dejar al HttpClient sin timeout o con uno inválido;
/// y que un valor absurdo (infinito, enorme, ínfimo) se acota en vez de lanzar al usarse.</summary>
public class OpcionesFiscalesTests
{
    [Fact]
    public void SinConfigurarLosValoresSonLosPorDefecto()
    {
        var opciones = new OpcionesFiscales();

        Assert.Equal(TimeSpan.FromSeconds(8), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(30), opciones.TimeoutDeWsaa);
        Assert.Equal(TimeSpan.FromSeconds(20), opciones.PresupuestoDeLote);
    }

    [Fact]
    public void UnValorConfiguradoSeRespetaEnSegundosFraccionarios()
    {
        var opciones = new OpcionesFiscales
        {
            TimeoutDeLlamadaSegundos = 0.25, TimeoutDeWsaaSegundos = 45, PresupuestoDeLoteSegundos = 3
        };

        Assert.Equal(TimeSpan.FromMilliseconds(250), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(45), opciones.TimeoutDeWsaa);
        Assert.Equal(TimeSpan.FromSeconds(3), opciones.PresupuestoDeLote);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void UnValorNoPositivoVuelveAlPorDefecto(double segundos)
    {
        var opciones = new OpcionesFiscales
        {
            TimeoutDeLlamadaSegundos = segundos, TimeoutDeWsaaSegundos = segundos, PresupuestoDeLoteSegundos = segundos
        };

        Assert.Equal(TimeSpan.FromSeconds(8), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(30), opciones.TimeoutDeWsaa);
        Assert.Equal(TimeSpan.FromSeconds(20), opciones.PresupuestoDeLote);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(1e12)]
    [InlineData(121)]
    public void UnValorMayorQueElTopeSeAcotaAlTope(double segundos)
    {
        var opciones = new OpcionesFiscales
        {
            TimeoutDeLlamadaSegundos = segundos, TimeoutDeWsaaSegundos = segundos, PresupuestoDeLoteSegundos = segundos
        };

        // Los topes se escriben acá a propósito (no se leen de las constantes): una constante mutada no puede validarse a sí misma.
        Assert.Equal(TimeSpan.FromSeconds(120), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(120), opciones.TimeoutDeWsaa);
        Assert.Equal(TimeSpan.FromSeconds(segundos > 300 ? 300 : segundos), opciones.PresupuestoDeLote);
    }

    [Fact]
    public void ElTopeDelPresupuestoDeLoteEsDeTrescientosSegundosYElDelTimeoutDeCientoVeinte()
    {
        var enElTope = new OpcionesFiscales
        {
            TimeoutDeLlamadaSegundos = 120, TimeoutDeWsaaSegundos = 120, PresupuestoDeLoteSegundos = 300
        };
        var pasadoDelTope = new OpcionesFiscales
        {
            TimeoutDeLlamadaSegundos = 120.5, TimeoutDeWsaaSegundos = 120.5, PresupuestoDeLoteSegundos = 300.5
        };

        Assert.Equal(TimeSpan.FromSeconds(120), enElTope.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(120), enElTope.TimeoutDeWsaa);
        Assert.Equal(TimeSpan.FromSeconds(300), enElTope.PresupuestoDeLote);
        Assert.Equal(TimeSpan.FromSeconds(120), pasadoDelTope.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(120), pasadoDelTope.TimeoutDeWsaa);
        Assert.Equal(TimeSpan.FromSeconds(300), pasadoDelTope.PresupuestoDeLote);
    }

    [Theory]
    [InlineData(1e-9)]
    [InlineData(double.Epsilon)]
    [InlineData(0.0004)]
    public void UnValorPositivoMenorQueUnMilisegundoSeSubeAUnMilisegundo(double segundos)
    {
        var opciones = new OpcionesFiscales
        {
            TimeoutDeLlamadaSegundos = segundos, TimeoutDeWsaaSegundos = segundos, PresupuestoDeLoteSegundos = segundos
        };

        // Un HttpClient con timeout cero lanza al usarse: el piso evita el valor degenerado.
        Assert.Equal(TimeSpan.FromMilliseconds(1), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromMilliseconds(1), opciones.TimeoutDeWsaa);
        Assert.Equal(TimeSpan.FromMilliseconds(1), opciones.PresupuestoDeLote);
    }

    [Fact]
    public void LosValoresAcotadosSonValidosParaHttpClientYParaElTokenDelLote()
    {
        var opciones = new OpcionesFiscales
        {
            TimeoutDeLlamadaSegundos = double.PositiveInfinity,
            TimeoutDeWsaaSegundos = double.MaxValue,
            PresupuestoDeLoteSegundos = double.PositiveInfinity
        };

        using var http = new HttpClient { Timeout = opciones.TimeoutDeLlamada };
        using var otroHttp = new HttpClient { Timeout = opciones.TimeoutDeWsaa };
        using var presupuesto = new CancellationTokenSource(opciones.PresupuestoDeLote);

        Assert.False(presupuesto.IsCancellationRequested);
    }
}
