using System.Security.Claims;
using Ways.Application.Abstracciones;
using Ways.Domain.Usuarios;

namespace Ways.Api.Seguridad;

/// <summary>Nombres de claim propios. Los estándar de .NET son URLs larguísimas.</summary>
public static class ClaimsWays
{
    public const string RolId = "ways:id_rol";

    /// <summary>Ausente todavía para toda cuenta: <c>usuarios.id_tenant</c> lo agrega el
    /// retrofit del stage 1 slice 2. Se lee de forma defensiva desde ya (ver
    /// <c>Program.cs</c>, <c>OnValidatePrincipal</c>) para no tener que retocar el pipeline
    /// de autenticación cuando el claim empiece a emitirse.</summary>
    public const string IdTenant = "ways:id_tenant";
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
}
