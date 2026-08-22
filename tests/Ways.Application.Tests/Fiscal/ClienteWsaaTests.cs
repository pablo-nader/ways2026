using System.Net;
using System.Text;
using System.Xml.Linq;
using Ways.Application.Fiscal;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Infrastructure.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice2 (tasks 2.20, design: The ARCA error taxonomy → domain codes, target 34, spec
/// fiscal-arca "A Non-Correlative Number Fixture..."): <see cref="ClienteWsaa"/> contra un
/// <see cref="HttpMessageHandler"/> falso que devuelve los fixtures de <c>Fixtures/Wsaa/</c> —
/// el mock ES el contrato (proposal decisión 8). Un test por cada uno de los seis códigos de
/// fault, más el camino exitoso (parseo de token/sign/expiración).
/// </summary>
public class ClienteWsaaTests
{
    private static readonly ClaveDeTicket Clave = new(1, AmbienteFiscal.Homologacion, "wsfe");

    private sealed class HttpMessageHandlerFalso(string cuerpoDeRespuesta) : HttpMessageHandler
    {
        public HttpRequestMessage? UltimaSolicitud { get; private set; }

        /// <summary>Cuerpo del request leído DURANTE <see cref="SendAsync"/> — <c>ClienteWsaa</c>
        /// descarta su <c>HttpRequestMessage</c> (y el <c>Content</c>) apenas termina el envío, así
        /// que leerlo después de <c>await ObtenerTicketAsync</c> lanza
        /// <see cref="ObjectDisposedException"/>.</summary>
        public string? CuerpoDeUltimaSolicitud { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaSolicitud = request;
            CuerpoDeUltimaSolicitud = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var respuesta = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(cuerpoDeRespuesta, Encoding.UTF8, "text/xml")
            };
            return respuesta;
        }
    }

    [Fact]
    public async Task UnaRespuestaExitosaSeParseaAToken_Sign_Expiracion()
    {
        var fixture = LeerFixture("LoginTicketResponse.xml");
        var handler = new HttpMessageHandlerFalso(fixture);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://wsaa.mock.test/") };
        var certificado = CertificadoDePrueba.Generar();
        var cliente = new ClienteWsaa(http, new GeneradorDeTra(new RelojDeReferencia()));

        var ticket = await cliente.ObtenerTicketAsync(
            new SolicitudDeTicket(Clave, certificado), CancellationToken.None);

        Assert.Equal("TOKEN_DE_PRUEBA_WSAA", ticket.Token);
        Assert.Equal("SIGN_DE_PRUEBA_WSAA", ticket.Sign);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 21, 50, 0, TimeSpan.FromHours(-3)), ticket.Expiracion);
        Assert.Equal(string.Empty, handler.UltimaSolicitud!.Headers.GetValues("SOAPAction").Single());

        // El CMS es determinista (RSA PKCS#1 v1.5 no usa relleno aleatorio): reconstruimos la TRA
        // con un GeneradorDeTra independiente pero con el mismo reloj fijo, y firmamos con el mismo
        // certificado que recibió el cliente. Así el assert compara el elemento <in0> exacto del
        // request capturado contra el valor exacto que ObtenerTicketAsync debió haber puesto.
        var traEsperada = new GeneradorDeTra(new RelojDeReferencia()).Construir(Clave.Servicio);
        var cmsEsperado = FirmanteCms.FirmarBase64(traEsperada, certificado);
        var in0 = XDocument.Parse(handler.CuerpoDeUltimaSolicitud!)
            .Descendants().Single(e => e.Name.LocalName == "in0");
        Assert.Equal(cmsEsperado, in0.Value);
    }

    [Theory]
    [InlineData("500", "certificado_fiscal_rechazado", 409)]
    [InlineData("501", "certificado_fiscal_rechazado", 409)]
    [InlineData("502", "certificado_fiscal_rechazado", 409)]
    [InlineData("600", "certificado_fiscal_sin_autorizacion", 409)]
    [InlineData("601", "wsaa_en_intervalo_minimo", 503)]
    [InlineData("602", "certificado_fiscal_sin_autorizacion", 409)]
    public async Task CadaFaultDeLaTaxonomiaMapeaAlCodigoDeDominioEsperado(
        string codigoFault, string codigoDeDominioEsperado, int estadoHttpEsperado)
    {
        var fixture = LeerFixture($"Faults/Fault{codigoFault}.xml");
        using var http = new HttpClient(new HttpMessageHandlerFalso(fixture))
        {
            BaseAddress = new Uri("https://wsaa.mock.test/")
        };
        var cliente = new ClienteWsaa(http, new GeneradorDeTra(new RelojDeReferencia()));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.ObtenerTicketAsync(
            new SolicitudDeTicket(Clave, CertificadoDePrueba.Generar()), CancellationToken.None));

        Assert.Equal(codigoDeDominioEsperado, error.Codigo);
        Assert.Equal(estadoHttpEsperado, error.EstadoHttp);
    }

    private sealed class RelojDeReferencia : Ways.Application.Abstracciones.IRelojDelSistema
    {
        public DateTimeOffset Ahora => new(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(-3));
    }

    private static string LeerFixture(string rutaRelativa)
    {
        var raiz = ResolverRaizDelRepositorio();
        var carpetaFixtures = Path.Combine(
            raiz, "tests", "Ways.Application.Tests", "Fiscal", "Fixtures", "Wsaa");
        var ruta = Path.Combine([carpetaFixtures, .. rutaRelativa.Split('/')]);
        return File.ReadAllText(ruta);
    }

    private static string ResolverRaizDelRepositorio()
    {
        var directorio = AppContext.BaseDirectory;

        while (directorio is not null && !File.Exists(Path.Combine(directorio, "Ways.slnx")))
        {
            directorio = Path.GetDirectoryName(directorio.TrimEnd(Path.DirectorySeparatorChar));
        }

        return directorio ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (Ways.slnx).");
    }
}
