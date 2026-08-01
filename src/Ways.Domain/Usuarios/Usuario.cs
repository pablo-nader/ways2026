using Ways.Domain.Common;

namespace Ways.Domain.Usuarios;

/// <summary>
/// Cuenta de acceso al sistema.
///
/// A diferencia del legacy, esta tabla es SOLO credenciales y acceso: no lleva saldo,
/// ni acuerdo, ni domicilio. Los clientes de cuenta corriente van a vivir en su propia
/// tabla cuando toque migrarlos.
/// </summary>
public class Usuario : EntidadBase
{
    public int Id { get; set; }

    /// <summary>
    /// Tenant al que pertenece la cuenta. <c>NULL</c> significa staff de plataforma
    /// (doc 09): no hereda de <see cref="Ways.Domain.Common.EntidadTenant"/> a propósito,
    /// ver el comentario de esa clase.
    /// </summary>
    public int? IdTenant { get; set; }

    /// <summary>Nombre de usuario para iniciar sesión. Único por tenant (incluida la
    /// agrupación de plataforma) entre los no eliminados — ver <c>ux_usuarios_usuario</c>.</summary>
    public required string NombreUsuario { get; set; }

    /// <summary>Correo. Único entre los no eliminados.</summary>
    public required string Mail { get; set; }

    public int RolId { get; set; }
    public Rol? Rol { get; set; }

    public EstadoUsuario Estado { get; set; } = EstadoUsuario.Activo;

    /// <summary>
    /// Hash completo en formato autodescriptivo: incluye el salt y los parámetros del
    /// algoritmo. No hay columna de salt aparte a propósito — ver <c>docs/08-usuarios-y-login.md</c>.
    /// </summary>
    public required string PasswordHash { get; set; }

    /// <summary>Identificador del algoritmo con el que se generó el hash, para poder migrarlo.</summary>
    public required string PasswordAlgoritmo { get; set; }

    public DateTimeOffset PasswordActualizadoEl { get; set; }

    public DateTimeOffset? UltimaConexion { get; set; }
    public DateTimeOffset? UltimoIntentoFallido { get; set; }
    public short IntentosFallidos { get; set; }

    public bool PuedeIniciarSesion => Estado == EstadoUsuario.Activo && DeletedAt is null;

    public void RegistrarIngreso(DateTimeOffset momento)
    {
        UltimaConexion = momento;
        IntentosFallidos = 0;
        UltimoIntentoFallido = null;
        UpdatedAt = momento;
    }

    /// <summary>
    /// Suma un intento fallido y bloquea la cuenta al llegar al umbral.
    /// Devuelve true si el intento dejó la cuenta bloqueada.
    /// </summary>
    public bool RegistrarIntentoFallido(DateTimeOffset momento, int umbralBloqueo)
    {
        IntentosFallidos = (short)Math.Min(IntentosFallidos + 1, short.MaxValue);
        UltimoIntentoFallido = momento;
        UpdatedAt = momento;

        if (umbralBloqueo > 0 && IntentosFallidos >= umbralBloqueo && Estado == EstadoUsuario.Activo)
        {
            Estado = EstadoUsuario.Bloqueado;
            return true;
        }

        return false;
    }

    public void Desbloquear(DateTimeOffset momento)
    {
        if (Estado == EstadoUsuario.Bloqueado)
        {
            Estado = EstadoUsuario.Activo;
        }

        IntentosFallidos = 0;
        UltimoIntentoFallido = null;
        UpdatedAt = momento;
    }

    public void CambiarPassword(string hash, string algoritmo, DateTimeOffset momento)
    {
        PasswordHash = hash;
        PasswordAlgoritmo = algoritmo;
        PasswordActualizadoEl = momento;
        UpdatedAt = momento;
    }
}
