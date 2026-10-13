using Ways.Domain.Fiscal;

namespace Ways.Application.Fiscal;

/// <summary>
/// Lo que la respuesta de una venta dice de su facturación. <see cref="Estado"/> es uno de
/// <see cref="EstadosFiscalesDeVenta"/>; <see cref="Motivo"/> es la etiqueta de <c>motivo_facturacion</c> con la que
/// se decidió facturar (solo con una fila de decisión) y <see cref="IdFacturacion"/> el id de esa fila. La emisión del
/// espejo (CAE, comprobante fiscal, error) agrega campos al final de este record sin cambiar los que ya existen.
/// </summary>
public sealed record EstadoFiscalDeVenta(string Estado, string? Motivo = null, int? IdFacturacion = null)
{
    public static EstadoFiscalDeVenta NoCorresponde { get; } = new(EstadosFiscalesDeVenta.NoCorresponde);

    public static EstadoFiscalDeVenta SinDecidir { get; } = new(EstadosFiscalesDeVenta.SinDecidir);
}

public static class EstadosFiscalesDeVenta
{
    /// <summary>Al ticket no le corresponde factura: no se decidió facturarlo, no es una venta, la empresa no puede
    /// facturar todavía o la venta no tiene qué cobrar.</summary>
    public const string NoCorresponde = "no_corresponde";

    /// <summary>Se decidió facturar el ticket y la factura todavía no se emitió.</summary>
    public const string Pendiente = "pendiente";

    /// <summary>La decisión de facturar o no todavía está debida: falló al ingerir la venta y se completa al volver a
    /// llamar (reenvío de la venta o procesador de pendientes). La venta ya está registrada.</summary>
    public const string SinDecidir = "sin_decidir";

    public const string Aprobado = "aprobado";

    public const string Rechazado = "rechazado";

    public const string Cancelado = "cancelado";

    public static string De(EstadoFacturacionDeVenta estado) => estado switch
    {
        EstadoFacturacionDeVenta.Pendiente => Pendiente,
        EstadoFacturacionDeVenta.Emitida => Aprobado,
        EstadoFacturacionDeVenta.Rechazada => Rechazado,
        EstadoFacturacionDeVenta.Cancelada => Cancelado,
        _ => throw new ArgumentOutOfRangeException(nameof(estado), estado, null)
    };
}
