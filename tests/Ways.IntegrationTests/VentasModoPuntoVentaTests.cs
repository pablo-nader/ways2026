using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos (DB CHANGE GATE aprobado): invariante "una PC-caja = un punto de venta" del
/// lado de <c>ServicioDeVentas.ResolverPuntoVentaAsync</c> — un actor con claim de dispositivo solo
/// puede vender contra SU PROPIO punto de venta Escritorio; un actor sin esa claim (sesión web
/// normal) solo puede vender contra un punto de venta Web. Cualquier otra combinación es 409
/// <c>punto_venta_modo_incompatible</c>, nunca 404 (el punto de venta existe y es del tenant
/// correcto). Las tres pruebas rechazan ANTES de <c>ResolverTurnoAbiertoAsync</c> (design decisión
/// 11: modo se resuelve inmediatamente después de existencia), así que no hace falta abrir turno
/// ni sembrar artículo/cliente — <c>IdCliente: null</c> resuelve a Consumidor Final (ya
/// aprovisionado) y el artículo nunca se llega a mirar.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasModoPuntoVentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private static SolicitudDeVenta SolicitudMinima(int idPuntoVenta) => new(
        idPuntoVenta, null, "TX", null, [new LineaDeVenta(1, 1m, null)], [], null, null);

    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta)> AprovisionarComoAdminAsync(
        string nombre, ModoPuntoVenta modo)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, modo);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (admin, resultado.IdTenant, resultado.IdPuntoVenta);
    }

    /// <summary>Agrega un segundo punto de venta al tenant vía EF directo — no hay endpoint de alta
    /// (plataforma-only vía aprovisionamiento, ADR-16); mismo criterio que
    /// <c>CostoCongeladoTests</c> sembrando filas auxiliares que la API no expone.</summary>
    private async Task<int> AgregarSegundoPuntoVentaAsync(int idTenant, string nombre, ModoPuntoVenta modo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var idEmpresa = await db.Empresas.Select(e => e.Id).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta
        {
            IdEmpresa = idEmpresa, Nombre = nombre, Modo = modo, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    /// <summary>Vincula un dispositivo al punto de venta indicado, siembra un cajero y devuelve un
    /// <see cref="HttpClient"/> YA logueado como ese cajero vía <c>login-dispositivo</c> — mismo
    /// flujo que <c>DispositivosTests</c>.</summary>
    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(
        HttpClient admin, int idTenant, int idPuntoVenta, string sufijo)
    {
        var alta = await admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = $"cajero-{sufijo}",
                Mail = $"cajero-{sufijo}-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo($"cajero-{sufijo}", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cajero;
    }

    [Fact]
    public async Task UnActorSinClaimDeDispositivoNoPuedeVenderContraUnPuntoVentaEscritorio()
    {
        var (admin, _, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnActorSinClaimDeDispositivoNoPuedeVenderContraUnPuntoVentaEscritorio), ModoPuntoVenta.Escritorio);
        using var _admin = admin;

        var respuesta = await admin.PostAsJsonAsync("/api/ventas", SolicitudMinima(idPuntoVenta));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDispositivoNoPuedeVenderContraUnPuntoVentaQueNoEsElSuyo()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeVenderContraUnPuntoVentaQueNoEsElSuyo), ModoPuntoVenta.Escritorio);
        var idPuntoVentaAjeno = await AgregarSegundoPuntoVentaAsync(idTenant, "Local 2", ModoPuntoVenta.Escritorio);

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVentaPropio, "propio");
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", SolicitudMinima(idPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDispositivoNoPuedeVenderContraUnPuntoVentaWeb()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeVenderContraUnPuntoVentaWeb), ModoPuntoVenta.Escritorio);
        var idPuntoVentaWeb = await AgregarSegundoPuntoVentaAsync(idTenant, "Local Web", ModoPuntoVenta.Web);

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVentaPropio, "web");
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", SolicitudMinima(idPuntoVentaWeb));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }
}
