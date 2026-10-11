using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Ways.Api.Spa;

namespace Ways.IntegrationTests;

/// <summary>
/// Lo que sirve la API del front: cada prefijo con su entrada, la raíz de la plataforma que va a
/// <c>/admin</c>, la landing en la raíz de un comercio y las redirecciones de las rutas viejas. Cada entrada
/// escribe en su <c>wwwroot</c> temporal un HTML con un texto distinto, así que un 200 dice cuál se sirvió.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RutasDeLaSpaEnElServidorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Landing = "<!doctype html><title>landing</title>";
    private const string Admin = "<!doctype html><title>admin</title>";
    private const string Online = "<!doctype html><title>online</title>";
    private const string Pos = "<!doctype html><title>pos</title>";

    private readonly ApoyoDeDominios apoyo = new(fixture);

    public static TheoryData<string> SegmentosHeredados()
    {
        var datos = new TheoryData<string>();
        foreach (var segmento in RutasDeLaSpa.RutasHeredadasDelBackOffice)
        {
            datos.Add(segmento);
        }

        return datos;
    }

    private static HttpClient ClienteEn(WebApplicationFactory<Program> host, string? hostDeLaRequest = null) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri($"http://{hostDeLaRequest ?? "localhost"}")
        });

    private async Task ConElFrontAsync(Func<WebApplicationFactory<Program>, Task> prueba)
    {
        var raiz = Directory.CreateTempSubdirectory("ways-wwwroot-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(raiz.FullName, "index.html"), Landing);
            await File.WriteAllTextAsync(Path.Combine(raiz.FullName, "admin.html"), Admin);
            await File.WriteAllTextAsync(Path.Combine(raiz.FullName, "online.html"), Online);
            await File.WriteAllTextAsync(Path.Combine(raiz.FullName, "pos.html"), Pos);

            await using var host = fixture.WithWebHostBuilder(builder => builder.UseWebRoot(raiz.FullName));

            await prueba(host);
        }
        finally
        {
            raiz.Delete(recursive: true);
        }
    }

    private static async Task AssertHtmlAsync(HttpResponseMessage respuesta, string esperado)
    {
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("text/html", respuesta.Content.Headers.ContentType?.MediaType);
        Assert.Equal(esperado, await respuesta.Content.ReadAsStringAsync());
    }

    private static void AssertRedireccion(HttpResponseMessage respuesta, string destino)
    {
        Assert.Equal(HttpStatusCode.Found, respuesta.StatusCode);
        Assert.Equal(destino, respuesta.Headers.Location?.OriginalString);
    }

    // ---- el fallback por prefijo -----------------------------------------------------------------

    [Theory]
    [InlineData("/admin", Admin)]
    [InlineData("/admin/", Admin)]
    [InlineData("/admin/login", Admin)]
    [InlineData("/admin/articulos/edit/3", Admin)]
    [InlineData("/online", Online)]
    [InlineData("/online/producto/3", Online)]
    [InlineData("/una-pagina-del-comercio", Landing)]
    [InlineData("/administrador", Landing)]
    public Task CadaPrefijoSirveSuEntrada(string ruta, string esperado) =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            await AssertHtmlAsync(await cliente.GetAsync(ruta), esperado);
        });

    [Fact]
    public Task PosHtmlSigueSiendoUnArchivoEstatico() =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            await AssertHtmlAsync(await cliente.GetAsync("/pos.html"), Pos);
        });

    [Theory]
    [InlineData("/api/no-existe")]
    [InlineData("/api/pos")]
    [InlineData("/api/login")]
    public Task UnaRutaApiInexistenteDa404AunqueSuNombreSeaUnaRutaHeredada(string ruta) =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(ruta)).StatusCode);
        });

    // ---- la raíz ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/", "/admin")]
    [InlineData("HEAD", "/", "/admin")]
    [InlineData("GET", "/?desde=correo", "/admin?desde=correo")]
    public Task LaRaizDeLaPlataformaVaAAdmin(string metodo, string ruta, string destino) =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);
            using var solicitud = new HttpRequestMessage(new HttpMethod(metodo), ruta);

            AssertRedireccion(await cliente.SendAsync(solicitud), destino);
        });

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("tauri.localhost")]
    public Task LaRaizVaAAdminEnCadaHostDeLaPlataforma(string hostDeLaPlataforma) =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host, hostDeLaPlataforma);

            AssertRedireccion(await cliente.GetAsync("/"), "/admin");
        });

    [Fact]
    public Task LaRaizDeUnComercioSirveLaLanding() =>
        ConElFrontAsync(async host =>
        {
            var tenant = await apoyo.SembrarTenantAsync("landing");
            var dominio = ApoyoDeDominios.HostUnico("landing");
            await apoyo.SembrarDominioAsync(tenant.IdTenant, dominio);
            using var cliente = ClienteEn(host, dominio);

            await AssertHtmlAsync(await cliente.GetAsync("/"), Landing);
            await AssertHtmlAsync(await cliente.GetAsync("/admin/login"), Admin);
            await AssertHtmlAsync(await cliente.GetAsync("/online/algo"), Online);
        });

    [Fact]
    public Task UnPostALaRaizNoSeRedirige() =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            var respuesta = await cliente.PostAsync("/", new StringContent("{}"));

            Assert.NotEqual(HttpStatusCode.Found, respuesta.StatusCode);
        });

    // ---- las rutas viejas -------------------------------------------------------------------------

    /// <summary>Una por cada segmento de la lista: borrar uno de la lista, o acotar la comparación a algunos,
    /// deja de redirigir justo esa pantalla.</summary>
    [Theory]
    [MemberData(nameof(SegmentosHeredados))]
    public Task CadaRutaHeredadaRedirigeABajoAdminConSuQuery(string segmento) =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            AssertRedireccion(await cliente.GetAsync($"/{segmento}?idTurno=7&x=a%20b"), $"/admin/{segmento}?idTurno=7&x=a%20b");
            AssertRedireccion(await cliente.GetAsync($"/{segmento}/detalle/12"), $"/admin/{segmento}/detalle/12");
        });

    [Fact]
    public Task LasRutasViejasTambienRedirigenEnElDominioDeUnComercio() =>
        ConElFrontAsync(async host =>
        {
            var tenant = await apoyo.SembrarTenantAsync("viejas");
            var dominio = ApoyoDeDominios.HostUnico("viejas");
            await apoyo.SembrarDominioAsync(tenant.IdTenant, dominio);
            using var cliente = ClienteEn(host, dominio);

            AssertRedireccion(await cliente.GetAsync("/login"), "/admin/login");
            AssertRedireccion(await cliente.GetAsync("/pos?idPresupuesto=5"), "/admin/pos?idPresupuesto=5");
        });

    [Fact]
    public Task LasRutasViejasSoloRedirigenParaGetYHead() =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            AssertRedireccion(await cliente.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/pos")), "/admin/pos");

            var post = await cliente.PostAsync("/login", new StringContent("{}"));
            Assert.NotEqual(HttpStatusCode.Found, post.StatusCode);
        });

    [Theory]
    [InlineData("/admin.html", "/admin")]
    [InlineData("/online.html", "/online")]
    [InlineData("/index.html", "/")]
    [InlineData("/Admin.html", "/admin")]
    [InlineData("/admin.html?idTurno=7", "/admin?idTurno=7")]
    public Task LosArchivosDeEntradaRedirigenASuPrefijo(string ruta, string destino) =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            AssertRedireccion(await cliente.GetAsync(ruta), destino);
            AssertRedireccion(await cliente.SendAsync(new HttpRequestMessage(HttpMethod.Head, ruta)), destino);
        });

    [Fact]
    public Task LosArchivosDeEntradaTambienRedirigenEnElDominioDeUnComercio() =>
        ConElFrontAsync(async host =>
        {
            var tenant = await apoyo.SembrarTenantAsync("entradas");
            var dominio = ApoyoDeDominios.HostUnico("entradas");
            await apoyo.SembrarDominioAsync(tenant.IdTenant, dominio);
            using var cliente = ClienteEn(host, dominio);

            AssertRedireccion(await cliente.GetAsync("/online.html"), "/online");
            AssertRedireccion(await cliente.GetAsync("/index.html"), "/");
        });

    [Fact]
    public Task UnPostAUnArchivoDeEntradaNoSeRedirige() =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            var respuesta = await cliente.PostAsync("/admin.html", new StringContent("{}"));

            Assert.NotEqual(HttpStatusCode.Found, respuesta.StatusCode);
        });

    [Theory]
    [InlineData("/admin/pos")]
    [InlineData("/admin/login")]
    [InlineData("/online/pos")]
    [InlineData("/assets/pos")]
    [InlineData("/pos.html")]
    [InlineData("/catalogo")]
    public Task LoQueNoEsUnaRutaViejaNoSeRedirige(string ruta) =>
        ConElFrontAsync(async host =>
        {
            using var cliente = ClienteEn(host);

            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(ruta)).StatusCode);
        });
}
