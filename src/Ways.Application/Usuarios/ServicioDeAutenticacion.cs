using System.Threading;
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
    /// <summary>Hash descartable precalculado una sola vez para todo el proceso, no por
    /// request: <see cref="IHasheadorDeContrasenas"/> es singleton (misma instancia para
    /// todas las requests), así que alcanza con hashear "usuario-inexistente" la primera vez
    /// y reusar el resultado. Hashearlo de nuevo en cada intento de mail inexistente sumaría
    /// un <c>Hashear</c> (mucho más caro que un <c>Verificar</c>) al costo del camino "mail
    /// desconocido", rompiendo la simetría de tiempos con el camino "mail conocido" (que hace
    /// un único <c>Verificar</c>) en vez de preservarla.
    ///
    /// <c>Lazy&lt;T&gt;</c> con <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> (su
    /// modo default) da la misma garantía que el double-checked locking a mano, sin escribirlo:
    /// como mucho un hilo ejecuta el factory, y todos ven el mismo resultado publicado. El
    /// <c>Lazy&lt;string&gt;</c> en sí no puede ser un campo estático construido inline porque
    /// su factory necesita <c>hasheador</c> (dependencia de instancia, no estática) — por eso
    /// se publica una única vez, en frío, con <see cref="LazyInitializer.EnsureInitialized{T}(ref T?, Func{T})"/>
    /// sobre un campo estático: <c>hasheador</c> siempre resuelve al mismo singleton sin
    /// importar qué instancia de <see cref="ServicioDeAutenticacion"/> gana la carrera.</summary>
    private static Lazy<string>? _hashDescartable;

    private string ObtenerHashDescartable() =>
        LazyInitializer.EnsureInitialized(
            ref _hashDescartable, () => new Lazy<string>(() => hasheador.Hashear("usuario-inexistente"))).Value;

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
            // Se verifica igual contra un hash descartable precalculado (ver
            // ObtenerHashDescartable) para que el tiempo de respuesta no delate si el mail
            // existe: acá también hay que pagar exactamente un Verificar, ni uno más.
            hasheador.Verificar(ObtenerHashDescartable(), solicitud.Password);

            // Judgment-day (batch 9, ronda 2): el camino "mail conocido, contraseña
            // incorrecta" persiste RegistrarIntentoFallido (un round trip extra de UPDATE).
            // Sin un round trip equivalente acá, la ausencia de esa segunda ida a la base
            // delataría por temporización que el mail no existe, aunque el hasheo ya esté
            // nivelado. Una consulta descartable sobre un id inexistente paga el mismo costo
            // de ida y vuelta sin escribir ni filtrar nada real.
            await db.Usuarios.AsNoTracking().AnyAsync(u => u.Id == -1, ct);

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
