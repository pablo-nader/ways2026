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
    bool UsadoEnVenta);

public static class CodigosDeErrorDeCobrosMercadoPago
{
    public const string EnCurso = "cobro_mercadopago_en_curso";

    public const string YaAprobado = "cobro_mercadopago_ya_aprobado";

    public const string PagadoSinDatos = "cobro_mercadopago_pagado_sin_datos";

    public const string MedioInvalido = "cobro_mercadopago_medio_invalido";

    public const string ImporteInvalido = "cobro_mercadopago_importe_invalido";

    public const string NumeroInvalido = "cobro_mercadopago_numero_invalido";
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
