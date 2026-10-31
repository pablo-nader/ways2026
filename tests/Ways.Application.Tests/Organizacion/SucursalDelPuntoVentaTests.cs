using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Organizacion;

/// <summary>
/// La regla "todo punto de venta nuevo recibe su propia sucursal" vive en un único punto de paso,
/// <c>WaysDbContext.SaveChanges</c>: el alta del ABM, el aprovisionamiento y la siembra la heredan.
/// La FK compuesta que obliga a que la sucursal sea de la misma empresa se prueba contra Postgres
/// en <c>SucursalesEsquemaTests</c>.
/// </summary>
public class SucursalDelPuntoVentaTests
{
    private const int IdTenant = 1;
    private static readonly DateTimeOffset Ahora = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static WaysDbContext CrearContexto() =>
        new(
            new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));

    private static PuntoVenta NuevoPuntoVenta(string nombre, DateTimeOffset? baja = null) => new()
    {
        IdTenant = IdTenant,
        IdEmpresa = 7,
        Nombre = nombre,
        Modo = ModoPuntoVenta.Web,
        CreatedAt = Ahora,
        UpdatedAt = Ahora,
        DeletedAt = baja
    };

    /// <summary>Mutación corrida: sacar la llamada a <c>CrearSucursalDeLosPuntosDeVentaNuevos</c> de
    /// <c>EstamparTenant</c> deja <c>IdSucursal</c> en 0 y la lista de sucursales vacía: rojo.</summary>
    [Fact]
    public async Task UnPuntoDeVentaNuevoRecibeSuPropiaSucursalConSuNombreYSuEmpresa()
    {
        await using var db = CrearContexto();
        var puntoVenta = NuevoPuntoVenta("Local Centro");
        db.PuntosVenta.Add(puntoVenta);

        await db.SaveChangesAsync();

        var sucursal = await db.Sucursales.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(sucursal.Id, puntoVenta.IdSucursal);
        Assert.NotEqual(0, puntoVenta.IdSucursal);
        Assert.Equal("Local Centro", sucursal.Nombre);
        Assert.Equal(7, sucursal.IdEmpresa);
        Assert.Equal(IdTenant, sucursal.IdTenant);
        Assert.Equal(Ahora, sucursal.CreatedAt);
        Assert.Null(sucursal.DeletedAt);
    }

    [Fact]
    public async Task DosPuntosDeVentaNuevosRecibenUnaSucursalCadaUno()
    {
        await using var db = CrearContexto();
        var a = NuevoPuntoVenta("A");
        var b = NuevoPuntoVenta("B");
        db.PuntosVenta.AddRange(a, b);

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Sucursales.IgnoreQueryFilters().CountAsync());
        Assert.NotEqual(a.IdSucursal, b.IdSucursal);
    }

    /// <summary>Una fila sembrada ya dada de baja nace con su sucursal también dada de baja: el
    /// mismo instante, para que la baja y el restore sigan siendo un solo conjunto.</summary>
    [Fact]
    public async Task LaSucursalDeUnPuntoDeVentaDadoDeBajaNaceDadaDeBaja()
    {
        await using var db = CrearContexto();
        db.PuntosVenta.Add(NuevoPuntoVenta("Cerrado", baja: Ahora.AddDays(-1)));

        await db.SaveChangesAsync();

        var sucursal = await db.Sucursales.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(Ahora.AddDays(-1), sucursal.DeletedAt);
    }

    /// <summary>Un punto de venta que ya trae sucursal (la que elija el ABM en etapas siguientes) no
    /// recibe una segunda.</summary>
    [Fact]
    public async Task UnPuntoDeVentaQueYaTraeSucursalNoRecibeOtra()
    {
        await using var db = CrearContexto();
        var existente = new Sucursal { IdTenant = IdTenant, IdEmpresa = 7, Nombre = "Sucursal propia", CreatedAt = Ahora, UpdatedAt = Ahora };
        db.Sucursales.Add(existente);
        await db.SaveChangesAsync();

        var puntoVenta = NuevoPuntoVenta("Caja 2");
        puntoVenta.IdSucursal = existente.Id;
        db.PuntosVenta.Add(puntoVenta);

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Sucursales.IgnoreQueryFilters().CountAsync());
        Assert.Equal(existente.Id, puntoVenta.IdSucursal);
    }

    /// <summary>Editar un punto de venta existente no crea sucursales: la regla es solo para los Added.</summary>
    [Fact]
    public async Task EditarUnPuntoDeVentaNoCreaSucursales()
    {
        await using var db = CrearContexto();
        var puntoVenta = NuevoPuntoVenta("Original");
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        puntoVenta.Nombre = "Editado";
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Sucursales.IgnoreQueryFilters().CountAsync());
    }
}
