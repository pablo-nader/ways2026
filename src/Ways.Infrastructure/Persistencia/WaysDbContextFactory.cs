using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Ways.Domain.Usuarios;

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
            .UseNpgsql(cadena, npgsql => npgsql.MapEnum<EstadoUsuario>("estado_usuario"))
            .Options;

        return new WaysDbContext(opciones);
    }
}
