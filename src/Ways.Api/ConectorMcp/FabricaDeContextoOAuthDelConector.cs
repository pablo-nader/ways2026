using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ways.Api.ConectorMcp;

/// <summary>Fábrica solo para las herramientas de EF (<c>dotnet ef migrations add --context
/// ContextoOAuthDelConector</c>): sin ella EF levantaría el host completo de la API.</summary>
public sealed class FabricaDeContextoOAuthDelConector : IDesignTimeDbContextFactory<ContextoOAuthDelConector>
{
    public ContextoOAuthDelConector CreateDbContext(string[] args)
    {
        var cadena = Environment.GetEnvironmentVariable("ConnectionStrings__Ways")
            ?? "Host=localhost;Port=5432;Database=ways;Username=ways;Password=ways";

        var opciones = new DbContextOptionsBuilder<ContextoOAuthDelConector>();
        ContextoOAuthDelConector.Configurar(opciones, cadena);
        return new ContextoOAuthDelConector(opciones.Options);
    }
}
