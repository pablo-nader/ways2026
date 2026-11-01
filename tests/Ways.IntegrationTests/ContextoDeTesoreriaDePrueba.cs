using System.Net;
using System.Net.Http.Json;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>Tenant aprovisionado con su Admin, un Supervisor y un Vendedor logueados: arranque común de las pruebas del libro de
/// tesorería y del ajuste manual del efectivo.</summary>
internal sealed record ContextoDeTesoreriaDePrueba(
    WaysApiFixture Fixture, int IdTenant, int IdEmpresa, int IdUsuarioAdmin, HttpClient Admin, HttpClient Supervisor,
    HttpClient Vendedor)
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    public WaysDbContext Db() => Fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));

    public static async Task<ContextoDeTesoreriaDePrueba> PrepararAsync(WaysApiFixture fixture, string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(fixture, admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(fixture, admin, nombre, "vendedor", RolConocido.Vendedor);

        return new ContextoDeTesoreriaDePrueba(
            fixture, resultado.IdTenant, resultado.IdEmpresa, resultado.IdUsuarioAdmin, admin, supervisor, vendedor);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        WaysApiFixture fixture, HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }
}
