using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Ways.Api.ConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>Pruebas puras de <see cref="DiagnosticoDelConector"/> sobre un <see cref="DefaultHttpContext"/>:
/// qué deja pasar la línea de lo que manda el cliente y qué cuerpos lee, sin levantar la app ni Docker.</summary>
public class ConectorMcpDiagnosticoTests
{
    /// <summary>Saltos de línea (CR, LF, U+2028, U+2029), un escape ANSI, un tab y un override
    /// bidireccional (carácter de formato).</summary>
    private static readonly string Hostil =
        "a" + (char)13 + (char)10 + "Conector MCP: forjada" + (char)0x2028 + (char)0x2029 + (char)0x1B + "[31m" + (char)9 +
        (char)0x202E + "z";

    /// <summary><see cref="Hostil"/> con cada carácter de control, de formato o separador cambiado por '?'.</summary>
    private const string HostilLimpio = "a??Conector MCP: forjada???[31m??z";

    private static readonly string ColaLarga = new('x', 5_000);

    private static async Task<string> LineaAsync(HttpContext http, Func<HttpContext, Task>? endpoint = null)
    {
        var captura = new CapturaDeLogs();

        await DiagnosticoDelConector.RegistrarAsync(
            http, h => endpoint?.Invoke(h) ?? Task.CompletedTask, captura.CreateLogger("prueba"));

        return Assert.Single(captura.Entradas).Mensaje;
    }

    private static DefaultHttpContext Post(string ruta, string contentType, byte[] cuerpo, bool conContentLength = true)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = ruta;
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(cuerpo);
        http.Request.ContentLength = conContentLength ? cuerpo.Length : null;
        return http;
    }

    private static byte[] JsonRpc(object mensajes) => JsonSerializer.SerializeToUtf8Bytes(mensajes);

    private static async Task<string> LeerCuerpoAsync(HttpContext http)
    {
        using var lector = new StreamReader(http.Request.Body, leaveOpen: true);
        return await lector.ReadToEndAsync();
    }

    /// <summary>Un mensaje JSON-RPC de exactamente <paramref name="largo"/> bytes.</summary>
    private static byte[] JsonRpcDeLargo(int largo)
    {
        const string Inicio = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"relleno":[""";
        const string Fin = "]}}";

        return Encoding.ASCII.GetBytes(Inicio + new string(' ', largo - Inicio.Length - Fin.Length) + Fin);
    }

    /// <summary>Un campo distinto por caso, cada uno con <see cref="Hostil"/> seguido de 5.000
    /// caracteres. En la línea no queda ningún carácter de control, de formato ni separador de línea o
    /// de párrafo, y el valor sale recortado a <see cref="DiagnosticoDelConector.LargoMaximoDeValor"/>
    /// caracteres con "…" al final.</summary>
    [Theory]
    [InlineData("metodo")]
    [InlineData("ruta")]
    [InlineData("MCP-Protocol-Version")]
    [InlineData("Mcp-Method")]
    [InlineData("User-Agent")]
    [InlineData("OAuth")]
    [InlineData("JSON-RPC")]
    public async Task CadaValorQueMandaElClienteSaleRecortadoYSinCaracteresDeControl(string campo)
    {
        var prefijo = campo == "ruta" ? "/.well-known/" : string.Empty;
        var valor = prefijo + Hostil + ColaLarga;
        var http = campo == "JSON-RPC"
            ? Post(ConstantesDeMcp.RutaMcp, "application/json", JsonRpc(new { jsonrpc = "2.0", id = 1, method = valor }))
            : new DefaultHttpContext();

        switch (campo)
        {
            case "metodo":
                http.Request.Method = valor;
                http.Request.Path = "/.well-known/openid-configuration";
                break;
            case "ruta":
                http.Request.Path = valor;
                break;
            case "OAuth":
                http.Request.Path = ConstantesDeMcp.RutaDeAutorizacion;
                http.Request.QueryString = QueryString.Create("client_id", valor);
                break;
            case "JSON-RPC":
                break;
            default:
                http.Request.Path = "/.well-known/openid-configuration";
                http.Request.Headers[campo] = valor;
                break;
        }

        var linea = await LineaAsync(http);

        Assert.DoesNotContain(linea, caracter => char.GetUnicodeCategory(caracter) is UnicodeCategory.Control
            or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
        Assert.Contains(
            (prefijo + HostilLimpio + ColaLarga)[..DiagnosticoDelConector.LargoMaximoDeValor] + "…", linea);
    }

    [Fact]
    public async Task UnLoteJsonRpcListaLosPrimerosMetodosYCuentaElResto()
    {
        var lote = Enumerable.Range(0, 25).Select(i => new { jsonrpc = "2.0", id = i, method = $"m{i}" });

        var linea = await LineaAsync(Post(ConstantesDeMcp.RutaMcp, "application/json", JsonRpc(lote)));

        Assert.Contains("| JSON-RPC=m0,m1,m2,m3,m4,m5,m6,m7,m8,m9,(+15 más) |", linea);
    }

    [Theory]
    [InlineData(65_536, "tools/list")]
    [InlineData(65_537, "(cuerpo no inspeccionado)")]
    public async Task UnCuerpoDeMcpSoloSeLeeHasta64KiBYLaRequestLoRecibeEntero(int largo, string metodos)
    {
        var cuerpo = JsonRpcDeLargo(largo);
        string? recibido = null;

        var linea = await LineaAsync(
            Post(ConstantesDeMcp.RutaMcp, "application/json", cuerpo), async h => recibido = await LeerCuerpoAsync(h));

        Assert.Contains($"| JSON-RPC={metodos} |", linea);
        Assert.Equal(Encoding.ASCII.GetString(cuerpo), recibido);
    }

    [Fact]
    public async Task UnCuerpoDeMcpSinContentLengthNoSeLeeYLaRequestLoRecibeEntero()
    {
        var cuerpo = JsonRpc(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        string? recibido = null;

        var linea = await LineaAsync(
            Post(ConstantesDeMcp.RutaMcp, "application/json", cuerpo, conContentLength: false),
            async h => recibido = await LeerCuerpoAsync(h));

        Assert.Contains("| JSON-RPC=(cuerpo no inspeccionado) |", linea);
        Assert.Equal(Encoding.UTF8.GetString(cuerpo), recibido);
    }

    [Fact]
    public async Task UnCuerpoDeMcpQueFallaAlLeerseSeInformaYLaRequestSigue()
    {
        var http = Post(ConstantesDeMcp.RutaMcp, "application/json", []);
        http.Request.Body = new CuerpoQueFalla();
        http.Request.ContentLength = 100;
        var siguio = false;

        var linea = await LineaAsync(http, _ =>
        {
            siguio = true;
            return Task.CompletedTask;
        });

        Assert.True(siguio);
        Assert.Contains("| JSON-RPC=(cuerpo no inspeccionado) |", linea);
    }

    [Theory]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/token")]
    public async Task ElFormularioDeUnEndpointOAuthSeDescribeYElEndpointLoRecibeIgual(string ruta)
    {
        var http = Post(ruta, "application/x-www-form-urlencoded", "grant_type=authorization_code&code=secreto"u8.ToArray());
        string? codigoRecibido = null;

        var linea = await LineaAsync(http, async h => codigoRecibido = (await h.Request.ReadFormAsync())["code"].ToString());

        Assert.Contains("| OAuth=grant_type=\"authorization_code\" ", linea);
        Assert.Contains(" code=true ", linea);
        Assert.DoesNotContain("secreto", linea);
        Assert.Equal("secreto", codigoRecibido);
    }

    /// <summary>La línea marca el formulario como no inspeccionado y describe igual la query.</summary>
    [Fact]
    public async Task UnFormularioSinContentLengthNoSeLeeYElEndpointLoRecibeEntero()
    {
        var http = Post(
            ConstantesDeMcp.RutaDeToken, "application/x-www-form-urlencoded", "grant_type=authorization_code"u8.ToArray(),
            conContentLength: false);
        http.Request.QueryString = QueryString.Create("client_id", "claude-ways");
        string? recibido = null;

        var linea = await LineaAsync(http, async h => recibido = (await h.Request.ReadFormAsync())["grant_type"].ToString());

        Assert.Contains("| OAuth=(formulario no inspeccionado) client_id=\"claude-ways\" ", linea);
        Assert.Equal("authorization_code", recibido);
    }

    /// <summary>Un GET a /connect/authorize trae la solicitud en la query: aunque declare un cuerpo de
    /// formulario, la línea no lo lee (le llega entero al endpoint), lo marca como no inspeccionado y
    /// describe la query.</summary>
    [Fact]
    public async Task ElFormularioDeUnGetAConnectAuthorizeNoSeLeeYLaLineaDescribeLaQuery()
    {
        const string Cuerpo = "grant_type=authorization_code";
        var http = Post(ConstantesDeMcp.RutaDeAutorizacion, "application/x-www-form-urlencoded", Encoding.ASCII.GetBytes(Cuerpo));
        http.Request.Method = HttpMethods.Get;
        http.Request.QueryString = QueryString.Create("client_id", "claude-ways");
        string? recibido = null;

        var linea = await LineaAsync(http, async h => recibido = await LeerCuerpoAsync(h));

        Assert.Contains("| OAuth=(formulario no inspeccionado) client_id=\"claude-ways\" ", linea);
        Assert.DoesNotContain("authorization_code", linea);
        Assert.Equal(Cuerpo, recibido);
    }

    /// <summary>Una clave más larga que el límite de <c>FormOptions</c> hace fallar la lectura: la línea lo
    /// informa, la request sigue y el endpoint que lee el formulario recibe la misma excepción.</summary>
    [Fact]
    public async Task UnFormularioQueElFrameworkRechazaSeInformaYElEndpointRecibeElMismoRechazo()
    {
        var http = Post(
            ConstantesDeMcp.RutaDeToken, "application/x-www-form-urlencoded", Encoding.ASCII.GetBytes($"{new string('k', 3_000)}=v"));
        Exception? delEndpoint = null;

        var linea = await LineaAsync(http, async h => delEndpoint = await Record.ExceptionAsync(() => h.Request.ReadFormAsync()));

        Assert.Contains("| OAuth=(formulario no inspeccionado) ", linea);
        Assert.IsType<InvalidDataException>(delEndpoint);
    }

    /// <summary>De las rutas que no son endpoints del conector (las rutas OAuth por defecto, otras de
    /// <c>/connect</c> y debajo de <c>/mcp</c>) la línea registra el pedido, pero el cuerpo no se lee: le
    /// llega entero a quien atienda la request.</summary>
    [Theory]
    [InlineData("/authorize", "application/x-www-form-urlencoded", "grant_type=authorization_code")]
    [InlineData("/token", "application/x-www-form-urlencoded", "grant_type=authorization_code")]
    [InlineData("/register", "application/x-www-form-urlencoded", "grant_type=authorization_code")]
    [InlineData("/connect/otra", "application/x-www-form-urlencoded", "grant_type=authorization_code")]
    [InlineData("/mcp/otra", "application/json", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")]
    public async Task ElCuerpoDeUnaRutaQueNoEsEndpointNoSeLee(string ruta, string contentType, string cuerpo)
    {
        string? recibido = null;

        var linea = await LineaAsync(
            Post(ruta, contentType, Encoding.UTF8.GetBytes(cuerpo)), async h => recibido = await LeerCuerpoAsync(h));

        Assert.StartsWith($"Conector MCP: POST {ruta} -> ", linea);
        Assert.DoesNotContain("authorization_code", linea);
        Assert.DoesNotContain("tools/list", linea);
        Assert.Equal(cuerpo, recibido);
    }

    private sealed class CuerpoQueFalla : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("La conexión se cortó.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("La conexión se cortó.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("La conexión se cortó.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
