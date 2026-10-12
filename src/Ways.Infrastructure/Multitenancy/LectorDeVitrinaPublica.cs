using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Vitrinas;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Lee la vitrina publicada de un tenant para una request sin sesión, en el modo de acceso
/// <see cref="ModoDeAcceso.Publico"/>: <c>SELECT</c> sobre <c>vitrinas</c> (solo la publicada),
/// <c>empresas</c> y <c>puntos_venta</c>, de un único tenant. Esa restricción la ponen las policies
/// <c>*_publico_lectura</c> de RLS; el filtro de EF repite el tenant, pero la publicación NO se repite
/// en las consultas: <c>publicada</c> la decide solo la policy, y si dejara de aplicarse, el endpoint
/// serviría una vitrina sin publicar.
///
/// Cada lectura abre un alcance de DI propio y fija el modo ahí, igual que <see cref="ResolvedorDeDominios"/>
/// con el modo de login: el <see cref="TenantActualDeSesion"/> de la request no se toca, así que el modo
/// público no puede filtrarse a nada más que corra en esa request (por ejemplo, una sesión de cookie que
/// llegó al endpoint público). Las consultas proyectan columna por columna: la policy de <c>empresas</c> y
/// la de <c>puntos_venta</c> dejan ver filas enteras (CUIT, alícuotas, modo), y lo que sale de acá es
/// exactamente lo que el contrato de <see cref="VitrinaPublica"/> declara.
/// </summary>
public sealed class LectorDeVitrinaPublica(IServiceScopeFactory alcances) : ILectorDeVitrinaPublica
{
    public async Task<VitrinaPublica?> ObtenerAsync(int idTenant, CancellationToken ct = default)
    {
        await using var alcance = EnModoPublico(idTenant);
        var db = alcance.ServiceProvider.GetRequiredService<IWaysDbContext>();

        var cabecera = await (
                from v in db.Vitrinas.AsNoTracking()
                join e in db.Empresas.AsNoTracking() on v.IdEmpresa equals e.Id
                select new
                {
                    e.Id,
                    Nombre = e.NombreFantasia ?? e.RazonSocial,
                    v.Tagline,
                    v.Descripcion,
                    v.ColorAcento,
                    v.Tema,
                    TieneLogo = v.Logo != null,
                    v.LogoActualizadoEn
                })
            .FirstOrDefaultAsync(ct);

        if (cabecera is null)
        {
            return null;
        }

        var sucursales = await db.PuntosVenta
            .AsNoTracking()
            .Where(p => p.IdEmpresa == cabecera.Id)
            .OrderBy(p => p.Nombre)
            .ThenBy(p => p.Id)
            .Select(p => new SucursalPublica(
                p.Nombre, p.Domicilio, p.Horario, p.Whatsapp, p.Instagram, p.Facebook, p.Web))
            .ToListAsync(ct);

        return new VitrinaPublica(
            cabecera.Nombre,
            cabecera.Tagline,
            cabecera.Descripcion,
            cabecera.ColorAcento,
            cabecera.Tema,
            cabecera.TieneLogo,
            Vitrina.VersionDelLogo(cabecera.LogoActualizadoEn),
            sucursales);
    }

    public async Task<VersionDeLogo?> ObtenerVersionDelLogoAsync(int idTenant, CancellationToken ct = default)
    {
        await using var alcance = EnModoPublico(idTenant);
        var db = alcance.ServiceProvider.GetRequiredService<IWaysDbContext>();

        // Solo el tipo y la marca de carga: `v.Logo != null` se traduce a `IS NOT NULL` y la columna bytea
        // nunca viaja. De esto sale el ETag, así que una condicional que coincide no lee los bytes.
        var version = await (
                from v in db.Vitrinas.AsNoTracking()
                join e in db.Empresas.AsNoTracking() on v.IdEmpresa equals e.Id
                where v.Logo != null
                select new { v.LogoContentType, v.LogoActualizadoEn })
            .FirstOrDefaultAsync(ct);

        return version is { LogoContentType: not null, LogoActualizadoEn: not null }
            ? new VersionDeLogo(version.LogoContentType, version.LogoActualizadoEn.Value)
            : null;
    }

    public async Task<LogoPublico?> ObtenerLogoAsync(int idTenant, CancellationToken ct = default)
    {
        await using var alcance = EnModoPublico(idTenant);
        var db = alcance.ServiceProvider.GetRequiredService<IWaysDbContext>();

        var logo = await (
                from v in db.Vitrinas.AsNoTracking()
                join e in db.Empresas.AsNoTracking() on v.IdEmpresa equals e.Id
                where v.Logo != null
                select new { v.Logo, v.LogoContentType, v.LogoActualizadoEn })
            .FirstOrDefaultAsync(ct);

        return logo is { Logo: not null, LogoContentType: not null, LogoActualizadoEn: not null }
            ? new LogoPublico(logo.Logo, logo.LogoContentType, logo.LogoActualizadoEn.Value)
            : null;
    }

    private AsyncServiceScope EnModoPublico(int idTenant) => AlcanceEnModoPublico.Abrir(alcances, idTenant);
}
