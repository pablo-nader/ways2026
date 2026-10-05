using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace Ways.IntegrationTests;

/// <summary>
/// La ruta de respaldo de <c>Program.cs</c>: una ruta <c>/api</c> que no existe responde 404 y cualquier otra
/// ruta sirve el <c>index.html</c> de la SPA. Es el borde del 415 que prueba
/// <see cref="SolicitudesMalFormadasTests"/>: el tipo de contenido solo se rechaza en un endpoint que existe.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RutaDeRespaldoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    [Theory]
    [InlineData("GET", null)]
    [InlineData("POST", "application/json")]
    [InlineData("POST", "text/plain")]
    public async Task UnaRutaApiInexistenteDa404(string metodo, string? contentType)
    {
        using var cliente = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(new HttpMethod(metodo), "/api/no-existe");
        if (contentType is not null)
        {
            solicitud.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
            solicitud.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        var respuesta = await cliente.SendAsync(solicitud);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaRutaFueraDeApiSirveElIndexDeLaSpa()
    {
        const string Indice = "<!doctype html><title>RutaDeRespaldoTests</title>";
        var raiz = Directory.CreateTempSubdirectory("ways-wwwroot-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(raiz.FullName, "index.html"), Indice);
            await using var conSpa = fixture.WithWebHostBuilder(builder => builder.UseWebRoot(raiz.FullName));
            using var cliente = conSpa.CreateClient();

            var respuesta = await cliente.GetAsync("/ventas/123");

            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
            Assert.Equal("text/html", respuesta.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Indice, await respuesta.Content.ReadAsStringAsync());
        }
        finally
        {
            raiz.Delete(recursive: true);
        }
    }
}
