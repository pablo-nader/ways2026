namespace Ways.Application.MercadoPago;

/// <summary>
/// Sección <c>Ways:MercadoPago</c>. Las claves maestras de cifrado no viven acá: se leen por su versión desde
/// <c>Ways:MercadoPago:ClaveMaestraActual</c> y <c>Ways:MercadoPago:ClavesMaestras:&lt;id&gt;</c>, igual que las fiscales.
/// </summary>
public class OpcionesDeMercadoPago
{
    public const string Seccion = "Ways:MercadoPago";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Debe coincidir exactamente con una URI registrada en la aplicación de Mercado Pago.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>Secreto con el que Mercado Pago firma los avisos (panel de la aplicación, Webhooks). Sin él el webhook responde 503.
    /// Nunca se registra ni se devuelve.</summary>
    public string? SecretoWebhook { get; set; }

    public string UrlAutorizacion { get; set; } = "https://auth.mercadopago.com/authorization";

    public string UrlApi { get; set; } = "https://api.mercadopago.com";

    public const int DiasDeAnticipacionPorDefecto = 30;
    public const int IntervaloDeRenovacionPorDefectoEnHoras = 6;

    /// <summary>Se renueva el token cuando le quedan este o menos días. Un valor no positivo vuelve al valor por defecto.</summary>
    public int DiasDeAnticipacionRenovacion { get; set; } = DiasDeAnticipacionPorDefecto;

    /// <summary>Cada cuántas horas corre la renovación automática. Un valor no positivo vuelve al valor por defecto.</summary>
    public int IntervaloRenovacionHoras { get; set; } = IntervaloDeRenovacionPorDefectoEnHoras;

    /// <summary>Apaga la renovación automática sin tocar las credenciales (los tests de integración la manejan a mano).</summary>
    public bool RenovacionAutomatica { get; set; } = true;

    public const int HorasDeMargenDelTokenPorDefecto = 24;

    /// <summary>Antes de llamar a Mercado Pago, un token al que le quedan estas horas o menos se renueva primero. Es distinto de la
    /// ventana de 30 días de la renovación en segundo plano. Un valor no positivo vuelve al valor por defecto.</summary>
    public int HorasDeMargenDelToken { get; set; } = HorasDeMargenDelTokenPorDefecto;

    public const int MinutosDeBackoffDeRenovacionPorDefecto = 15;

    /// <summary>Tras una renovación previa al uso que falló, no se vuelve a intentar antes de estos minutos mientras el token
    /// guardado siga siendo utilizable. Un valor no positivo vuelve al valor por defecto.</summary>
    public int MinutosDeBackoffDeRenovacion { get; set; } = MinutosDeBackoffDeRenovacionPorDefecto;

    public TimeSpan BackoffDeRenovacion =>
        TimeSpan.FromMinutes(
            MinutosDeBackoffDeRenovacion > 0 ? MinutosDeBackoffDeRenovacion : MinutosDeBackoffDeRenovacionPorDefecto);

    public TimeSpan MargenDelToken =>
        TimeSpan.FromHours(HorasDeMargenDelToken > 0 ? HorasDeMargenDelToken : HorasDeMargenDelTokenPorDefecto);

    public TimeSpan VentanaDeRenovacion =>
        TimeSpan.FromDays(DiasDeAnticipacionRenovacion > 0 ? DiasDeAnticipacionRenovacion : DiasDeAnticipacionPorDefecto);

    public TimeSpan IntervaloDeRenovacion =>
        TimeSpan.FromHours(IntervaloRenovacionHoras > 0 ? IntervaloRenovacionHoras : IntervaloDeRenovacionPorDefectoEnHoras);

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}
