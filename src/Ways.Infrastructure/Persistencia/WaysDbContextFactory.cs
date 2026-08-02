using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Infrastructure.Persistencia;

/// <summary>
/// Fábrica solo para las herramientas de EF (<c>dotnet ef migrations</c>).
/// Sin esto, EF levantaría el host completo de la API y dispararía las migraciones
/// y la semilla al generar un scaffold.
/// </summary>
public class WaysDbContextFactory : IDesignTimeDbContextFactory<WaysDbContext>
{
    public WaysDbContext CreateDbContext(string[] args)
    {
        var cadena = Environment.GetEnvironmentVariable("ConnectionStrings__Ways")
            ?? "Host=localhost;Port=5432;Database=ways;Username=ways;Password=ways";

        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(cadena, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
            })
            .Options;

        // Las herramientas de diseño no son un request HTTP: operan en modo plataforma.
        return new WaysDbContext(opciones, TenantActualFijo.Plataforma);
    }
}
