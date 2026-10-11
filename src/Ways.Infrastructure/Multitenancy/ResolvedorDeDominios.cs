using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Dominios;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Resuelve un host al tenant dueño de su dominio verificado. Corre antes de que exista una sesión,
/// así que lee <c>dominios</c> en modo <see cref="ModoDeAcceso.Login"/> (política
/// <c>dominios_login_lectura</c>) y el nombre del comercio con el contexto de plataforma, como hace
/// <c>ServicioDeAutenticacion</c> con <c>tenants</c> (RLS no tiene una política de login sobre
/// <c>tenants</c>).
///
/// Cada consulta usa un alcance de DI propio: el modo de login vive en el <c>TenantActualDeSesion</c>
/// de ESE alcance y nunca se filtra al de la request. Singleton, porque guarda lo resuelto en memoria
/// por <see cref="Vigencia"/>; incluso el "no existe", para que un host cualquiera no pague una
/// consulta por request. La caché es propia y acotada: el host lo manda el cliente, y sin tope un
/// goteo de hosts inventados crecería sin límite.
/// </summary>
public sealed class ResolvedorDeDominios(IServiceScopeFactory alcances) : IResolvedorDeDominios, IDisposable
{
    internal static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(60);

    private const int TopeDeEntradas = 2048;

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = TopeDeEntradas });

    public async Task<DominioResuelto?> ResolverAsync(string host, CancellationToken ct = default)
    {
        if (cache.TryGetValue(host, out DominioResuelto? recordado))
        {
            return recordado;
        }

        var resuelto = await ConsultarAsync(host, ct);

        cache.Set(
            host,
            resuelto,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Vigencia });

        return resuelto;
    }

    public void Invalidar(string host) => cache.Remove(host);

    public void Dispose() => cache.Dispose();

    private async Task<DominioResuelto?> ConsultarAsync(string host, CancellationToken ct)
    {
        await using var alcance = alcances.CreateAsyncScope();

        alcance.ServiceProvider
            .GetRequiredService<TenantActualDeSesion>()
            .Establecer(ModoDeAcceso.Login, idTenant: null);

        // IgnoreQueryFilters("Tenant"): el filtro de EF compara contra un tenant que en modo login
        // no existe; el acceso lo decide la política de RLS. La baja lógica sigue filtrando.
        var idTenant = await alcance.ServiceProvider.GetRequiredService<IWaysDbContext>()
            .Dominios
            .AsNoTracking()
            .IgnoreQueryFilters(["Tenant"])
            .Where(d => d.Host == host && d.VerificadoEn != null)
            .Select(d => (int?)d.IdTenant)
            .FirstOrDefaultAsync(ct);

        if (idTenant is null)
        {
            return null;
        }

        var nombre = await alcance.ServiceProvider
            .GetRequiredKeyedService<IWaysDbContext>(ClavesDeContexto.Plataforma)
            .Tenants
            .AsNoTracking()
            .Where(t => t.Id == idTenant)
            .Select(t => t.Nombre)
            .FirstOrDefaultAsync(ct);

        // Sin nombre el tenant no existe o está dado de baja: el dominio no resuelve.
        return nombre is null ? null : new DominioResuelto(idTenant.Value, nombre);
    }
}
