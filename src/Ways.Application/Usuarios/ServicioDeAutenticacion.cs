using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

public class ServicioDeAutenticacion(
    IWaysDbContext db,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj,
    ILogger<ServicioDeAutenticacion> log)
{
    /// <summary>
    /// Valida las credenciales. Devuelve el usuario autenticado o lanza <see cref="ErrorDominio"/>.
    ///
    /// El mensaje de error es deliberadamente el mismo para "usuario inexistente" y
    /// "contraseña incorrecta": el legacy los distinguía y eso permite enumerar cuentas.
    /// </summary>
    public async Task<UsuarioAutenticado> IniciarSesionAsync(
        SolicitudDeLogin solicitud,
        CancellationToken ct = default)
    {
        var credencialesInvalidas = new ErrorDominio(
            "credenciales_invalidas", "Usuario o contraseña incorrectos.", 401);

        var nombre = solicitud.Usuario?.Trim() ?? string.Empty;
        if (nombre.Length == 0 || string.IsNullOrEmpty(solicitud.Password))
        {
            throw credencialesInvalidas;
        }

        // La comparación es case-insensitive por el tipo citext de la columna,
        // así que un '==' plano alcanza y usa el índice único.
        var usuario = await db.Usuarios
            .Include(u => u.Rol)
            .FirstOrDefaultAsync(u => u.NombreUsuario == nombre, ct);

        if (usuario is null)
        {
            // Se verifica igual contra un hash descartable para que el tiempo de respuesta
            // no delate si el usuario existe.
            hasheador.Verificar(hasheador.Hashear("usuario-inexistente"), solicitud.Password);
            throw credencialesInvalidas;
        }

        var resultado = hasheador.Verificar(usuario.PasswordHash, solicitud.Password);

        if (resultado == ResultadoVerificacion.Invalida)
        {
            var quedoBloqueado = usuario.RegistrarIntentoFallido(
                reloj.Ahora, PoliticaDeRoles.UmbralBloqueoPorIntentosFallidos);

            await db.SaveChangesAsync(ct);

            if (quedoBloqueado)
            {
                log.LogWarning(
                    "Usuario {Usuario} bloqueado por {Intentos} intentos fallidos.",
                    usuario.NombreUsuario, usuario.IntentosFallidos);
            }

            throw credencialesInvalidas;
        }

        // La contraseña es correcta. Recién ahora se informa el estado de la cuenta.
        if (usuario.DeletedAt is not null)
        {
            throw credencialesInvalidas;
        }

        if (usuario.Estado == EstadoUsuario.Bloqueado)
        {
            throw new ErrorDominio(
                "usuario_bloqueado",
                "La cuenta está bloqueada. Pedile a un administrador que la desbloquee.",
                403);
        }

        if (usuario.Estado == EstadoUsuario.Inactivo)
        {
            throw new ErrorDominio("usuario_inactivo", "La cuenta está inactiva.", 403);
        }

        if (resultado == ResultadoVerificacion.ValidaPeroHayQueRehashear)
        {
            usuario.CambiarPassword(
                hasheador.Hashear(solicitud.Password), hasheador.Algoritmo, reloj.Ahora);
        }

        usuario.RegistrarIngreso(reloj.Ahora);
        await db.SaveChangesAsync(ct);

        return Mapear(usuario);
    }

    public async Task<UsuarioAutenticado?> ObtenerAsync(int usuarioId, CancellationToken ct = default)
    {
        var usuario = await db.Usuarios
            .Include(u => u.Rol)
            .FirstOrDefaultAsync(u => u.Id == usuarioId, ct);

        return usuario is null || !usuario.PuedeIniciarSesion ? null : Mapear(usuario);
    }

    private static UsuarioAutenticado Mapear(Usuario u) => new(
        u.Id,
        u.NombreUsuario,
        u.Mail,
        u.RolId,
        u.Rol?.Nombre ?? ((RolConocido)u.RolId).ToString(),
        u.UltimaConexion);
}
