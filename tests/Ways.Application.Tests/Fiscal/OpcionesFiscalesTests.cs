using Ways.Application.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>Los valores por defecto del timeout de las llamadas a ARCA (10 s) y del presupuesto de un lote (20 s), y que un valor
/// no positivo vuelve al por defecto en vez de dejar al HttpClient sin timeout o con uno inválido.</summary>
public class OpcionesFiscalesTests
{
    [Fact]
    public void SinConfigurarElTimeoutEsDeDiezSegundosYElPresupuestoDeVeinte()
    {
        var opciones = new OpcionesFiscales();

        Assert.Equal(TimeSpan.FromSeconds(10), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(20), opciones.PresupuestoDeLote);
    }

    [Fact]
    public void UnValorConfiguradoSeRespetaEnSegundosFraccionarios()
    {
        var opciones = new OpcionesFiscales { TimeoutDeLlamadaSegundos = 0.25, PresupuestoDeLoteSegundos = 3 };

        Assert.Equal(TimeSpan.FromMilliseconds(250), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(3), opciones.PresupuestoDeLote);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void UnValorNoPositivoVuelveAlPorDefecto(double segundos)
    {
        var opciones = new OpcionesFiscales { TimeoutDeLlamadaSegundos = segundos, PresupuestoDeLoteSegundos = segundos };

        Assert.Equal(TimeSpan.FromSeconds(10), opciones.TimeoutDeLlamada);
        Assert.Equal(TimeSpan.FromSeconds(20), opciones.PresupuestoDeLote);
    }
}
