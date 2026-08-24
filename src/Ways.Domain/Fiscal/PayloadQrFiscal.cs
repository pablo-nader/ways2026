using System.Globalization;
using System.Text;

namespace Ways.Domain.Fiscal;

/// <summary>
/// El QR fiscal de la RG 4291 (proposal.md §75-76, design.md:366, spec comprobante-fiscal: "The RG
/// 4291 QR Payload Uses A Synthetic codAut In 19a"). Los 13 campos exactos del payload JSON,
/// codificados a base64 y embebidos en <c>https://www.afip.gob.ar/fe/qr/?p=&lt;base64&gt;</c>.
///
/// <c>codAut</c> es "sintético" en 19a en el sentido de la spec: es el CAE que devuelve el MOCK de
/// WSFE (nunca uno emitido por un servidor ARCA real, invariante I4/OD1), pero estructuralmente
/// correcto (14 dígitos) — no un valor inventado aparte del que el comprobante ya persistió.
///
/// Hand-rolled, no <c>System.Text.Json</c> (mismo criterio que <c>MapeadorWsfe.FormatearMoneda</c>,
/// design D3): un test "byte a byte contra un vector armado a mano" necesita control total del
/// formato — el <c>scale</c> implícito de <c>decimal</c> al serializar con el serializador genérico
/// no está garantizado dígito a dígito (p. ej. <c>3m</c> vs. <c>3.00m</c> imprimen distinto según el
/// scale interno), así que <see cref="FormatearMoneda"/> fija el formato a mano, igual que el resto
/// del programa fiscal.
/// </summary>
public static class PayloadQrFiscal
{
    private const string UrlBase = "https://www.afip.gob.ar/fe/qr/?p=";

    /// <summary><c>tipoCodAut</c> siempre <c>"E"</c> (CAE por webservice) — 19a nunca emite CAEA
    /// (proposal decisión 4, OD1: CAEA es contingencia, 19c).</summary>
    private const string TipoCodAutWebservice = "E";

    public static string Construir(
        DateOnly fecha,
        long cuitEmisor,
        int ptoVta,
        short tipoCmp,
        long nroCmp,
        decimal importe,
        short tipoDocRec,
        long nroDocRec,
        long codAut)
    {
        var json =
            "{\"ver\":1" +
            ",\"fecha\":\"" + fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\"" +
            ",\"cuit\":" + cuitEmisor.ToString(CultureInfo.InvariantCulture) +
            ",\"ptoVta\":" + ptoVta.ToString(CultureInfo.InvariantCulture) +
            ",\"tipoCmp\":" + tipoCmp.ToString(CultureInfo.InvariantCulture) +
            ",\"nroCmp\":" + nroCmp.ToString(CultureInfo.InvariantCulture) +
            ",\"importe\":" + FormatearMoneda(importe) +
            ",\"moneda\":\"PES\"" +
            ",\"ctz\":" + FormatearMoneda(1m) +
            ",\"tipoDocRec\":" + tipoDocRec.ToString(CultureInfo.InvariantCulture) +
            ",\"nroDocRec\":" + nroDocRec.ToString(CultureInfo.InvariantCulture) +
            ",\"tipoCodAut\":\"" + TipoCodAutWebservice + "\"" +
            ",\"codAut\":" + codAut.ToString(CultureInfo.InvariantCulture) +
            "}";

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return UrlBase + base64;
    }

    private static string FormatearMoneda(decimal valor) => valor.ToString("0.00", CultureInfo.InvariantCulture);
}
