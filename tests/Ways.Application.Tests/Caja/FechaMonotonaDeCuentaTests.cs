using Ways.Application.Caja;

namespace Ways.Application.Tests.Caja;

public class FechaMonotonaDeCuentaTests
{
    private static readonly DateTimeOffset Diez = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SinMovimientosPrevisLaHoraPropiaSeConserva()
    {
        Assert.Equal(Diez, EscriturasDeCuentaDeTesoreria.FechaMonotona(Diez, null));
    }

    [Fact]
    public void UnaColaMasNuevaQueLaHoraPropiaSubeLaFecha()
    {
        var cola = Diez.AddSeconds(5);

        Assert.Equal(cola, EscriturasDeCuentaDeTesoreria.FechaMonotona(Diez, cola));
    }

    [Fact]
    public void UnaColaMasViejaOIgualIgualNoBajaLaHoraPropia()
    {
        Assert.Equal(Diez, EscriturasDeCuentaDeTesoreria.FechaMonotona(Diez, Diez.AddSeconds(-5)));
        Assert.Equal(Diez, EscriturasDeCuentaDeTesoreria.FechaMonotona(Diez, Diez));
    }

    [Fact]
    public void LaFuncionEncadenadaConHorasDesordenadasNuncaDevuelveUnaFechaDecreciente()
    {
        DateTimeOffset? cola = null;
        var horas = new[] { Diez.AddSeconds(5), Diez, Diez.AddSeconds(2), Diez.AddSeconds(9), Diez.AddSeconds(1) };
        var fechas = new List<DateTimeOffset>();

        foreach (var hora in horas)
        {
            var fecha = EscriturasDeCuentaDeTesoreria.FechaMonotona(hora, cola);
            fechas.Add(fecha);
            cola = fecha;
        }

        Assert.Equal(fechas.Order().ToList(), fechas);
    }
}
