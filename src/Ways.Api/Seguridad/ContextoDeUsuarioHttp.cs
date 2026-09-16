using System.Security.Claims;
using Ways.Application.Abstracciones;
using Ways.Domain.Usuarios;

namespace Ways.Api.Seguridad;

/// <summary>Nombres de claim propios. Los estándar de .NET son URLs larguísimas.</summary>
public static class ClaimsWays
{
    public const string RolId = "ways:id_rol";

    /// <summary>Presente solo cuando la cuenta pertenece a un tenant (doc 09): ausente para
    /// staff de plataforma. Lo emite <c>POST /api/auth/login</c> (stage 1 slice 2) y lo lee
    /// <c>Program.cs</c>, <c>OnValidatePrincipal</c>.</summary>
    public const string IdTenant = "ways:id_tenant";

    /// <summary>Presente solo en una sesión iniciada por <c>POST /api/auth/login-dispositivo</c>
    /// (stage-desktop-pos): el <c>id_dispositivo</c> que emitió la sesión, para que
    /// <c>Program.cs</c>, <c>OnValidatePrincipal</c> pueda revalidar en cada request que el
    /// dispositivo siga vigente (no revocado, su PV no dada de baja) — una sesión de la web
    /// normal (login por mail) nunca lleva esta claim.</summary>
    public const string IdDispositivo = "ways:id_dispositivo";
}

public class ContextoDeUsuarioHttp(IHttpContextAccessor accessor) : IContextoDeUsuario
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool EstaAutenticado => Principal?.Identity?.IsAuthenticated ?? false;

    public int UsuarioId =>
        int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public string NombreUsuario => Principal?.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    public RolConocido Rol =>
        int.TryParse(Principal?.FindFirstValue(ClaimsWays.RolId), out var rol)
            ? (RolConocido)rol
            : default;

    public int? IdTenant =>
        int.TryParse(Principal?.FindFirstValue(ClaimsWays.IdTenant), out var idTenant)
            ? idTenant
            : null;
}
