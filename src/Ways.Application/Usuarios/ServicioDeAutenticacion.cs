using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

public class ServicioDeAutenticacion(
    IWaysDbContext db,
    [FromKeyedServices(ClavesDeContexto.Plataforma)] IWaysDbContext dbPlataforma,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj,
    ILogger<ServicioDeAutenticacion> log)
{
    /// <summary>
    /// Valida las credenciales. Devuelve el usuario autenticado o lanza <see cref="ErrorDominio"/>.
    ///
    /// El mensaje de error es deliberadamente el mismo para "mail inexistente" y
    /// "contraseña incorrecta": el legacy los distinguía y eso permite enumerar cuentas.
    ///
    /// Login es por <c>mail</c> (flow B, doc 09 stage 1): el mail resuelve la cuenta — y con
    /// ella el tenant — sin que el request cargue ningún contexto de tenant. El llamador
    /// (<c>AuthEndpoints</c>) tiene que dejar el contexto de tenant de la request en
    /// <see cref="Abstracciones.ModoDeAcceso.Login"/> antes de invocar este método: es el
    /// único modo bajo el cual RLS permite leer/actualizar <c>usuarios</c> sin un tenant
    /// resuelto todavía.
    /// </summary>
    public async Task<UsuarioAutenticado> IniciarSesionAsync(
        SolicitudDeLogin solicitud,
        CancellationToken ct = default)
    {
        var credencialesInvalidas = new ErrorDominio(
            "credenciales_invalidas", "Mail o contraseña incorrectos.", 401);

        var mail = solicitud.Mail?.Trim() ?? string.Empty;
        if (mail.Length == 0 || string.IsNullOrEmpty(solicitud.Password))
        {
            throw credencialesInvalidas;
        }

        // La comparación es case-insensitive por el tipo citext de la columna,
        // así que un '==' plano alcanza y usa el índice único.
        var usuario = await db.Usuarios
            .Include(u => u.Rol)
            .FirstOrDefaultAsync(u => u.Mail == mail, ct);

        if (usuario is null)
        {
            // Se verifica igual contra un hash descartable para que el tiempo de respuesta
            // no delate si el mail existe.
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

        // El tenant se consulta con un contexto de plataforma aparte (no el de la request,
        // que sigue en modo login sin tenant resuelto): así RLS no necesita una policy de
        // lectura adicional sobre `tenants` solo para este chequeo (doc 09, ADR-4).
        if (usuario.IdTenant is not null)
        {
            var tenant = await dbPlataforma.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == usuario.IdTenant.Value, ct);

            if (tenant is null || !tenant.PuedeOperar)
            {
                throw new ErrorDominio(
                    "tenant_suspendido",
                    "El tenant está suspendido o dado de baja. Contactá al administrador de la plataforma.",
                    403);
            }
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
        u.UltimaConexion,
        u.IdTenant);
}
