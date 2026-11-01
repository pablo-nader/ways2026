using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>
/// El vínculo de un punto de venta con su caja (<i>POS</i>) en la cuenta de Mercado Pago de la empresa (doc 10 §7). Hay a lo sumo
/// una fila por punto de venta y cuenta de Mercado Pago: si la empresa se reconecta con otra cuenta, el punto de venta se registra
/// de nuevo y la fila de la cuenta anterior queda como historia. No guarda ningún token.
/// </summary>
public class CajaMercadoPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public int IdPuntoVenta { get; set; }

    /// <summary>El local (<i>store</i>) del que cuelga la caja en Mercado Pago: el de la sucursal que tenía el punto de venta en la
    /// última sincronización. Si el punto de venta se mueve a otra sucursal, registrarlo de nuevo lo reapunta.</summary>
    public int IdLocalMercadoPago { get; set; }

    /// <summary><c>id</c> del local (<i>store</i>) de Mercado Pago bajo el que se registró la caja. Si el local de la sucursal se vuelve a
    /// registrar con otro <c>id</c> (por ejemplo tras un 404), la fila queda desactualizada hasta registrar la caja de nuevo.</summary>
    public long IdStoreMercadoPago { get; set; }

    /// <summary><c>user_id</c> del vendedor dueño de la caja.</summary>
    public long IdUsuarioMercadoPago { get; set; }

    /// <summary><c>id</c> numérico que Mercado Pago asignó a la caja.</summary>
    public long IdPosMercadoPago { get; set; }

    /// <summary><see cref="CajaParaMercadoPago.ExternalIdDe"/>: el identificador de Ways de la caja.</summary>
    public required string ExternalId { get; set; }

    /// <summary>Imagen del QR estático que Mercado Pago devolvió para la caja, si la devolvió.</summary>
    public string? UrlQr { get; set; }

    public DateTimeOffset FechaRegistro { get; set; }

    /// <summary>Cuándo se leyó el punto de venta para enviar sus datos a Mercado Pago por última vez; una edición posterior del
    /// punto de venta (o su paso a otra sucursal) la deja desactualizada.</summary>
    public DateTimeOffset FechaSincronizacion { get; set; }
}
