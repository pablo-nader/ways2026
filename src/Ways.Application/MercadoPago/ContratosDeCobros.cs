namespace Ways.Application.MercadoPago;

/// <summary>
/// Pedido del dispositivo para cobrar una venta con QR de Mercado Pago. <see cref="NumeroPreasignado"/> es el número que el
/// dispositivo reservó para la venta (mismo tipo que <c>SolicitudDeVenta.NumeroPreasignado</c>); el punto de venta sale de la
/// sesión del dispositivo, nunca del cuerpo.
/// </summary>
public sealed record SolicitudDeCobroMercadoPago(int IdMedioPago, long NumeroPreasignado, decimal Importe);

/// <summary>
/// Un cobro tal como lo ve el dispositivo. <see cref="QrData"/> solo viaja mientras el cobro está pendiente. <see cref="ImporteDistinto"/>
/// avisa que Mercado Pago aprobó un importe pagado distinto de <see cref="Importe"/> (<see cref="ImportePagado"/>): el cobro sigue
/// aprobado y decide el cajero. <see cref="VerificandoPago"/> es verdadero cuando el cobro está pendiente pero no hay QR
/// para mostrar (una orden ya procesada que se recuperó sin un pago legible, o que Mercado Pago ya informó pagada): el dispositivo
/// muestra "verificando el pago" en lugar de un QR y vuelve a consultar. <see cref="PagoInformadoSinDatos"/> es verdadero cuando
/// Mercado Pago informó la orden procesada pero sin un pago legible (importe o id): el cliente ya pagó, el QR no viaja, el cobro no se
/// puede cancelar y el dispositivo dice "Mercado Pago informa el pago, falta confirmarlo" mientras sigue consultando.
/// <see cref="UsadoEnVenta"/> es verdadero cuando una venta ya quedó ligada al cobro: el dispositivo descarta su reserva local.
/// <see cref="Tipo"/> es <c>qr</c> o <c>point</c>; un cobro Point nunca lleva <see cref="QrData"/> y nombra su terminal en
/// <see cref="IdTerminal"/>. Mientras un cobro Point está pendiente, <see cref="EnTerminal"/> avisa que la orden llegó a la terminal
/// (<c>at_terminal</c>: ya no se cancela desde el POS) y <see cref="RevisarTerminal"/> que Mercado Pago pide mirar el resultado en
/// la terminal (<c>action_required</c>). Un cobro <c>fallido</c> (Point rechazado en la terminal) ya no está vivo.
/// </summary>
public sealed record CobroDeMercadoPago(
    int IdCobro,
    string Estado,
    string? QrData,
    DateTimeOffset ExpiraEn,
    decimal Importe,
    decimal? ImportePagado,
    bool ImporteDistinto,
    bool VerificandoPago,
    bool PagoInformadoSinDatos,
    bool UsadoEnVenta,
    string Tipo,
    string? IdTerminal,
    bool EnTerminal,
    bool RevisarTerminal);

/// <summary>
/// Un cobro apartado (<c>sin_venta</c>) tal como lo lista el back office para la conciliación: el dinero entró en Mercado Pago pero
/// ninguna venta lo usó.
/// </summary>
public sealed record CobroSinVentaListado(
    int IdCobro,
    int IdPuntoVenta,
    int IdMedioPago,
    long NumeroPreasignado,
    decimal Importe,
    decimal? ImportePagado,
    string? IdPagoMercadoPago,
    string? IdOrdenMercadoPago,
    DateTimeOffset? FechaAprobacion,
    DateTimeOffset? FechaApartado);

public static class CodigosDeErrorDeCobrosMercadoPago
{
    public const string EnCurso = "cobro_mercadopago_en_curso";

    public const string YaAprobado = "cobro_mercadopago_ya_aprobado";

    public const string PagadoSinDatos = "cobro_mercadopago_pagado_sin_datos";

    public const string MedioInvalido = "cobro_mercadopago_medio_invalido";

    public const string ImporteInvalido = "cobro_mercadopago_importe_invalido";

    public const string NumeroInvalido = "cobro_mercadopago_numero_invalido";

    /// <summary>El medio es Point y la caja del punto de venta no tiene terminal asignada (<c>cajas_mercadopago.id_terminal_point</c>).</summary>
    public const string CajaSinTerminal = "mercadopago_caja_sin_terminal";

    /// <summary>Mercado Pago contestó 409 <c>already_queued_order_for_terminal</c>: la terminal ya tiene otra orden esperando.</summary>
    public const string TerminalOcupada = "mercadopago_terminal_ocupada";

    /// <summary>Cancelar un cobro Point que ya llegó a la terminal: Mercado Pago solo cancela una orden <c>created</c>.</summary>
    public const string CancelarEnLaTerminal = "mercadopago_cancelar_en_la_terminal";

    /// <summary>Mercado Pago contestó 409 al cancelar y la lectura posterior dejó el cobro pendiente y fuera de la terminal: la cancelación no se aplicó.</summary>
    public const string CancelacionNoAplicada = "mercadopago_cancelacion_no_aplicada";
}

/// <summary>
/// Cuerpo de <c>POST /v1/orders</c> para un cobro con QR dinámico. Los nombres salen en snake_case y los importes como cadena con dos
/// decimales, que es la forma que documenta Mercado Pago.
/// </summary>
public sealed record SolicitudDeOrdenQrMercadoPago(
    string Type,
    string ExternalReference,
    string TotalAmount,
    string ExpirationTime,
    ConfiguracionDeOrdenMercadoPago Config,
    IReadOnlyList<ItemDeOrdenMercadoPago> Items);

public sealed record ConfiguracionDeOrdenMercadoPago(ConfiguracionDeQrDeOrdenMercadoPago Qr);

public sealed record ConfiguracionDeQrDeOrdenMercadoPago(string ExternalPosId, string Mode);

public sealed record ItemDeOrdenMercadoPago(string Title, string UnitPrice, int Quantity, string UnitMeasure);

/// <summary>
/// Cuerpo de <c>POST /v1/orders</c> para un cobro en la terminal Point: la terminal y el pago a cobrar (<c>print_on_terminal</c> es
/// <c>no_ticket</c>: el ticket lo imprime Ways). Mismos nombres snake_case e importes como cadena con dos decimales. No lleva
/// <c>total_amount</c>: el ejemplo documentado de Point trae solo <c>transactions.payments[{amount}]</c> y el total no está verificado.
/// </summary>
public sealed record SolicitudDeOrdenPointMercadoPago(
    string Type,
    string ExternalReference,
    string ExpirationTime,
    ConfiguracionDeOrdenPointMercadoPago Config,
    TransaccionesDeSolicitudPointMercadoPago Transactions);

public sealed record ConfiguracionDeOrdenPointMercadoPago(ConfiguracionDePointDeOrdenMercadoPago Point);

public sealed record ConfiguracionDePointDeOrdenMercadoPago(string TerminalId, string PrintOnTerminal);

public sealed record TransaccionesDeSolicitudPointMercadoPago(IReadOnlyList<PagoDeSolicitudPointMercadoPago> Payments);

public sealed record PagoDeSolicitudPointMercadoPago(string Amount);

/// <summary>Una orden tal como la devuelven la creación y la lectura; lo que Ways no usa se ignora.</summary>
public sealed record OrdenDeMercadoPago(
    string? Id, string? Status, TypeResponseDeOrdenMercadoPago? TypeResponse, TransaccionesDeOrdenMercadoPago? Transactions);

public sealed record TypeResponseDeOrdenMercadoPago(string? QrData);

public sealed record TransaccionesDeOrdenMercadoPago(IReadOnlyList<PagoDeOrdenMercadoPago>? Payments);

public sealed record PagoDeOrdenMercadoPago(
    string? Id, string? Status, decimal? Amount, decimal? PaidAmount, System.Text.Json.JsonElement? ReferenceId)
{
    /// <summary>El <c>reference_id</c> numérico del pago; si Mercado Pago lo manda con otra forma no se guarda, pero no impide leer la orden.</summary>
    public long? ReferenciaNumerica => ReferenceId switch
    {
        { ValueKind: System.Text.Json.JsonValueKind.Number } n when n.TryGetInt64(out var v) => v,
        { ValueKind: System.Text.Json.JsonValueKind.String } s when long.TryParse(s.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
        _ => null
    };
}
