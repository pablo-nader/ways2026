using Ways.Domain.Common;

namespace Ways.Domain.Dispositivos;

/// <summary>
/// Un dispositivo de escritorio (Tauri) vinculado a un punto de venta (stage-desktop-pos).
/// Se vincula una vez, con la sesión de un Admin, y después cualquier cajero inicia sesión
/// contra ESE dispositivo con usuario + contraseña — el dispositivo es la unidad de confianza,
/// no la sesión del cajero (que dura hasta logout o revocación).
///
/// El secreto en sí NUNCA se persiste: solo viaja una vez, en la cookie HttpOnly
/// <c>ways.dispositivo</c>, y acá se guarda su hash SHA-256 (<see cref="TokenHash"/>).
/// </summary>
public class Dispositivo : EntidadTenant
{
    public int Id { get; set; }

    /// <summary>FK compuesta a <see cref="Ways.Domain.Organizacion.PuntoVenta"/> (ver
    /// configuración de EF): un dispositivo de un tenant no puede vincularse al punto de venta
    /// de otro tenant ni por bug (ADR-9).</summary>
    public int IdPuntoVenta { get; set; }

    /// <summary>Etiqueta humana del dispositivo ("Caja 1", "Mostrador"), elegida por el Admin
    /// que lo vincula.</summary>
    public required string Nombre { get; set; }

    /// <summary>SHA-256 hex (64 caracteres) del secreto de 32 bytes generado al vincular. El
    /// secreto en texto plano no se guarda en ningún lado — ver <see cref="TokenDeDispositivo"/>.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Quién vinculó el dispositivo (el Admin autenticado en ese momento).</summary>
    public int IdUsuarioAlta { get; set; }

    public DateTimeOffset? UltimoUsoAt { get; set; }

    public void RegistrarUso(DateTimeOffset momento)
    {
        UltimoUsoAt = momento;
        UpdatedAt = momento;
    }

    public void Revocar(DateTimeOffset momento)
    {
        DeletedAt = momento;
        UpdatedAt = momento;
    }
}
