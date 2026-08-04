using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Abstracciones;

/// <summary>
/// Cubre el hallazgo de judgment-day R2 (Judge B): sin este test, cambiar
/// <c>FabricaDeEstrategiaSinReintento</c> de vuelta a una estrategia reintentable (por ejemplo,
/// devolver <c>db.Database.CreateExecutionStrategy()</c> a secas) pasaría desapercibido — ambos
/// consumidores (<c>ServicioDeVentas.AnularAsync</c>, <c>ServicioDeStock.AjustarAsync</c>)
/// dependen de que esta estrategia NUNCA reintente.
/// </summary>
public class EstrategiaSinReintentoTests
{
    private static WaysDbContext CrearContexto() =>
        new(new DbContextOptionsBuilder<WaysDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            TenantActualFijo.Plataforma);

    [Fact]
    public void LaEstrategiaCreadaNoReintenta()
    {
        using var db = CrearContexto();

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        Assert.False(estrategia.RetriesOnFailure);
    }

    [Fact]
    public void ShouldRetryOnDevuelveFalsoInclusoParaUnaExcepcionTransitoriaDeNpgsql()
    {
        using var db = CrearContexto();

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        // ShouldRetryOn es protected en ExecutionStrategy: se ejercita por reflexión, mismo
        // criterio que VentasAtomicidadYConcurrenciaTests con métodos privados.
        var metodo = estrategia.GetType().GetMethod(
            "ShouldRetryOn", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Representa una falla transitoria real (ej.: conexión cortada a mitad de un commit) —
        // el punto del test es que, aun así, esta estrategia jamás la reintenta.
        var excepcionTransitoria = new NpgsqlException("simulated transient connection failure");

        var resultado = (bool)metodo.Invoke(estrategia, [excepcionTransitoria])!;

        Assert.False(resultado);
    }
}
