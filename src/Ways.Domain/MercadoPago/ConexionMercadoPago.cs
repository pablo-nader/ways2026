using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>
/// Conexión OAuth de una empresa con la cuenta de Mercado Pago de su vendedor (doc 10 §7). Una empresa
/// tiene a lo sumo una activa, y una cuenta de Mercado Pago (<see cref="IdUsuarioMercadoPago"/>) está activa en
/// a lo sumo una empresa de todo el sistema: los índices únicos parciales lo garantizan en la base.
///
/// Los tokens viajan cifrados con AES-256-GCM, cada uno con su propio nonce y tag, y nunca salen en un DTO, un
/// log ni una fila de auditoría. Al desconectar, los seis campos de cifrado quedan en <c>NULL</c>: una conexión
/// desconectada es solo historia.
/// </summary>
public class ConexionMercadoPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    /// <summary>Cuenta de tesorería de la misma empresa que representa esta cuenta de Mercado Pago.</summary>
    public int IdCuentaTesoreria { get; set; }

    /// <summary><c>user_id</c> del vendedor, tal como lo devuelve el canje del código.</summary>
    public long IdUsuarioMercadoPago { get; set; }

    public byte[]? AccessTokenCifrado { get; set; }

    public byte[]? AccessTokenNonce { get; set; }

    public byte[]? AccessTokenTag { get; set; }

    public byte[]? RefreshTokenCifrado { get; set; }

    public byte[]? RefreshTokenNonce { get; set; }

    public byte[]? RefreshTokenTag { get; set; }

    /// <summary>Versión de la clave maestra que cifró los dos tokens. No entra al AAD: la clave ya se elige por
    /// esta columna, no es identidad de la fila.</summary>
    public required string IdClaveMaestra { get; set; }

    public DateTimeOffset AccessTokenExpiraEn { get; set; }

    public EstadoConexionMercadoPago Estado { get; set; }

    /// <summary>Usuario de Ways que inició la conexión.</summary>
    public int IdUsuarioConecto { get; set; }

    public DateTimeOffset FechaConexion { get; set; }

    public DateTimeOffset? FechaDesconexion { get; set; }

    public DateTimeOffset? FechaUltimaRenovacion { get; set; }

    /// <summary>Cuándo la renovación automática la marcó como <see cref="EstadoConexionMercadoPago.RequiereReconexion"/>.</summary>
    public DateTimeOffset? FechaRequiereReconexion { get; set; }
}
