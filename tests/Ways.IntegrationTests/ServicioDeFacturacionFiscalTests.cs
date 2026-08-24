using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Fiscal;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-19a-slice5 (tasks 5.10-5.24, targets 64-76): <see cref="ServicioDeFacturacionFiscal"/>
/// end-to-end contra Postgres real + un <see cref="HttpMessageHandler"/> espía por cliente
/// (WSAA/WSFE) — los cinco gates de I4, I2 (<c>FECompConsultar</c> antes de reintentar), U2
/// (mutation-proof-tests regla 3 v1.1), el primer caller real de
/// <see cref="Ways.Domain.Ventas.ResolvedorDeLetraComprobante"/>, el guard NO_RESP y el guard del
/// POS reasertado byte-idéntico.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeFacturacionFiscalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    /// <summary>Mismo hallazgo que <c>ServicioDeCertificadosTests</c>: <c>Program.cs</c> lee
    /// <c>builder.Configuration</c> síncrono, así que la clave maestra tiene que estar en el
    /// entorno ANTES del primer <c>CreateClient()</c> de esta clase.</summary>
    static ServicioDeFacturacionFiscalTests()
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

    // --- Los dos espías: WSAA siempre firma un TA válido; WSFE rutea por SOAPAction ---

    private sealed class EspiaWsaa(string cuerpoDeRespuesta) : HttpMessageHandler
    {
        public int Solicitudes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Solicitudes++;
            return Task.FromResult(RespuestaXml(cuerpoDeRespuesta));
        }
    }

    private sealed class EspiaWsfe : HttpMessageHandler
    {
        public List<string> Operaciones { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Solicitar { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? Consultar { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var accion = request.Headers.TryGetValues("SOAPAction", out var valores) ? valores.FirstOrDefault() ?? string.Empty : string.Empty;

            if (accion.Contains("FECAESolicitar", StringComparison.Ordinal))
            {
                Operaciones.Add("FECAESolicitar");
                return (Solicitar ?? throw new InvalidOperationException("EspiaWsfe.Solicitar no configurado."))(request);
            }

            if (accion.Contains("FECompConsultar", StringComparison.Ordinal))
            {
                Operaciones.Add("FECompConsultar");
                return (Consultar ?? throw new InvalidOperationException("EspiaWsfe.Consultar no configurado."))(request);
            }

            _ = await Task.FromResult(0);
            throw new InvalidOperationException($"Operación WSFE no soportada por el espía: '{accion}'.");
        }
    }

    private static HttpResponseMessage RespuestaXml(string contenido) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(contenido, Encoding.UTF8, "text/xml")
    };

    /// <summary>TA con vencimiento lejano — evita que <c>RepositorioEnMemoriaDeTicketDeAcceso</c>
    /// lo trate como expirado contra el reloj real del sistema y dispare un <c>loginCms</c> extra
    /// en medio de un test que cuenta llamadas.</summary>
    private static string LoginCmsGolden(string token = "TOKEN_ESPIA", string sign = "SIGN_ESPIA") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope " +
        "xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body>" +
        "<loginCmsResponse xmlns=\"http://wsaa.view.sua.dvadac.desa.afip.gov\"><loginCmsReturn>" +
        "&lt;?xml version=\"1.0\" encoding=\"UTF-8\"?&gt;&lt;loginTicketResponse version=\"1.0\"&gt;" +
        "&lt;header&gt;&lt;source&gt;CN=test&lt;/source&gt;&lt;destination&gt;CN=ways&lt;/destination&gt;" +
        "&lt;uniqueId&gt;1&lt;/uniqueId&gt;&lt;generationTime&gt;2020-01-01T00:00:00-03:00&lt;/generationTime&gt;" +
        "&lt;expirationTime&gt;2099-01-01T00:00:00-03:00&lt;/expirationTime&gt;&lt;/header&gt;" +
        $"&lt;credentials&gt;&lt;token&gt;{token}&lt;/token&gt;&lt;sign&gt;{sign}&lt;/sign&gt;&lt;/credentials&gt;" +
        "&lt;/loginTicketResponse&gt;</loginCmsReturn></loginCmsResponse></soapenv:Body></soapenv:Envelope>";

    private static string FecaeAprobado(long cbteDesde, string cae = "70123456789012") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECAESolicitarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECAESolicitarResult>" +
        "<FeCabResp><Cuit>20111111112</Cuit><PtoVta>1</PtoVta><CbteTipo>1</CbteTipo>" +
        "<FchProceso>20260115121500</FchProceso><CantReg>1</CantReg><Resultado>A</Resultado><Reproceso>N</Reproceso></FeCabResp>" +
        "<FeDetResp><FECAEDetResponse><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro>" +
        $"<CbteDesde>{cbteDesde}</CbteDesde><CbteHasta>{cbteDesde}</CbteHasta><CbteFch>20260115</CbteFch>" +
        $"<Resultado>A</Resultado><Observaciones /><CAE>{cae}</CAE><CAEFchVto>20260125</CAEFchVto></FECAEDetResponse></FeDetResp>" +
        "<Errors /><Events /></FECAESolicitarResult></FECAESolicitarResponse></soapenv:Body></soapenv:Envelope>";

    private static string FecaeRechazado() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECAESolicitarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECAESolicitarResult>" +
        "<FeCabResp><Cuit>20111111112</Cuit><PtoVta>1</PtoVta><CbteTipo>1</CbteTipo>" +
        "<FchProceso>20260115121500</FchProceso><CantReg>1</CantReg><Resultado>R</Resultado><Reproceso>N</Reproceso></FeCabResp>" +
        "<FeDetResp><FECAEDetResponse><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro>" +
        "<CbteDesde>1</CbteDesde><CbteHasta>1</CbteHasta><CbteFch>20260115</CbteFch>" +
        "<Resultado>R</Resultado><Observaciones><Obs><Code>10015</Code><Msg>Rechazado.</Msg></Obs></Observaciones></FECAEDetResponse></FeDetResp>" +
        "<Errors /><Events /></FECAESolicitarResult></FECAESolicitarResponse></soapenv:Body></soapenv:Envelope>";

    private static string FecompConsultarEncontrado(long cbteDesde, string cae = "70999999999999") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECompConsultarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECompConsultarResult>" +
        $"<ResultGet><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro><CbteDesde>{cbteDesde}</CbteDesde>" +
        $"<CbteHasta>{cbteDesde}</CbteHasta><CbteFch>20260115</CbteFch><ImpTotal>100.00</ImpTotal><ImpTotConc>0.00</ImpTotConc>" +
        $"<ImpNeto>82.64</ImpNeto><ImpOpEx>0.00</ImpOpEx><ImpIVA>17.36</ImpIVA><ImpTrib>0.00</ImpTrib><MonId>PES</MonId>" +
        $"<MonCotiz>1</MonCotiz><CodAutorizacion>{cae}</CodAutorizacion><EmisionTipo>CAE</EmisionTipo><FchVto>20260125</FchVto>" +
        "<Resultado>A</Resultado><Observaciones /></ResultGet><Errors /></FECompConsultarResult></FECompConsultarResponse>" +
        "</soapenv:Body></soapenv:Envelope>";

    private static string FecompConsultarNoEncontrado() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECompConsultarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECompConsultarResult>" +
        "<ResultGet /><Errors><Err><Code>602</Code><Msg>No encontrado.</Msg></Err></Errors></FECompConsultarResult>" +
        "</FECompConsultarResponse></soapenv:Body></soapenv:Envelope>";

    // --- Setup: tenant + empresa (condición fiscal + CUIT) + PV (número fiscal) + certificado ---

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdArea, int IdListaPrecio, int IdAlicuota21,
        int IdClienteRi, int IdClienteConsumidorFinal, int IdClienteNoResp);

    private static (byte[] Pfx, string Password) GenerarPfx(string cn)
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest(cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var ahora = DateTimeOffset.UtcNow;
        using var certificado = solicitud.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(1));
        var password = Guid.NewGuid().ToString("N");
        return (certificado.Export(X509ContentType.Pkcs12, password), password);
    }

    private async Task<(Contexto Ctx, HttpClient Admin, HttpClient Vendedor, HttpClient Root)> PrepararAsync(
        string nombre, EspiaWsaa espiaWsaa, EspiaWsfe espiaWsfe)
    {
        var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // Sobre-registro DELIBERADO (mismo trámite que WaysApiFixture.ConfigureWebHost con el
            // DbContext): AddHttpClient<TClient,TImpl> vuelve a agregar el named client de
            // ASP.NET Core — ConfigurePrimaryHttpMessageHandler, llamado DESPUÉS del registro de
            // producción (AgregarInfrastructure), es el que gana al resolver el handler primario.
            // El BaseAddress ES necesario acá aunque el espía intercepte todo antes de la red
            // (HttpClient exige una URI absoluta para armar el HttpRequestMessage) — nunca viaja a
            // ningún lado, verify criterion 8 sigue intacto (producción sin default, ver
            // DependencyInjection.AgregarInfrastructure).
            services.AddHttpClient<IClienteWsaa, ClienteWsaa>(http => http.BaseAddress = new Uri("https://wsaa.espia.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => espiaWsaa);
            services.AddHttpClient<IClienteWsfe, ClienteWsfe>(http => http.BaseAddress = new Uri("https://wsfe.espia.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => espiaWsfe);
        }));

        var root = factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants", new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var corto = Guid.NewGuid().ToString("N")[..8];
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vend@ways.test";
        var altaVendedor = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"vend-{corto}", mailVendedor, (int)RolConocido.Vendedor, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);
        var vendedor = factory.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var empresa = await db.Empresas.FirstAsync(e => e.Id == resultado.IdEmpresa);
        empresa.Cuit = "20111111112";
        var idCondicionRi = await db.CondicionesFiscales.Where(c => c.Codigo == "RI").Select(c => c.Id).FirstAsync();
        empresa.IdCondicionFiscal = idCondicionRi;

        var puntoVenta = await db.PuntosVenta.FirstAsync(p => p.Id == resultado.IdPuntoVenta);
        puntoVenta.NumeroFiscal = 1;
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Fiscal-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista fiscal", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idAlicuota21 = await db.AlicuotasIva.Where(a => a.CodigoAfip == 5).Select(a => a.Id).FirstAsync();
        var idCondicionCf = await db.CondicionesFiscales.Where(c => c.Codigo == "CF").Select(c => c.Id).FirstAsync();
        var idCondicionNoResp = await db.CondicionesFiscales.Where(c => c.Codigo == "NO_RESP").Select(c => c.Id).FirstAsync();

        var clienteRi = SembrarCliente(db, resultado.IdTenant, lista.Id, idCondicionRi, "20222222223", TipoDocumento.Cuit, ahora);
        var clienteCf = SembrarCliente(db, resultado.IdTenant, lista.Id, idCondicionCf, null, null, ahora);
        var clienteNoResp = SembrarCliente(db, resultado.IdTenant, lista.Id, idCondicionNoResp, "30333333334", TipoDocumento.Cuil, ahora);
        await db.SaveChangesAsync();

        var (pfx, password) = GenerarPfx("CN=Ways Test");
        var registroCertificado = await admin.PostAsJsonAsync("/api/fiscal/certificados", new
        {
            IdEmpresa = resultado.IdEmpresa,
            Ambiente = AmbienteFiscal.Homologacion,
            Alias = "Homo",
            CuitTitular = "20111111112",
            Pfx = pfx,
            PasswordPfx = password
        });
        Assert.Equal(HttpStatusCode.Created, registroCertificado.StatusCode);

        var ctx = new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, area.Id, lista.Id, idAlicuota21,
            clienteRi.Id, clienteCf.Id, clienteNoResp.Id);

        return (ctx, admin, vendedor, root);
    }

    private static Cliente SembrarCliente(
        Ways.Infrastructure.Persistencia.WaysDbContext db, int idTenant, int idListaPrecio, int idCondicionFiscal,
        string? numeroDocumento, TipoDocumento? tipoDocumento, DateTimeOffset ahora)
    {
        var numero = Random.Shared.Next(1000, 999_999);
        var cliente = new Cliente
        {
            IdTenant = idTenant, Numero = numero, Nombre = $"Cliente {numero}", IdCondicionFiscal = idCondicionFiscal,
            IdListaPrecio = idListaPrecio, TipoDocumento = tipoDocumento, NumeroDocumento = numeroDocumento,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        return cliente;
    }

    private static object SolicitudEmision(Contexto ctx, int idCliente, string codigo = "FA") => new
    {
        IdPuntoVenta = ctx.IdPuntoVenta,
        CodigoTipoComprobante = codigo,
        IdCliente = idCliente,
        Lineas = new[]
        {
            new
            {
                IdArticulo = (int?)null, Descripcion = "Producto fiscal", IdArea = ctx.IdArea,
                IdListaPrecio = ctx.IdListaPrecio, IdAlicuotaIva = ctx.IdAlicuota21, Cantidad = 1m,
                PrecioUnitario = 121.00m, DescuentoUnitario = 0m
            }
        },
        Observaciones = (string?)null
    };

    // --- I4: los cinco gates, CERO requests HTTP en cada uno (target 64/65) ---

    [Fact]
    public async Task LosCincoGatesDevuelvenSuPropio409YCeroRequestsHttp()
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe();
        var (ctx, admin, _, _) = await PrepararAsync(nameof(LosCincoGatesDevuelvenSuPropio409YCeroRequestsHttp), espiaWsaa, espiaWsfe);

        // Gate 1: empresa sin condición fiscal.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var empresa = await db.Empresas.FirstAsync(e => e.Id == ctx.IdEmpresa);
            empresa.IdCondicionFiscal = null;
            await db.SaveChangesAsync();
        }
        var r1 = await admin.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteRi));
        await AssertCodigoAsync(r1, HttpStatusCode.Conflict, "empresa_sin_condicion_fiscal");

        // Restaurar y romper gate 2: PV sin número fiscal.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var empresa = await db.Empresas.FirstAsync(e => e.Id == ctx.IdEmpresa);
            empresa.IdCondicionFiscal = await db.CondicionesFiscales.Where(c => c.Codigo == "RI").Select(c => c.Id).FirstAsync();
            var pv = await db.PuntosVenta.FirstAsync(p => p.Id == ctx.IdPuntoVenta);
            pv.NumeroFiscal = null;
            await db.SaveChangesAsync();
        }
        var r2 = await admin.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteRi));
        await AssertCodigoAsync(r2, HttpStatusCode.Conflict, "punto_venta_sin_numero_fiscal");

        // Restaurar y romper gate 3: tipo inexistente.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var pv = await db.PuntosVenta.FirstAsync(p => p.Id == ctx.IdPuntoVenta);
            pv.NumeroFiscal = 1;
            await db.SaveChangesAsync();
        }
        var r3 = await admin.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteRi, codigo: "TX"));
        await AssertCodigoAsync(r3, HttpStatusCode.Conflict, "tipo_fiscal_invalido");

        // Gate 4: receptor NO_RESP.
        var r4 = await admin.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteNoResp));
        await AssertCodigoAsync(r4, HttpStatusCode.Conflict, "condicion_fiscal_receptor_no_mapeada");

        // Gate 5: sin certificado activo — lo desactivamos.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var certificado = await db.CertificadosFiscales.FirstAsync(c => c.IdEmpresa == ctx.IdEmpresa);
            certificado.Activo = false;
            await db.SaveChangesAsync();
        }
        var r5 = await admin.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteRi));
        await AssertCodigoAsync(r5, HttpStatusCode.Conflict, "certificado_fiscal_ausente");

        // target 64: los CINCO caminos, CERO bytes en el cable — ni WSAA ni WSFE vieron un solo request.
        Assert.Equal(0, espiaWsaa.Solicitudes);
        Assert.Empty(espiaWsfe.Operaciones);
    }

    // --- Emisión exitosa: letra resuelta (target 70), D12 gap (target 75), DTO honesto (5.23) ---

    [Theory]
    [InlineData(true, 'A')]  // RI → RI ⇒ A
    [InlineData(false, 'B')] // RI → CF ⇒ B
    public async Task LaLetraResueltaPorElCruceDeCondicionesEsCorrectaYLaEmisionEsAprobada(
        bool receptorEsRi, char letraEsperada)
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe();
        var (ctx, admin, _, _) = await PrepararAsync(
            $"{nameof(LaLetraResueltaPorElCruceDeCondicionesEsCorrectaYLaEmisionEsAprobada)}-{receptorEsRi}",
            espiaWsaa, espiaWsfe);

        espiaWsfe.Solicitar = req => RespuestaXml(FecaeAprobado(1));

        var idCliente = receptorEsRi ? ctx.IdClienteRi : ctx.IdClienteConsumidorFinal;
        var respuesta = await admin.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, idCliente));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteFiscalEmitido>(OpcionesJson))!;
        Assert.Equal(letraEsperada, emitido.Letra); // target 70
        Assert.Equal(ResultadoFiscal.Aprobado, emitido.ResultadoFiscal);
        Assert.NotNull(emitido.Cae);
        Assert.NotNull(emitido.PayloadQr);
        Assert.StartsWith("https://www.afip.gob.ar/fe/qr/?p=", emitido.PayloadQr);
        Assert.Single(espiaWsfe.Operaciones); // un solo FECAESolicitar, cero FECompConsultar

        // target 75 — D12's declared gap, el trip-wire de 19c: CERO filas en las tres tablas que
        // esta slice NUNCA escribe.
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.False(await db.MovimientosStock.AnyAsync(m => m.IdTenant == ctx.IdTenant));
        Assert.False(await db.PagosComprobante.AnyAsync(p => p.IdComprobanteVenta == emitido.Id));
        Assert.False(await db.MovimientosCuentaCorriente.AnyAsync(m => m.IdTenant == ctx.IdTenant));

        // target 5.23 — dto-contract-honesty: ningún nombre de propiedad de material de clave en
        // la respuesta serializada.
        var crudo = await respuesta.Content.ReadAsStringAsync();
        foreach (var prohibido in new[] { "clavePrivadaCifrada", "nonce", "tagAutenticacion", "certificadoPem", "claveMaestra" })
        {
            Assert.DoesNotContain(prohibido, crudo, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task UnRechazoDeArcaPersisteRechazadoSinCaeYElNumeroQuedaLigado()
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe { Solicitar = _ => RespuestaXml(FecaeRechazado()) };
        var (ctx, admin, _, _) = await PrepararAsync(
            nameof(UnRechazoDeArcaPersisteRechazadoSinCaeYElNumeroQuedaLigado), espiaWsaa, espiaWsfe);

        var respuesta = await admin.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteRi));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteFiscalEmitido>(OpcionesJson))!;
        Assert.Equal(ResultadoFiscal.Rechazado, emitido.ResultadoFiscal);
        Assert.Null(emitido.Cae);
        Assert.Null(emitido.PayloadQr);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var numeracion = await db.Database.SqlQuery<long>(
            $"SELECT proximo_numero AS \"Value\" FROM numeraciones_fiscales WHERE id_punto_venta = {ctx.IdPuntoVenta} AND codigo_afip = 1")
            .FirstAsync();
        Assert.Equal(emitido.Numero + 1, numeracion); // I1: el número quedó CONSUMIDO, ligado a esta fila rechazada
    }

    // --- I2: FECompConsultar SIEMPRE antes de reintentar (targets 66/67) ---

    private async Task<int> SembrarPendienteAsync(Contexto ctx, long numero)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var proximoNumero = numero + 1;
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero) VALUES ({ctx.IdPuntoVenta}, 1, {ctx.IdTenant}, {proximoNumero}) ON CONFLICT (id_punto_venta, codigo_afip) DO UPDATE SET proximo_numero = {proximoNumero}");

        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();
        var comprobante = new Ways.Domain.Ventas.ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = await db.TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.Id).FirstAsync(),
            Numero = numero,
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = idEmpleado,
            IdCliente = ctx.IdClienteRi,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            NetoGravado = 82.64m,
            IvaTotal = 17.36m,
            Estado = Ways.Domain.Ventas.EstadoComprobante.Emitido,
            ResultadoFiscal = ResultadoFiscal.Pendiente,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    [Fact]
    public async Task ElReintentoConsultaPrimeroYAdoptaElCaeExistenteSinReSolicitar()
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe { Consultar = _ => RespuestaXml(FecompConsultarEncontrado(55, "70999999999999")) };
        var (ctx, admin, _, _) = await PrepararAsync(
            nameof(ElReintentoConsultaPrimeroYAdoptaElCaeExistenteSinReSolicitar), espiaWsaa, espiaWsfe);

        var idComprobante = await SembrarPendienteAsync(ctx, 55);

        var respuesta = await admin.PostAsync($"/api/fiscal/comprobantes/{idComprobante}/reintentar", null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteFiscalEmitido>(OpcionesJson))!;
        Assert.Equal("70999999999999", emitido.Cae);
        Assert.Equal(ResultadoFiscal.Aprobado, emitido.ResultadoFiscal);

        // target 67: CERO FECAESolicitar, el CAE se adoptó — solo hubo UN FECompConsultar.
        Assert.Equal(["FECompConsultar"], espiaWsfe.Operaciones);
    }

    [Fact]
    public async Task ElReintentoConsultaPrimeroYSoloEmiteUnFecaeSolicitarSiNoEncuentraNada()
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe
        {
            Consultar = _ => RespuestaXml(FecompConsultarNoEncontrado()),
            Solicitar = _ => RespuestaXml(FecaeAprobado(56))
        };
        var (ctx, admin, _, _) = await PrepararAsync(
            nameof(ElReintentoConsultaPrimeroYSoloEmiteUnFecaeSolicitarSiNoEncuentraNada), espiaWsaa, espiaWsfe);

        var idComprobante = await SembrarPendienteAsync(ctx, 56);

        var respuesta = await admin.PostAsync($"/api/fiscal/comprobantes/{idComprobante}/reintentar", null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        // target 66: FECompConsultar PRECEDE, y exactamente UN FECAESolicitar en todo el reintento.
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], espiaWsfe.Operaciones);
    }

    // --- U2 conjuncts (target 68) ---

    [Fact]
    public async Task ElReintentoSobreUnComprobanteYaTerminalNoLoTocaINunca()
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe();
        var (ctx, admin, _, _) = await PrepararAsync(
            nameof(ElReintentoSobreUnComprobanteYaTerminalNoLoTocaINunca), espiaWsaa, espiaWsfe);

        var idComprobante = await SembrarPendienteAsync(ctx, 60);

        // Lo dejamos 'aprobado' directo (below-the-confound), como si otra transacción ya lo
        // hubiera resuelto — I3: un comprobante terminal nunca vuelve a FECAESolicitar.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE comprobantes_venta SET cae = '70000000000099', cae_vencimiento = '2026-06-01', resultado_fiscal = 'aprobado' WHERE id_comprobante_venta = {idComprobante}");
        }

        var respuesta = await admin.PostAsync($"/api/fiscal/comprobantes/{idComprobante}/reintentar", null);

        // ix_comprobantes_venta_fiscal_pendientes ya no lo indexa (dejó de ser 'pendiente') — el
        // servicio no lo encuentra: 404, CERO llamadas WSFE.
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Empty(espiaWsfe.Operaciones);

        await using var verificacion = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await verificacion.ComprobantesVenta.AsNoTracking().FirstAsync(c => c.Id == idComprobante);
        Assert.Equal("70000000000099", actual.Cae); // intocado
    }

    // --- Reasersión del guard del POS (target 73) + autorización de la emisión (task 5.24) ---

    [Fact]
    public async Task UnaVentaConTipoFiscalSigueRechazadaConCuatrocientosEnElPos()
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe();
        var (ctx, admin, _, _) = await PrepararAsync(
            nameof(UnaVentaConTipoFiscalSigueRechazadaConCuatrocientosEnElPos), espiaWsaa, espiaWsfe);

        var respuesta = await admin.PostAsJsonAsync("/api/ventas", new
        {
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdCliente = (int?)null,
            CodigoTipoComprobante = "FA",
            IdComprobanteAsociado = (int?)null,
            // ExigirLineasValidas corre ANTES que ResolverTipoComprobanteAsync en
            // ServicioDeVentas.EmitirAsync — una línea estructuralmente válida (el artículo no
            // necesita existir: el rechazo del tipo llega antes de resolverlo) alcanza para
            // ejercer el camino real hasta el guard del POS.
            Lineas = new[] { new { IdArticulo = 1, Cantidad = 1m, CodigoBarra = (string?)null } },
            Pagos = Array.Empty<object>()
        });

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "tipo_comprobante_invalido");
    }

    [Fact]
    public async Task UnVendedorPuedeIntentarLaEmisionFiscalYUnRootEsRechazadoPorAutorizacion()
    {
        var espiaWsaa = new EspiaWsaa(LoginCmsGolden());
        var espiaWsfe = new EspiaWsfe { Solicitar = _ => RespuestaXml(FecaeAprobado(1)) };
        var (ctx, admin, vendedor, root) = await PrepararAsync(
            nameof(UnVendedorPuedeIntentarLaEmisionFiscalYUnRootEsRechazadoPorAutorizacion), espiaWsaa, espiaWsfe);

        // Vendedor: 200 — el riesgo gateado es el certificado/la letra/el CAE, no quién aprieta el botón.
        var comoVendedor = await vendedor.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteRi));
        Assert.Equal(HttpStatusCode.Created, comoVendedor.StatusCode);

        // Root (staff de plataforma, sin tenant): 403 — OperacionDePos no admite plataforma.
        var comoRoot = await root.PostAsJsonAsync("/api/fiscal/comprobantes", SolicitudEmision(ctx, ctx.IdClienteRi));
        Assert.Equal(HttpStatusCode.Forbidden, comoRoot.StatusCode);
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(esperado == respuesta.StatusCode, $"Esperado {esperado}, fue {respuesta.StatusCode}. Cuerpo: {cuerpo}");
        using var documento = JsonDocument.Parse(cuerpo);
        Assert.Equal(codigo, documento.RootElement.GetProperty("codigo").GetString());
    }
}
