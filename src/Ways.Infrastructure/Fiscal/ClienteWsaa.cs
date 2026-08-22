using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Ways.Application.Fiscal;
using Ways.Domain.Common;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Implementa <see cref="IClienteWsaa"/> contra un mock local en 19a (decisión 8 del proposal: el
/// mock encarna el contrato del manual, versionado en <c>REVISION.md</c>). Arma la TRA
/// (<see cref="GeneradorDeTra"/>), la firma (<see cref="FirmanteCms"/>), la envuelve
/// (<c>SobreSoap</c>, caja negra — este archivo nunca nombra el protocolo por su nombre) y mapea
/// la respuesta o la falla a <see cref="TicketDeAcceso"/>/<see cref="ErrorDominio"/>. Sin caller
/// de producción hasta la slice 5.
/// </summary>
public sealed class ClienteWsaa(HttpClient http, GeneradorDeTra generadorDeTra) : IClienteWsaa
{
    private const string Operacion = "loginCms";

    public async Task<TicketDeAcceso> ObtenerTicketAsync(SolicitudDeTicket solicitud, CancellationToken ct)
    {
        var tra = generadorDeTra.Construir(solicitud.Clave.Servicio);
        var cms = FirmanteCms.FirmarBase64(tra, solicitud.Certificado);
        var sobre = SobreSoap.Construir(SobreSoap.EspacioWsaa, Operacion, new XElement("in0", cms));

        using var mensaje = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(sobre, Encoding.UTF8, "text/xml")
        };
        mensaje.Headers.TryAddWithoutValidation(
            "SOAPAction", SobreSoap.AccionDe(SobreSoap.EspacioWsaa, Operacion));

        using var respuestaHttp = await http.SendAsync(mensaje, ct);
        var textoRespuesta = await respuestaHttp.Content.ReadAsStringAsync(ct);
        var respuesta = SobreSoap.Leer(textoRespuesta);

        if (respuesta.Fault is { } fault)
        {
            throw MapearFalla(fault);
        }

        return LeerTicket(respuesta.Cuerpo!);
    }

    /// <summary>El body no-fault es <c>loginCmsResponse</c> con un único hijo
    /// <c>loginCmsReturn</c> cuyo TEXTO es el <c>loginTicketResponse</c> completo, escapado — el
    /// wire real de WSAA anida un documento XML dentro de otro (fixture: <c>LoginTicketResponse.xml</c>,
    /// <c>REVISION.md</c>).</summary>
    private static TicketDeAcceso LeerTicket(XElement cuerpo)
    {
        var xmlInterno = cuerpo.Elements().First(e => e.Name.LocalName == "loginCmsReturn").Value;
        var raiz = XDocument.Parse(xmlInterno).Root!;

        var credenciales = raiz.Elements().First(e => e.Name.LocalName == "credentials");
        var token = credenciales.Elements().First(e => e.Name.LocalName == "token").Value;
        var sign = credenciales.Elements().First(e => e.Name.LocalName == "sign").Value;

        var header = raiz.Elements().First(e => e.Name.LocalName == "header");
        var expiracionTexto = header.Elements().First(e => e.Name.LocalName == "expirationTime").Value;
        var expiracion = DateTimeOffset.Parse(expiracionTexto, CultureInfo.InvariantCulture);

        return new TicketDeAcceso(token, sign, expiracion);
    }

    /// <summary>design.md: The ARCA error taxonomy → domain codes (fila WSAA). La numeración
    /// 500/501/502/600/601/602 es la del proposal, no la de los strings de fault simbólicos del
    /// manual (T3) — confirmar la numeración real es tarea de 19b.</summary>
    private static ErrorDominio MapearFalla(FaultSoap fault)
    {
        var codigo = int.Parse(fault.FaultCode, CultureInfo.InvariantCulture);

        return codigo switch
        {
            500 or 501 or 502 => ErrorDominio.Conflicto("certificado_fiscal_rechazado", fault.FaultString),
            600 or 602 => ErrorDominio.Conflicto("certificado_fiscal_sin_autorizacion", fault.FaultString),
            601 => new ErrorDominio("wsaa_en_intervalo_minimo", fault.FaultString, 503),
            _ => new ErrorDominio("wsaa_error_no_mapeado", fault.FaultString, 502)
        };
    }
}
