using System.Net;
using System.Text;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Tests.Infraestructura;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Infrastructure.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice3 (tasks 3.15, 3.17-3.20, design.md: The ARCA error taxonomy → domain codes fila
/// WSFE, targets 46, 48, 49, 50, 51): <see cref="ClienteWsfe"/> contra un
/// <see cref="HttpMessageHandler"/> falso que devuelve los fixtures de <c>Fixtures/Wsfe/</c> — el
/// mock ES el contrato (proposal decisión 8), mismo patrón que <c>ClienteWsaaTests</c>.
/// </summary>
public class ClienteWsfeTests
{
    private static readonly TicketDeAcceso Ticket =
        new("TOKEN_WSFE_DE_PRUEBA", "SIGN_WSFE_DE_PRUEBA", new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero));

    private const string Cuit = "20111111111";
    private static readonly ClaveDeSerie Serie = new(PtoVta: 3, CbteTipo: 6);
    private static readonly PermisoDeSolicitud Permiso = MaquinaDeEstadosCae.AutorizarSolicitud(1, 105);

    private static readonly SolicitudDeCae SolicitudDeReferencia = new(
        Serie: Serie,
        CbteDesde: 105,
        CbteHasta: 105,
        Concepto: 1,
        DocTipo: 99,
        DocNro: 0,
        CbteFch: new DateOnly(2026, 1, 15),
        ImpTotal: 121.00m,
        ImpTotConc: 0.00m,
        ImpNeto: 100.00m,
        ImpOpEx: 0.00m,
        ImpIVA: 21.00m,
        ImpTrib: 0.00m,
        CondicionIVAReceptorId: 5,
        Iva: [new ItemIvaFiscal(5, 100.00m, 21.00m)]);

    private sealed class HttpMessageHandlerFalso(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int Solicitudes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Solicitudes++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class EsperadorFalso : IEsperador
    {
        public List<TimeSpan> Esperas { get; } = [];

        public Task EsperarAsync(TimeSpan duracion, CancellationToken ct)
        {
            Esperas.Add(duracion);
            return Task.CompletedTask;
        }
    }

    private static HttpResponseMessage RespuestaXml(string contenido) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(contenido, Encoding.UTF8, "text/xml")
    };

    private static string LeerFixtureDeRespuesta(string archivo)
    {
        var raiz = RaizDelRepositorio.Resolver();
        var ruta = Path.Combine(
            raiz, "tests", "Ways.Application.Tests", "Fiscal", "Fixtures", "Wsfe", "Respuestas", archivo);
        return File.ReadAllText(ruta);
    }

    private static ClienteWsfe Construir(HttpMessageHandler handler, IEsperador? esperador = null,
        int intentosMaximos = 3, int umbralDeAperturaDeCircuito = 5) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://wsfe.mock.test/") },
            new RelojFijo(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)),
            esperador,
            intentosMaximos,
            TimeSpan.FromMilliseconds(1),
            umbralDeAperturaDeCircuito,
            TimeSpan.FromMinutes(1));

    // ---- target 46: las tres respuestas ------------------------------------------------------

    [Fact]
    public async Task UnaAprobacionSimpleEscribeElCaeYQuedaAprobado()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecaeSolicitarAprobado.xml"))));

        var respuesta = await cliente.SolicitarCaeAsync(Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None);

        Assert.Equal(ResultadoFiscal.Aprobado, respuesta.Resultado);
        Assert.Equal("70123456789012", respuesta.Cae);
        Assert.Equal(new DateOnly(2026, 1, 25), respuesta.CaeVencimiento);
        Assert.Empty(respuesta.Observaciones);
    }

    [Fact]
    public async Task UnaAprobacionConObservacionesEscribeElCaeYPersisteLasObservaciones()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecaeSolicitarAprobadoConObservaciones.xml"))));

        var respuesta = await cliente.SolicitarCaeAsync(Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None);

        // Dos kills: (1) el CAE se escribe igual que una aprobación simple; (2) las observaciones
        // NO se pierden — ambas condiciones tienen que sobrevivir juntas (target 46).
        Assert.Equal(ResultadoFiscal.AprobadoConObservaciones, respuesta.Resultado);
        Assert.Equal("70123456789013", respuesta.Cae);
        var observacion = Assert.Single(respuesta.Observaciones);
        Assert.Equal(2101, observacion.Codigo);
    }

    [Fact]
    public async Task UnRechazoNoEscribeCaeYPersisteElErrorEnErrors()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecaeSolicitarRechazado.xml"))));

        var respuesta = await cliente.SolicitarCaeAsync(Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None);

        Assert.Equal(ResultadoFiscal.Rechazado, respuesta.Resultado);
        Assert.Null(respuesta.Cae);
        Assert.Null(respuesta.CaeVencimiento);
        Assert.Empty(respuesta.Observaciones);
        var errorArca = Assert.Single(respuesta.Errors);
        Assert.Equal(10015, errorArca.Codigo);
    }

    // ---- target 48: 10016 ---------------------------------------------------------------------

    [Fact]
    public async Task UnNumeroFueraDeSecuenciaLanzaNumeracionFiscalDesincronizadaSinAutoAvance()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecaeSolicitarNumeroNoCorrelativo.xml"))));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.SolicitarCaeAsync(
            Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None));

        Assert.Equal("numeracion_fiscal_desincronizada", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    // ---- target 49: ticket inválido (600) ------------------------------------------------------

    [Fact]
    public async Task UnTicketInvalidoMapeaAlErrorDeDominioReintentable()
    {
        var handler = new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecaeSolicitarTicketInvalido.xml")));
        var cliente = Construir(handler);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.SolicitarCaeAsync(
            Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None));

        Assert.Equal("ticket_de_acceso_invalido", error.Codigo);
        Assert.Equal(503, error.EstadoHttp);
        // Este cliente NO reintenta con un TA fresco por su cuenta — no tiene ni IClienteWsaa ni
        // certificado (DEVIATION registrada en ClienteWsfe.cs); un solo request por invocación.
        Assert.Equal(1, handler.Solicitudes);
    }

    // ---- target 50: backoff + circuit breaker --------------------------------------------------

    [Fact]
    public async Task UnaFallaDeTransporteReintentaUnNumeroAcotadoDeVecesYLuegoFallaDefinitivo()
    {
        var handler = new HttpMessageHandlerFalso(_ => throw new HttpRequestException("simulada"));
        var esperador = new EsperadorFalso();
        var cliente = Construir(handler, esperador, intentosMaximos: 3, umbralDeAperturaDeCircuito: 10);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.SolicitarCaeAsync(
            Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None));

        Assert.Equal("arca_no_definitivo", error.Codigo);
        Assert.Equal(503, error.EstadoHttp);
        Assert.Equal(3, handler.Solicitudes); // acotado, no infinito
        Assert.Equal(2, esperador.Esperas.Count); // backoff entre intentos 1→2 y 2→3, nunca tras el último
    }

    [Fact]
    public async Task ElCircuitoAbiertoNoEmiteNingunRequestHastaQueCierra()
    {
        var handler = new HttpMessageHandlerFalso(_ => throw new HttpRequestException("simulada"));
        var cliente = Construir(handler, new EsperadorFalso(), intentosMaximos: 1, umbralDeAperturaDeCircuito: 1);

        await Assert.ThrowsAsync<ErrorDominio>(() => cliente.SolicitarCaeAsync(
            Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None));
        var solicitudesTrasElPrimerFallo = handler.Solicitudes;
        Assert.Equal(1, solicitudesTrasElPrimerFallo); // el primer fallo abre el circuito

        var errorConCircuitoAbierto = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.SolicitarCaeAsync(
            Ticket, Cuit, Permiso, SolicitudDeReferencia, CancellationToken.None));

        Assert.Equal("arca_no_definitivo", errorConCircuitoAbierto.Codigo);
        Assert.Equal(solicitudesTrasElPrimerFallo, handler.Solicitudes); // CERO requests nuevos
    }

    // ---- FECompConsultar / FECompUltimoAutorizado ----------------------------------------------

    [Fact]
    public async Task FeCompConsultarEncontradoAdoptaElCaeExistente()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecompConsultarEncontrado.xml"))));

        var consulta = await cliente.ConsultarAsync(Ticket, Cuit, Serie, 105, CancellationToken.None);

        // rule 12b: los CUATRO campos de ConsultaDeComprobante leídos de vuelta, cada uno con un
        // valor discriminante (no un default que un mutante de omisión dejaría pasar igual).
        Assert.True(consulta.Encontrado);
        Assert.Equal("70123456789012", consulta.Cae);
        Assert.Equal(new DateOnly(2026, 1, 25), consulta.CaeVencimiento);
        Assert.Equal(ResultadoFiscal.Aprobado, consulta.Resultado);
    }

    [Fact]
    public async Task FeCompConsultarNoEncontradoNoAdoptaNada()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecompConsultarNoEncontrado.xml"))));

        var consulta = await cliente.ConsultarAsync(Ticket, Cuit, Serie, 999, CancellationToken.None);

        Assert.False(consulta.Encontrado);
        Assert.Null(consulta.Cae);
    }

    // ---- target 51 -------------------------------------------------------------------------

    [Fact]
    public async Task FeCompUltimoAutorizadoDevuelveElNumeroDeCabezaDeSerie()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecompUltimoAutorizadoHead.xml"))));

        var ultimoAutorizado = await cliente.UltimoAutorizadoAsync(Ticket, Cuit, Serie, CancellationToken.None);

        Assert.Equal(104, ultimoAutorizado);
    }

    [Fact]
    public async Task FeCompUltimoAutorizadoDeUnaSerieVaciaMapeaACeroNoANullNiAUno()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FecompUltimoAutorizadoVacio.xml"))));

        var ultimoAutorizado = await cliente.UltimoAutorizadoAsync(Ticket, Cuit, Serie, CancellationToken.None);

        Assert.Equal(0, ultimoAutorizado); // target 51
    }

    // ---- FEParamGet* — sin target numerado, mencionado explícitamente en el mandato de esta
    // slice; cobertura mínima de completitud de interfaz, no un golden byte-exacto (ningún target
    // 37-51 lo exige). ----------------------------------------------------------------------

    [Fact]
    public async Task ParametrosAsyncParseaElCatalogoDeAlicuotasDeIva()
    {
        var cliente = Construir(new HttpMessageHandlerFalso(_ => RespuestaXml(
            LeerFixtureDeRespuesta("FeParamGetTiposIva.xml"))));

        var parametros = await cliente.ParametrosAsync(
            Ticket, Cuit, "FEParamGetTiposIva", CancellationToken.None);

        Assert.Equal(4, parametros.Count);
        Assert.Contains(parametros, p => p is { Id: "5", Descripcion: "21%" });
        Assert.Contains(parametros, p => p is { Id: "3", Descripcion: "0%" });
    }
}
