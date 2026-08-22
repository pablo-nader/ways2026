using System.Xml.Linq;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// ÚNICO archivo de <c>src/</c> que nombra SOAP, <c>soapenv</c> o una URI de namespace SOAP
/// (design decisión D2, verify criterion 10, target 29 — <c>rg</c> lo comprueba; precedente
/// invertido de <c>ExportadorXlsx</c>: acá el aislado es el PROTOCOLO, no una dependencia, y
/// quedan CERO paquetes nuevos, decisión 7 del proposal). Puro: sin <c>HttpClient</c>, sin
/// reloj, sin DI — así el golden compara bytes contra una función, no contra una interfaz
/// (design D3). <c>ClienteWsaa</c>/<c>ClienteWsfe</c> llaman a estos métodos como caja negra;
/// nunca necesitan saber qué es un namespace SOAP. <c>Construir</c>/<c>AccionDe</c> son
/// <c>public</c> (el repo no usa <c>InternalsVisibleTo</c> en ningún lado — mismo criterio que
/// <c>ExportadorXlsx</c>, que también es <c>public</c>) para que el golden test de la slice los
/// llame directo desde <c>Ways.Application.Tests</c>; <c>Leer</c> queda <c>internal</c>, sin
/// caller fuera de este ensamblado.
/// </summary>
public static class SobreSoap
{
    private static readonly XNamespace Soapenv = "http://schemas.xmlsoap.org/soap/envelope/";

    public const string EspacioWsaa = "http://wsaa.view.sua.dvadac.desa.afip.gov";
    public const string EspacioWsfe = "http://ar.gov.afip.dif.FEV1/";

    /// <summary><c>SOAPAction: ""</c> para WSAA; <c>"{espacioDeNombres}{operacion}"</c> para WSFE
    /// (design.md:190).</summary>
    public static string AccionDe(string espacioDeNombres, string operacion) =>
        espacioDeNombres == EspacioWsaa ? string.Empty : espacioDeNombres + operacion;

    /// <summary>
    /// Arma el sobre completo concatenando la declaración XML como texto crudo (NO vía
    /// <see cref="XDeclaration"/>) + <see cref="SaveOptions.DisableFormatting"/> (D3: sin
    /// indentación, sin salto de línea — esos son parte del contrato byte a byte). El
    /// <c>encoding</c> se escribe a mano porque el writer de XLinq normaliza ese atributo a
    /// minúsculas tomándolo del <see cref="System.IO.TextWriter.Encoding"/> del destino, sin
    /// importar el texto pasado a <see cref="XDeclaration"/> — cualquier ruta que pase por
    /// <c>XDeclaration</c>/<c>XDocument.Save</c> termina en <c>encoding="utf-8"</c>, verificado
    /// antes de fijar el golden; el manual pinea <c>UTF-8</c> en mayúsculas.
    /// </summary>
    public static string Construir(string espacioDeNombres, string operacion, params object[] cuerpo)
    {
        XNamespace ar = espacioDeNombres;

        var sobre = new XElement(Soapenv + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", Soapenv.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ar", espacioDeNombres),
            new XElement(Soapenv + "Header"),
            new XElement(Soapenv + "Body", new XElement(ar + operacion, cuerpo)));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + sobre.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Body → el primer hijo, o el <c>soap:Fault</c> con <c>faultcode</c>/
    /// <c>faultstring</c>. Recorre por <see cref="XName.LocalName"/> (nunca por namespace exacto)
    /// porque los mocks de esta slice representan tanto la respuesta con namespace por-defecto
    /// (<c>loginCmsResponse</c>) como el <c>soapenv:Fault</c>, sin prefijo propio.</summary>
    internal static RespuestaSoap Leer(string xml)
    {
        var documento = XDocument.Parse(xml);
        var body = documento.Root!.Elements().First(e => e.Name.LocalName == "Body");
        var primerHijo = body.Elements().First();

        if (primerHijo.Name.LocalName == "Fault")
        {
            var faultCode = primerHijo.Elements().First(e => e.Name.LocalName == "faultcode").Value;
            var faultString = primerHijo.Elements().First(e => e.Name.LocalName == "faultstring").Value;
            return new RespuestaSoap(null, new FaultSoap(faultCode, faultString));
        }

        return new RespuestaSoap(primerHijo, null);
    }
}

/// <summary>Resultado de <see cref="SobreSoap.Leer"/>: exactamente uno de los dos es no-nulo.</summary>
internal sealed record RespuestaSoap(XElement? Cuerpo, FaultSoap? Fault);

/// <summary>Un <c>soap:Fault</c> crudo — <see cref="FaultCode"/> es la numeración del proposal
/// (500/501/502/600/601/602), NO verificada contra los strings simbólicos del manual (T3, design
/// open questions).</summary>
internal sealed record FaultSoap(string FaultCode, string FaultString);
