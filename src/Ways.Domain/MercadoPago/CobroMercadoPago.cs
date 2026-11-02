using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>Cómo se cobra una orden de Mercado Pago (<c>cobros_mercadopago.tipo</c>).</summary>
public static class TiposDeCobroMercadoPago
{
    public const string Qr = "qr";

    public const string Point = "point";
}

/// <summary>
/// Estado de un cobro de Ways frente a Mercado Pago (<c>cobros_mercadopago.estado</c>). Solo <see cref="Creando"/> y
/// <see cref="Pendiente"/> cambian: <see cref="Aprobado"/> es final en esta etapa y <see cref="Devuelto"/> solo se observa mientras el cobro
/// estaba pendiente (una devolución posterior a la aprobación es de la conciliación, E6). <see cref="SinVenta"/> es el cobro aprobado
/// que el cajero apartó porque su venta no se pudo cerrar: ya no ocupa el número y lo resuelve la conciliación.
/// </summary>
public static class EstadosDeCobroMercadoPago
{
    /// <summary>
    /// La fila se reservó (con la orden que se va a pedir) y todavía no se sabe qué respondió Mercado Pago: la orden puede existir allá
    /// sin que Ways la haya guardado. Reintentar reenvía la misma orden con la misma clave de idempotencia.
    /// </summary>
    public const string Creando = "creando";

    /// <summary>La orden existe y todavía no se pagó, venció ni se canceló.</summary>
    public const string Pendiente = "pendiente";

    public const string Aprobado = "aprobado";

    public const string Cancelado = "cancelado";

    public const string Expirado = "expirado";

    /// <summary>Mercado Pago informó <c>refunded</c> mientras el cobro seguía pendiente; la devolución de un cobro ya aprobado no se observa acá.</summary>
    public const string Devuelto = "devuelto";

    /// <summary>
    /// Un cobro aprobado que ninguna venta usó y que el dispositivo apartó (<c>POST …/apartar</c>): el dinero entró, pero esa venta no
    /// se cerró. No es vivo (libera el número y el medio), no paga ninguna venta (solo <see cref="Aprobado"/> la paga) y queda listado
    /// para la conciliación (E6). Es final.
    /// </summary>
    public const string SinVenta = "sin_venta";

    /// <summary>
    /// Solo Point: Mercado Pago informó la orden <c>failed</c> (el pago se rechazó o falló en la terminal). No es vivo: libera el número
    /// y el medio, el cajero puede cobrar de nuevo (otro intento) o usar otro medio. Mercado Pago puede informar después un pago de esa
    /// orden: por eso se sigue leyendo hasta que vence su plazo más el margen de abandono, y un pago legible lo pasa a
    /// <see cref="SinVenta"/> (nunca a <see cref="Aprobado"/>: su número pudo reutilizarse).
    /// </summary>
    public const string Fallido = "fallido";

    /// <summary>Los estados que ocupan el número de venta: a lo sumo un cobro por número (<c>ux_cobros_mercadopago_vivo_numero</c>).</summary>
    public static bool EsVivo(string estado) => estado is Creando or Pendiente or Aprobado;
}

/// <summary>
/// Dónde está un cobro Point pendiente según la última lectura de la orden (<c>cobros_mercadopago.situacion_terminal</c>); nulo
/// mientras la orden está <c>created</c> (la terminal todavía no la tomó) y en cualquier cobro que no sea Point o ya no esté pendiente.
/// </summary>
public static class SituacionesDeTerminalMercadoPago
{
    /// <summary>La orden llegó a la terminal (<c>at_terminal</c>) y el cliente la está pagando.</summary>
    public const string EnTerminal = "en_terminal";

    /// <summary>Mercado Pago pide mirar la terminal (<c>action_required</c>): el cajero revisa el resultado ahí.</summary>
    public const string RevisarTerminal = "revisar_terminal";
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

    /// <summary>
    /// <c>external_id</c> de la caja con el que se pidió la orden. Se guarda para que reintentar una creación reenvíe la misma
    /// orden aunque la caja haya cambiado desde entonces.
    /// </summary>
    public required string ExternalPosId { get; set; }

    /// <summary>
    /// <c>X-Idempotency-Key</c> de <c>POST /v1/orders</c>: huella del cuerpo completo de la orden. Mercado Pago devuelve la misma orden
    /// para la misma clave y el mismo cuerpo.
    /// </summary>
    public required string ClaveIdempotencia { get; set; }

    /// <summary><c>id</c> de la orden en Mercado Pago (<c>ORD…</c>); nulo solo mientras el cobro está <see cref="EstadosDeCobroMercadoPago.Creando"/> o si la fila se abandonó sin que se confirmara ninguna orden y quedó <see cref="EstadosDeCobroMercadoPago.Expirado"/> (o <see cref="EstadosDeCobroMercadoPago.Cancelado"/>, en un Point que Mercado Pago rechazó por terminal ocupada).</summary>
    public string? IdOrdenMercadoPago { get; set; }

    /// <see cref="EstadosDeCobroMercadoPago"/>.
    public required string Estado { get; set; }

    public string? IdPagoMercadoPago { get; set; }

    /// <summary><c>reference_id</c> numérico del pago, con el que se lo busca en <c>/v1/payments</c> y en los informes.</summary>
    public long? ReferenciaPagoMercadoPago { get; set; }

    /// <summary>Lo que Mercado Pago dice que se pagó; puede diferir de <see cref="Importe"/>.</summary>
    public decimal? ImportePagado { get; set; }

    /// <summary>Solo Point: la terminal (<c>id_terminal_point</c> de la caja) a la que se pidió la orden, tal como se pidió: reintentar una
    /// creación reenvía la misma orden aunque la caja haya cambiado de terminal. Nulo exactamente en los cobros QR.</summary>
    public string? IdTerminalPoint { get; set; }

    /// <summary><see cref="SituacionesDeTerminalMercadoPago"/>; solo un cobro Point pendiente la tiene.</summary>
    public string? SituacionTerminal { get; set; }

    /// <summary>Contenido EMVCo del QR dinámico; se dibuja en pantalla. Un cobro Point no tiene QR.</summary>
    public string? QrData { get; set; }

    public DateTimeOffset ExpiraEn { get; set; }

    public DateTimeOffset? FechaUltimaConsulta { get; set; }

    public DateTimeOffset? FechaAprobacion { get; set; }

    /// <summary>Cuándo el dispositivo apartó el cobro aprobado (<see cref="EstadosDeCobroMercadoPago.SinVenta"/>); nulo en cualquier otro estado.</summary>
    public DateTimeOffset? FechaApartado { get; set; }

    /// <summary>
    /// La primera vez que Mercado Pago informó la orden <c>processed</c> sin un pago con importe e id legibles: el cliente ya pagó, pero
    /// Ways todavía no puede aprobar el cobro. Mientras esté puesta y el cobro siga pendiente no se muestra el QR ni se puede cancelar;
    /// la consulta sigue refrescando y un pago legible posterior lo aprueba. Una vez puesta no se borra.
    /// </summary>
    public DateTimeOffset? FechaProcesadaMercadoPago { get; set; }

    /// <summary>La venta que este cobro aprobado pagó (E3-T2); nulo hasta que una venta lo liga. Único: un cobro paga a lo sumo una venta.
    /// Anular la venta lo deja ligado (el dinero entró; su devolución es de la conciliación).</summary>
    public int? IdComprobanteVenta { get; set; }
}
