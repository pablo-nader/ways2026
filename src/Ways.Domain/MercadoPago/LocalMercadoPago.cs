using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>
/// El vínculo de una sucursal con su local (<i>store</i>) en la cuenta de Mercado Pago de la empresa (doc 10 §7). Hay a lo sumo
/// una fila por sucursal y cuenta de Mercado Pago: si la empresa se reconecta con otra cuenta, la sucursal se registra de nuevo y la
/// fila de la cuenta anterior queda como historia. No guarda ningún token.
/// </summary>
public class LocalMercadoPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public int IdSucursal { get; set; }

    /// <summary><c>user_id</c> del vendedor dueño del local.</summary>
    public long IdUsuarioMercadoPago { get; set; }

    /// <summary><c>id</c> numérico que Mercado Pago asignó al local.</summary>
    public long IdStoreMercadoPago { get; set; }

    /// <summary><see cref="LocalParaMercadoPago.ExternalIdDe"/>: el identificador de Ways del local.</summary>
    public required string ExternalId { get; set; }

    public DateTimeOffset FechaRegistro { get; set; }

    /// <summary>Cuándo se leyó la sucursal para enviar sus datos a Mercado Pago por última vez; una edición posterior de la
    /// sucursal la deja desactualizada.</summary>
    public DateTimeOffset FechaSincronizacion { get; set; }
}
