using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Fiscal;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T5a: <c>ServicioDeFacturacionDeVentas.ProcesarDecisionAsync</c> emite la factura espejo de UNA
/// venta ya decidida (<c>POST /api/fiscal/facturaciones/{id}/procesar</c>) contra Postgres real y espías de WSAA/WSFE. La venta
/// (el ticket) se siembra directo y su decisión la toma el servicio real de decisión, así que el acumulado parte de un estado
/// previo distinto de cero y cada reversión se compara contra él.
///
/// Mutation-proof-tests: las cláusulas bajo prueba son cada conjunción del UPDATE que devuelve el número, el lock y la relectura de
/// la decisión (en la creación del espejo, en la cancelación y al aplicar un resultado), la relectura de "sin espejo" bajo el lock de
/// la venta, resolver siempre el espejo pendiente aunque su decisión no lo esté, consultar antes de pedir, qué es "no encontrado",
/// guardar la fecha antes de pedir, la consulta previa a liberar tras un error de llamada, consultar antes de las compuertas, la
/// lista blanca de resultados definitivos, la verificación de que la venta siga emitida, la pregunta de la anulación por el espejo
/// vigente, los signos de la reversión del acumulado (rechazada y cancelada), el acumulado faltante y la exclusión de espejos en
/// <c>ReintentarAsync</c>. Los pasos de una misma llamada se pausan con interceptores de transacción, sin esperas por tiempo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public partial class FacturacionDeVentasEmisionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly TimeSpan Tope = TimeSpan.FromSeconds(20);

    static FacturacionDeVentasEmisionTests()
    {
        Environment.SetEnvironmentVariable("Ways__Fiscal__ClaveMaestraActual", "v1");
        Environment.SetEnvironmentVariable(
            "Ways__Fiscal__ClavesMaestras__v1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TimeZoneInfo ZonaFiscal = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    private static long _numeroSembrado = 5_000_000;
    private static int _numeroDeCliente = 1_000;

    // ---- Espías ------------------------------------------------------------------------------------

    private sealed class EspiaWsaa(string cuerpoDeRespuesta) : HttpMessageHandler
    {
        public int Solicitudes { get; private set; }

        public bool Falla { get; set; }

        /// <summary>Se ejecuta en cada pedido, antes de contestar: simula que algo cambia mientras el servicio espera el ticket.</summary>
        public Func<Task>? AntesDeResponder { get; set; }

        /// <summary>Como <see cref="AntesDeResponder"/>, pero recibe el token del pedido, para que la espera la corte la
        /// cancelación igual que la de un handler real.</summary>
        public Func<CancellationToken, Task>? AntesDeResponderConToken { get; set; }

        /// <summary>Si está, arma la respuesta de cada firma a partir de su número de orden (1, 2, …): permite distinguir qué
        /// ticket viajó en cada pedido a WSFE.</summary>
        public Func<int, string>? CuerpoPorFirma { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Solicitudes++;
            if (AntesDeResponder is not null)
            {
                await AntesDeResponder();
            }

            if (AntesDeResponderConToken is not null)
            {
                await AntesDeResponderConToken(ct);
            }

            return Falla
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : RespuestaXml(CuerpoPorFirma?.Invoke(Solicitudes) ?? cuerpoDeRespuesta);
        }
    }

    /// <summary>Espía de WSFE por SOAPAction. Se agregó a la versión de <c>ServicioDeFacturacionFiscalTests</c>: las respuestas son
    /// asíncronas (un test puede retenerlas en medio del pedido) y guarda el cuerpo de cada <c>FECAESolicitar</c> para
    /// verificar el número y la fecha que viajaron.</summary>
    private sealed class EspiaWsfe : HttpMessageHandler
    {
        private readonly object _candado = new();
        private readonly List<string> _operaciones = [];
        private readonly List<string> _cuerpos = [];

        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Solicitar { get; set; } =
            _ => throw new InvalidOperationException("EspiaWsfe.Solicitar no configurado.");

        /// <summary>Si está, atiende los <c>FECAESolicitar</c> con el token del pedido (el que cancela el HttpClient por su timeout o
        /// el llamador) en lugar de <see cref="Solicitar"/>.</summary>
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SolicitarConToken { get; set; }

        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Consultar { get; set; } =
            _ => throw new InvalidOperationException("EspiaWsfe.Consultar no configurado.");

        /// <summary>Si está, atiende los <c>FECompConsultar</c> con el token del pedido en lugar de <see cref="Consultar"/>.</summary>
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ConsultarConToken { get; set; }

        public IReadOnlyList<string> Operaciones
        {
            get
            {
                lock (_candado)
                {
                    return [.. _operaciones];
                }
            }
        }

        public IReadOnlyList<string> CuerposDeSolicitud
        {
            get
            {
                lock (_candado)
                {
                    return [.. _cuerpos];
                }
            }
        }

        public int Contar(string operacion) => Operaciones.Count(o => o == operacion);

        public void Reiniciar()
        {
            lock (_candado)
            {
                _operaciones.Clear();
                _cuerpos.Clear();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var accion = request.Headers.TryGetValues("SOAPAction", out var valores) ? valores.FirstOrDefault() ?? string.Empty : string.Empty;
            var cuerpo = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);

            if (accion.Contains("FECAESolicitar", StringComparison.Ordinal))
            {
                lock (_candado)
                {
                    _operaciones.Add("FECAESolicitar");
                    _cuerpos.Add(cuerpo);
                }

                return SolicitarConToken is { } conToken ? await conToken(request, ct) : await Solicitar(request);
            }

            if (accion.Contains("FECompConsultar", StringComparison.Ordinal))
            {
                lock (_candado)
                {
                    _operaciones.Add("FECompConsultar");
                }

                return ConsultarConToken is { } conToken ? await conToken(request, ct) : await Consultar(request);
            }

            throw new InvalidOperationException($"Operación WSFE no soportada por el espía: '{accion}'.");
        }
    }

    private static HttpResponseMessage RespuestaXml(string contenido) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(contenido, Encoding.UTF8, "text/xml")
    };

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Responde(string xml) =>
        _ => Task.FromResult(RespuestaXml(xml));

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> RespondeHttp500() =>
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

    private sealed class EsperadorInstantaneo : IEsperador
    {
        public Task EsperarAsync(TimeSpan duracion, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RelojControlable(DateTimeOffset inicio) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; set; } = inicio;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private sealed class RegistroDeLogs : ILoggerProvider
    {
        private readonly object _candado = new();
        private readonly List<(LogLevel Nivel, string Categoria, string Mensaje)> _entradas = [];

        public IReadOnlyList<(LogLevel Nivel, string Categoria, string Mensaje)> Entradas
        {
            get
            {
                lock (_candado)
                {
                    return [.. _entradas];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Registrador(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Registrador(RegistroDeLogs padre, string categoria) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (padre._candado)
                {
                    padre._entradas.Add((logLevel, categoria, formatter(state, exception)));
                }
            }
        }
    }

    // ---- Respuestas de ARCA ----------------------------------------------------------------------------

    private static string LoginCmsGolden(string token = "TOKEN_ESPIA") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope " +
        "xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body>" +
        "<loginCmsResponse xmlns=\"http://wsaa.view.sua.dvadac.desa.afip.gov\"><loginCmsReturn>" +
        "&lt;?xml version=\"1.0\" encoding=\"UTF-8\"?&gt;&lt;loginTicketResponse version=\"1.0\"&gt;" +
        "&lt;header&gt;&lt;source&gt;CN=test&lt;/source&gt;&lt;destination&gt;CN=ways&lt;/destination&gt;" +
        "&lt;uniqueId&gt;1&lt;/uniqueId&gt;&lt;generationTime&gt;2020-01-01T00:00:00-03:00&lt;/generationTime&gt;" +
        "&lt;expirationTime&gt;2099-01-01T00:00:00-03:00&lt;/expirationTime&gt;&lt;/header&gt;" +
        $"&lt;credentials&gt;&lt;token&gt;{token}&lt;/token&gt;&lt;sign&gt;SIGN_ESPIA&lt;/sign&gt;&lt;/credentials&gt;" +
        "&lt;/loginTicketResponse&gt;</loginCmsReturn></loginCmsResponse></soapenv:Body></soapenv:Envelope>";

    private static string FecaeAprobado(string cae = "70123456789012") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECAESolicitarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECAESolicitarResult>" +
        "<FeCabResp><Cuit>20111111112</Cuit><PtoVta>1</PtoVta><CbteTipo>1</CbteTipo>" +
        "<FchProceso>20260115121500</FchProceso><CantReg>1</CantReg><Resultado>A</Resultado><Reproceso>N</Reproceso></FeCabResp>" +
        "<FeDetResp><FECAEDetResponse><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro>" +
        "<CbteDesde>1</CbteDesde><CbteHasta>1</CbteHasta><CbteFch>20260115</CbteFch>" +
        $"<Resultado>A</Resultado><Observaciones /><CAE>{cae}</CAE><CAEFchVto>20260125</CAEFchVto></FECAEDetResponse></FeDetResp>" +
        "<Errors /><Events /></FECAESolicitarResult></FECAESolicitarResponse></soapenv:Body></soapenv:Envelope>";

    private static string FecaeAprobadoConObservaciones(string cae = "70123456789013") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECAESolicitarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECAESolicitarResult>" +
        "<FeCabResp><Cuit>20111111112</Cuit><PtoVta>1</PtoVta><CbteTipo>1</CbteTipo>" +
        "<FchProceso>20260115121500</FchProceso><CantReg>1</CantReg><Resultado>A</Resultado><Reproceso>N</Reproceso></FeCabResp>" +
        "<FeDetResp><FECAEDetResponse><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro>" +
        "<CbteDesde>1</CbteDesde><CbteHasta>1</CbteHasta><CbteFch>20260115</CbteFch>" +
        "<Resultado>A</Resultado><Observaciones><Obs><Code>2101</Code>" +
        $"<Msg>El comprobante fue autorizado con observaciones.</Msg></Obs></Observaciones><CAE>{cae}</CAE>" +
        "<CAEFchVto>20260125</CAEFchVto></FECAEDetResponse></FeDetResp>" +
        "<Errors /><Events /></FECAESolicitarResult></FECAESolicitarResponse></soapenv:Body></soapenv:Envelope>";

    private static string FecaeRechazado() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECAESolicitarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECAESolicitarResult>" +
        "<FeCabResp><Cuit>20111111112</Cuit><PtoVta>1</PtoVta><CbteTipo>1</CbteTipo>" +
        "<FchProceso>20260115121500</FchProceso><CantReg>1</CantReg><Resultado>R</Resultado><Reproceso>N</Reproceso></FeCabResp>" +
        "<FeDetResp><FECAEDetResponse><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro>" +
        "<CbteDesde>1</CbteDesde><CbteHasta>1</CbteHasta><CbteFch>20260115</CbteFch>" +
        "<Resultado>R</Resultado><Observaciones><Obs><Code>10015</Code><Msg>Rechazado por el receptor.</Msg></Obs></Observaciones>" +
        "</FECAEDetResponse></FeDetResp><Errors /><Events /></FECAESolicitarResult></FECAESolicitarResponse></soapenv:Body></soapenv:Envelope>";

    /// <summary>Error de NIVEL DE LLAMADA que no es el 600: ARCA no procesó ningún detalle y el cliente lo mapea al
    /// error <c>arca_rechazo</c>.</summary>
    private static string FecaeErrorDeLlamada() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECAESolicitarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECAESolicitarResult>" +
        "<FeCabResp><Cuit>20111111112</Cuit><PtoVta>0</PtoVta><CbteTipo>0</CbteTipo>" +
        "<FchProceso>20260115121500</FchProceso><CantReg>0</CantReg><Resultado>R</Resultado><Reproceso>N</Reproceso></FeCabResp>" +
        "<Errors><Err><Code>10000</Code><Msg>Error de validación de la llamada.</Msg></Err></Errors><Events />" +
        "</FECAESolicitarResult></FECAESolicitarResponse></soapenv:Body></soapenv:Envelope>";

    /// <summary>La consulta que encuentra el comprobante. <paramref name="fecha"/> es el <c>CbteFch</c> que ARCA tiene registrado.</summary>
    private static string ConsultarEncontrado(string cae = "70999999999999", string fecha = "20260115") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECompConsultarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECompConsultarResult>" +
        "<ResultGet><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro><CbteDesde>1</CbteDesde>" +
        $"<CbteHasta>1</CbteHasta><CbteFch>{fecha}</CbteFch><ImpTotal>231.00</ImpTotal><ImpTotConc>0.00</ImpTotConc>" +
        "<ImpNeto>199.55</ImpNeto><ImpOpEx>0.00</ImpOpEx><ImpIVA>31.45</ImpIVA><ImpTrib>0.00</ImpTrib><MonId>PES</MonId>" +
        $"<MonCotiz>1</MonCotiz><CodAutorizacion>{cae}</CodAutorizacion><EmisionTipo>CAE</EmisionTipo><FchVto>20260125</FchVto>" +
        "<Resultado>A</Resultado><Observaciones /></ResultGet><Errors /></FECompConsultarResult></FECompConsultarResponse>" +
        "</soapenv:Body></soapenv:Envelope>";

    private static string ConsultarNoEncontrado() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECompConsultarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECompConsultarResult>" +
        "<ResultGet /><Errors><Err><Code>602</Code><Msg>No encontrado.</Msg></Err></Errors></FECompConsultarResult>" +
        "</FECompConsultarResponse></soapenv:Body></soapenv:Envelope>";

    /// <summary>Una consulta que falla con un error de ARCA que NO es "comprobante inexistente" (602).</summary>
    private static string ConsultarConError(int codigo) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECompConsultarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECompConsultarResult>" +
        $"<ResultGet /><Errors><Err><Code>{codigo}</Code><Msg>Error de ARCA {codigo}.</Msg></Err></Errors></FECompConsultarResult>" +
        "</FECompConsultarResponse></soapenv:Body></soapenv:Envelope>";

    // ---- Escenario -------------------------------------------------------------------------------------

    private sealed record Ctx(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdUsuario, int IdArea, int IdLista, int IdAlicuota21,
        int IdAlicuota105, int IdClienteRi, int IdClienteRiSinCuit, int IdClienteCf, int IdClienteNoResp,
        int IdEfectivo, int IdTransferencia, int IdTurno, HttpClient Admin, HttpClient Vendedor, HttpClient Root,
        string MailAdmin, string PasswordAdmin, EspiaWsaa Wsaa, EspiaWsfe Wsfe, RelojControlable Reloj,
        RegistroDeLogs Logs, Action<IServiceCollection> Servicios, WebApplicationFactory<Program> Fabrica);

    private sealed record Venta(int IdVenta, int IdFacturacion, DateOnly Periodo);

    private static (byte[] Pfx, string Password) GenerarPfx()
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest("CN=Ways Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var ahora = DateTimeOffset.UtcNow;
        using var certificado = solicitud.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(1));
        var password = Guid.NewGuid().ToString("N");
        return (certificado.Export(X509ContentType.Pkcs12, password), password);
    }

    private async Task<Ctx> PrepararAsync(
        string nombre, ModoPuntoVenta modo = ModoPuntoVenta.Web, Action<IServiceCollection>? extra = null)
    {
        var wsaa = new EspiaWsaa(LoginCmsGolden());
        var wsfe = new EspiaWsfe { Solicitar = Responde(FecaeAprobado()), Consultar = Responde(ConsultarNoEncontrado()) };
        // Postgres guarda microsegundos: un reloj con ticks de 100 ns no sobreviviría a la ida y vuelta.
        var ahoraUtc = DateTimeOffset.UtcNow;
        var reloj = new RelojControlable(ahoraUtc.AddTicks(-(ahoraUtc.Ticks % TimeSpan.TicksPerMillisecond)));
        var logs = new RegistroDeLogs();

        void Servicios(IServiceCollection services)
        {
            services.AddSingleton<IRelojDelSistema>(reloj);
            services.AddSingleton<IEsperador>(new EsperadorInstantaneo());
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddHttpClient<IClienteWsaa, ClienteWsaa>(http => http.BaseAddress = new Uri("https://wsaa.espia.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => wsaa);
            services.AddHttpClient<IClienteWsfe, ClienteWsfe>(http => http.BaseAddress = new Uri("https://wsfe.espia.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => wsfe);
        }

        var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            Servicios(services);
            extra?.Invoke(services);
        }));

        var root = factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..6]}".ToLowerInvariant();
        var mailAdmin = $"{unico}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, modo));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var corto = Guid.NewGuid().ToString("N")[..8];
        var mailVendedor = $"{unico}-vend@ways.test";
        var altaVendedor = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"vend-{corto}", mailVendedor, (int)RolConocido.Vendedor, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);
        var vendedor = factory.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        int idArea, idLista, idAlicuota21, idAlicuota105, idRi, idRiSinCuit, idCf, idNoResp, idEfectivo, idTransferencia, idTurno;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;

            var empresa = await db.Empresas.FirstAsync(e => e.Id == resultado.IdEmpresa);
            empresa.Cuit = "20111111112";
            var idCondicionRi = await db.CondicionesFiscales.Where(c => c.Codigo == "RI").Select(c => c.Id).FirstAsync();
            empresa.IdCondicionFiscal = idCondicionRi;
            var puntoVenta = await db.PuntosVenta.FirstAsync(p => p.Id == resultado.IdPuntoVenta);
            puntoVenta.NumeroFiscal = 1;
            await db.SaveChangesAsync();

            var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area fiscal", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            var lista = new ListaPrecio
            {
                IdTenant = resultado.IdTenant, Nombre = "Lista fiscal", EsDefault = false, Modo = ModoLista.Fija,
                Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.ListasPrecio.Add(lista);
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
                IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
                Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();

            idArea = area.Id;
            idLista = lista.Id;
            idAlicuota21 = await db.AlicuotasIva.Where(a => a.CodigoAfip == 5).Select(a => a.Id).FirstAsync();
            idAlicuota105 = await db.AlicuotasIva.Where(a => a.CodigoAfip == 4).Select(a => a.Id).FirstAsync();
            var idCondicionCf = await db.CondicionesFiscales.Where(c => c.Codigo == "CF").Select(c => c.Id).FirstAsync();
            var idCondicionNoResp = await db.CondicionesFiscales.Where(c => c.Codigo == "NO_RESP").Select(c => c.Id).FirstAsync();

            var ri = SembrarCliente(db, resultado.IdTenant, lista.Id, idCondicionRi, "20222222223", TipoDocumento.Cuit, ahora);
            var riSinCuit = SembrarCliente(db, resultado.IdTenant, lista.Id, idCondicionRi, null, null, ahora);
            var cf = SembrarCliente(db, resultado.IdTenant, lista.Id, idCondicionCf, null, null, ahora);
            var noResp = SembrarCliente(db, resultado.IdTenant, lista.Id, idCondicionNoResp, "30333333334", TipoDocumento.Cuil, ahora);
            await db.SaveChangesAsync();
            idRi = ri.Id;
            idRiSinCuit = riSinCuit.Id;
            idCf = cf.Id;
            idNoResp = noResp.Id;

            idEfectivo = await db.MediosPago
                .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
            idTransferencia = await db.MediosPago
                .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();
            idTurno = await db.TurnosCaja.Where(t => t.IdPuntoVenta == resultado.IdPuntoVenta).Select(t => t.Id).FirstAsync();
        }

        var (pfx, password) = GenerarPfx();
        var registro = await admin.PostAsJsonAsync("/api/fiscal/certificados", new
        {
            IdEmpresa = resultado.IdEmpresa,
            Ambiente = AmbienteFiscal.Homologacion,
            Alias = "Homo",
            CuitTitular = "20111111112",
            Pfx = pfx,
            PasswordPfx = password
        });
        Assert.Equal(HttpStatusCode.Created, registro.StatusCode);

        return new Ctx(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idArea, idLista,
            idAlicuota21, idAlicuota105, idRi, idRiSinCuit, idCf, idNoResp, idEfectivo, idTransferencia, idTurno,
            admin, vendedor, root, mailAdmin, resultado.PasswordTemporal, wsaa, wsfe, reloj, logs, Servicios, factory);
    }

    private static Cliente SembrarCliente(
        WaysDbContext db, int idTenant, int idLista, int idCondicion, string? documento, TipoDocumento? tipo,
        DateTimeOffset ahora)
    {
        var numero = Interlocked.Increment(ref _numeroDeCliente);
        var cliente = new Cliente
        {
            IdTenant = idTenant, Numero = numero, Nombre = $"Cliente {numero}", IdCondicionFiscal = idCondicion,
            IdListaPrecio = idLista, TipoDocumento = tipo, NumeroDocumento = documento, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        return cliente;
    }

    /// <summary>Un cliente HTTP nuevo sobre un host derivado con servicios extra (por ejemplo, un interceptor de la base).</summary>
    private async Task<HttpClient> ClienteConServiciosAsync(Ctx c, Action<IServiceCollection> extra)
    {
        var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            c.Servicios(services);
            extra(services);
        }));
        var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(c.MailAdmin, c.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    // ---- Siembra de la venta y de su decisión ---------------------------------------------------------------

    private const decimal PorcentajeEfectivo = 50m;
    private const decimal PorcentajeTransferencia = 30m;
    private const decimal ImporteEfectivo = 100m;
    private const decimal ImporteTransferencia = 131m;
    private const decimal TotalDeLaVenta = 231m;

    /// <summary>Un ticket TX emitido de dos líneas (121 al 21 % y 110 al 10,5 %: total 231) con un pago en efectivo (100) y
    /// otro por transferencia (131), forzado a facturar. Cada medio tiene un acumulado previo distinto de cero, para que la
    /// decisión y su reversión se midan contra un estado que no sea trivial. La decisión la toma el servicio real.</summary>
    private async Task<Venta> SembrarVentaDecididaAsync(
        Ctx c, int? idCliente = null, DateTimeOffset? fecha = null, bool sembrarAcumulados = true)
    {
        var fechaVenta = fecha ?? c.Reloj.Ahora.AddDays(-3);
        var periodo = PeriodoDe(fechaVenta);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant)))
        {
            foreach (var (id, porcentaje) in new[] { (c.IdEfectivo, PorcentajeEfectivo), (c.IdTransferencia, PorcentajeTransferencia) })
            {
                var medio = await db.MediosPago.SingleAsync(m => m.Id == id);
                medio.PorcentajeFacturacion = porcentaje;
            }

            await db.SaveChangesAsync();
        }

        if (sembrarAcumulados)
        {
            await AsegurarAcumuladoAsync(c, c.IdEfectivo, periodo, 17.25m, 300m, 200m);
            await AsegurarAcumuladoAsync(c, c.IdTransferencia, periodo, -3.5m, 40m, 10m);
        }

        int idVenta;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var idTipoTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
            var venta = new ComprobanteVenta
            {
                IdTenant = c.IdTenant, IdTipoComprobante = idTipoTx, Numero = Interlocked.Increment(ref _numeroSembrado),
                Fecha = fechaVenta, IdPuntoVenta = c.IdPuntoVenta, IdTurnoCaja = c.IdTurno, IdEmpleado = c.IdUsuario,
                IdCliente = idCliente ?? c.IdClienteCf, Subtotal = TotalDeLaVenta, DescuentoTotal = 0m, Total = TotalDeLaVenta,
                Estado = EstadoComprobante.Emitido, FacturaForzada = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.ComprobantesVenta.Add(venta);
            await db.SaveChangesAsync();

            db.ItemsComprobanteVenta.Add(NuevoItem(
                c, venta.Id, 1, "Producto A", c.IdAlicuota21, 21m, 2m, 60.50m, 121m, costo: 50m));
            db.ItemsComprobanteVenta.Add(NuevoItem(
                c, venta.Id, 2, "Producto B", c.IdAlicuota105, 10.5m, 1m, 110m, 110m, costo: 70m));
            db.PagosComprobante.Add(new PagoComprobante
            {
                IdTenant = c.IdTenant, IdComprobanteVenta = venta.Id, IdMedioPago = c.IdEfectivo, Importe = 120m,
                Vuelto = 20m, CreatedAt = ahora, UpdatedAt = ahora
            });
            db.PagosComprobante.Add(new PagoComprobante
            {
                IdTenant = c.IdTenant, IdComprobanteVenta = venta.Id, IdMedioPago = c.IdTransferencia,
                Importe = ImporteTransferencia, Referencia = "OP-1", CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            idVenta = venta.Id;
        }

        var estado = await DecidirAsync(c, idVenta);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        return new Venta(idVenta, estado.IdFacturacion!.Value, periodo);
    }

    private static ItemComprobanteVenta NuevoItem(
        Ctx c, int idVenta, int orden, string descripcion, int idAlicuota, decimal porcentajeIva, decimal cantidad,
        decimal precio, decimal total, decimal costo)
    {
        var ahora = DateTimeOffset.UtcNow;
        return new ItemComprobanteVenta
        {
            IdTenant = c.IdTenant, IdComprobanteVenta = idVenta, Orden = orden, Descripcion = descripcion,
            IdArea = c.IdArea, IdListaPrecio = c.IdLista, IdAlicuotaIva = idAlicuota, PorcentajeIva = porcentajeIva,
            Cantidad = cantidad, PrecioUnitario = precio, Descuento = 0m, Total = total, CostoUnitario = costo,
            CostoEsEstimado = true, CreatedAt = ahora, UpdatedAt = ahora
        };
    }

    private static DateOnly PeriodoDe(DateTimeOffset fecha)
    {
        var dia = FechaFiscal.DeInstante(fecha, ZonaFiscal);
        return new DateOnly(dia.Year, dia.Month, 1);
    }

    private async Task<EstadoFiscalDeVenta> DecidirAsync(Ctx c, int idVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant));
        var parametros = new ServicioDeParametros(
            db, c.Reloj, new ServicioDeLotes(db, c.Reloj, new ContextoFijo(c.IdTenant, c.IdUsuario)));
        var servicio = new ServicioDeDecisionDeFacturacion(
            db, c.Reloj, Options.Create(new OpcionesFiscales()), parametros,
            NullLogger<ServicioDeDecisionDeFacturacion>.Instance);
        return await servicio.DecidirAsync(idVenta);
    }

    // ---- Lecturas crudas -------------------------------------------------------------------------------------

    private async Task<NpgsqlConnection> AbrirOwnerAsync()
    {
        var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();
        return conexion;
    }

    private async Task EjecutarComoOwnerAsync(string sql, params object?[] parametros)
    {
        await using var conexion = await AbrirOwnerAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro ?? DBNull.Value });
        }

        await comando.ExecuteNonQueryAsync();
    }

    private async Task AsegurarAcumuladoAsync(
        Ctx c, int idMedio, DateOnly periodo, decimal saldo, decimal vendido, decimal facturado) =>
        await EjecutarComoOwnerAsync(
            "INSERT INTO acumulados_de_facturacion (id_empresa, id_medio_pago, periodo, id_tenant, saldo_a_facturar, " +
            "total_vendido, total_facturado) VALUES ($1, $2, $3, $4, $5, $6, $7) " +
            "ON CONFLICT (id_empresa, id_medio_pago, periodo) DO UPDATE SET saldo_a_facturar = $5, total_vendido = $6, " +
            "total_facturado = $7",
            c.IdEmpresa, idMedio, periodo, c.IdTenant, saldo, vendido, facturado);

    private async Task<List<T>> ConsultarAsync<T>(Ctx c, string sql, Func<DbDataReader, T> mapa, params object?[] parametros)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", c.IdTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro ?? DBNull.Value });
        }

        var filas = new List<T>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(mapa(lector));
        }

        return filas;
    }

    private sealed record FilaDeAcumulado(int IdMedio, DateOnly Periodo, decimal Saldo, decimal Vendido, decimal Facturado);

    private Task<List<FilaDeAcumulado>> LeerAcumuladosAsync(Ctx c) => ConsultarAsync(
        c,
        "SELECT id_medio_pago, periodo, saldo_a_facturar, total_vendido, total_facturado FROM acumulados_de_facturacion " +
        "WHERE id_empresa = $1 ORDER BY id_medio_pago, periodo",
        l => new FilaDeAcumulado(l.GetInt32(0), l.GetFieldValue<DateOnly>(1), l.GetDecimal(2), l.GetDecimal(3), l.GetDecimal(4)),
        c.IdEmpresa);

    private sealed record FilaDeEspejo(
        int Id, int IdVentaOrigen, long Numero, DateTimeOffset Fecha, string Resultado, string? Cae, int? IdTurno,
        DateTimeOffset? Decidida, string CodigoTipo, decimal Total, decimal? Neto, decimal? Iva, int IdEmpleado,
        int IdCliente, string? Observaciones);

    private Task<List<FilaDeEspejo>> LeerEspejosAsync(Ctx c) => ConsultarAsync(
        c,
        "SELECT cv.id_comprobante_venta, cv.id_venta_origen, cv.numero, cv.fecha, cv.resultado_fiscal::text, cv.cae, " +
        "cv.id_turno_caja, cv.facturacion_decidida_en, t.codigo, cv.total, cv.neto_gravado, cv.iva_total, cv.id_empleado, " +
        "cv.id_cliente, cv.observaciones_fiscales::text " +
        "FROM comprobantes_venta cv JOIN tipos_comprobante t ON t.id_tipo_comprobante = cv.id_tipo_comprobante " +
        "WHERE cv.id_venta_origen IS NOT NULL AND cv.id_punto_venta = $1 ORDER BY cv.id_comprobante_venta",
        l => new FilaDeEspejo(
            l.GetInt32(0), l.GetInt32(1), l.GetInt64(2), l.GetFieldValue<DateTimeOffset>(3), l.GetString(4),
            l.IsDBNull(5) ? null : l.GetString(5), l.IsDBNull(6) ? null : l.GetInt32(6),
            l.IsDBNull(7) ? null : l.GetFieldValue<DateTimeOffset>(7), l.GetString(8), l.GetDecimal(9),
            l.IsDBNull(10) ? null : l.GetDecimal(10), l.IsDBNull(11) ? null : l.GetDecimal(11), l.GetInt32(12),
            l.GetInt32(13), l.IsDBNull(14) ? null : l.GetString(14)),
        c.IdPuntoVenta);

    private sealed record FilaDeItem(
        int Orden, string Descripcion, int IdAlicuota, decimal PorcentajeIva, decimal Cantidad, decimal Precio,
        decimal Total, decimal? Costo, bool CostoEstimado, int? IdLote);

    private Task<List<FilaDeItem>> LeerItemsAsync(Ctx c, int idComprobante) => ConsultarAsync(
        c,
        "SELECT orden, descripcion, id_alicuota_iva, porcentaje_iva, cantidad, precio_unitario, total, costo_unitario, " +
        "costo_es_estimado, id_lote FROM items_comprobante_venta WHERE id_comprobante_venta = $1 ORDER BY orden",
        l => new FilaDeItem(
            l.GetInt32(0), l.GetString(1), l.GetInt32(2), l.GetDecimal(3), l.GetDecimal(4), l.GetDecimal(5),
            l.GetDecimal(6), l.IsDBNull(7) ? null : l.GetDecimal(7), l.GetBoolean(8), l.IsDBNull(9) ? null : l.GetInt32(9)),
        idComprobante);

    private sealed record FilaDeDecision(
        string Estado, int Intentos, string? UltimoError, DateTimeOffset? ProximoIntento, int? IdComprobanteFiscal);

    private async Task<FilaDeDecision> LeerDecisionAsync(Ctx c, int idFacturacion) => Assert.Single(await ConsultarAsync(
        c,
        "SELECT estado::text, intentos, ultimo_error, proximo_intento_en, id_comprobante_fiscal " +
        "FROM facturaciones_de_venta WHERE id_facturacion_venta = $1",
        l => new FilaDeDecision(
            l.GetString(0), l.GetInt32(1), l.IsDBNull(2) ? null : l.GetString(2),
            l.IsDBNull(3) ? null : l.GetFieldValue<DateTimeOffset>(3), l.IsDBNull(4) ? null : l.GetInt32(4)),
        idFacturacion));

    private sealed record FilaDeSerie(short CodigoAfip, long Proximo, long? UltimoAutorizado);

    private Task<List<FilaDeSerie>> LeerSeriesAsync(Ctx c) => ConsultarAsync(
        c,
        "SELECT codigo_afip, proximo_numero, ultimo_autorizado_arca FROM numeraciones_fiscales " +
        "WHERE id_punto_venta = $1 ORDER BY codigo_afip",
        l => new FilaDeSerie(l.GetInt16(0), l.GetInt64(1), l.IsDBNull(2) ? null : l.GetInt64(2)),
        c.IdPuntoVenta);

    private async Task<long?> LeerProximoAsync(Ctx c, short codigoAfip) =>
        (await LeerSeriesAsync(c)).SingleOrDefault(s => s.CodigoAfip == codigoAfip)?.Proximo;

    /// <summary>Todo lo que el espejo no puede tocar de la venta: su estado, importes, turno, pagos, ítems y los ledgers de stock y
    /// cuenta corriente.</summary>
    private sealed record InstantaneaDeVenta(
        string Estado, decimal Total, decimal Subtotal, long Numero, int? IdTurno, DateTimeOffset Fecha,
        DateTimeOffset UpdatedAt, bool Forzada, DateTimeOffset? Decidida, int Pagos, decimal SumaDePagos, int Items,
        decimal SumaDeItems, int MovimientosDeStock, int MovimientosDeCuentaCorriente);

    private async Task<InstantaneaDeVenta> InstantaneaAsync(Ctx c, int idVenta)
    {
        var cabecera = Assert.Single(await ConsultarAsync(
            c,
            "SELECT estado::text, total, subtotal, numero, id_turno_caja, fecha, updated_at, factura_forzada, " +
            "facturacion_decidida_en, " +
            "(SELECT count(*) FROM pagos_comprobante WHERE id_comprobante_venta = $1), " +
            "(SELECT COALESCE(sum(importe), 0) FROM pagos_comprobante WHERE id_comprobante_venta = $1), " +
            "(SELECT count(*) FROM items_comprobante_venta WHERE id_comprobante_venta = $1), " +
            "(SELECT COALESCE(sum(total), 0) FROM items_comprobante_venta WHERE id_comprobante_venta = $1), " +
            "(SELECT count(*) FROM movimientos_stock WHERE id_comprobante_venta = $1), " +
            "(SELECT count(*) FROM movimientos_cuenta_corriente WHERE id_comprobante_venta = $1) " +
            "FROM comprobantes_venta WHERE id_comprobante_venta = $1",
            l => new InstantaneaDeVenta(
                l.GetString(0), l.GetDecimal(1), l.GetDecimal(2), l.GetInt64(3), l.IsDBNull(4) ? null : l.GetInt32(4),
                l.GetFieldValue<DateTimeOffset>(5), l.GetFieldValue<DateTimeOffset>(6), l.GetBoolean(7),
                l.IsDBNull(8) ? null : l.GetFieldValue<DateTimeOffset>(8), (int)l.GetInt64(9), l.GetDecimal(10),
                (int)l.GetInt64(11), l.GetDecimal(12), (int)l.GetInt64(13), (int)l.GetInt64(14)),
            idVenta));
        return cabecera;
    }

    /// <summary>Filas que un espejo no puede tener nunca: pagos, movimientos de stock y de cuenta corriente.</summary>
    private async Task<(int Pagos, int Stock, int CuentaCorriente)> LedgersDelEspejoAsync(Ctx c, int idEspejo)
    {
        var fila = Assert.Single(await ConsultarAsync(
            c,
            "SELECT (SELECT count(*) FROM pagos_comprobante WHERE id_comprobante_venta = $1), " +
            "(SELECT count(*) FROM movimientos_stock WHERE id_comprobante_venta = $1), " +
            "(SELECT count(*) FROM movimientos_cuenta_corriente WHERE id_comprobante_venta = $1)",
            l => ((int)l.GetInt64(0), (int)l.GetInt64(1), (int)l.GetInt64(2)),
            idEspejo));
        return fila;
    }

    // ---- Llamadas -----------------------------------------------------------------------------------------

    private static string Ruta(int idFacturacion) => $"/api/fiscal/facturaciones/{idFacturacion}/procesar";

    private static async Task<EstadoFiscalDeVenta> ProcesarAsync(HttpClient cliente, int idFacturacion)
    {
        var respuesta = await cliente.PostAsync(Ruta(idFacturacion), null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<EstadoFiscalDeVenta>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(esperado == respuesta.StatusCode, $"Esperado {esperado}, fue {respuesta.StatusCode}. Cuerpo: {cuerpo}");
        using var documento = JsonDocument.Parse(cuerpo);
        Assert.Equal(codigo, documento.RootElement.GetProperty("codigo").GetString());
    }

    private static string CampoDelCuerpo(string cuerpo, string campo)
    {
        var coincidencia = Regex.Match(cuerpo, $@"<(?:\w+:)?{campo}>([^<]*)</", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(coincidencia.Success, $"El cuerpo no tiene {campo}: {cuerpo}");
        return coincidencia.Groups[1].Value;
    }

    private static DateOnly FechaFiscalDe(DateTimeOffset instante) => FechaFiscal.DeInstante(instante, ZonaFiscal);

    private static string Yyyymmdd(DateOnly fecha) => fecha.ToString("yyyyMMdd");

    private static async Task<T> ConTopeAsync<T>(Task<T> tarea)
    {
        var terminada = await Task.WhenAny(tarea, Task.Delay(Tope));
        Assert.True(ReferenceEquals(terminada, tarea), "La llamada no terminó dentro del tope: quedó esperando un lock.");
        return await tarea;
    }

    private static async Task EsperarAsync(Func<Task<bool>> condicion, string descripcion)
    {
        var limite = DateTime.UtcNow + Tope;
        while (DateTime.UtcNow < limite)
        {
            if (await condicion())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"No se alcanzó a tiempo: {descripcion}");
    }

    /// <summary>¿Hay una sesión esperando un lock cuya sentencia empieza con <paramref name="prefijo"/>?</summary>
    private async Task<bool> HayEsperaDeLockAsync(string prefijo)
    {
        await using var conexion = await AbrirOwnerAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE $1 AND pid <> pg_backend_pid()";
        comando.Parameters.Add(new NpgsqlParameter { Value = prefijo + "%" });
        return Convert.ToInt64(await comando.ExecuteScalarAsync()) > 0;
    }

    // ===================================================================================================
    // Emisión aprobada
    // ===================================================================================================

    [Theory]
    [InlineData(false, "FB", 'B')]
    [InlineData(true, "FA", 'A')]
    public async Task UnaVentaAprobadaEmiteSuEspejoConLaLetraYElTipoDelCruceDeCondiciones(
        bool receptorIdentificado, string codigoEsperado, char letraEsperada)
    {
        var c = await PrepararAsync($"apr{receptorIdentificado}");
        var venta = await SembrarVentaDecididaAsync(c, receptorIdentificado ? c.IdClienteRi : c.IdClienteCf);
        var acumuladosTrasLaDecision = await LeerAcumuladosAsync(c);
        var antes = await InstantaneaAsync(c, venta.IdVenta);
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70123456789012"));

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.True(estado.Estado == EstadosFiscalesDeVenta.Aprobado, $"{estado.Estado}: {estado.Error}");
        Assert.Equal("forzada", estado.Motivo);
        Assert.Equal(venta.IdFacturacion, estado.IdFacturacion);
        Assert.Null(estado.Error);
        var factura = estado.Comprobante!;
        Assert.Equal(codigoEsperado, factura.CodigoTipoComprobante);
        Assert.Equal(letraEsperada, factura.Letra);
        Assert.Equal(1, factura.PuntoDeVentaFiscal);
        Assert.Equal(1, factura.Numero);
        Assert.Equal(FechaFiscalDe(c.Reloj.Ahora), factura.Fecha);
        Assert.Equal("70123456789012", factura.Cae);
        Assert.Equal(new DateOnly(2026, 1, 25), factura.CaeVencimiento);
        Assert.StartsWith("https://www.arca.gob.ar/fe/qr/?p=", factura.PayloadQr);
        Assert.Empty(factura.Observaciones);

        // Lo que viajó a ARCA: un solo pedido, número 1, fecha de HOY (no la de la venta) y el documento del receptor.
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        var cuerpo = Assert.Single(c.Wsfe.CuerposDeSolicitud);
        Assert.Equal("1", CampoDelCuerpo(cuerpo, "CbteDesde"));
        Assert.Equal(Yyyymmdd(FechaFiscalDe(c.Reloj.Ahora)), CampoDelCuerpo(cuerpo, "CbteFch"));
        Assert.Equal(receptorIdentificado ? "80" : "99", CampoDelCuerpo(cuerpo, "DocTipo"));
        Assert.Equal(receptorIdentificado ? "20222222223" : "0", CampoDelCuerpo(cuerpo, "DocNro"));
        Assert.Equal("231.00", CampoDelCuerpo(cuerpo, "ImpTotal"));
        Assert.Equal("199.55", CampoDelCuerpo(cuerpo, "ImpNeto"));
        Assert.Equal("31.45", CampoDelCuerpo(cuerpo, "ImpIVA"));

        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(venta.IdVenta, espejo.IdVentaOrigen);
        Assert.Equal(factura.IdComprobante, espejo.Id);
        Assert.Equal(codigoEsperado, espejo.CodigoTipo);
        Assert.Equal("aprobado", espejo.Resultado);
        Assert.Equal("70123456789012", espejo.Cae);
        Assert.Equal(1, espejo.Numero);
        Assert.Equal(c.Reloj.Ahora, espejo.Fecha);
        Assert.NotEqual(antes.Fecha, espejo.Fecha);
        Assert.Null(espejo.IdTurno);
        Assert.Null(espejo.Decidida);
        Assert.Equal(c.IdUsuario, espejo.IdEmpleado);
        Assert.Equal(receptorIdentificado ? c.IdClienteRi : c.IdClienteCf, espejo.IdCliente);
        Assert.Equal(231m, espejo.Total);
        Assert.Equal(199.55m, espejo.Neto);
        Assert.Equal(31.45m, espejo.Iva);

        // Ítems copiados del ticket, sin costo ni lote.
        var items = await LeerItemsAsync(c, espejo.Id);
        Assert.Equal(2, items.Count);
        Assert.Equal(("Producto A", c.IdAlicuota21, 21m, 2m, 60.50m, 121m), (items[0].Descripcion, items[0].IdAlicuota, items[0].PorcentajeIva, items[0].Cantidad, items[0].Precio, items[0].Total));
        Assert.Equal(("Producto B", c.IdAlicuota105, 10.5m, 1m, 110m, 110m), (items[1].Descripcion, items[1].IdAlicuota, items[1].PorcentajeIva, items[1].Cantidad, items[1].Precio, items[1].Total));
        Assert.All(items, i =>
        {
            Assert.Null(i.Costo);
            Assert.False(i.CostoEstimado);
            Assert.Null(i.IdLote);
        });

        // Ni pagos, ni stock, ni cuenta corriente.
        Assert.Equal((0, 0, 0), await LedgersDelEspejoAsync(c, espejo.Id));

        // La decisión.
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal("emitida", decision.Estado);
        Assert.Equal(espejo.Id, decision.IdComprobanteFiscal);
        Assert.Null(decision.UltimoError);

        // El contador avanza y se reconcilia; el acumulado no se mueve; el ticket queda idéntico.
        var serie = Assert.Single(await LeerSeriesAsync(c));
        Assert.Equal((codigoEsperado == "FA" ? (short)1 : (short)6, 2L, (long?)1L), (serie.CodigoAfip, serie.Proximo, serie.UltimoAutorizado));
        Assert.Equal(acumuladosTrasLaDecision, await LeerAcumuladosAsync(c));
        Assert.Equal(antes, await InstantaneaAsync(c, venta.IdVenta));

        // Idempotente: otra llamada devuelve lo mismo sin ninguna llamada a ARCA.
        c.Wsfe.Reiniciar();
        var otra = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equivalent(estado, otra, strict: true);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal(2L, (await LeerSeriesAsync(c)).Single().Proximo);
        Assert.Single(await LeerEspejosAsync(c));
    }

    [Fact]
    public async Task UnaAprobacionConObservacionesEsUnaFacturaValidaConSusObservaciones()
    {
        var c = await PrepararAsync("obs");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeAprobadoConObservaciones());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        var observacion = Assert.Single(estado.Comprobante!.Observaciones);
        Assert.Equal(2101, observacion.Codigo);
        Assert.Equal("El comprobante fue autorizado con observaciones.", observacion.Mensaje);
        Assert.Equal("70123456789013", estado.Comprobante.Cae);

        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal("aprobado_con_observaciones", espejo.Resultado);
        Assert.Contains("2101", espejo.Observaciones);
        Assert.Equal("emitida", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);

        // La lectura idempotente trae las mismas observaciones.
        Assert.Equivalent(estado, await ProcesarAsync(c.Admin, venta.IdFacturacion), strict: true);
    }

    // ===================================================================================================
    // Rechazo definitivo
    // ===================================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnRechazoDefinitivoBorraElEspejoDevuelveElNumeroYElDebitoYNoTocaElTicket(bool errorDeLlamada)
    {
        var c = await PrepararAsync($"rech{errorDeLlamada}");
        var venta = await SembrarVentaDecididaAsync(c);
        var tras = await LeerAcumuladosAsync(c);
        var antes = await InstantaneaAsync(c, venta.IdVenta);

        // Series vecinas con el contador en N+1 que el UPDATE del número NO puede tocar: otra serie del mismo punto de
        // venta y la misma serie de otro punto de venta.
        var idOtroPuntoVenta = await SembrarOtroPuntoDeVentaAsync(c);
        await EjecutarComoOwnerAsync(
            "INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero) VALUES ($1, 1, $2, 2)",
            c.IdPuntoVenta, c.IdTenant);
        await EjecutarComoOwnerAsync(
            "INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero) VALUES ($1, 6, $2, 2)",
            idOtroPuntoVenta, c.IdTenant);

        c.Wsfe.Solicitar = Responde(errorDeLlamada ? FecaeErrorDeLlamada() : FecaeRechazado());
        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        // Un error de nivel de llamada solo se da por definitivo tras consultar el número (que ARCA no tiene).
        Assert.Equal(errorDeLlamada ? ["FECAESolicitar", "FECompConsultar"] : ["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Null(estado.Comprobante);
        Assert.Contains(errorDeLlamada ? "Error de validación de la llamada." : "10015: Rechazado por el receptor.", estado.Error);

        // Regla 1: no queda comprobante fiscal, ni sus ítems; los del ticket siguen.
        Assert.Empty(await LeerEspejosAsync(c));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal("rechazada", decision.Estado);
        Assert.Null(decision.IdComprobanteFiscal);
        Assert.Equal(estado.Error, decision.UltimoError);
        Assert.Equal(0, decision.Intentos);
        Assert.Equal(2, (await InstantaneaAsync(c, venta.IdVenta)).Items);

        // El número vuelve a la serie y las vecinas quedan como estaban.
        Assert.Equal(1L, await LeerProximoAsync(c, 6));
        Assert.Equal(2L, await LeerProximoAsync(c, 1));
        await using (var conexion = await AbrirOwnerAsync())
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT proximo_numero FROM numeraciones_fiscales WHERE id_punto_venta = $1 AND codigo_afip = 6";
            comando.Parameters.Add(new NpgsqlParameter { Value = idOtroPuntoVenta });
            Assert.Equal(2L, Convert.ToInt64(await comando.ExecuteScalarAsync()));
        }

        // Regla 3: solo el débito se devuelve (saldo += vendido, facturado −= vendido); lo vendido y el devengado quedan.
        var esperados = tras.Select(f =>
        {
            var vendido = f.IdMedio == c.IdEfectivo ? ImporteEfectivo : ImporteTransferencia;
            return f with { Saldo = f.Saldo + vendido, Facturado = f.Facturado - vendido };
        }).ToList();
        Assert.Equal(esperados, await LeerAcumuladosAsync(c));

        // El ticket no se toca nunca.
        Assert.Equal(antes, await InstantaneaAsync(c, venta.IdVenta));

        // El próximo comprobante de la serie reusa el número 1.
        var otraVenta = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        c.Wsfe.Reiniciar();
        var siguiente = await ProcesarAsync(c.Admin, otraVenta.IdFacturacion);
        Assert.Equal(1, siguiente.Comprobante!.Numero);
        Assert.Equal("1", CampoDelCuerpo(Assert.Single(c.Wsfe.CuerposDeSolicitud), "CbteDesde"));

        // Idempotente: reprocesar la rechazada devuelve su estado, sin ARCA.
        c.Wsfe.Reiniciar();
        var tras2 = await LeerAcumuladosAsync(c);
        var otra = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, otra.Estado);
        Assert.Equal(estado.Error, otra.Error);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal(tras2, await LeerAcumuladosAsync(c));
    }

    /// <summary>El borrado del espejo rechazado queda acotado a ESE espejo: otro espejo pendiente (de otra serie) y los ítems de las
    /// ventas siguen intactos.</summary>
    [Fact]
    public async Task UnRechazoNoBorraNiLosEspejosNiLosItemsDeOtrasVentas()
    {
        var c = await PrepararAsync("alcance");
        var conFactura = await SembrarVentaDecididaAsync(c, c.IdClienteRi);
        var rechazada = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, conFactura.IdFacturacion);
        var vecino = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal("FA", vecino.CodigoTipo);
        var itemsDelVecino = await LeerItemsAsync(c, vecino.Id);
        Assert.Equal(2, itemsDelVecino.Count);

        c.Wsfe.Solicitar = Responde(FecaeRechazado());
        var estado = await ProcesarAsync(c.Admin, rechazada.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Equal(vecino, Assert.Single(await LeerEspejosAsync(c)));
        Assert.Equal(itemsDelVecino, await LeerItemsAsync(c, vecino.Id));
        Assert.Equal(2, (await InstantaneaAsync(c, conFactura.IdVenta)).Items);
        Assert.Equal(2, (await InstantaneaAsync(c, rechazada.IdVenta)).Items);
    }

    private async Task<int> SembrarOtroPuntoDeVentaAsync(Ctx c)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var puntoVenta = new PuntoVenta
        {
            IdTenant = c.IdTenant, IdEmpresa = c.IdEmpresa, Nombre = "Otro punto de venta", NumeroFiscal = 2,
            Modo = ModoPuntoVenta.Web, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();
        return puntoVenta.Id;
    }

    [Fact]
    public async Task SiElContadorYaAvanzoElNumeroNoSeDevuelveYElEspejoQuedaRechazadoConSuNumero()
    {
        var c = await PrepararAsync("guarda");
        var venta = await SembrarVentaDecididaAsync(c);
        var tras = await LeerAcumuladosAsync(c);

        // Primera llamada sin respuesta: el espejo queda pendiente con el número 1 y el contador en 2.
        c.Wsfe.Solicitar = RespondeHttp500();
        var pendiente = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, pendiente.Estado);
        Assert.Equal(2L, await LeerProximoAsync(c, 6));

        // Alguien tomó otro número de la serie (el contador está en 3) y ARCA rechaza el 1.
        await EjecutarComoOwnerAsync(
            "UPDATE numeraciones_fiscales SET proximo_numero = 3 WHERE id_punto_venta = $1 AND codigo_afip = 6", c.IdPuntoVenta);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeRechazado());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal("rechazado", espejo.Resultado);
        Assert.Equal(1, espejo.Numero);
        Assert.Contains("10015", espejo.Observaciones);
        Assert.Equal(3L, await LeerProximoAsync(c, 6));
        Assert.Equal("rechazada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Contains(
            c.Logs.Entradas,
            e => e.Nivel == LogLevel.Error && e.Categoria.EndsWith(nameof(ServicioDeFacturacionDeVentas), StringComparison.Ordinal)
                && e.Mensaje.Contains("No se pudo devolver el número 1", StringComparison.Ordinal));

        // El débito se devuelve igual.
        var esperados = tras.Select(f =>
        {
            var vendido = f.IdMedio == c.IdEfectivo ? ImporteEfectivo : ImporteTransferencia;
            return f with { Saldo = f.Saldo + vendido, Facturado = f.Facturado - vendido };
        }).ToList();
        Assert.Equal(esperados, await LeerAcumuladosAsync(c));

        // Un espejo rechazado que quedó en pie no impide anular el ticket.
        var anulacion = await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);
    }

    // ===================================================================================================
    // Sin respuesta definitiva
    // ===================================================================================================

    [Fact]
    public async Task SinRespuestaDefinitivaElEspejoQuedaPendienteConSuNumeroYLaSiguienteLlamadaConsultaYAdopta()
    {
        var c = await PrepararAsync("nodef");
        var venta = await SembrarVentaDecididaAsync(c);
        var tras = await LeerAcumuladosAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();

        var primera = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, primera.Estado);
        Assert.Contains("arca_no_definitivo", primera.Error);
        Assert.Null(primera.Comprobante);
        Assert.Equal(0, c.Wsfe.Contar("FECompConsultar"));
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(("pendiente", 1L), (espejo.Resultado, espejo.Numero));
        Assert.Equal(2L, await LeerProximoAsync(c, 6));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal("pendiente", decision.Estado);
        Assert.Equal(1, decision.Intentos);
        Assert.Contains("arca_no_definitivo", decision.UltimoError);
        Assert.Equal(c.Reloj.Ahora.AddSeconds(30), decision.ProximoIntento);
        Assert.Equal(tras, await LeerAcumuladosAsync(c));

        // Segunda llamada: ARCA ya tiene el número. Se consulta y se adopta, SIN ningún FECAESolicitar.
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70555555555555", Yyyymmdd(FechaFiscalDe(espejo.Fecha))));
        c.Wsfe.Solicitar = RespondeHttp500();
        var segunda = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, segunda.Estado);
        Assert.Equal("70555555555555", segunda.Comprobante!.Cae);
        Assert.Equal(1, segunda.Comprobante.Numero);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
        var adoptado = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(("aprobado", "70555555555555", espejo.Fecha), (adoptado.Resultado, adoptado.Cae, adoptado.Fecha));
        Assert.Equal(espejo.Id, (await LeerDecisionAsync(c, venta.IdFacturacion)).IdComprobanteFiscal);
        Assert.Equal(1L, (await LeerSeriesAsync(c)).Single().UltimoAutorizado);
        Assert.Equal(tras, await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task SiLaConsultaNoLoEncuentraSePideConElMismoNumeroYLaFechaDeHoy()
    {
        var c = await PrepararAsync("nohall");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        var primerEspejo = Assert.Single(await LeerEspejosAsync(c));

        c.Reloj.Ahora = c.Reloj.Ahora.AddDays(2);
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70666666666666"));
        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
        var cuerpo = Assert.Single(c.Wsfe.CuerposDeSolicitud);
        Assert.Equal("1", CampoDelCuerpo(cuerpo, "CbteDesde"));
        Assert.Equal(Yyyymmdd(FechaFiscalDe(c.Reloj.Ahora)), CampoDelCuerpo(cuerpo, "CbteFch"));
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(primerEspejo.Id, espejo.Id);
        Assert.Equal(c.Reloj.Ahora, espejo.Fecha);
        Assert.Equal(FechaFiscalDe(c.Reloj.Ahora), estado.Comprobante!.Fecha);
        Assert.Equal(2L, await LeerProximoAsync(c, 6));
    }

    [Fact]
    public async Task UnaConsultaSinRespuestaDejaElEspejoIntactoYDuplicaLaEspera()
    {
        var c = await PrepararAsync("conslt");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        var antes = Assert.Single(await LeerEspejosAsync(c));

        c.Reloj.Ahora = c.Reloj.Ahora.AddMinutes(5);
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = RespondeHttp500();
        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.Equal(0, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(antes, Assert.Single(await LeerEspejosAsync(c)));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(2, decision.Intentos);
        Assert.Equal(c.Reloj.Ahora.AddSeconds(60), decision.ProximoIntento);
    }

    [Fact]
    public async Task LaEsperaTrasUnaFallaSinRespuestaTieneTopeDeDiezMinutos()
    {
        var c = await PrepararAsync("topend");
        var venta = await SembrarVentaDecididaAsync(c);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET intentos = 30 WHERE id_facturacion_venta = $1", venta.IdFacturacion);
        c.Wsfe.Solicitar = RespondeHttp500();

        await ProcesarAsync(c.Admin, venta.IdFacturacion);

        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(31, decision.Intentos);
        Assert.Equal(c.Reloj.Ahora.AddMinutes(10), decision.ProximoIntento);
    }

    [Fact]
    public async Task UnaFallaDelTicketDeAccesoNoConsumeNumeroYSeRegistra()
    {
        var c = await PrepararAsync("wsaa");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsaa.Falla = true;

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.False(string.IsNullOrEmpty(estado.Error));
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Empty(await LeerSeriesAsync(c));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(1, decision.Intentos);
        Assert.Equal(c.Reloj.Ahora.AddSeconds(30), decision.ProximoIntento);
    }

    // ===================================================================================================
    // Compuertas
    // ===================================================================================================

    [Theory]
    [InlineData("empresa_sin_condicion_fiscal")]
    [InlineData("punto_venta_sin_numero_fiscal")]
    [InlineData("empresa_sin_cuit_valido")]
    [InlineData("certificado_fiscal_ausente")]
    [InlineData("condicion_fiscal_receptor_no_mapeada")]
    [InlineData("receptor_sin_cuit")]
    [InlineData("alicuota_sin_mapeo_afip")]
    [InlineData("total_fiscal_no_coincide")]
    [InlineData("tipo_fiscal_invalido")]
    public async Task UnaCompuertaQueFallaNoEmiteNadaNoQuemaNumeroYRegistraElError(string compuerta)
    {
        var c = await PrepararAsync($"g{Math.Abs(compuerta.GetHashCode()) % 100000}");
        var idCliente = compuerta switch
        {
            "condicion_fiscal_receptor_no_mapeada" => c.IdClienteNoResp,
            "receptor_sin_cuit" => c.IdClienteRiSinCuit,
            _ => c.IdClienteCf
        };
        var venta = await SembrarVentaDecididaAsync(c, idCliente);
        var tras = await LeerAcumuladosAsync(c);

        await using var restauracion = await RomperCompuertaAsync(c, venta, compuerta);
        var antes = await InstantaneaAsync(c, venta.IdVenta);

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith(compuerta + ":", estado.Error, StringComparison.Ordinal);
        Assert.Equal(0, c.Wsaa.Solicitudes);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Empty(await LeerSeriesAsync(c));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(("pendiente", 1, estado.Error), (decision.Estado, decision.Intentos, decision.UltimoError));
        Assert.Equal(c.Reloj.Ahora.AddMinutes(5), decision.ProximoIntento);
        Assert.Equal(tras, await LeerAcumuladosAsync(c));
        Assert.Equal(antes, await InstantaneaAsync(c, venta.IdVenta));

        // Un segundo intento duplica la espera (5 min × 2).
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        var segunda = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(2, segunda.Intentos);
        Assert.Equal(c.Reloj.Ahora.AddMinutes(10), segunda.ProximoIntento);

        // Y la espera de una compuerta tiene tope de 4 horas.
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET intentos = 30 WHERE id_facturacion_venta = $1", venta.IdFacturacion);
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(c.Reloj.Ahora.AddHours(4), (await LeerDecisionAsync(c, venta.IdFacturacion)).ProximoIntento);
    }

    private sealed class Restauracion(Func<Task> restaurar) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restaurar();
    }

    /// <summary>Rompe UNA compuerta sobre la venta ya decidida. Los catálogos globales (tipos de comprobante, alícuotas) se
    /// restauran al terminar: la colección es secuencial.</summary>
    private async Task<Restauracion> RomperCompuertaAsync(Ctx c, Venta venta, string compuerta)
    {
        switch (compuerta)
        {
            case "empresa_sin_condicion_fiscal":
                await EjecutarComoOwnerAsync("UPDATE empresas SET id_condicion_fiscal = NULL WHERE id_empresa = $1", c.IdEmpresa);
                break;
            case "punto_venta_sin_numero_fiscal":
                await EjecutarComoOwnerAsync("UPDATE puntos_venta SET numero_fiscal = NULL WHERE id_punto_venta = $1", c.IdPuntoVenta);
                break;
            case "empresa_sin_cuit_valido":
                await EjecutarComoOwnerAsync("UPDATE empresas SET cuit = 'no-numerico' WHERE id_empresa = $1", c.IdEmpresa);
                break;
            case "certificado_fiscal_ausente":
                await EjecutarComoOwnerAsync("UPDATE certificados_fiscales SET activo = false WHERE id_empresa = $1", c.IdEmpresa);
                break;
            case "alicuota_sin_mapeo_afip":
                await EjecutarComoOwnerAsync(
                    "UPDATE items_comprobante_venta SET id_alicuota_iva = (SELECT id_alicuota_iva FROM alicuotas_iva WHERE nombre = 'Exento'), " +
                    "porcentaje_iva = 0 WHERE id_comprobante_venta = $1 AND orden = 1", venta.IdVenta);
                await EjecutarComoOwnerAsync("UPDATE alicuotas_iva SET nombre = 'Exento-roto' WHERE nombre = 'Exento'");
                return new Restauracion(() => EjecutarComoOwnerAsync(
                    "UPDATE alicuotas_iva SET nombre = 'Exento' WHERE nombre = 'Exento-roto'"));
            case "total_fiscal_no_coincide":
                await EjecutarComoOwnerAsync(
                    "UPDATE comprobantes_venta SET total = total + 1 WHERE id_comprobante_venta = $1", venta.IdVenta);
                break;
            case "tipo_fiscal_invalido":
                await EjecutarComoOwnerAsync("UPDATE tipos_comprobante SET activo = false WHERE codigo = 'FB'");
                return new Restauracion(() => EjecutarComoOwnerAsync("UPDATE tipos_comprobante SET activo = true WHERE codigo = 'FB'"));
        }

        return new Restauracion(() => Task.CompletedTask);
    }

    // ===================================================================================================
    // Venta anulada antes de procesar
    // ===================================================================================================

    [Fact]
    public async Task UnaVentaAnuladaAntesDeProcesarCancelaLaDecisionYDevuelveElAcumuladoExacto()
    {
        var c = await PrepararAsync("cancel");
        var previo = new[] { (c.IdEfectivo, 17.25m, 300m, 200m), (c.IdTransferencia, -3.5m, 40m, 10m) };
        var venta = await SembrarVentaDecididaAsync(c);
        Assert.NotEqual(
            previo.Select(p => new FilaDeAcumulado(p.Item1, venta.Periodo, p.Item2, p.Item3, p.Item4)),
            await LeerAcumuladosAsync(c));

        // Una venta con la decisión pendiente y SIN espejo se puede anular.
        var anulacion = await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal(0, c.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Empty(await LeerSeriesAsync(c));
        Assert.Equal("cancelada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);

        // El acumulado vuelve EXACTAMENTE a lo que era antes de la decisión.
        Assert.Equal(
            previo.OrderBy(p => p.Item1).Select(p => new FilaDeAcumulado(p.Item1, venta.Periodo, p.Item2, p.Item3, p.Item4)),
            await LeerAcumuladosAsync(c));

        // Idempotente: no se revierte dos veces.
        var otra = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, otra.Estado);
        Assert.Equal(
            previo.OrderBy(p => p.Item1).Select(p => new FilaDeAcumulado(p.Item1, venta.Periodo, p.Item2, p.Item3, p.Item4)),
            await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task SiLaVentaSeAnulaMientrasSeCreaElEspejoNoSeCreaNingunoYLaDecisionSeCancela()
    {
        var c = await PrepararAsync("carrera1");
        var venta = await SembrarVentaDecididaAsync(c);
        var previo = await LeerAcumuladosAsync(c);

        // Otra sesión sostiene la fila de la venta; el procesamiento llega a su transacción A y se queda esperando ese lock.
        await using var sostenedor = await AbrirOwnerAsync();
        await using var transaccionDelSostenedor = await sostenedor.BeginTransactionAsync();
        await using (var bloqueo = sostenedor.CreateCommand())
        {
            bloqueo.Transaction = transaccionDelSostenedor;
            bloqueo.CommandText = "SELECT 1 FROM comprobantes_venta WHERE id_comprobante_venta = $1 FOR UPDATE";
            bloqueo.Parameters.Add(new NpgsqlParameter { Value = venta.IdVenta });
            await bloqueo.ExecuteScalarAsync();
        }

        var procesamiento = ProcesarAsync(c.Admin, venta.IdFacturacion);
        await EsperarAsync(
            () => HayEsperaDeLockAsync("SELECT estado::text FROM comprobantes_venta"),
            "la transacción A esperando el lock de la venta");

        // La anulación gana: la venta deja de estar emitida y suelta el lock.
        await using (var anulacion = sostenedor.CreateCommand())
        {
            anulacion.Transaction = transaccionDelSostenedor;
            anulacion.CommandText = "UPDATE comprobantes_venta SET estado = 'anulado' WHERE id_comprobante_venta = $1";
            anulacion.Parameters.Add(new NpgsqlParameter { Value = venta.IdVenta });
            await anulacion.ExecuteNonQueryAsync();
        }

        await transaccionDelSostenedor.CommitAsync();

        var estado = await ConTopeAsync(procesamiento);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Empty(await LeerSeriesAsync(c));
        Assert.Equal("cancelada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.NotEqual(previo, await LeerAcumuladosAsync(c));
    }

    /// <summary>El orden de los locks del acumulado (por medio de pago ascendente, igual que la decisión) no se ve en el resultado: se
    /// observa tomando el lock del medio MAYOR desde otra sesión. La reversión tiene que quedar esperando ese lock habiendo
    /// tomado ya el del medio menor; si recorriera al revés, esperaría el mayor sin haber tocado el menor.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaReversionDelAcumuladoTomaLosLocksDeLosMediosEnOrdenAscendente(bool cancelada)
    {
        var c = await PrepararAsync($"orden{cancelada}");
        var venta = await SembrarVentaDecididaAsync(c);
        if (cancelada)
        {
            Assert.Equal(HttpStatusCode.OK, (await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null)).StatusCode);
        }
        else
        {
            c.Wsfe.Solicitar = Responde(FecaeRechazado());
        }

        var menor = Math.Min(c.IdEfectivo, c.IdTransferencia);
        var mayor = Math.Max(c.IdEfectivo, c.IdTransferencia);
        const string Bloqueo =
            "SELECT 1 FROM acumulados_de_facturacion WHERE id_empresa = $1 AND id_medio_pago = $2 AND periodo = $3 FOR UPDATE";

        await using var sostenedor = await AbrirOwnerAsync();
        await using var transaccion = await sostenedor.BeginTransactionAsync();
        await using (var bloqueoDelMayor = sostenedor.CreateCommand())
        {
            bloqueoDelMayor.Transaction = transaccion;
            bloqueoDelMayor.CommandText = Bloqueo;
            bloqueoDelMayor.Parameters.Add(new NpgsqlParameter { Value = c.IdEmpresa });
            bloqueoDelMayor.Parameters.Add(new NpgsqlParameter { Value = mayor });
            bloqueoDelMayor.Parameters.Add(new NpgsqlParameter { Value = venta.Periodo });
            await bloqueoDelMayor.ExecuteScalarAsync();
        }

        var procesamiento = ProcesarAsync(c.Admin, venta.IdFacturacion);
        await EsperarAsync(
            () => HayEsperaDeLockAsync("UPDATE acumulados_de_facturacion"), "la reversión esperando el acumulado del medio mayor");

        await using (var sonda = await AbrirOwnerAsync())
        {
            await using var intento = sonda.CreateCommand();
            intento.CommandText = Bloqueo + " NOWAIT";
            intento.Parameters.Add(new NpgsqlParameter { Value = c.IdEmpresa });
            intento.Parameters.Add(new NpgsqlParameter { Value = menor });
            intento.Parameters.Add(new NpgsqlParameter { Value = venta.Periodo });
            var ex = await Assert.ThrowsAsync<PostgresException>(() => intento.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, ex.SqlState);
        }

        await transaccion.CommitAsync();
        var estado = await ConTopeAsync(procesamiento);
        Assert.Equal(cancelada ? EstadosFiscalesDeVenta.Cancelado : EstadosFiscalesDeVenta.Rechazado, estado.Estado);
    }

    // ===================================================================================================
    // Anulación de una venta facturada
    // ===================================================================================================

    [Fact]
    public async Task UnaVentaConLaFacturaEnTramiteNoSeAnula()
    {
        var c = await PrepararAsync("anultramite");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        var antes = await InstantaneaAsync(c, venta.IdVenta);

        var respuesta = await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null);

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "venta_con_factura_en_tramite");
        Assert.Equal(antes, await InstantaneaAsync(c, venta.IdVenta));
        Assert.Equal("emitido", antes.Estado);
        Assert.Empty(await LeerNotasAsync(c));
    }

    [Fact]
    public async Task AnularUnaVentaInexistenteSigueSiendoUn404()
    {
        var c = await PrepararAsync("anul404");

        var respuesta = await c.Admin.PostAsync("/api/ventas/987654/anulacion", null);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>La carrera real: la transacción A (que bloquea la venta e inserta el espejo) queda pausada justo antes de insertarlo
    /// y la anulación espera el lock de la venta. Al reanudar, el espejo se comitea primero y la anulación, que decide bajo su
    /// propio lock, lo ve todavía pendiente y se rechaza: el cierre del espejo también necesita el lock de la venta.</summary>
    [Fact]
    public async Task LaAnulacionQueEsperaElLockDeLaVentaVeElEspejoRecienComiteado()
    {
        var c = await PrepararAsync("carrera2");
        var venta = await SembrarVentaDecididaAsync(c);

        var insertando = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaEnElInsertDelEspejo(insertando, continuar);
        var pausado = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor)));

        var procesamiento = ProcesarAsync(pausado, venta.IdFacturacion);
        await insertando.Task.WaitAsync(Tope);

        var anulacion = c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null);
        await EsperarAsync(
            () => HayEsperaDeLockAsync("UPDATE comprobantes_venta SET estado"), "la anulación esperando el lock de la venta");

        continuar.TrySetResult();

        var estado = await ConTopeAsync(procesamiento);
        var respuestaDeAnulacion = await ConTopeAsync(anulacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        await AssertCodigoAsync(respuestaDeAnulacion, HttpStatusCode.Conflict, "venta_con_factura_en_tramite");
        Assert.Equal("emitido", (await InstantaneaAsync(c, venta.IdVenta)).Estado);
    }

    private sealed class InterceptorDePausaEnElInsertDelEspejo(
        TaskCompletionSource insertando, TaskCompletionSource continuar) : DbCommandInterceptor
    {
        private int _disparos;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await PausarSiCorrespondeAsync(command);
            return result;
        }

        private async Task PausarSiCorrespondeAsync(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO comprobantes_venta", StringComparison.Ordinal)
                && Interlocked.Increment(ref _disparos) == 1)
            {
                insertando.TrySetResult();
                await continuar.Task;
            }
        }
    }

    // ===================================================================================================
    // Serie ocupada, serie bloqueada, concurrencia
    // ===================================================================================================

    [Fact]
    public async Task UnEspejoPendienteDeLaSerieDetieneLaEmisionDeLaSiguienteSinPenalidad()
    {
        var c = await PrepararAsync("bloq");
        var primera = await SembrarVentaDecididaAsync(c);
        var segunda = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, primera.IdFacturacion);
        c.Wsfe.Reiniciar();
        var acumuladosAntes = await LeerAcumuladosAsync(c);

        var estado = await ProcesarAsync(c.Admin, segunda.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith(ServicioDeFacturacionDeVentas.SerieBloqueada + ":", estado.Error, StringComparison.Ordinal);
        Assert.Empty(c.Wsfe.Operaciones);
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(primera.IdVenta, espejo.IdVentaOrigen);
        Assert.Equal(2L, await LeerProximoAsync(c, 6));
        var decision = await LeerDecisionAsync(c, segunda.IdFacturacion);
        Assert.Equal(("pendiente", 0, null), (decision.Estado, decision.Intentos, decision.ProximoIntento));
        Assert.Equal(acumuladosAntes, await LeerAcumuladosAsync(c));

        // Resuelta la primera, la segunda toma el número 2.
        c.Wsfe.Consultar = Responde(ConsultarEncontrado());
        await ProcesarAsync(c.Admin, primera.IdFacturacion);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        var emitida = await ProcesarAsync(c.Admin, segunda.IdFacturacion);
        Assert.Equal(2, emitida.Comprobante!.Numero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaSerieOcupadaPorOtraLlamadaNoEsperaNiPenaliza(bool conEspejoPreexistente)
    {
        var c = await PrepararAsync($"ocup{conEspejoPreexistente}");
        var venta = await SembrarVentaDecididaAsync(c);
        if (conEspejoPreexistente)
        {
            c.Wsfe.Solicitar = RespondeHttp500();
            await ProcesarAsync(c.Admin, venta.IdFacturacion);
        }
        else
        {
            await EjecutarComoOwnerAsync(
                "INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero) VALUES ($1, 6, $2, 1)",
                c.IdPuntoVenta, c.IdTenant);
        }

        c.Wsfe.Reiniciar();
        var antes = await LeerDecisionAsync(c, venta.IdFacturacion);
        await using var sostenedor = await AbrirOwnerAsync();
        await using var transaccion = await sostenedor.BeginTransactionAsync();
        await using (var bloqueo = sostenedor.CreateCommand())
        {
            bloqueo.Transaction = transaccion;
            bloqueo.CommandText = "SELECT 1 FROM numeraciones_fiscales WHERE id_punto_venta = $1 AND codigo_afip = 6 FOR UPDATE";
            bloqueo.Parameters.Add(new NpgsqlParameter { Value = c.IdPuntoVenta });
            await bloqueo.ExecuteScalarAsync();
        }

        var estado = await ConTopeAsync(ProcesarAsync(c.Admin, venta.IdFacturacion));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith(ServicioDeFacturacionDeVentas.SerieOcupada + ":", estado.Error, StringComparison.Ordinal);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal(antes, await LeerDecisionAsync(c, venta.IdFacturacion));
    }

    /// <summary>La segunda llamada de una carrera: leyó la decisión pendiente, pero otra llamada la resolvió antes de que tomara el
    /// lock de la serie. Bajo el lock relee la decisión y devuelve su estado sin tocar ARCA.</summary>
    [Fact]
    public async Task UnaLlamadaQueTomaElLockDespuesDeQueOtraResolvioLaDecisionDevuelveSuEstadoSinArca()
    {
        var c = await PrepararAsync("carrera3");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, venta.IdFacturacion);

        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(iniciada, continuar);
        var pausado = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor)));

        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70777777777777"));
        var pausada = ProcesarAsync(pausado, venta.IdFacturacion);
        await iniciada.Task.WaitAsync(Tope);

        var ganadora = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, ganadora.Estado);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);

        continuar.TrySetResult();
        var estado = await ConTopeAsync(pausada);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal("70777777777777", estado.Comprobante!.Cae);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
    }

    /// <summary>La decisión se relee bajo su lock antes de moverla: si alguien la cambió mientras ARCA contestaba, el espejo igual
    /// se resuelve (ARCA ya contestó), pero la decisión no se pisa ni se le toca el acumulado; solo se le enlaza el comprobante
    /// adoptado.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SiLaDecisionCambiaMientrasArcaContestaElEspejoSeResuelveYLaDecisionNoSePisa(bool arcaAprueba)
    {
        var c = await PrepararAsync($"guardad{arcaAprueba}");
        var venta = await SembrarVentaDecididaAsync(c);
        var acumulados = await LeerAcumuladosAsync(c);
        c.Wsfe.Solicitar = async _ =>
        {
            await EjecutarComoOwnerAsync(
                "UPDATE facturaciones_de_venta SET estado = 'cancelada' WHERE id_facturacion_venta = $1", venta.IdFacturacion);
            return RespuestaXml(arcaAprueba ? FecaeAprobado("70444444444444") : FecaeRechazado());
        };

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal("cancelada", decision.Estado);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
        if (arcaAprueba)
        {
            var espejo = Assert.Single(await LeerEspejosAsync(c));
            Assert.Equal(("aprobado", "70444444444444"), (espejo.Resultado, espejo.Cae));
            Assert.Equal(espejo.Id, decision.IdComprobanteFiscal);
        }
        else
        {
            Assert.Empty(await LeerEspejosAsync(c));
            Assert.Equal(1L, await LeerProximoAsync(c, 6));
            Assert.Null(decision.IdComprobanteFiscal);
        }

        Assert.Contains(
            c.Logs.Entradas,
            e => e.Nivel == LogLevel.Error && e.Categoria.EndsWith(nameof(ServicioDeFacturacionDeVentas), StringComparison.Ordinal)
                && e.Mensaje.Contains($"La decisión {venta.IdFacturacion} está 'cancelada'", StringComparison.Ordinal));
    }

    /// <summary>La misma guarda en el camino de la falla: si la decisión ya no está pendiente, no se le anota un intento ni un error.</summary>
    [Fact]
    public async Task SiLaDecisionCambiaMientrasArcaNoRespondeNoSeLeAnotaElFallo()
    {
        var c = await PrepararAsync("guardaf");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = async _ =>
        {
            await EjecutarComoOwnerAsync(
                "UPDATE facturaciones_de_venta SET estado = 'cancelada' WHERE id_facturacion_venta = $1", venta.IdFacturacion);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        };

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(("cancelada", 0, null, null), (decision.Estado, decision.Intentos, decision.UltimoError, decision.ProximoIntento));
    }

    // ===================================================================================================
    // Contratos y superficie
    // ===================================================================================================

    [Fact]
    public async Task ReintentarSobreUnEspejoDaElMismo404QueUnIdInexistente()
    {
        var c = await PrepararAsync("reint");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        c.Wsfe.Reiniciar();

        var respuesta = await c.Admin.PostAsync($"/api/fiscal/comprobantes/{espejo.Id}/reintentar", null);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal("pendiente", Assert.Single(await LeerEspejosAsync(c)).Resultado);
    }

    [Fact]
    public async Task OtroTenantNoTocaNadaYObtieneUn404AunConUnIdInexistente()
    {
        var c = await PrepararAsync("rlsa");
        var otro = await PrepararAsync("rlsb");
        var venta = await SembrarVentaDecididaAsync(c);

        var ajena = await otro.Admin.PostAsync(Ruta(venta.IdFacturacion), null);
        var inexistente = await otro.Admin.PostAsync(Ruta(987654), null);

        Assert.Equal(HttpStatusCode.NotFound, ajena.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Empty(otro.Wsfe.Operaciones);
        Assert.Equal("pendiente", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Equal(0, (await LeerDecisionAsync(c, venta.IdFacturacion)).Intentos);
        Assert.Empty(await LeerEspejosAsync(c));
    }

    [Fact]
    public async Task UnVendedorProcesaLaDecisionYUnRootEsRechazadoPorAutorizacion()
    {
        var c = await PrepararAsync("roles");
        var venta = await SembrarVentaDecididaAsync(c);

        var comoRoot = await c.Root.PostAsync(Ruta(venta.IdFacturacion), null);
        Assert.Equal(HttpStatusCode.Forbidden, comoRoot.StatusCode);
        Assert.Empty(c.Wsfe.Operaciones);

        var comoVendedor = await ProcesarAsync(c.Vendedor, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, comoVendedor.Estado);
        Assert.Equal(1, comoVendedor.Comprobante!.Numero);
    }

    [Fact]
    public async Task LosAgregadosYElListadoDeVentasNoContabilizanElEspejoQueEmiteElServicio()
    {
        var c = await PrepararAsync("repor");
        var venta = await SembrarVentaDecididaAsync(c);
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Single(await LeerEspejosAsync(c));

        var listado = await c.Admin.GetAsync($"/api/ventas?idPuntoVenta={c.IdPuntoVenta}&tamanio=50");
        using var documento = JsonDocument.Parse(await listado.Content.ReadAsStringAsync());
        Assert.Equal(1, documento.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(venta.IdVenta, Assert.Single(documento.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());

        var desde = DateOnly.FromDateTime(c.Reloj.Ahora.AddDays(-10).UtcDateTime);
        var hasta = DateOnly.FromDateTime(c.Reloj.Ahora.AddDays(2).UtcDateTime);
        var resumen = await c.Admin.GetAsync(
            $"/api/reportes/ventas/resumen?idEmpresa={c.IdEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}&granularidad=Dia");
        Assert.Equal(HttpStatusCode.OK, resumen.StatusCode);
        var datos = JsonSerializer.Deserialize<ResumenDeVentas>(await resumen.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(1, datos.CantidadTx);
        Assert.Equal(TotalDeLaVenta, datos.NetoVendido);
    }

    [Fact]
    public async Task ElServicioNoEscribeNingunaFilaDeStockPagosCuentaCorrienteNiTurnoAlEmitir()
    {
        var c = await PrepararAsync("cero");
        var venta = await SembrarVentaDecididaAsync(c);
        var antes = await InstantaneaAsync(c, venta.IdVenta);
        var movimientosAntes = await ContarMovimientosDelTenantAsync(c);

        await ProcesarAsync(c.Admin, venta.IdFacturacion);

        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal((0, 0, 0), await LedgersDelEspejoAsync(c, espejo.Id));
        Assert.Null(espejo.IdTurno);
        Assert.Equal(movimientosAntes, await ContarMovimientosDelTenantAsync(c));
        Assert.Equal(antes, await InstantaneaAsync(c, venta.IdVenta));
    }

    private async Task<(long Pagos, long Stock, long CuentaCorriente, long Caja)> ContarMovimientosDelTenantAsync(Ctx c)
    {
        var fila = Assert.Single(await ConsultarAsync(
            c,
            "SELECT (SELECT count(*) FROM pagos_comprobante), (SELECT count(*) FROM movimientos_stock), " +
            "(SELECT count(*) FROM movimientos_cuenta_corriente), (SELECT count(*) FROM turnos_caja)",
            l => (l.GetInt64(0), l.GetInt64(1), l.GetInt64(2), l.GetInt64(3))));
        return fila;
    }

    /// <summary>Una fila de acumulado que falta no frena la resolución fiscal: ARCA ya contestó, así que el espejo se libera, el
    /// número vuelve y la decisión pasa a su estado; el hueco se loguea como error y las demás filas se ajustan igual.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SiFaltaLaFilaDelAcumuladoLaResolucionSigueYSeLoguea(bool cancelada)
    {
        var c = await PrepararAsync($"faltaac{cancelada}");
        var venta = await SembrarVentaDecididaAsync(c);
        await EjecutarComoOwnerAsync(
            "DELETE FROM acumulados_de_facturacion WHERE id_empresa = $1 AND id_medio_pago = $2", c.IdEmpresa, c.IdTransferencia);
        var tras = await LeerAcumuladosAsync(c);
        if (cancelada)
        {
            Assert.Equal(HttpStatusCode.OK, (await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null)).StatusCode);
        }
        else
        {
            c.Wsfe.Solicitar = Responde(FecaeRechazado());
        }

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(cancelada ? EstadosFiscalesDeVenta.Cancelado : EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Equal(cancelada ? "cancelada" : "rechazada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Empty(await LeerEspejosAsync(c));
        if (!cancelada)
        {
            Assert.Equal(1L, await LeerProximoAsync(c, 6));
        }

        // La fila que existe se ajusta; la que falta no se inventa.
        var efectivo = Assert.Single(await LeerAcumuladosAsync(c));
        var previo = Assert.Single(tras);
        Assert.Equal(c.IdEfectivo, efectivo.IdMedio);
        Assert.NotEqual(previo, efectivo);
        Assert.Contains(
            c.Logs.Entradas,
            e => e.Nivel == LogLevel.Error && e.Categoria.EndsWith(nameof(ServicioDeFacturacionDeVentas), StringComparison.Ordinal)
                && e.Mensaje.Contains($"medio {c.IdTransferencia}", StringComparison.Ordinal)
                && e.Mensaje.Contains($"decisión {venta.IdFacturacion}", StringComparison.Ordinal));
    }

    // ===================================================================================================
    // Lecturas viejas, locks y carreras (juicio J-1)
    // ===================================================================================================

    /// <summary>Pausa la transacción número <c>numero</c> (contando desde que se arma) de un host derivado, justo después de abrirla.</summary>
    private sealed class InterceptorDePausaEnLaTransaccionNumero(
        int numero, TaskCompletionSource iniciada, TaskCompletionSource continuar) : DbTransactionInterceptor
    {
        private int _contador;

        public bool Armado { get; set; }

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            if (Armado && Interlocked.Increment(ref _contador) == numero)
            {
                iniciada.TrySetResult();
                await continuar.Task;
            }

            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>Hace fallar el commit número <c>numero</c> (contando desde que se arma): el pedido a ARCA ya salió y la transacción se
    /// deshace.</summary>
    private sealed class InterceptorQueFallaAlComitar(int numero) : DbTransactionInterceptor
    {
        private int _contador;

        public bool Armado { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Armado && Interlocked.Increment(ref _contador) == numero)
            {
                throw new InvalidOperationException("Falla simulada al comitar.");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private sealed record Pausa(HttpClient Cliente, TaskCompletionSource Iniciada, TaskCompletionSource Continuar);

    /// <summary>Un cliente cuya transacción número <paramref name="numero"/> queda pausada tras abrirse.</summary>
    private async Task<Pausa> PausarEnLaTransaccionAsync(Ctx c, int numero)
    {
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaEnLaTransaccionNumero(numero, iniciada, continuar);
        var cliente = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor)));
        interceptor.Armado = true;
        return new Pausa(cliente, iniciada, continuar);
    }

    private async Task<(Venta Venta, FilaDeEspejo Espejo)> SembrarVentaConEspejoPendienteAsync(Ctx c)
    {
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        c.Wsfe.Reiniciar();
        return (venta, espejo);
    }

    /// <summary>Las operaciones hacia ARCA sin los reintentos de transporte del cliente: ante un 5xx el cliente repite el mismo pedido
    /// hasta tres veces, y el espía los cuenta todos.</summary>
    private static IReadOnlyList<string> Pasos(EspiaWsfe wsfe)
    {
        var pasos = new List<string>();
        foreach (var operacion in wsfe.Operaciones.Where(o => pasos.Count == 0 || pasos[^1] != o))
        {
            pasos.Add(operacion);
        }

        return pasos;
    }

    private static bool EsErrorDelServicio(
        (LogLevel Nivel, string Categoria, string Mensaje) entrada, string fragmento) =>
        entrada.Nivel == LogLevel.Error && entrada.Categoria.EndsWith(nameof(ServicioDeFacturacionDeVentas), StringComparison.Ordinal)
        && entrada.Mensaje.Contains(fragmento, StringComparison.Ordinal);

    /// <summary>Lecturas viejas: la segunda llamada leyó la decisión pendiente y sin espejo, y se pausó antes de su transacción A. La
    /// primera terminó con un rechazo definitivo. Al retomar, la segunda relee bajo los locks, no inserta nada ni toma número, y
    /// devuelve el estado actual de la decisión sin llamar a ARCA.</summary>
    [Fact]
    public async Task UnaLlamadaConLecturasViejasNoCreaUnEspejoSiOtraYaRechazoLaDecision()
    {
        var c = await PrepararAsync("viejar");
        var venta = await SembrarVentaDecididaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);
        c.Wsfe.Solicitar = Responde(FecaeRechazado());

        var segunda = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        var primera = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, primera.Estado);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Equal(1L, await LeerProximoAsync(c, 6));
        Assert.Equal("rechazada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);

        // La serie no quedó bloqueada y la venta se puede anular.
        var otra = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarAsync(c.Admin, otra.IdFacturacion)).Comprobante!.Numero);
        Assert.Equal(
            HttpStatusCode.OK, (await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null)).StatusCode);
    }

    [Fact]
    public async Task UnaLlamadaConLecturasViejasDevuelveLaFacturaSiOtraYaLaEmitio()
    {
        var c = await PrepararAsync("viejaa");
        var venta = await SembrarVentaDecididaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70121212121212"));

        var segunda = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        var primera = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, primera.Estado);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal("70121212121212", estado.Comprobante!.Cae);
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(2L, await LeerProximoAsync(c, 6));
    }

    /// <summary>La decisión sigue pendiente pero la primera llamada dejó un espejo pendiente (sin respuesta): la segunda no puede
    /// insertar otro. Al reevaluar lo encuentra y lo resuelve consultando.</summary>
    [Fact]
    public async Task UnaLlamadaConLecturasViejasNoInsertaUnSegundoEspejoSiOtraDejoUnoPendiente()
    {
        var c = await PrepararAsync("viejap");
        var venta = await SembrarVentaDecididaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);
        c.Wsfe.Solicitar = RespondeHttp500();

        var segunda = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        var primera = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, primera.Estado);
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70131313131313", Yyyymmdd(FechaFiscalDe(espejo.Fecha))));
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(["FECAESolicitar", "FECompConsultar"], Pasos(c.Wsfe));
        Assert.Equal(espejo.Id, Assert.Single(await LeerEspejosAsync(c)).Id);
        Assert.Equal(2L, await LeerProximoAsync(c, 6));
    }

    /// <summary>El lock de la fila de la decisión en la transacción A: otra sesión la sostiene y la cancela. La transacción A espera
    /// ese lock, relee la decisión ya cancelada y no inserta nada ni toma número.</summary>
    [Fact]
    public async Task LaCreacionDelEspejoEsperaElLockDeLaDecisionYRespetaLoQueEseLockResolvio()
    {
        var c = await PrepararAsync("lockdec");
        var venta = await SembrarVentaDecididaAsync(c);

        await using var sostenedor = await AbrirOwnerAsync();
        await using var transaccion = await sostenedor.BeginTransactionAsync();
        await using (var bloqueo = sostenedor.CreateCommand())
        {
            bloqueo.Transaction = transaccion;
            bloqueo.CommandText = "SELECT 1 FROM facturaciones_de_venta WHERE id_facturacion_venta = $1 FOR UPDATE";
            bloqueo.Parameters.Add(new NpgsqlParameter { Value = venta.IdFacturacion });
            await bloqueo.ExecuteScalarAsync();
        }

        var procesamiento = ProcesarAsync(c.Admin, venta.IdFacturacion);
        await EsperarAsync(
            () => HayEsperaDeLockAsync("SELECT estado::text FROM facturaciones_de_venta"),
            "la transacción A esperando el lock de la decisión");

        await using (var cancelacion = sostenedor.CreateCommand())
        {
            cancelacion.Transaction = transaccion;
            cancelacion.CommandText =
                "UPDATE facturaciones_de_venta SET estado = 'cancelada' WHERE id_facturacion_venta = $1";
            cancelacion.Parameters.Add(new NpgsqlParameter { Value = venta.IdFacturacion });
            await cancelacion.ExecuteNonQueryAsync();
        }

        await transaccion.CommitAsync();
        var estado = await ConTopeAsync(procesamiento);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Empty(await LeerSeriesAsync(c));
        Assert.Empty(c.Wsfe.Operaciones);
    }

    /// <summary>La anulación gana la carrera: la segunda llamada leyó la venta emitida, pero cuando toma los locks otra ya canceló la
    /// decisión. Su cancelación relee bajo el lock y no revierte el acumulado por segunda vez.</summary>
    [Fact]
    public async Task LaCancelacionQueLlegaTardeNoRevierteElAcumuladoDosVeces()
    {
        var c = await PrepararAsync("cancel2");
        var previo = new[] { (c.IdEfectivo, 17.25m, 300m, 200m), (c.IdTransferencia, -3.5m, 40m, 10m) };
        var venta = await SembrarVentaDecididaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);

        var segunda = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        Assert.Equal(HttpStatusCode.OK, (await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null)).StatusCode);
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, (await ProcesarAsync(c.Admin, venta.IdFacturacion)).Estado);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Empty(await LeerSeriesAsync(c));
        Assert.Equal(
            previo.OrderBy(p => p.Item1).Select(p => new FilaDeAcumulado(p.Item1, venta.Periodo, p.Item2, p.Item3, p.Item4)),
            await LeerAcumuladosAsync(c));
    }

    /// <summary>La otra mitad de la carrera, fabricada con SQL (ninguna ruta del servicio la produce: la anulación rechaza una venta con
    /// espejo): una cancelación con lecturas viejas ve, bajo el lock de la venta, que ya hay un espejo y no cancela.</summary>
    [Fact]
    public async Task LaCancelacionConLecturasViejasNoCancelaSiYaHayUnEspejo()
    {
        var c = await PrepararAsync("cancel3");
        var venta = await SembrarVentaDecididaAsync(c);
        var acumulados = await LeerAcumuladosAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null)).StatusCode);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);

        var cancelacion = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET estado = 'emitido' WHERE id_comprobante_venta = $1", venta.IdVenta);
        c.Wsfe.Solicitar = RespondeHttp500();
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, (await ProcesarAsync(c.Admin, venta.IdFacturacion)).Estado);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET estado = 'anulado' WHERE id_comprobante_venta = $1", venta.IdVenta);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(cancelacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.Equal("pendiente", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Equal("pendiente", Assert.Single(await LeerEspejosAsync(c)).Resultado);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
    }

    /// <summary>La cancelación toma primero el lock de la venta (el mismo que la creación del espejo): mientras otra sesión lo sostiene,
    /// espera, y recién entonces decide.</summary>
    [Fact]
    public async Task LaCancelacionTomaElLockDeLaVentaAntesDeDecidir()
    {
        var c = await PrepararAsync("lockven");
        var venta = await SembrarVentaDecididaAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null)).StatusCode);

        await using var sostenedor = await AbrirOwnerAsync();
        await using var transaccion = await sostenedor.BeginTransactionAsync();
        await using (var bloqueo = sostenedor.CreateCommand())
        {
            bloqueo.Transaction = transaccion;
            bloqueo.CommandText = "SELECT 1 FROM comprobantes_venta WHERE id_comprobante_venta = $1 FOR UPDATE";
            bloqueo.Parameters.Add(new NpgsqlParameter { Value = venta.IdVenta });
            await bloqueo.ExecuteScalarAsync();
        }

        var cancelacion = ProcesarAsync(c.Admin, venta.IdFacturacion);
        await EsperarAsync(
            () => HayEsperaDeLockAsync("SELECT estado::text FROM comprobantes_venta"),
            "la cancelación esperando el lock de la venta");
        await transaccion.CommitAsync();

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, (await ConTopeAsync(cancelacion)).Estado);
    }

    // ===================================================================================================
    // Un espejo pendiente siempre se resuelve (juicio J-1c)
    // ===================================================================================================

    [Theory]
    [InlineData("rechazada", true)]
    [InlineData("rechazada", false)]
    [InlineData("cancelada", true)]
    [InlineData("cancelada", false)]
    public async Task UnEspejoPendienteCuyaDecisionYaNoEstaPendienteSeResuelveSinPedirUnCaeNuevo(string estadoDeLaDecision, bool arcaLoTiene)
    {
        var c = await PrepararAsync($"huerf{estadoDeLaDecision}{arcaLoTiene}");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        await EjecutarComoOwnerAsync(
            $"UPDATE facturaciones_de_venta SET estado = '{estadoDeLaDecision}', ultimo_error = 'previo' WHERE id_facturacion_venta = $1",
            venta.IdFacturacion);
        var acumulados = await LeerAcumuladosAsync(c);
        var antes = await LeerDecisionAsync(c, venta.IdFacturacion);
        c.Wsfe.Consultar = arcaLoTiene
            ? Responde(ConsultarEncontrado("70888888888888", Yyyymmdd(FechaFiscalDe(espejo.Fecha))))
            : Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(antes with { IdComprobanteFiscal = arcaLoTiene ? espejo.Id : null }, decision);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
        if (arcaLoTiene)
        {
            var adoptado = Assert.Single(await LeerEspejosAsync(c));
            Assert.Equal(("aprobado", "70888888888888"), (adoptado.Resultado, adoptado.Cae));
            Assert.Equal(2L, await LeerProximoAsync(c, 6));
        }
        else
        {
            Assert.Empty(await LeerEspejosAsync(c));
            Assert.Equal(1L, await LeerProximoAsync(c, 6));
        }

        Assert.Contains(
            c.Logs.Entradas,
            e => EsErrorDelServicio(e, $"La decisión {venta.IdFacturacion} está '{estadoDeLaDecision}'")
                && e.Mensaje.Contains($"espejo {espejo.Id}", StringComparison.Ordinal));

        // Resuelto, la siguiente llamada no hace nada.
        c.Wsfe.Reiniciar();
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Empty(c.Wsfe.Operaciones);
    }

    /// <summary>Aplicar el resultado bloquea la fila de la decisión y la relee: otra sesión la sostiene mientras ARCA contesta y la
    /// cancela. El resultado espera ese lock, ve la decisión cancelada y no la pisa.</summary>
    [Fact]
    public async Task AplicarElResultadoEsperaElLockDeLaDecisionYNoPisaLoQueEseLockResolvio()
    {
        var c = await PrepararAsync("lockapl");
        var venta = await SembrarVentaDecididaAsync(c);
        var sostenedor = await AbrirOwnerAsync();
        var transaccion = await sostenedor.BeginTransactionAsync();
        c.Wsfe.Solicitar = async _ =>
        {
            await using var bloqueo = sostenedor.CreateCommand();
            bloqueo.Transaction = transaccion;
            bloqueo.CommandText = "SELECT 1 FROM facturaciones_de_venta WHERE id_facturacion_venta = $1 FOR UPDATE";
            bloqueo.Parameters.Add(new NpgsqlParameter { Value = venta.IdFacturacion });
            await bloqueo.ExecuteScalarAsync();
            return RespuestaXml(FecaeAprobado("70181818181818"));
        };

        try
        {
            var procesamiento = ProcesarAsync(c.Admin, venta.IdFacturacion);
            await EsperarAsync(
                () => HayEsperaDeLockAsync("SELECT estado::text FROM facturaciones_de_venta"),
                "el resultado esperando el lock de la decisión");

            await using (var cancelacion = sostenedor.CreateCommand())
            {
                cancelacion.Transaction = transaccion;
                cancelacion.CommandText =
                    "UPDATE facturaciones_de_venta SET estado = 'cancelada' WHERE id_facturacion_venta = $1";
                cancelacion.Parameters.Add(new NpgsqlParameter { Value = venta.IdFacturacion });
                await cancelacion.ExecuteNonQueryAsync();
            }

            await transaccion.CommitAsync();
            var estado = await ConTopeAsync(procesamiento);

            Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
            var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
            Assert.Equal("cancelada", decision.Estado);
            var espejo = Assert.Single(await LeerEspejosAsync(c));
            Assert.Equal(("aprobado", "70181818181818"), (espejo.Resultado, espejo.Cae));
            Assert.Equal(espejo.Id, decision.IdComprobanteFiscal);
        }
        finally
        {
            await transaccion.DisposeAsync();
            await sostenedor.DisposeAsync();
        }
    }

    /// <summary>Si ARCA no contesta la consulta de un espejo cuya decisión ya está resuelta, el espejo sigue pendiente, a la decisión no
    /// se le anota ningún intento y la llamada siguiente lo resuelve.</summary>
    [Fact]
    public async Task UnaConsultaSinRespuestaDejaIntactoElEspejoDeUnaDecisionYaResuelta()
    {
        var c = await PrepararAsync("huerfred");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'rechazada' WHERE id_facturacion_venta = $1", venta.IdFacturacion);
        var antes = await LeerDecisionAsync(c, venta.IdFacturacion);
        c.Wsfe.Consultar = RespondeHttp500();

        await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(["FECompConsultar"], Pasos(c.Wsfe));
        Assert.Equal(antes, await LeerDecisionAsync(c, venta.IdFacturacion));
        Assert.Equal(espejo, Assert.Single(await LeerEspejosAsync(c)));

        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Empty(await LeerEspejosAsync(c));
    }

    /// <summary>Un espejo ya resuelto con una decisión que sigue pendiente es una inconsistencia: se rechaza con un error, no se
    /// reevalúa sin fin.</summary>
    [Fact]
    public async Task UnEspejoResueltoConUnaDecisionPendienteEsUnaInconsistenciaYNoCicla()
    {
        var c = await PrepararAsync("inconsis");
        var venta = await SembrarVentaDecididaAsync(c);
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'pendiente', id_comprobante_fiscal = NULL WHERE id_facturacion_venta = $1",
            venta.IdFacturacion);
        c.Wsfe.Reiniciar();

        var respuesta = await ConTopeAsync(c.Admin.PostAsync(Ruta(venta.IdFacturacion), null));

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.Empty(c.Wsfe.Operaciones);
    }

    /// <summary>La decisión estaba resuelta al empezar y volvió a pendiente antes de que la consulta tomara los locks: se reevalúa y se
    /// sigue como una decisión pendiente más (con sus compuertas), sin dar por resuelto lo que no se resolvió.</summary>
    [Fact]
    public async Task UnaDecisionQueVuelveAPendienteMientrasSeConsultaSeReevaluaConSusCompuertas()
    {
        var c = await PrepararAsync("vuelve");
        var (venta, _) = await SembrarVentaConEspejoPendienteAsync(c);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'rechazada' WHERE id_facturacion_venta = $1", venta.IdFacturacion);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var procesamiento = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'pendiente' WHERE id_facturacion_venta = $1", venta.IdFacturacion);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(procesamiento);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(["FECompConsultar", "FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
    }

    // ===================================================================================================
    // Época del espejo: ninguna otra llamada se mete entre dos pasos de la misma
    // ===================================================================================================

    /// <summary>La primera llamada crea el espejo y se pausa antes de pedir. Otra llamada lo encuentra, lo consulta, guarda la fecha y
    /// pide (sin respuesta). Al retomar, la primera ya no puede pedir directo: el espejo cambió, así que vuelve a consultar.</summary>
    [Fact]
    public async Task UnaLlamadaNoPideDirectoSiOtraConsultoYPidioEntreLaCreacionYElPedido()
    {
        var c = await PrepararAsync("epoca");
        var venta = await SembrarVentaDecididaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 2);
        c.Wsfe.Solicitar = RespondeHttp500();

        var primera = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        var otra = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, otra.Estado);
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], Pasos(c.Wsfe));
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(primera);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.Equal(["FECompConsultar", "FECAESolicitar", "FECompConsultar", "FECAESolicitar"], Pasos(c.Wsfe));
        Assert.Single(await LeerEspejosAsync(c));
    }

    /// <summary>La decisión dejó de estar pendiente entre la creación del espejo y el pedido: no se pide un CAE para ella; el espejo se
    /// consulta y, como ARCA no lo tiene, se libera.</summary>
    [Fact]
    public async Task UnaDecisionQueDejaDeEstarPendienteEntreLaCreacionYElPedidoNoPideCae()
    {
        var c = await PrepararAsync("entre");
        var venta = await SembrarVentaDecididaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 2);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var procesamiento = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'cancelada' WHERE id_facturacion_venta = $1", venta.IdFacturacion);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(procesamiento);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
        Assert.Empty(await LeerEspejosAsync(c));
        Assert.Equal(1L, await LeerProximoAsync(c, 6));
    }

    // ===================================================================================================
    // Consulta, fecha y rechazo de nivel de llamada (juicios J-2, J-3)
    // ===================================================================================================

    [Theory]
    [InlineData(10000, "arca_no_definitivo")]
    [InlineData(600, "ticket_de_acceso_invalido")]
    public async Task UnErrorDeLaConsultaQueNoEsComprobanteInexistenteNoRefrescaLaFechaNiPide(int codigo, string codigoDeDominio)
    {
        var c = await PrepararAsync($"cerr{codigo}");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        c.Reloj.Ahora = c.Reloj.Ahora.AddDays(2);
        c.Wsfe.Consultar = Responde(ConsultarConError(codigo));
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        // Un 600 se consulta una vez más con un ticket fresco; cualquier otro error, una sola vez.
        Assert.Equal(
            codigo == 600 ? new[] { "FECompConsultar", "FECompConsultar" } : new[] { "FECompConsultar" }, c.Wsfe.Operaciones);
        Assert.Equal(espejo, Assert.Single(await LeerEspejosAsync(c)));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(2, decision.Intentos);
        Assert.StartsWith(codigoDeDominio + ":", decision.UltimoError, StringComparison.Ordinal);
    }

    // ===================================================================================================
    // El 600 de la consulta refresca el ticket (K-1)
    // ===================================================================================================

    /// <summary>Una consulta que rechaza el ticket viejo con el 600 y contesta <paramref name="conTicketNuevo"/> a cualquier otro.</summary>
    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> RechazaElTicketViejo(
        string tokenViejo, string conTicketNuevo) =>
        async pedido =>
        {
            var cuerpo = await pedido.Content!.ReadAsStringAsync();
            return RespuestaXml(cuerpo.Contains(tokenViejo, StringComparison.Ordinal) ? ConsultarConError(600) : conTicketNuevo);
        };

    private static void FirmarTicketsDistintos(Ctx c) => c.Wsaa.CuerpoPorFirma = n => LoginCmsGolden($"TOKEN_{n}");

    [Fact]
    public async Task UnSeiscientoEnLaConsultaDelEspejoRefrescaElTicketYAdoptaElCae()
    {
        var c = await PrepararAsync("k1hallado");
        FirmarTicketsDistintos(c);
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        Assert.Equal(1, c.Wsaa.Solicitudes);
        c.Wsfe.Consultar = RechazaElTicketViejo(
            "TOKEN_1", ConsultarEncontrado("70181818181818", Yyyymmdd(FechaFiscalDe(espejo.Fecha))));
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal("70181818181818", estado.Comprobante!.Cae);
        Assert.Equal(["FECompConsultar", "FECompConsultar"], c.Wsfe.Operaciones);
        Assert.Equal(2, c.Wsaa.Solicitudes);
    }

    [Fact]
    public async Task UnSeiscientoEnLaConsultaDelEspejoRefrescaElTicketYSiNoHayNadaPideConElTicketFresco()
    {
        var c = await PrepararAsync("k1nohall");
        FirmarTicketsDistintos(c);
        var (venta, _) = await SembrarVentaConEspejoPendienteAsync(c);
        c.Wsfe.Consultar = RechazaElTicketViejo("TOKEN_1", ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70191919191919"));

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(["FECompConsultar", "FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(2, c.Wsaa.Solicitudes);
        var cuerpo = Assert.Single(c.Wsfe.CuerposDeSolicitud);
        Assert.Contains("TOKEN_2", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN_1", cuerpo, StringComparison.Ordinal);
    }

    /// <summary>El pedido que sigue a la consulta refrescada también consulta (tras un error de llamada) y tiene que hacerlo con el
    /// ticket fresco: usar el invalidado sería un tercer ticket y una consulta de más.</summary>
    [Fact]
    public async Task ElTicketFrescoDeLaConsultaEsElQueUsaLaConsultaPosteriorAUnErrorDeLlamada()
    {
        var c = await PrepararAsync("k1posterior");
        FirmarTicketsDistintos(c);
        var (venta, _) = await SembrarVentaConEspejoPendienteAsync(c);
        c.Wsfe.Consultar = RechazaElTicketViejo("TOKEN_1", ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeErrorDeLlamada());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Equal(["FECompConsultar", "FECompConsultar", "FECAESolicitar", "FECompConsultar"], c.Wsfe.Operaciones);
        Assert.Equal(2, c.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(c));
    }

    [Fact]
    public async Task DosSeiscientosEnLaConsultaDelEspejoSonNoDefinitivosConUnaSolaInvalidacionYNoPiden()
    {
        var c = await PrepararAsync("k1dos");
        FirmarTicketsDistintos(c);
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        c.Wsfe.Consultar = Responde(ConsultarConError(600));
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.Equal(["FECompConsultar", "FECompConsultar"], c.Wsfe.Operaciones);
        Assert.Equal(2, c.Wsaa.Solicitudes);
        Assert.Equal(espejo, Assert.Single(await LeerEspejosAsync(c)));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(2, decision.Intentos);
        Assert.StartsWith("ticket_de_acceso_invalido:", decision.UltimoError, StringComparison.Ordinal);
    }

    // ===================================================================================================
    // Concurrencia entre la lectura de la decisión y la del espejo (K-2)
    // ===================================================================================================

    /// <summary>Pausa la lectura del espejo de la venta (<c>WHERE … id_venta_origen = …</c>) de un host derivado, que en
    /// <c>ProcesarAsync</c> viene después de la lectura de la decisión.</summary>
    private sealed class InterceptorDePausaEnLaLecturaDelEspejo(
        TaskCompletionSource leyendo, TaskCompletionSource continuar) : DbCommandInterceptor
    {
        private int _disparos;

        public bool Armado { get; set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armado
                && command.CommandText.Contains("FROM comprobantes_venta", StringComparison.Ordinal)
                && command.CommandText.Contains(".id_venta_origen = @", StringComparison.Ordinal)
                && Interlocked.Increment(ref _disparos) == 1)
            {
                leyendo.TrySetResult();
                await continuar.Task;
            }

            return result;
        }
    }

    [Fact]
    public async Task UnaLlamadaQueLeyoLaDecisionPendienteYVeElEspejoYaResueltoDevuelveElEstadoActualSinArca()
    {
        var c = await PrepararAsync("k2carrera");
        var venta = await SembrarVentaDecididaAsync(c);
        var leyendo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaEnLaLecturaDelEspejo(leyendo, continuar);
        var pausado = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor)));
        interceptor.Armado = true;

        var segunda = ProcesarAsync(pausado, venta.IdFacturacion);
        await leyendo.Task.WaitAsync(Tope);
        var primera = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, primera.Estado);
        var firmas = c.Wsaa.Solicitudes;
        var operaciones = c.Wsfe.Operaciones.Count;
        continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(primera.Comprobante!.Cae, estado.Comprobante!.Cae);
        Assert.Equal(primera.Comprobante.Numero, estado.Comprobante.Numero);
        Assert.Equal(firmas, c.Wsaa.Solicitudes);
        Assert.Equal(operaciones, c.Wsfe.Operaciones.Count);
        Assert.Single(await LeerEspejosAsync(c));
    }

    /// <summary>La fecha de hoy se guarda en una transacción propia, antes de pedir. El pedido sale y el commit del resultado falla: el
    /// espejo conserva la fecha que viajó, y una adopción posterior la ve.</summary>
    [Fact]
    public async Task LaFechaQueViajoAArcaQuedaGuardadaAunqueSeDeshagaLaTransaccionDelResultado()
    {
        var c = await PrepararAsync("fechaenv");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        c.Reloj.Ahora = c.Reloj.Ahora.AddDays(2);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70141414141414"));
        var falla = new InterceptorQueFallaAlComitar(2);
        var cliente = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(falla)));
        falla.Armado = true;

        var respuesta = await cliente.PostAsync(Ruta(venta.IdFacturacion), null);

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
        var enviada = CampoDelCuerpo(Assert.Single(c.Wsfe.CuerposDeSolicitud), "CbteFch");
        var tras = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(("pendiente", c.Reloj.Ahora), (tras.Resultado, tras.Fecha));
        Assert.NotEqual(espejo.Fecha, tras.Fecha);
        Assert.Equal(enviada, Yyyymmdd(FechaFiscalDe(tras.Fecha)));
        Assert.Equal(1, (await LeerDecisionAsync(c, venta.IdFacturacion)).Intentos);

        // ARCA sí registró el comprobante con la fecha enviada: la adopción posterior muestra la fecha que ARCA tiene.
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70141414141414", enviada));
        var adoptado = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, adoptado.Estado);
        Assert.Equal(FechaFiscalDe(c.Reloj.Ahora), adoptado.Comprobante!.Fecha);
        Assert.Equal(c.Reloj.Ahora, Assert.Single(await LeerEspejosAsync(c)).Fecha);
    }

    /// <summary>Si ARCA registró el comprobante con otro día que el guardado, la adopción deja la fecha de ARCA.</summary>
    [Fact]
    public async Task AlAdoptarElEspejoTomaLaFechaQueArcaTieneRegistrada()
    {
        var c = await PrepararAsync("fechaarca");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        var diaDeArca = FechaFiscalDe(espejo.Fecha).AddDays(-3);
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70151515151515", Yyyymmdd(diaDeArca)));

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(diaDeArca, estado.Comprobante!.Fecha);
        Assert.Equal(diaDeArca, FechaFiscalDe(Assert.Single(await LeerEspejosAsync(c)).Fecha));
    }

    [Theory]
    [InlineData("encontrado")]
    [InlineData("no_encontrado")]
    [InlineData("error")]
    public async Task UnErrorDeLlamadaDeArcaSoloEsDefinitivoSiLaConsultaConfirmaQueNoTieneElNumero(string consulta)
    {
        var c = await PrepararAsync($"llam{consulta}");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeErrorDeLlamada());
        c.Wsfe.Consultar = consulta switch
        {
            "encontrado" => Responde(ConsultarEncontrado("70161616161616", Yyyymmdd(FechaFiscalDe(c.Reloj.Ahora)))),
            "no_encontrado" => Responde(ConsultarNoEncontrado()),
            _ => RespondeHttp500()
        };

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(["FECAESolicitar", "FECompConsultar"], Pasos(c.Wsfe));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        switch (consulta)
        {
            case "encontrado":
                Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
                Assert.Equal("70161616161616", estado.Comprobante!.Cae);
                Assert.Equal("emitida", decision.Estado);
                Assert.Equal("aprobado", Assert.Single(await LeerEspejosAsync(c)).Resultado);
                Assert.Equal(2L, await LeerProximoAsync(c, 6));
                break;
            case "no_encontrado":
                Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
                Assert.Equal("rechazada", decision.Estado);
                Assert.Empty(await LeerEspejosAsync(c));
                Assert.Equal(1L, await LeerProximoAsync(c, 6));
                break;
            default:
                Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
                Assert.Equal(("pendiente", 1), (decision.Estado, decision.Intentos));
                Assert.Contains("arca_no_definitivo", decision.UltimoError);
                var espejo = Assert.Single(await LeerEspejosAsync(c));
                Assert.Equal(("pendiente", 1L), (espejo.Resultado, espejo.Numero));
                Assert.Equal(2L, await LeerProximoAsync(c, 6));
                break;
        }
    }

    // ===================================================================================================
    // Un espejo ya creado se consulta antes que las compuertas de la venta (S-1)
    // ===================================================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnaCompuertaDeLaVentaRotaNoImpideAdoptarUnCaeQueArcaYaTiene(bool arcaLoTiene)
    {
        var c = await PrepararAsync($"adopt{arcaLoTiene}");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        await EjecutarComoOwnerAsync("UPDATE empresas SET id_condicion_fiscal = NULL WHERE id_empresa = $1", c.IdEmpresa);
        c.Wsfe.Consultar = arcaLoTiene
            ? Responde(ConsultarEncontrado("70171717171717", Yyyymmdd(FechaFiscalDe(espejo.Fecha))))
            : Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
        if (arcaLoTiene)
        {
            Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
            Assert.Equal("70171717171717", estado.Comprobante!.Cae);
        }
        else
        {
            Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
            Assert.StartsWith("empresa_sin_condicion_fiscal:", estado.Error, StringComparison.Ordinal);
            Assert.Equal(espejo, Assert.Single(await LeerEspejosAsync(c)));
            Assert.Equal(2, (await LeerDecisionAsync(c, venta.IdFacturacion)).Intentos);
        }
    }

    /// <summary>Evaluar las compuertas puede LANZAR (no solo devolver un fallo): un código de condición fiscal sin mapeo hace que
    /// <c>MapearCondicionFiscal</c> lance. Las compuertas se evalúan después de la consulta, así que eso no impide adoptar un CAE que
    /// ARCA ya tiene.</summary>
    [Fact]
    public async Task UnaCompuertaQueLanzaAlEvaluarseNoImpideAdoptarUnCaeQueArcaYaTiene()
    {
        var c = await PrepararAsync("lanza");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        await EjecutarComoOwnerAsync("UPDATE condiciones_fiscales SET codigo = 'CF_ROTO' WHERE codigo = 'CF'");
        await using var restauracion = new Restauracion(
            () => EjecutarComoOwnerAsync("UPDATE condiciones_fiscales SET codigo = 'CF' WHERE codigo = 'CF_ROTO'"));
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70202020202020", Yyyymmdd(FechaFiscalDe(espejo.Fecha))));
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal("70202020202020", estado.Comprobante!.Cae);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
    }

    [Fact]
    public async Task SiLaConsultaNoPuedeCorrerPorFaltarSuCertificadoSeRegistraLaCompuertaComoSiempre()
    {
        var c = await PrepararAsync("sincert");
        var (venta, espejo) = await SembrarVentaConEspejoPendienteAsync(c);
        await EjecutarComoOwnerAsync("UPDATE certificados_fiscales SET activo = false WHERE id_empresa = $1", c.IdEmpresa);

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith("certificado_fiscal_ausente:", estado.Error, StringComparison.Ordinal);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal(espejo, Assert.Single(await LeerEspejosAsync(c)));
        Assert.Equal(2, (await LeerDecisionAsync(c, venta.IdFacturacion)).Intentos);
    }

    /// <summary>La decisión cambió entre la lectura inicial y el registro de una falla del ticket de acceso: el intento no se anota.</summary>
    [Fact]
    public async Task UnaFallaDelTicketNoSeLeAnotaAUnaDecisionQueYaNoEstaPendiente()
    {
        var c = await PrepararAsync("ticketv");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsaa.Falla = true;
        c.Wsaa.AntesDeResponder = () => EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'cancelada' WHERE id_facturacion_venta = $1", venta.IdFacturacion);

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(("cancelada", 0, null), (decision.Estado, decision.Intentos, decision.UltimoError));
    }
}
