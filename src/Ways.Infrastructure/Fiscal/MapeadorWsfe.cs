using System.Globalization;
using System.Xml.Linq;
using Ways.Application.Fiscal;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Arma los sobres SOAP de WSFE — separado de <see cref="ClienteWsfe"/> a propósito (design D2/D3,
/// mismo criterio que <c>GeneradorDeTra</c>/<c>FirmanteCms</c> en la slice 2): los goldens (targets
/// 37-39) lo prueban DIRECTO, sin <c>HttpClient</c>/TA real/circuito de por medio. Cada operación
/// delega el sobre completo a <c>SobreSoap.Construir</c> — este archivo nunca nombra SOAP por su
/// nombre (target 29, la caja negra sigue siendo <c>SobreSoap</c>). Los elementos que arma acá NO
/// llevan el prefijo <c>ar:</c> (solo la raíz de la operación lo lleva, vía
/// <c>SobreSoap.Construir</c>) — mismo patrón que el <c>in0</c> sin prefijo de WSAA
/// (<c>LoginCmsEnvelopeGolden.xml</c>), no una elección nueva de esta slice.
///
/// <b>T4 reasertada (design.md:648-651, tasks.md Slice 3 header)</b>: el orden/nombre exacto de
/// cada elemento de <c>FeDetReq</c> es una transcripción del contrato público de WSFEv1 hecha por
/// este agente sin acceso al PDF del manual — igual que las fixtures de la slice 2, ningún test de
/// 19a puede detectar un error de transcripción contra el cable real; confirmarlo es tarea de 19b.
/// </summary>
public static class MapeadorWsfe
{
    public static string ConstruirFecaeSolicitar(TicketDeAcceso ticket, string cuitRepresentado, SolicitudDeCae s) =>
        SobreSoap.Construir(SobreSoap.EspacioWsfe, "FECAESolicitar",
            ConstruirAuth(ticket, cuitRepresentado), ConstruirFeCaeReq(s));

    public static string ConstruirFeCompConsultar(
        TicketDeAcceso ticket, string cuitRepresentado, ClaveDeSerie clave, long numero) =>
        SobreSoap.Construir(SobreSoap.EspacioWsfe, "FECompConsultar",
            ConstruirAuth(ticket, cuitRepresentado),
            new XElement("FeCompConsReq",
                new XElement("CbteTipo", clave.CbteTipo),
                new XElement("CbteNro", numero),
                new XElement("PtoVta", clave.PtoVta)));

    public static string ConstruirFeCompUltimoAutorizado(
        TicketDeAcceso ticket, string cuitRepresentado, ClaveDeSerie clave) =>
        SobreSoap.Construir(SobreSoap.EspacioWsfe, "FECompUltimoAutorizado",
            ConstruirAuth(ticket, cuitRepresentado),
            new XElement("PtoVta", clave.PtoVta),
            new XElement("CbteTipo", clave.CbteTipo));

    public static string ConstruirParametros(TicketDeAcceso ticket, string cuitRepresentado, string operacion) =>
        SobreSoap.Construir(SobreSoap.EspacioWsfe, operacion, ConstruirAuth(ticket, cuitRepresentado));

    private static XElement ConstruirAuth(TicketDeAcceso ticket, string cuit) =>
        new("Auth",
            new XElement("Token", ticket.Token),
            new XElement("Sign", ticket.Sign),
            new XElement("Cuit", cuit));

    private static XElement ConstruirFeCaeReq(SolicitudDeCae s) =>
        new("FeCAEReq",
            new XElement("FeCabReq",
                new XElement("CantReg", 1),
                new XElement("PtoVta", s.Serie.PtoVta),
                new XElement("CbteTipo", s.Serie.CbteTipo)),
            new XElement("FeDetReq", new XElement("FECAEDetRequest", ConstruirDetalle(s))));

    /// <summary>Orden EXACTO pineado por el golden (target 37). <see cref="SolicitudDeCae.FchServDesde"/>/
    /// <c>FchServHasta</c>/<c>FchVtoPago</c> solo aparecen si no son <c>null</c> — nunca emitidos
    /// vacíos (target 39, D3).</summary>
    private static IEnumerable<XElement> ConstruirDetalle(SolicitudDeCae s)
    {
        yield return new XElement("Concepto", s.Concepto);
        yield return new XElement("DocTipo", s.DocTipo);
        yield return new XElement("DocNro", s.DocNro);
        yield return new XElement("CbteDesde", s.CbteDesde);
        yield return new XElement("CbteHasta", s.CbteHasta);
        yield return new XElement("CbteFch", FormatearFecha(s.CbteFch));
        yield return new XElement("ImpTotal", FormatearMoneda(s.ImpTotal));
        yield return new XElement("ImpTotConc", FormatearMoneda(s.ImpTotConc));
        yield return new XElement("ImpNeto", FormatearMoneda(s.ImpNeto));
        yield return new XElement("ImpOpEx", FormatearMoneda(s.ImpOpEx));
        yield return new XElement("ImpIVA", FormatearMoneda(s.ImpIVA));
        yield return new XElement("ImpTrib", FormatearMoneda(s.ImpTrib));

        if (s.FchServDesde is { } fchServDesde)
        {
            yield return new XElement("FchServDesde", FormatearFecha(fchServDesde));
        }

        if (s.FchServHasta is { } fchServHasta)
        {
            yield return new XElement("FchServHasta", FormatearFecha(fchServHasta));
        }

        if (s.FchVtoPago is { } fchVtoPago)
        {
            yield return new XElement("FchVtoPago", FormatearFecha(fchVtoPago));
        }

        yield return new XElement("MonId", "PES");
        yield return new XElement("MonCotiz", 1);
        yield return new XElement("CondicionIVAReceptorId", s.CondicionIVAReceptorId);

        yield return new XElement("Iva", s.Iva.Select(i =>
            new XElement("AlicIva",
                new XElement("Id", i.Id),
                new XElement("BaseImp", FormatearMoneda(i.BaseImp)),
                new XElement("Importe", FormatearMoneda(i.Importe)))));
    }

    /// <summary>design.md D3: dinero siempre <c>"0.00"</c> bajo <see cref="CultureInfo.InvariantCulture"/>
    /// — un separador decimal <c>,</c> es un defecto de wire, nunca aceptable aunque el hilo actual
    /// corra bajo una cultura <c>es-AR</c> (target 38).</summary>
    private static string FormatearMoneda(decimal valor) =>
        valor.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatearFecha(DateOnly fecha) =>
        fecha.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
