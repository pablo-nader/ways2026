using Microsoft.EntityFrameworkCore;
using Ways.Domain.Clientes;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Judgment-day ronda 1 (item de hardening): <c>WaysDbContext.RechazarEscriturasDeNumeracionCliente</c>
/// — mismo patrón defense-in-depth que <c>EstamparTenant</c> sobre <c>IdTenant</c>, acá
/// rechazando cualquier <c>Added</c>/<c>Modified</c> de <see cref="NumeracionCliente"/> que
/// llegue por el <c>ChangeTracker</c> (design decision 3: <c>AsignadorDeNumeroCliente</c> con
/// SQL crudo es el único punto de escritura legítimo). Usa el proveedor InMemory: el guard
/// tira ANTES de que <c>SaveChanges</c> llegue a tocar la base, así que no hace falta Postgres
/// para probarlo — mismo criterio que <c>FiltroDeTenantTests</c>.
/// </summary>
public class GuardDeNumeracionClienteTests
{
    private static WaysDbContext CrearContexto() =>
        new(new DbContextOptionsBuilder<WaysDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            TenantActualFijo.Plataforma);

    [Fact]
    public void UnNumeracionClienteAgregadoPorElChangeTrackerSeRechaza()
    {
        using var db = CrearContexto();

        db.NumeracionesClientes.Add(new NumeracionCliente { IdTenant = 1, ProximoNumero = 1 });

        var excepcion = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("numeraciones_clientes", excepcion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnNumeracionClienteModificadoPorElChangeTrackerSeRechaza()
    {
        using var db = CrearContexto();

        var entrada = db.Attach(new NumeracionCliente { IdTenant = 1, ProximoNumero = 1 });
        entrada.Entity.ProximoNumero = 2;
        entrada.State = EntityState.Modified;

        var excepcion = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("numeraciones_clientes", excepcion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnNumeracionClienteSinCambiosNoDisparaElGuard()
    {
        using var db = CrearContexto();

        db.Attach(new NumeracionCliente { IdTenant = 1, ProximoNumero = 1 });

        // EntityState.Unchanged (el default de Attach): no es una escritura, así que
        // SaveChanges no tiene que tirar por esta fila -- confirma que el guard es específico
        // de Added/Modified, no un rechazo ciego de cualquier entrada trackeada.
        var excepcion = Record.Exception(() => db.SaveChanges());
        Assert.Null(excepcion);
    }
}
