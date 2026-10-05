using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ways.Application.Usuarios;

namespace Ways.IntegrationTests;

/// <summary>
/// Lo que responde la API cuando el binding de un endpoint rechaza la solicitud antes de llegar al
/// servicio: un cuerpo JSON que no deserializa, vacío o <c>null</c>, un cuerpo cuyo <c>Content-Type</c> no es
/// JSON, un parámetro de query que no parsea.
/// La respuesta es un ProblemDetails con código estable y sin el mensaje del framework, que nombra tipos y
/// parámetros internos. La clasificación de cada excepción la prueban, brazo por brazo,
/// <see cref="ManejadorDeErroresBindingTests"/>; acá se prueba que el framework la tira de verdad.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SolicitudesMalFormadasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailRoot = "test@test.com";
    private const string PasswordRoot = "root";

    /// <summary>Fragmentos de los mensajes del framework y de la <see cref="JsonException"/> que causa el
    /// rechazo. Ninguno puede llegar al cliente.</summary>
    private static readonly string[] DetallesInternos =
        ["Failed to", "Required parameter", "SolicitudDeLogin", "pagina", "Path:", "LineNumber", "System.", "media type"];

    private static StringContent Cuerpo(string texto) => new(texto, Encoding.UTF8, "application/json");

    /// <summary><see cref="StringContent"/> siempre manda un <c>Content-Type</c>; este cuerpo manda el indicado o
    /// ninguno.</summary>
    private static ByteArrayContent CuerpoCon(string texto, string? contentType)
    {
        var cuerpo = new ByteArrayContent(Encoding.UTF8.GetBytes(texto));
        if (contentType is not null)
        {
            cuerpo.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        return cuerpo;
    }

    private static string LoginDeRoot() =>
        JsonSerializer.Serialize(new SolicitudDeLogin(MailRoot, PasswordRoot), JsonSerializerOptions.Web);

    private static async Task<string?> CodigoSinDetallesInternosAsync(HttpResponseMessage respuesta)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrEmpty(texto), $"La respuesta {(int)respuesta.StatusCode} no trae cuerpo.");

        foreach (var fragmento in DetallesInternos)
        {
            Assert.DoesNotContain(fragmento, texto);
        }

        var problema = JsonSerializer.Deserialize<JsonElement>(texto);
        Assert.False(problema.TryGetProperty("detail", out _), texto);

        return problema.GetProperty("codigo").GetString();
    }

    [Theory]
    [InlineData("{\"mail\": ")]
    [InlineData("{\"mail\": \"x@ways.test\", \"password\": \"x\", \"solicitarBearer\": \"quizas\"}")]
    public async Task UnCuerpoQueNoDeserializaDa400CuerpoInvalido(string cuerpo)
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsync("/api/auth/login", Cuerpo(cuerpo));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("cuerpo_invalido", await CodigoSinDetallesInternosAsync(respuesta));
    }

    /// <summary>Con <c>Content-Type</c> JSON y un cuerpo vacío o <c>null</c> no hay JSON que falle al
    /// deserializar: el framework informa el parámetro requerido ausente y la respuesta es
    /// <c>solicitud_invalida</c>, no <c>cuerpo_invalido</c>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task UnCuerpoJsonVacioONullDa400SolicitudInvalida(string cuerpo)
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsync("/api/auth/login", Cuerpo(cuerpo));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("solicitud_invalida", await CodigoSinDetallesInternosAsync(respuesta));
    }

    /// <summary>El cuerpo es un login de root válido; lo único que falla es el <c>Content-Type</c>. El endpoint
    /// tiene que llegar a seleccionarse para que el binding rechace el tipo con 415: si el ruteo lo descarta
    /// por tipo de contenido, la ruta de respaldo responde 404 vacío.</summary>
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData(null)]
    public async Task UnCuerpoSinContentTypeJsonDa415SolicitudInvalida(string? contentType)
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsync("/api/auth/login", CuerpoCon(LoginDeRoot(), contentType));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, respuesta.StatusCode);
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
        Assert.Equal("solicitud_invalida", await CodigoSinDetallesInternosAsync(respuesta));
    }

    /// <summary>La autorización va antes que el binding: sin sesión, una ruta que la pide responde 401 aunque
    /// el <c>Content-Type</c> no sea JSON.</summary>
    [Fact]
    public async Task SinSesionUnContentTypeQueNoEsJsonEnUnaRutaQuePideSesionDa401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsync("/api/usuarios", CuerpoCon("{}", "text/plain"));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    /// <summary>Sin cuerpo no hay tipo de contenido que rechazar: el framework informa el cuerpo requerido
    /// ausente, igual que con un <c>Content-Type</c> JSON y el cuerpo vacío.</summary>
    [Fact]
    public async Task UnaSolicitudSinCuerpoNiContentTypeDa400SolicitudInvalida()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsync("/api/auth/login", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("solicitud_invalida", await CodigoSinDetallesInternosAsync(respuesta));
    }

    [Fact]
    public async Task UnParametroDeQueryQueNoParseaDa400SolicitudInvalida()
    {
        using var root = fixture.CreateClient();
        var login = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await root.GetAsync("/api/usuarios?pagina=dos");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("solicitud_invalida", await CodigoSinDetallesInternosAsync(respuesta));
    }

    /// <summary>Prueba <c>ThrowOnBadRequest</c> en <c>Program.cs</c>. En Development el framework ya tira la
    /// excepción por default, así que las pruebas de arriba no ven esa línea; fuera de Development, sin ella,
    /// responde con el estado y el cuerpo vacío, y <c>ManejadorDeErrores</c> no llega a traducir nada. El 415
    /// va contra una ruta que pide sesión: con la sesión abierta, el tipo de contenido se rechaza igual.</summary>
    [Fact]
    public async Task FueraDeDevelopmentElRechazoDelBindingTambienTraeSuCodigo()
    {
        await using var produccion = fixture.WithWebHostBuilder(
            builder => builder.UseEnvironment(Environments.Production));
        Assert.True(produccion.Services.GetRequiredService<IHostEnvironment>().IsProduction());

        using var cliente = produccion.CreateClient();

        var cuerpo = await cliente.PostAsync("/api/auth/login", Cuerpo("{\"mail\": "));
        Assert.Equal(HttpStatusCode.BadRequest, cuerpo.StatusCode);
        Assert.Equal("cuerpo_invalido", await CodigoSinDetallesInternosAsync(cuerpo));

        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var query = await cliente.GetAsync("/api/usuarios?pagina=dos");
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal("solicitud_invalida", await CodigoSinDetallesInternosAsync(query));

        var tipo = await cliente.PostAsync("/api/usuarios", CuerpoCon("{}", "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, tipo.StatusCode);
        Assert.Equal("solicitud_invalida", await CodigoSinDetallesInternosAsync(tipo));
    }
}
