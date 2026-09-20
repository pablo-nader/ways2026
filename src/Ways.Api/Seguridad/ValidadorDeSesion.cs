using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Api.Seguridad;

/// <summary>
/// stage-desktop-pos, slice bearer: núcleo de vigencia de una sesión de cajero — el ÚNICO lugar
/// que decide si un <see cref="ClaimsPrincipal"/> YA autenticado sigue vigente, sin importar por
/// qué transporte llegó (cookie <c>ways.sesion</c>, <c>Program.cs</c>/<c>OnValidatePrincipal</c>,
/// o el esquema bearer nuevo, <see cref="ManejadorBearerDeSesion"/>).
///
/// Antes de este slice esta lógica vivía inline, solo en <c>OnValidatePrincipal</c>. El bearer
/// necesita EXACTAMENTE el mismo chequeo (usuario activo, dispositivo/PV vigente, tenant activo)
/// — si cada esquema tuviera su propia copia, un motivo de revocación nuevo agregado a uno de los
/// dos podría no aplicarse al otro sin que nadie lo note. Que los dos llamen a este único método
/// es lo que hace imposible esa divergencia.
/// </summary>
public static class ValidadorDeSesion
{
    /// <summary>Resuelve el modo/tenant del contexto de la request a partir de los claims YA
    /// decodificados y confirma que el usuario sigue activo — y, si la sesión viene de un login
    /// de dispositivo (claim <see cref="ClaimsWays.IdDispositivo"/>), que ese dispositivo siga
    /// vigente (no revocado, su punto de venta no dado de baja). Deliberadamente sin efectos de
    /// transporte (sign-out, etc.): cada esquema de autenticación decide qué hacer con un
    /// resultado negativo — la cookie cierra la sesión de verdad, el bearer simplemente falla la
    /// request (no hay nada que cerrar: no es stateful del lado del servidor).</summary>
    public static async Task<bool> EsVigenteAsync(
        ClaimsPrincipal principal, HttpContext http, WaysDbContext db)
    {
        var claim = principal.FindFirst(ClaimTypes.NameIdentifier);
        if (claim is null || !int.TryParse(claim.Value, out var usuarioId))
        {
            return false;
        }

        // El modo/tenant se resuelve ANTES de tocar `usuarios` a propósito (mismo motivo que el
        // comentario original en Program.cs): el filtro de tenant de EF (ADR-1) falla cerrado en
        // modo `Ninguno`, así que revisar la cuenta propia con el contexto todavía sin resolver
        // la dejaría siempre invisible, para cualquier cuenta de tenant.
        if (!await ResolverModoDeLaSesionAsync(principal, http, db))
        {
            return false;
        }

        var vigente = await db.Usuarios
            .AsNoTracking()
            .AnyAsync(u => u.Id == usuarioId && u.Estado == EstadoUsuario.Activo);

        if (!vigente)
        {
            return false;
        }

        // Una sesión iniciada por /auth/login-dispositivo lleva la claim ways:id_dispositivo.
        // Revocar el dispositivo o dar de baja su punto de venta tiene que cortar la sesión en la
        // request siguiente, igual que bloquear al usuario o suspender el tenant. El tenant de la
        // fila ya quedó garantizado arriba (ResolverModoDeLaSesionAsync puso el contexto en el
        // tenant del claim): si el dispositivo fuera de otro tenant, el filtro de EF + RLS ya lo
        // esconderían, así que "no aparece" cubre revocado, PV de baja Y tenant distinto sin tres
        // chequeos separados.
        if (int.TryParse(principal.FindFirstValue(ClaimsWays.IdDispositivo), out var idDispositivo))
        {
            var dispositivoVigente = await db.Dispositivos
                .AsNoTracking()
                .Where(d => d.Id == idDispositivo)
                .Join(db.PuntosVenta, d => d.IdPuntoVenta, p => p.Id, (d, _) => d.Id)
                .AnyAsync();

            if (!dispositivoVigente)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Mismo contrato que el <c>ResolverModoDeLaSesionAsync</c> original de
    /// <c>Program.cs</c> (ahora movido acá): <c>false</c> cuando ya hay que rechazar la sesión
    /// (tenant inexistente/suspendido/de baja).</summary>
    private static async Task<bool> ResolverModoDeLaSesionAsync(
        ClaimsPrincipal principal, HttpContext http, WaysDbContext db)
    {
        var tenantActual = http.RequestServices.GetRequiredService<TenantActualDeSesion>();

        var esRoot =
            int.TryParse(principal.FindFirstValue(ClaimsWays.RolId), out var rolId)
            && (RolConocido)rolId == RolConocido.Root;

        if (esRoot)
        {
            tenantActual.Establecer(ModoDeAcceso.Plataforma, idTenant: null);
            return true;
        }

        // El claim ways:id_tenant está ausente para staff de plataforma (ya cubierto arriba,
        // esRoot) y para cualquier cuenta creada antes del backfill de la migración 2 (gate #2
        // pendiente). Sin claim el contexto queda "Ninguno": no ve nada scopeado.
        if (!int.TryParse(principal.FindFirstValue(ClaimsWays.IdTenant), out var idTenant))
        {
            tenantActual.Establecer(ModoDeAcceso.Ninguno, idTenant: null);
            return true;
        }

        tenantActual.Establecer(ModoDeAcceso.Tenant, idTenant);

        // IgnoreQueryFilters(["BajaLogica"]) para distinguir "el tenant no existe" (bug) de "está
        // dado de baja" (estado de negocio) — las dos rechazan la sesión igual, pero sin ignorar
        // la baja lógica un tenant borrado devolvería null y se confundiría con el
        // default(EstadoTenant) = Activo si se seleccionara solo el campo.
        var tenant = await db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters(["BajaLogica"])
            .FirstOrDefaultAsync(t => t.Id == idTenant);

        return tenant is not null && tenant.Estado == EstadoTenant.Activo && tenant.DeletedAt is null;
    }
}
