using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;
using Ways.Infrastructure.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

public class ClienteApiMercadoPagoTests
{
    private const string Token = "APP_USR-7654321-token-del-vendedor-que-no-debe-salir";
    private const int IdEmpresa = 7;
    private static readonly DateTimeOffset Expira = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManejadorFalso(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Pedido, string Cuerpo)> Pedidos { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct)
        {
            var cuerpo = pedido.Content is null ? string.Empty : await pedido.Content.ReadAsStringAsync(ct);
            Pedidos.Add((pedido, cuerpo));
            return responder(pedido, cuerpo);
        }
    }

    private sealed class ProveedorFalso : IProveedorDeTokenMercadoPago
    {
        public Exception? Falla { get; set; }

        public Exception? FallaAlMarcar { get; set; }

        public List<TokenDelVendedorMercadoPago> Rechazados { get; } = [];

        public Task<TokenDelVendedorMercadoPago> ObtenerAccessTokenAsync(int idEmpresa, CancellationToken ct = default) =>
            Falla is null
                ? Task.FromResult(new TokenDelVendedorMercadoPago(11, 987654321, Token, Expira))
                : Task.FromException<TokenDelVendedorMercadoPago>(Falla);

        public Task<bool> MarcarTokenRechazadoAsync(TokenDelVendedorMercadoPago token, CancellationToken ct = default)
        {
            Rechazados.Add(token);
            return FallaAlMarcar is null ? Task.FromResult(true) : Task.FromException<bool>(FallaAlMarcar);
        }
    }

    /// <summary>Flujo que sirve <paramref name="total"/> bytes en tandas; con <paramref name="falla"/> se corta apenas arranca la
    /// lectura y con <paramref name="colgar"/> no responde nunca.</summary>
    private sealed class FlujoContado(int total, Exception? falla = null, bool colgar = false) : Stream
    {
        public int Servidos { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Leer(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (colgar)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return falla is null ? Leer(buffer.Span) : throw falla;
        }

        private int Leer(Span<byte> destino)
        {
            var n = Math.Min(Math.Min(destino.Length, 4096), total - Servidos);
            destino[..n].Fill((byte)'x');
            Servidos += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage ConFlujo(HttpStatusCode estado, Stream flujo) =>
        new(estado) { Content = new StreamContent(flujo) };

    private sealed class RegistroDeLogs : ILogger<ClienteApiMercadoPago>
    {
        public List<string> Mensajes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Mensajes.Add(formatter(state, exception) + exception);
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (ClienteApiMercadoPago Cliente, ManejadorFalso Manejador, ProveedorFalso Proveedor, RegistroDeLogs Logs) Crear(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder, TimeSpan? plazoDeLectura = null)
    {
        var manejador = new ManejadorFalso(responder);
        var proveedor = new ProveedorFalso();
        var logs = new RegistroDeLogs();
        var http = new HttpClient(manejador) { BaseAddress = new Uri("https://api.mp.test/") };
        var cliente = new ClienteApiMercadoPago(http, proveedor, logs)
        {
            PlazoDeLecturaDelCuerpo = plazoDeLectura ?? TimeSpan.FromSeconds(15)
        };
        return (cliente, manejador, proveedor, logs);
    }

    private sealed record Eco(long Id, string? NombreCompleto);

    private sealed record CuerpoDePrueba(string NombreCompleto, string? Nulo, int NumeroDeCalle);

    // ---- Pedido -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ElPedidoLlevaElBearerDelVendedorLaClaveDeIdempotenciaYUnCuerpoJsonEnSnakeCaseSinNulos()
    {
        var (cliente, manejador, _, _) = Crear((_, _) => Json(HttpStatusCode.OK, """{"id":5,"nombre_completo":"Caja 1"}"""));

        var eco = await cliente.EnviarAsync<Eco>(
            IdEmpresa, HttpMethod.Post, "/v2/pos", new CuerpoDePrueba("Caja 1", null, 12), "clave-1");

        Assert.Equal(new Eco(5, "Caja 1"), eco);
        var (pedido, cuerpo) = Assert.Single(manejador.Pedidos);
        Assert.Equal(HttpMethod.Post, pedido.Method);
        Assert.Equal("https://api.mp.test/v2/pos", pedido.RequestUri!.ToString());
        Assert.Equal("Bearer", pedido.Headers.Authorization!.Scheme);
        Assert.Equal(Token, pedido.Headers.Authorization.Parameter);
        Assert.Equal(["clave-1"], pedido.Headers.GetValues(ClienteApiMercadoPago.EncabezadoDeIdempotencia));
        Assert.Equal("""{"nombre_completo":"Caja 1","numero_de_calle":12}""", cuerpo);
        Assert.Equal("application/json", pedido.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SinClaveNiCuerpoElPedidoNoLlevaEncabezadoDeIdempotenciaNiContenido()
    {
        var (cliente, manejador, _, _) = Crear((_, _) => Json(HttpStatusCode.OK, """{"id":5}"""));

        await cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos");

        var (pedido, cuerpo) = Assert.Single(manejador.Pedidos);
        Assert.False(pedido.Headers.Contains(ClienteApiMercadoPago.EncabezadoDeIdempotencia));
        Assert.Null(pedido.Content);
        Assert.Equal(string.Empty, cuerpo);
    }

    [Fact]
    public async Task SinTokenNoSeLlamaAMercadoPagoYSubeElErrorDelProveedor()
    {
        var (cliente, manejador, proveedor, _) = Crear((_, _) => Json(HttpStatusCode.OK, "{}"));
        proveedor.Falla = new ErrorDominio(CodigosDeErrorDeMercadoPago.NoConectada, "sin conexión", 409);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoConectada, error.Codigo);
        Assert.Empty(manejador.Pedidos);
    }

    // ---- Respuesta 2xx ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("esto no es json")]
    [InlineData("<html>gateway</html>")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public async Task UnDosXxQueNoEsElJsonEsperadoEsUnErrorTipadoYNoUnaExcepcionDelSerializador(string cuerpo)
    {
        var (cliente, _, proveedor, logs) = Crear((_, _) => Json(HttpStatusCode.OK, cuerpo));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.RespuestaInvalida, error.Codigo);
        Assert.Empty(proveedor.Rechazados);
        Assert.DoesNotContain(logs.Mensajes, m => cuerpo.Length > 3 && m.Contains(cuerpo));
    }

    [Fact]
    public async Task UnDosXxConUnTipoQueElSerializadorNoSoportaTambienEsUnErrorTipado()
    {
        var (cliente, _, _, _) = Crear((_, _) => Json(HttpStatusCode.OK, """{"id":5}"""));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => cliente.EnviarAsync<Action>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.RespuestaInvalida, error.Codigo);
    }

    // ---- 401 --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnRechazo401MarcaElTokenUsadoComoRechazadoYPideReconectar()
    {
        var (cliente, _, proveedor, _) = Crear((_, _) => Json(HttpStatusCode.Unauthorized, """{"message":"invalid access token","status":401}"""));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.RequiereReconexion, error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
        var rechazado = Assert.Single(proveedor.Rechazados);
        Assert.Equal(11, rechazado.IdConexion);
        Assert.Equal(Expira, rechazado.ExpiraEn);
    }

    // ---- Otros 4xx --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(400, """{"message":"Invalid state_name","error":"bad_request","status":400,"cause":[]}""", "bad_request", "Invalid state_name")]
    [InlineData(409, """{"errors":[{"code":"pos_already_exists","detail":"external_id ya existe"}]}""", "pos_already_exists", "external_id ya existe")]
    [InlineData(404, """{"message":"store not found","cause":[{"code":"1234","description":"sin tienda"}]}""", "1234", "store not found")]
    [InlineData(403, """{"code":"forbidden","message":"sin permiso"}""", "forbidden", "sin permiso")]
    [InlineData(422, "esto no es json", null, null)]
    [InlineData(400, "[1]", null, null)]
    public async Task UnCuatroXxDistintoDe401YDeReintentoEsUnErrorTipadoConElEstadoElCodigoYElMensajeDeMercadoPago(
        int estado, string cuerpo, string? codigo, string? mensaje)
    {
        var (cliente, _, proveedor, _) = Crear((_, _) => Json((HttpStatusCode)estado, cuerpo));

        var error = await Assert.ThrowsAsync<ErrorDeApiMercadoPago>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.SolicitudRechazada, error.Codigo);
        Assert.Equal(estado, error.EstadoMercadoPago);
        Assert.Equal(codigo, error.CodigoMercadoPago);
        Assert.Equal(mensaje, error.MensajeMercadoPago);
        Assert.Empty(proveedor.Rechazados);
    }

    [Fact]
    public async Task ElMensajeDeMercadoPagoSeSaneaSinTokenSinControlesYAcotado()
    {
        var cuerpo = JsonSerializer.Serialize(new
        {
            error = "bad_request",
            message = $"el token {Token} es\ninválido " + new string('x', 500)
        });
        var (cliente, _, _, _) = Crear((_, _) => Json(HttpStatusCode.BadRequest, cuerpo));

        var error = await Assert.ThrowsAsync<ErrorDeApiMercadoPago>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos"));

        Assert.DoesNotContain(Token, error.MensajeMercadoPago);
        Assert.DoesNotContain(Token, error.Message);
        Assert.DoesNotContain('\n', error.MensajeMercadoPago!);
        Assert.StartsWith("el token *** esinválido", error.MensajeMercadoPago);
        Assert.Equal(200, error.MensajeMercadoPago!.Length);
    }

    [Fact]
    public async Task UnErrorDeLecturaDelCuerpoDeUnCuatroXxPorTiempoAgotadoEsNoDisponible()
    {
        var (cliente, _, proveedor, logs) = Crear(
            (_, _) => ConFlujo(HttpStatusCode.BadRequest, new FlujoContado(10, new TaskCanceledException("timeout"))));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
        Assert.Equal(503, error.EstadoHttp);
        Assert.Empty(proveedor.Rechazados);
        Assert.All(logs.Mensajes, m => Assert.DoesNotContain(Token, m));
    }

    [Fact]
    public async Task UnCuerpoDeUnCuatroXxQueNuncaTerminaDeLlegarSeCortaPorElPlazoYEsNoDisponible()
    {
        var (cliente, _, _, _) = Crear(
            (_, _) => ConFlujo(HttpStatusCode.BadRequest, new FlujoContado(10, colgar: true)), TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    public async Task UnCuerpoDeUnCuatroXxCortadoPorLaRedEsNoDisponible(Type tipo)
    {
        var (cliente, _, _, _) = Crear(
            (_, _) => ConFlujo(HttpStatusCode.Conflict, new FlujoContado(10, (Exception)Activator.CreateInstance(tipo, "sin red")!)));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
        Assert.IsNotType<ErrorDeApiMercadoPago>(error);
    }

    [Fact]
    public async Task UnaCancelacionDelLlamadorLeyendoElCuerpoDeUnCuatroXxSigueSiendoCancelacion()
    {
        using var cancelado = new CancellationTokenSource();
        await cancelado.CancelAsync();
        var (cliente, _, _, _) = Crear(
            (_, _) => ConFlujo(HttpStatusCode.BadRequest, new FlujoContado(10, new TaskCanceledException("cancelado"))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos", ct: cancelado.Token));
    }

    [Fact]
    public async Task ElCuerpoDeUnErrorSeInterpretaAcotadoYUnoMasLargoQueElTopeQuedaSinCodigoNiMensaje()
    {
        var relleno = new string(' ', 4 * ClienteApiMercadoPago.LargoMaximoDelCuerpoDeError);
        var cuerpo = "{\"error\":\"bad_request\",\"message\":\"corto\"," + relleno + "\"status\":400}";
        var (cliente, _, _, _) = Crear((_, _) => Json(HttpStatusCode.BadRequest, cuerpo));

        var error = await Assert.ThrowsAsync<ErrorDeApiMercadoPago>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos"));

        Assert.Equal(400, error.EstadoMercadoPago);
        Assert.Null(error.CodigoMercadoPago);
        Assert.Null(error.MensajeMercadoPago);
    }

    [Fact]
    public async Task UnCuerpoDeErrorQueEntraEnElTopeSeInterpretaCompleto()
    {
        var relleno = new string(' ', ClienteApiMercadoPago.LargoMaximoDelCuerpoDeError / 2);
        var cuerpo = "{\"error\":\"bad_request\"," + relleno + "\"message\":\"corto\"}";
        var (cliente, _, _, _) = Crear((_, _) => Json(HttpStatusCode.BadRequest, cuerpo));

        var error = await Assert.ThrowsAsync<ErrorDeApiMercadoPago>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos"));

        Assert.Equal("bad_request", error.CodigoMercadoPago);
        Assert.Equal("corto", error.MensajeMercadoPago);
    }

    [Fact]
    public async Task SiLaMarcaDelRechazoFallaElLlamadorIgualRecibeRequiereReconexionYElTokenNoSale()
    {
        var (cliente, _, proveedor, logs) = Crear((_, _) => Json(HttpStatusCode.Unauthorized, """{"message":"invalid token"}"""));
        proveedor.FallaAlMarcar = new InvalidOperationException("la base no responde");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.RequiereReconexion, error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
        Assert.Single(proveedor.Rechazados);
        Assert.Contains(logs.Mensajes, m => m.Contains("la base no responde"));
        Assert.All(logs.Mensajes, m => Assert.DoesNotContain(Token, m));
    }

    // ---- Reintentables ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task UnEstadoReintentableEsNoDisponibleYNoTocaLaConexion(int estado)
    {
        var (cliente, _, proveedor, _) = Crear((_, _) => Json((HttpStatusCode)estado, """{"message":"x"}"""));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
        Assert.IsNotType<ErrorDeApiMercadoPago>(error);
        Assert.Empty(proveedor.Rechazados);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    public async Task UnDosXxCortadoALaMitadDeLaLecturaEsNoDisponible(Type tipo)
    {
        var (cliente, _, _, _) = Crear(
            (_, _) => ConFlujo(HttpStatusCode.OK, new FlujoContado(10, (Exception)Activator.CreateInstance(tipo, "sin red")!)));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
    }

    [Fact]
    public async Task UnaFallaDeRedEsNoDisponible()
    {
        var (cliente, _, _, _) = Crear((_, _) => throw new HttpRequestException("sin red"));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
    }

    [Fact]
    public async Task UnTiempoAgotadoEsNoDisponibleYUnaCancelacionDelLlamadorSigueSiendoCancelacion()
    {
        var (cliente, _, _, _) = Crear((_, _) => throw new TaskCanceledException("timeout"));
        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));
        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);

        using var cancelado = new CancellationTokenSource();
        await cancelado.CancelAsync();
        var (otro, _, _, _) = Crear((_, _) => throw new TaskCanceledException("cancelado"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => otro.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos", ct: cancelado.Token));
    }

    // ---- Logs -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(200, "esto no es json")]
    [InlineData(400, """{"message":"x"}""")]
    [InlineData(401, """{"message":"x"}""")]
    [InlineData(429, """{"message":"x"}""")]
    [InlineData(503, """{"message":"x"}""")]
    public async Task ElTokenNuncaApareceEnLosLogsNiEnElMensajeDeLasExcepciones(int estado, string cuerpo)
    {
        var (cliente, _, _, logs) = Crear((_, _) => Json((HttpStatusCode)estado, cuerpo));

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Post, "v2/pos?clave=1", new CuerpoDePrueba("a", null, 1), "k"));

        Assert.NotEmpty(logs.Mensajes);
        Assert.All(logs.Mensajes, m => Assert.DoesNotContain(Token, m));
        Assert.DoesNotContain(Token, error.ToString());
        Assert.All(logs.Mensajes, m => Assert.DoesNotContain("clave=1", m));
    }

    [Fact]
    public async Task UnaFallaDeRedNoVuelcaElTokenEnLosLogs()
    {
        var (cliente, _, _, logs) = Crear((_, _) => throw new HttpRequestException("sin red"));

        await Assert.ThrowsAsync<ErrorDominio>(() => cliente.EnviarAsync<Eco>(IdEmpresa, HttpMethod.Get, "v2/pos"));

        Assert.NotEmpty(logs.Mensajes);
        Assert.All(logs.Mensajes, m => Assert.DoesNotContain(Token, m));
    }

    // ---- Terminales -------------------------------------------------------------------------------------------------

    private const string Terminales =
        """
        {"data":{"terminals":[
          {"id":"NEWLAND_N950__N950NCB123","pos_id":47792476,"store_id":"59554","external_pos_id":"SUC0101POS","operating_mode":"PDV"},
          {"id":"PAX_A910__8701","pos_id":1,"store_id":2,"operating_mode":"STANDALONE"},
          {"pos_id":3}
        ]},"paging":{"total":3,"offset":0,"limit":50}}
        """;

    [Fact]
    public async Task ListarTerminalesLlamaAlListadoConLosFiltrosYMapeaLosCamposYLaPaginacion()
    {
        var (cliente, manejador, _, _) = Crear((_, _) => Json(HttpStatusCode.OK, Terminales));

        var listado = await cliente.ListarTerminalesAsync(IdEmpresa, idStore: 59554, idPos: 47792476);

        var (pedido, _) = Assert.Single(manejador.Pedidos);
        Assert.Equal(HttpMethod.Get, pedido.Method);
        Assert.Equal("https://api.mp.test/terminals/v1/list?store_id=59554&pos_id=47792476", pedido.RequestUri!.ToString());
        Assert.Equal(
            [
                new TerminalMercadoPago("NEWLAND_N950__N950NCB123", 47792476, 59554, "SUC0101POS", "PDV"),
                new TerminalMercadoPago("PAX_A910__8701", 1, 2, null, "STANDALONE")
            ],
            listado.Terminales);
        Assert.Equal((3, 0, 50), (listado.Total, listado.Offset, listado.Limite));
    }

    [Fact]
    public async Task ListarTerminalesSinFiltrosNoLlevaConsultaYUnaRespuestaSinDatosEsUnListadoVacio()
    {
        var (cliente, manejador, _, _) = Crear((_, _) => Json(HttpStatusCode.OK, "{}"));

        var listado = await cliente.ListarTerminalesAsync(IdEmpresa);

        Assert.Equal("https://api.mp.test/terminals/v1/list", Assert.Single(manejador.Pedidos).Pedido.RequestUri!.ToString());
        Assert.Empty(listado.Terminales);
        Assert.Equal(0, listado.Total);
    }
}
