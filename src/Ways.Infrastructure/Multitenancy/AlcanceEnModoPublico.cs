using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Abre un alcance de DI propio y fija ahí el modo de acceso <see cref="ModoDeAcceso.Publico"/> para un tenant,
/// igual que <see cref="ResolvedorDeDominios"/> con el modo de login: el <see cref="TenantActualDeSesion"/> de
/// la request no se toca, así que el modo público no puede filtrarse a nada más que corra en esa request (por
/// ejemplo, una sesión de cookie que llegó a un endpoint público). Lo comparten los lectores públicos
/// (<see cref="LectorDeVitrinaPublica"/>, <see cref="LectorDeCatalogoPublico"/>).
/// </summary>
internal static class AlcanceEnModoPublico
{
    public static AsyncServiceScope Abrir(IServiceScopeFactory alcances, int idTenant)
    {
        var alcance = alcances.CreateAsyncScope();

        alcance.ServiceProvider
            .GetRequiredService<TenantActualDeSesion>()
            .Establecer(ModoDeAcceso.Publico, idTenant);

        return alcance;
    }
}
