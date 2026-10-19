using Microsoft.AspNetCore.Authorization;
using Ways.Api.Seguridad;

namespace Ways.Api.ConectorMcp;

/// <summary>Las herramientas del conector evalúan las mismas políticas que la API (<see cref="Politicas"/>) sobre el
/// principal que armó <see cref="ManejadorDeTokenMcp"/>. Sin request o sin principal, ninguna política se
/// cumple.</summary>
public static class PermisoDelConector
{
    /// <summary>Compras y carga de compras: la política de toda escritura de compras en la API
    /// (<see cref="Politicas.GestionDeCatalogo"/>, solo Admin).</summary>
    public const string MensajeSinPermisoDeCompras =
        "Tu usuario de Ways no tiene permiso para operar compras: estas herramientas son solo para administradores.";

    /// <summary>Asociación de códigos: la política de los endpoints del ABM (<see cref="Politicas.GestionDeCatalogo"/>,
    /// solo Admin).</summary>
    public const string MensajeSinRolAdminParaEscribir =
        "Esta herramienta requiere el rol Admin y el usuario que autorizó la conexión no lo tiene. No se escribió nada.";

    public static async Task<bool> CumpleAsync(
        IAuthorizationService autorizacion, IHttpContextAccessor accesor, string politica)
    {
        var usuario = accesor.HttpContext?.User;
        return usuario is not null && (await autorizacion.AuthorizeAsync(usuario, politica)).Succeeded;
    }

    /// <summary>Corta antes de leer cualquier dato: <see cref="ResultadoDeHerramienta"/> devuelve el
    /// <see cref="ErrorDeHerramienta"/> como resultado con <c>isError</c> y <paramref name="mensaje"/>.</summary>
    public static async Task ExigirAsync(
        IAuthorizationService autorizacion, IHttpContextAccessor accesor, string politica, string mensaje)
    {
        if (!await CumpleAsync(autorizacion, accesor, politica))
        {
            throw new ErrorDeHerramienta(mensaje);
        }
    }
}
