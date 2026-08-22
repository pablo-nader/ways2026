using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Domain.Fiscal;
using Ways.Infrastructure.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice2 (tasks 2.17-2.19, design D8, targets 31-33): el cache del TA — hit dentro de
/// la vigencia, el borde exacto del margen de seguridad bajo un reloj que AVANZA (el mismo criterio
/// de <c>RelojQueAvanza</c> de la etapa 18: un reloj fijo no puede distinguir "se leyó una vez" de
/// "se leyó más de una vez", y acá cada llamada a <see cref="RepositorioEnMemoriaDeTicketDeAcceso.ObtenerVigenteAsync"/>
/// hace exactamente UNA lectura de <see cref="IRelojDelSistema.Ahora"/>), y el single-flight bajo
/// concurrencia real.
/// </summary>
public class RepositorioEnMemoriaDeTicketDeAccesoTests
{
    private static readonly ClaveDeTicket Clave = new(1, AmbienteFiscal.Homologacion, "wsfe");

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    /// <summary>judgment-day skill precedent (EtiquetasEndpointsTests, stage 18): a diferencia de
    /// <see cref="RelojFijo"/>, devuelve un valor DISTINTO en cada lectura (arranca en
    /// <paramref name="inicio"/> y suma 1 segundo por get).</summary>
    private sealed class RelojQueAvanza(DateTimeOffset inicio) : IRelojDelSistema
    {
        private DateTimeOffset _proximaLectura = inicio;

        public DateTimeOffset Ahora
        {
            get
            {
                var valor = _proximaLectura;
                _proximaLectura = _proximaLectura.AddSeconds(1);
                return valor;
            }
        }
    }

    [Fact]
    public async Task UnSegundoPedidoDentroDeLaVigenciaNoReemiteLoginCms()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
        var repositorio = new RepositorioEnMemoriaDeTicketDeAcceso(reloj);
        var llamadas = 0;

        Task<TicketDeAcceso> Fabricar(CancellationToken ct)
        {
            llamadas++;
            return Task.FromResult(new TicketDeAcceso("tok", "sig", reloj.Ahora.AddHours(12)));
        }

        var primero = await repositorio.ObtenerOFirmarAsync(Clave, Fabricar, CancellationToken.None);
        var segundo = await repositorio.ObtenerOFirmarAsync(Clave, Fabricar, CancellationToken.None);

        Assert.Equal(1, llamadas);
        Assert.Equal(primero.Token, segundo.Token);
    }

    [Fact]
    public async Task ElMargenDeSeguridadEsElBordeExactoDeVigencia()
    {
        // El margen se hardcodea acá (10 min), a propósito, en vez de leer
        // RepositorioEnMemoriaDeTicketDeAcceso.MargenDeSeguridad — leerlo de la propia constante
        // que el test debería estar pineando vuelve el test "overdetermined" (mutation-proof-tests
        // rule 3): una mutación que cambie el VALOR de la constante pasaría igual, porque el
        // cálculo del borde se recalcularía con el mismo valor mutado (confirmado corriendo la
        // mutación: TimeSpan.Zero como margen seguía en verde con la versión anterior de este
        // test).
        var expiracion = new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero);
        var margen = TimeSpan.FromMinutes(10);
        var inicio = expiracion - margen - TimeSpan.FromSeconds(1);

        Assert.Equal(margen, RepositorioEnMemoriaDeTicketDeAcceso.MargenDeSeguridad);

        var reloj = new RelojQueAvanza(inicio);
        var repositorio = new RepositorioEnMemoriaDeTicketDeAcceso(reloj);
        await repositorio.GuardarAsync(Clave, new TicketDeAcceso("tok", "sig", expiracion), CancellationToken.None);

        var unSegundoAntesDelBorde = await repositorio.ObtenerVigenteAsync(Clave, CancellationToken.None);
        var enElBorde = await repositorio.ObtenerVigenteAsync(Clave, CancellationToken.None);

        Assert.NotNull(unSegundoAntesDelBorde);
        Assert.Null(enElBorde);
    }

    [Fact]
    public async Task NConcurrentesEnFrioEmitenUnSoloLoginCms()
    {
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var repositorio = new RepositorioEnMemoriaDeTicketDeAcceso(reloj);
        var llamadas = 0;

        async Task<TicketDeAcceso> Fabricar(CancellationToken ct)
        {
            Interlocked.Increment(ref llamadas);
            await Task.Delay(50, ct);
            return new TicketDeAcceso("tok", "sig", DateTimeOffset.UtcNow.AddHours(12));
        }

        var tareas = Enumerable.Range(0, 10)
            .Select(_ => repositorio.ObtenerOFirmarAsync(Clave, Fabricar, CancellationToken.None))
            .ToArray();

        var resultados = await Task.WhenAll(tareas);

        Assert.Equal(1, llamadas);
        Assert.All(resultados, r => Assert.Equal(resultados[0].Token, r.Token));
    }
}
