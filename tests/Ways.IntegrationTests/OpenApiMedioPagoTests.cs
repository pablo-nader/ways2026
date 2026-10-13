using System.Net;
using System.Text.Json;

namespace Ways.IntegrationTests;

/// <summary>
/// El documento OpenAPI de la API (solo en Development, que es como corre el fixture) se genera y describe el campo de la
/// cuenta del medio de pago, que viaja en un <c>CampoOpcional</c> con conversor propio, como un entero o null.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OpenApiMedioPagoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<JsonElement> ObtenerEsquemasAsync()
    {
        using var cliente = fixture.CreateClient();
        var respuesta = await cliente.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        return documento.RootElement.GetProperty("components").GetProperty("schemas").Clone();
    }

    private static JsonElement Resolver(JsonElement esquemas, JsonElement esquema)
    {
        if (!esquema.TryGetProperty("$ref", out var referencia))
        {
            return esquema;
        }

        var nombre = referencia.GetString()!.Split('/')[^1];
        return esquemas.GetProperty(nombre);
    }

    [Fact]
    public async Task LaCuentaDelAltaDeMedioDePagoSeDocumentaComoEnteroONull()
    {
        var esquemas = await ObtenerEsquemasAsync();

        var propiedad = Resolver(
            esquemas,
            esquemas.GetProperty("MedioPagoAlta").GetProperty("properties").GetProperty("idCuentaTesoreria"));

        var tipos = propiedad.GetProperty("type") is { ValueKind: JsonValueKind.Array } arreglo
            ? arreglo.EnumerateArray().Select(t => t.GetString()).Order().ToArray()
            : [propiedad.GetProperty("type").GetString()];

        Assert.Equal(["integer", "null"], tipos);
        Assert.False(propiedad.TryGetProperty("properties", out var propias) && propias.EnumerateObject().Any());
        Assert.False(propiedad.TryGetProperty("required", out var requeridas) && requeridas.GetArrayLength() > 0);
        Assert.False(propiedad.TryGetProperty("$ref", out _));

        // Ningún esquema del documento conserva la forma del struct (presente/valor).
        Assert.DoesNotContain(
            esquemas.EnumerateObject(),
            e => e.Value.TryGetProperty("properties", out var campos) && campos.TryGetProperty("presente", out _));
    }
}
