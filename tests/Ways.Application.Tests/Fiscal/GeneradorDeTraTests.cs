using Ways.Application.Abstracciones;
using Ways.Application.Tests.Infraestructura;
using Ways.Infrastructure.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice2 (tasks 2.10-2.12, design D3/D4, targets 24-26): golden byte a byte de la TRA
/// bajo un reloj fijo + una instancia nueva de <see cref="GeneradorDeTra"/> (el desambiguador de
/// <c>uniqueId</c> es POR INSTANCIA — construir una fresca y llamar <c>Construir</c> UNA vez lo
/// deja en un valor determinístico) y la prueba del desambiguador bajo el mismo tick de reloj.
/// </summary>
public class GeneradorDeTraTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(-3));

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    [Fact]
    public void LaTraCoincideByteAByteConElGoldenDelManual()
    {
        var raiz = RaizDelRepositorio.Resolver();
        var golden = File.ReadAllText(Path.Combine(
            raiz, "tests", "Ways.Application.Tests", "Fiscal", "Fixtures", "Wsaa", "TraGolden.xml"));

        var generador = new GeneradorDeTra(new RelojFijo(Ahora));
        var tra = generador.Construir("wsfe");

        Assert.Equal(golden, tra);
    }

    [Fact]
    public void GenerationTimeYExpirationTimeSalenDeIRelojDelSistemaConLaVentanaDeDiezMinutos()
    {
        var generador = new GeneradorDeTra(new RelojFijo(Ahora));
        var tra = generador.Construir("wsfe");

        Assert.Contains("<generationTime>2026-01-15T09:50:00-03:00</generationTime>", tra);
        Assert.Contains("<expirationTime>2026-01-15T10:10:00-03:00</expirationTime>", tra);
        Assert.Equal(TimeSpan.FromMinutes(10), GeneradorDeTra.Ventana);
    }

    [Fact]
    public void DosTrasArmadasEnElMismoTickDeRelojDifierenEnElUniqueId()
    {
        var generador = new GeneradorDeTra(new RelojFijo(Ahora));

        var primera = generador.Construir("wsfe");
        var segunda = generador.Construir("wsfe");

        Assert.NotEqual(ExtraerUniqueId(primera), ExtraerUniqueId(segunda));
    }

    private static string ExtraerUniqueId(string tra)
    {
        const string apertura = "<uniqueId>";
        const string cierre = "</uniqueId>";
        var inicio = tra.IndexOf(apertura, StringComparison.Ordinal) + apertura.Length;
        var fin = tra.IndexOf(cierre, StringComparison.Ordinal);
        return tra[inicio..fin];
    }

}
