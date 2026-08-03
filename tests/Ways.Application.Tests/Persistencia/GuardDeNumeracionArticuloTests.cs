using Microsoft.EntityFrameworkCore;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Stage-3-articulos-y-precios (design decision 6): mismo patrón que
/// <c>GuardDeNumeracionClienteTests</c> — <c>WaysDbContext.RechazarEscriturasDeNumeracionArticulo</c>
/// rechaza cualquier <c>Added</c>/<c>Modified</c> de <see cref="NumeracionArticulo"/> que llegue
/// por el <c>ChangeTracker</c> (<see cref="Application.Articulos.AsignadorDeCodigoInternoArticulo"/>
/// con SQL crudo es el único punto de escritura legítimo). Usa el proveedor InMemory: el guard
/// tira antes de que <c>SaveChanges</c> llegue a tocar la base.
/// </summary>
public class GuardDeNumeracionArticuloTests
{
    private static WaysDbContext CrearContexto() =>
        new(new DbContextOptionsBuilder<WaysDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            TenantActualFijo.Plataforma);

    [Fact]
    public void UnNumeracionArticuloAgregadoPorElChangeTrackerSeRechaza()
    {
        using var db = CrearContexto();

        db.NumeracionesArticulos.Add(new NumeracionArticulo { IdTenant = 1, ProximoNumero = 1 });

        var excepcion = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("numeraciones_articulos", excepcion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnNumeracionArticuloModificadoPorElChangeTrackerSeRechaza()
    {
        using var db = CrearContexto();

        var entrada = db.Attach(new NumeracionArticulo { IdTenant = 1, ProximoNumero = 1 });
        entrada.Entity.ProximoNumero = 2;
        entrada.State = EntityState.Modified;

        var excepcion = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("numeraciones_articulos", excepcion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnNumeracionArticuloSinCambiosNoDisparaElGuard()
    {
        using var db = CrearContexto();

        db.Attach(new NumeracionArticulo { IdTenant = 1, ProximoNumero = 1 });

        var excepcion = Record.Exception(() => db.SaveChanges());
        Assert.Null(excepcion);
    }
}
