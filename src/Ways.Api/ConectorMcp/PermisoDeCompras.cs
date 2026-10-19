using Microsoft.AspNetCore.Authorization;
using Ways.Api.Seguridad;

namespace Ways.Api.ConectorMcp;

/// <summary>Las herramientas de compras del conector exigen la misma política que toda escritura de
/// compras en la API (<see cref="Politicas.GestionDeCatalogo"/>, solo Admin), evaluada sobre el
/// principal que armó <see cref="ManejadorDeTokenMcp"/>.</summary>
public static class PermisoDeCompras
{
    public const string MensajeSinPermiso =
        "Tu usuario de Ways no tiene permiso para operar compras: estas herramientas son solo para administradores.";

    /// <summary>Corta antes de leer cualquier dato: <see cref="ResultadoDeHerramienta"/> devuelve el
    /// <see cref="ErrorDeHerramienta"/> como resultado con <c>isError</c> y este mensaje.</summary>
    public static async Task ExigirAsync(IAuthorizationService autorizacion, IHttpContextAccessor accesor)
    {
        var usuario = accesor.HttpContext?.User;
        if (usuario is null || !(await autorizacion.AuthorizeAsync(usuario, Politicas.GestionDeCatalogo)).Succeeded)
        {
            throw new ErrorDeHerramienta(MensajeSinPermiso);
        }
    }
}
