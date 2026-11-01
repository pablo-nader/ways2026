using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>Cómo se cobra una orden de Mercado Pago (<c>cobros_mercadopago.tipo</c>).</summary>
public static class TiposDeCobroMercadoPago
{
    public const string Qr = "qr";

    public const string Point = "point";
}

/// <summary>Estado de un cobro de Ways frente a Mercado Pago (<c>cobros_mercadopago.estado</c>). Solo <see cref="Pendiente"/> cambia.</summary>
public static class EstadosDeCobroMercadoPago
{
    /// <summary>La orden existe y todavía no se pagó, venció ni se canceló.</summary>
    public const string Pendiente = "pendiente";

    public const string Aprobado = "aprobado";

    public const string Cancelado = "cancelado";

    public const string Expirado = "expirado";

    public const string Devuelto = "devuelto";

    /// <summary>Los estados que ocupan el número de venta: a lo sumo un cobro por número (<c>ux_cobros_mercadopago_vivo_numero</c>).</summary>
    public static bool EsVivo(string estado) => estado is Pendiente or Aprobado;
}

/// <summary>
/// Un intento de cobrar una venta del POS con Mercado Pago (doc 10 §7): la orden que Ways creó en la cuenta del vendedor, para el
/// número de venta que el dispositivo había reservado. Cada reintento después de un cobro cancelado o vencido es otra fila con
/// <see cref="Intento"/> mayor. La venta que lo use (E3-T2) apunta a esta fila; por eso no se borra.
/// </summary>
public class CobroMercadoPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public int IdPuntoVenta { get; set; }

    public int IdDispositivo { get; set; }

    public int IdMedioPago { get; set; }

    /// <summary>Número de venta que el dispositivo reservó (<c>SolicitudDeVenta.NumeroPreasignado</c>).</summary>
    public long NumeroPreasignado { get; set; }

    /// <summary>1 para el primer cobro del número; sube con cada cobro nuevo después de uno cancelado o vencido.</summary>
    public int Intento { get; set; }

    /// <see cref="TiposDeCobroMercadoPago"/>.
    public required string Tipo { get; set; }

    public decimal Importe { get; set; }

    /// <summary><c>W{tenant}P{punto de venta}N{número}I{intento}</c>: lo que Mercado Pago devuelve en sus informes.</summary>
    public required string ExternalReference { get; set; }

    /// <summary><c>id</c> de la orden en Mercado Pago (<c>ORD…</c>).</summary>
    public required string IdOrdenMercadoPago { get; set; }

    /// <see cref="EstadosDeCobroMercadoPago"/>.
    public required string Estado { get; set; }

    public string? IdPagoMercadoPago { get; set; }

    /// <summary><c>reference_id</c> numérico del pago, con el que se lo busca en <c>/v1/payments</c> y en los informes.</summary>
    public long? ReferenciaPagoMercadoPago { get; set; }

    /// <summary>Lo que Mercado Pago dice que se pagó; puede diferir de <see cref="Importe"/>.</summary>
    public decimal? ImportePagado { get; set; }

    /// <summary>Contenido EMVCo del QR dinámico; se dibuja en pantalla.</summary>
    public string? QrData { get; set; }

    public DateTimeOffset ExpiraEn { get; set; }

    public DateTimeOffset? FechaUltimaConsulta { get; set; }

    public DateTimeOffset? FechaAprobacion { get; set; }
}
