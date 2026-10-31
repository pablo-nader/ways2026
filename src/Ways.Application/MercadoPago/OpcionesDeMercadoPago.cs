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

    public string UrlAutorizacion { get; set; } = "https://auth.mercadopago.com/authorization";

    public string UrlApi { get; set; } = "https://api.mercadopago.com";

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}
