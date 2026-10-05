using System.Net;
using System.Net.Http.Json;
using Ways.Application.Familias;
using Ways.Domain.Usuarios;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// Todas las rutas de <c>/api/familias</c> son solo de admin (<c>GestionDeCatalogo</c>, la puerta del alta y la
/// edición de artículos), las de lectura también: el detalle trae los costos de la referencia de la familia. Cada
/// rol es su propio caso —un vendedor, un supervisor y el usuario root— porque una policy más laxa
/// (<c>OperacionDePos</c>) dejaría pasar a los dos primeros y no al tercero; y las rutas de escritura no tienen
/// entrada en el allowlist de <c>SuperficieDeAutorizacionTests</c>, así que ese guard sí las vigila. Lo que prueba
/// este archivo es lo que el guard estructural no puede: que el rol real recibe 403 en CADA ruta (una ruta que
/// no existiera daría 404) y que sin sesión es 401.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasAutorizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    /// <summary>Una ruta del grupo. <paramref name="EstadoDelAdmin"/> es lo que recibe el admin cuando la
    /// ruta no destruye lo que las demás necesitan; <c>null</c> para las que sí, que prueban su camino feliz en su
    /// propio archivo.</summary>
    private sealed record Ruta(HttpMethod Metodo, string Url, object? Cuerpo, HttpStatusCode? EstadoDelAdmin);

    private static IReadOnlyList<Ruta> Rutas(int idFamilia) =>
    [
        new(HttpMethod.Get, "/api/familias", null, HttpStatusCode.OK),
        new(HttpMethod.Get, $"/api/familias/{idFamilia}", null, HttpStatusCode.OK),
        new(HttpMethod.Put, $"/api/familias/{idFamilia}", new EdicionFamilia("Con otro nombre", true), HttpStatusCode.OK)
    ];

    private static Task<HttpResponseMessage> EnviarAsync(HttpClient cliente, Ruta ruta) =>
        cliente.SendAsync(new HttpRequestMessage(ruta.Metodo, ruta.Url)
        {
            Content = ruta.Cuerpo is null ? null : JsonContent.Create(ruta.Cuerpo, options: OpcionesJson)
        });

    [Theory]
    [InlineData(RolConocido.Vendedor)]
    [InlineData(RolConocido.Supervisor)]
    [InlineData(RolConocido.Root)]
    public async Task UnRolQueNoEsAdminRecibe403EnCadaRutaDeFamilias(RolConocido rol)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnRolQueNoEsAdminRecibe403EnCadaRutaDeFamilias) + rol);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Solo para admin");
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);

        using var cliente = rol == RolConocido.Root ? await apoyo.ClienteRootAsync() : await apoyo.ClienteConRolAsync(e, rol);

        foreach (var ruta in Rutas(familia))
        {
            var respuesta = await EnviarAsync(cliente, ruta);

            Assert.True(
                respuesta.StatusCode == HttpStatusCode.Forbidden,
                $"{rol} en {ruta.Metodo} {ruta.Url}: esperaba 403 y recibió {(int)respuesta.StatusCode}.");
        }

        // La familia sigue como estaba: ningún rechazo escribió nada.
        Assert.Equal("Solo para admin", (await apoyo.LeerFamiliaAsync(familia)).Nombre);
    }

    /// <summary>El admin, en cambio, recibe lo que cada ruta da: sin eso, un 403 para todos —también para el admin—
    /// pasaría las pruebas de arriba.</summary>
    [Fact]
    public async Task ElAdminRecibeLoQueDaCadaRutaDeFamilias()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAdminRecibeLoQueDaCadaRutaDeFamilias));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Solo para admin");
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);

        foreach (var ruta in Rutas(familia).Where(r => r.EstadoDelAdmin is not null))
        {
            var respuesta = await EnviarAsync(e.Admin, ruta);

            Assert.True(
                respuesta.StatusCode == ruta.EstadoDelAdmin,
                $"Admin en {ruta.Metodo} {ruta.Url}: esperaba {(int)ruta.EstadoDelAdmin!.Value} y recibió {(int)respuesta.StatusCode}.");
        }
    }

    [Fact]
    public async Task SinSesionCadaRutaDeFamiliasDa401()
    {
        using var anonimo = fixture.CreateClient();

        foreach (var ruta in Rutas(idFamilia: 1))
        {
            var respuesta = await EnviarAsync(anonimo, ruta);

            Assert.True(
                respuesta.StatusCode == HttpStatusCode.Unauthorized,
                $"Sin sesión en {ruta.Metodo} {ruta.Url}: esperaba 401 y recibió {(int)respuesta.StatusCode}.");
        }
    }
}
