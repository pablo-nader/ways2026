using Ways.Domain.Pagos;

namespace Ways.Domain.Tests.Pagos;

public class RecorteDeAplicacionesTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AplicacionARecortar Aplicacion(int id, int diasDespuesDeLaBase, decimal importe) =>
        new(id, Base.AddDays(diasDespuesDeLaBase), importe);

    [Fact]
    public void SinExcesoNoCambiaNada()
    {
        var plan = RecorteDeAplicaciones.Planificar([Aplicacion(1, 0, 60m), Aplicacion(2, 1, 40m)], 100m);

        Assert.Empty(plan);
    }

    [Fact]
    public void SinAplicacionesNoCambiaNada()
    {
        Assert.Empty(RecorteDeAplicaciones.Planificar([], 0m));
    }

    [Fact]
    public void ElExcesoSeSacaPrimeroDeLaMasNuevaPorFecha()
    {
        // La vieja tiene el id más alto: manda la fecha, no el id.
        var plan = RecorteDeAplicaciones.Planificar([Aplicacion(9, 0, 50m), Aplicacion(1, 5, 50m)], 70m);

        Assert.Equal([new AplicacionRecortada(1, 50m, 20m, false)], plan);
    }

    [Fact]
    public void ConLaMismaFechaDesempataElIdMasAlto()
    {
        var plan = RecorteDeAplicaciones.Planificar([Aplicacion(1, 0, 50m), Aplicacion(2, 0, 50m)], 70m);

        Assert.Equal([new AplicacionRecortada(2, 50m, 20m, false)], plan);
    }

    [Fact]
    public void LasQueEntranEnterasSeDanDeBajaYSeAchicaLaUltima()
    {
        var plan = RecorteDeAplicaciones.Planificar(
            [Aplicacion(1, 0, 100m), Aplicacion(2, 1, 30m), Aplicacion(3, 2, 20m)], 90m);

        Assert.Equal(
            [new AplicacionRecortada(3, 20m, 0m, true), new AplicacionRecortada(2, 30m, 0m, true), new AplicacionRecortada(1, 100m, 90m, false)],
            plan);
    }

    [Fact]
    public void UnExcesoIgualAUnaAplicacionLaDaDeBajaEntera()
    {
        var plan = RecorteDeAplicaciones.Planificar([Aplicacion(1, 0, 60m), Aplicacion(2, 1, 40m)], 60m);

        Assert.Equal([new AplicacionRecortada(2, 40m, 0m, true)], plan);
    }

    [Fact]
    public void UnTopeEnCeroLasDaDeBajaTodas()
    {
        var plan = RecorteDeAplicaciones.Planificar([Aplicacion(1, 0, 60m), Aplicacion(2, 1, 40m)], 0m);

        Assert.Equal([2, 1], plan.Select(r => r.Id));
        Assert.All(plan, r => Assert.True(r.Baja));
    }

    [Fact]
    public void UnTopeNegativoSeTrataComoCero()
    {
        Assert.Equal(2, RecorteDeAplicaciones.Planificar([Aplicacion(1, 0, 60m), Aplicacion(2, 1, 40m)], -5m).Count);
    }

    [Fact]
    public void LaListaDeEntradaPuedeVenirEnCualquierOrden()
    {
        var plan = RecorteDeAplicaciones.Planificar([Aplicacion(2, 1, 40m), Aplicacion(1, 0, 60m)], 60m);

        Assert.Equal([new AplicacionRecortada(2, 40m, 0m, true)], plan);
    }
}
