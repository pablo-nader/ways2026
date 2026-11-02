using System.Security.Cryptography;
using System.Text;

namespace Ways.Domain.MercadoPago;

public enum ResultadoDeFirmaDeWebhook
{
    Valida,

    /// <summary>Falta alguna parte, el formato no se entiende o la firma no corresponde al contenido y al secreto.</summary>
    Invalida,

    /// <summary>La firma es de un instante demasiado viejo o demasiado futuro: puede ser un aviso reenviado por un tercero.</summary>
    FueraDeVentana
}

/// <summary>
/// Verificación de la firma <c>x-signature</c> de los avisos (webhooks) de Mercado Pago. El encabezado trae <c>ts=&lt;instante&gt;,v1=&lt;hex&gt;</c>
/// y la firma es el HMAC-SHA256, con el secreto de la aplicación, del manifiesto <c>id:{data.id};request-id:{x-request-id};ts:{ts};</c>.
/// El <c>data.id</c> sale de la URL y, si es alfanumérico, entra al manifiesto en minúsculas (regla de Mercado Pago para los ids).
/// El <c>ts</c> se acepta en segundos o en milisegundos y tiene que caer dentro de la ventana; la comparación es en tiempo constante.
/// Es una función pura: no registra nada y el secreto no sale de acá.
/// </summary>
public static class FirmaDeWebhookMercadoPago
{
    public static readonly TimeSpan AntiguedadMaxima = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan AdelantoMaximo = TimeSpan.FromMinutes(2);

    /// <summary>Desde este valor un <c>ts</c> se lee en milisegundos (en segundos recién se alcanza en el año 5138).</summary>
    private const long UmbralDeMilisegundos = 100_000_000_000L;

    private const int LargoDeLaFirmaEnBytes = 32;

    public static ResultadoDeFirmaDeWebhook Verificar(
        string? encabezadoFirma, string? idDeSolicitud, string? idDelDato, string? secreto, DateTimeOffset ahora)
    {
        if (string.IsNullOrWhiteSpace(secreto)
            || string.IsNullOrWhiteSpace(idDeSolicitud)
            || string.IsNullOrWhiteSpace(idDelDato)
            || !TryLeerEncabezado(encabezadoFirma, out var ts, out var v1)
            || !long.TryParse(ts, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var instante)
            || !TryLeerHex(v1, out var recibida))
        {
            return ResultadoDeFirmaDeWebhook.Invalida;
        }

        var esperada = Calcular(secreto, ArmarManifiesto(idDelDato, idDeSolicitud, ts));
        if (!CryptographicOperations.FixedTimeEquals(esperada, recibida))
        {
            return ResultadoDeFirmaDeWebhook.Invalida;
        }

        return EstaEnVentana(instante, ahora) ? ResultadoDeFirmaDeWebhook.Valida : ResultadoDeFirmaDeWebhook.FueraDeVentana;
    }

    /// <summary><c>id:{data.id};request-id:{x-request-id};ts:{ts};</c> con el id en minúsculas si es alfanumérico.</summary>
    public static string ArmarManifiesto(string idDelDato, string idDeSolicitud, string ts) =>
        $"id:{NormalizarId(idDelDato)};request-id:{idDeSolicitud};ts:{ts};";

    /// <summary>HMAC-SHA256 del manifiesto con el secreto. Lo usan la verificación y los tests que firman un aviso.</summary>
    public static byte[] Calcular(string secreto, string manifiesto) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes(manifiesto));

    private static string NormalizarId(string id) => id.All(char.IsAsciiLetterOrDigit) ? id.ToLowerInvariant() : id;

    private static bool EstaEnVentana(long instante, DateTimeOffset ahora)
    {
        try
        {
            var firmado = instante >= UmbralDeMilisegundos
                ? DateTimeOffset.FromUnixTimeMilliseconds(instante)
                : DateTimeOffset.FromUnixTimeSeconds(instante);
            return firmado >= ahora - AntiguedadMaxima && firmado <= ahora + AdelantoMaximo;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryLeerEncabezado(string? encabezado, out string ts, out string v1)
    {
        ts = string.Empty;
        v1 = string.Empty;
        if (string.IsNullOrWhiteSpace(encabezado))
        {
            return false;
        }

        string? tsLeido = null, v1Leido = null;
        foreach (var parte in encabezado.Split(','))
        {
            var igual = parte.IndexOf('=');
            if (igual <= 0)
            {
                return false;
            }

            var clave = parte[..igual].Trim();
            var valor = parte[(igual + 1)..].Trim();
            switch (clave)
            {
                case "ts" when tsLeido is null:
                    tsLeido = valor;
                    break;
                case "v1" when v1Leido is null:
                    v1Leido = valor;
                    break;
                case "ts" or "v1":
                    return false;
            }
        }

        if (string.IsNullOrEmpty(tsLeido) || string.IsNullOrEmpty(v1Leido))
        {
            return false;
        }

        ts = tsLeido;
        v1 = v1Leido;
        return true;
    }

    private static bool TryLeerHex(string hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length != LargoDeLaFirmaEnBytes * 2 || !hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        bytes = Convert.FromHexString(hex);
        return true;
    }
}
