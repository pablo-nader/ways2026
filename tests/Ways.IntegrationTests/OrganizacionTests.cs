using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// <see cref="ServicioDeOrganizacion"/> punta a punta (batch 11, etapa 4B): lectura/edición de
/// tenants (plataforma-only), empresas y puntos de venta (plataforma ve/edita cualquiera, un
/// admin de tenant solo los propios), y las acciones de suspender/reactivar un tenant a través
/// del endpoint real — extiende la prueba de suspensión de la etapa 2
/// (<c>UsuariosYLoginTests.SuspenderElTenantCortaLaSesionActivaEnLaProximaRequest</c>, que
/// suspendía escribiendo directo en la base) para probar el camino completo vía HTTP. Corre
/// contra Postgres real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OrganizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // El servidor serializa enums como texto (Program.cs, JsonStringEnumConverter) — el
    // HttpClient de prueba no hereda esa configuración: hay que repetirla para los DTOs que
    // llevan un enum (TenantListado.Estado), mismo criterio que CatalogosTests. Además de eso,
    // hay que declarar PropertyNameCaseInsensitive = true a mano: un `new JsonSerializerOptions()`
    // recién creado NO lo trae en true por default (a diferencia del `ReadFromJsonAsync<T>()`
    // sin opciones, que sí matchea "nombre" del servidor con "Nombre" del record sin problema)
    // — sin esto, cada propiedad de un DTO leído con estas opciones queda en su default
    // (`null`/el primer valor del enum/0) en vez de tirar un error, así que el bug pasa
    // desapercibido salvo que la prueba efectivamente assert un valor de campo (encontrado acá
    // debugueando `TenantListado.Estado`/`.Nombre`; el mismo problema latente ya existía en
    // `CatalogosTests.OpcionesJson`, fijado en el mismo commit).
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<HttpClient> ClienteComoRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Siembra un tenant activo con una empresa, un punto de venta y un admin
    /// propio, en modo plataforma — igual criterio que
    /// <c>AprovisionamientoTests</c>/<c>UsuariosYLoginTests</c>: hash real, no una API
    /// pública, porque la API bajo prueba acá es la de organización, no la de alta.</summary>
    private async Task<(Tenant Tenant, Empresa Empresa, PuntoVenta PuntoVenta, string MailAdmin)> SembrarTenantAsync(
        string nombre, EstadoTenant estado = EstadoTenant.Activo)
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra roles

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Nombre = nombre, Estado = estado, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa
        {
            IdTenant = tenant.Id,
            RazonSocial = $"{nombre} SRL",
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id,
            IdEmpresa = empresa.Id,
            Nombre = $"{nombre} - Local 1",
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mailAdmin,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (tenant, empresa, puntoVenta, mailAdmin);
    }

    private async Task<HttpClient> ClienteComoAdminAsync(string mailAdmin)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact]
    public async Task UnAdminEditaSuPropiaEmpresaOk()
    {
        var (_, empresa, _, mailAdmin) = await SembrarTenantAsync(nameof(UnAdminEditaSuPropiaEmpresaOk));
        using var cliente = await ClienteComoAdminAsync(mailAdmin);

        var respuesta = await cliente.PutAsJsonAsync(
            $"/api/empresas/{empresa.Id}", new EmpresaEdicion("Razón social nueva SRL", "Fantasía", "20-11111111-1"));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var actualizada = await respuesta.Content.ReadFromJsonAsync<EmpresaListado>();
        Assert.NotNull(actualizada);
        Assert.Equal("Razón social nueva SRL", actualizada!.RazonSocial);
    }

    [Fact]
    public async Task UnAdminRecibe404AlEditarLaEmpresaDeOtroTenant()
    {
        var (_, _, _, mailAdminA) = await SembrarTenantAsync(
            nameof(UnAdminRecibe404AlEditarLaEmpresaDeOtroTenant) + "-A");
        var (_, empresaB, _, _) = await SembrarTenantAsync(
            nameof(UnAdminRecibe404AlEditarLaEmpresaDeOtroTenant) + "-B");

        using var cliente = await ClienteComoAdminAsync(mailAdminA);

        var respuesta = await cliente.PutAsJsonAsync(
            $"/api/empresas/{empresaB.Id}", new EmpresaEdicion("Intento ajeno SRL", null, null));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAdminRecibe404AlEditarElPuntoDeVentaDeOtroTenant()
    {
        var (_, _, _, mailAdminA) = await SembrarTenantAsync(
            nameof(UnAdminRecibe404AlEditarElPuntoDeVentaDeOtroTenant) + "-A");
        var (_, _, puntoVentaB, _) = await SembrarTenantAsync(
            nameof(UnAdminRecibe404AlEditarElPuntoDeVentaDeOtroTenant) + "-B");

        using var cliente = await ClienteComoAdminAsync(mailAdminA);

        var respuesta = await cliente.PutAsJsonAsync(
            $"/api/puntos-venta/{puntoVentaB.Id}",
            new PuntoVentaEdicion("Intento ajeno", null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAdminRecibe403AlIntentarSuspenderUnTenant()
    {
        var (tenant, _, _, mailAdmin) = await SembrarTenantAsync(
            nameof(UnAdminRecibe403AlIntentarSuspenderUnTenant));

        using var cliente = await ClienteComoAdminAsync(mailAdmin);

        var respuesta = await cliente.PostAsync($"/api/plataforma/tenants/{tenant.Id}/suspender", null);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAdminRecibe403AlListarTenants()
    {
        var (_, _, _, mailAdmin) = await SembrarTenantAsync(nameof(UnAdminRecibe403AlListarTenants));

        using var cliente = await ClienteComoAdminAsync(mailAdmin);
        var respuesta = await cliente.GetAsync("/api/plataforma/tenants");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task PlataformaListaYEditaCualquierEmpresaYPuntoDeVenta()
    {
        var (tenant, empresa, puntoVenta, _) = await SembrarTenantAsync(
            nameof(PlataformaListaYEditaCualquierEmpresaYPuntoDeVenta));

        using var cliente = await ClienteComoRootAsync();

        var listado = await cliente.GetFromJsonAsync<List<EmpresaListado>>("/api/empresas");
        Assert.NotNull(listado);
        Assert.Contains(listado!, e => e.Id == empresa.Id && e.IdTenant == tenant.Id);

        var respuestaEmpresa = await cliente.PutAsJsonAsync(
            $"/api/empresas/{empresa.Id}", new EmpresaEdicion("Editada por plataforma SRL", null, null));
        Assert.Equal(HttpStatusCode.OK, respuestaEmpresa.StatusCode);

        var respuestaPv = await cliente.PutAsJsonAsync(
            $"/api/puntos-venta/{puntoVenta.Id}",
            new PuntoVentaEdicion("Local editado por plataforma", null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, respuestaPv.StatusCode);

        var puntoVentaActualizado = await respuestaPv.Content.ReadFromJsonAsync<PuntoVentaListado>();
        Assert.NotNull(puntoVentaActualizado);
        Assert.Equal("Local editado por plataforma", puntoVentaActualizado!.Nombre);
    }

    [Fact]
    public async Task PlataformaSuspendeUnTenantYSuUsuarioPierdeLaSesionEnLaProximaRequest()
    {
        var (tenant, _, _, mailAdmin) = await SembrarTenantAsync(
            nameof(PlataformaSuspendeUnTenantYSuUsuarioPierdeLaSesionEnLaProximaRequest));

        using var clienteAdmin = await ClienteComoAdminAsync(mailAdmin);
        var previa = await clienteAdmin.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, previa.StatusCode);

        using var clienteRoot = await ClienteComoRootAsync();
        var suspension = await clienteRoot.PostAsync($"/api/plataforma/tenants/{tenant.Id}/suspender", null);
        Assert.Equal(HttpStatusCode.OK, suspension.StatusCode);

        var tenantSuspendido = await suspension.Content.ReadFromJsonAsync<TenantListado>(OpcionesJson);
        Assert.NotNull(tenantSuspendido);
        Assert.Equal(EstadoTenant.Suspendido, tenantSuspendido!.Estado);

        // Misma cookie del admin, próxima request: OnValidatePrincipal revalida el estado del
        // tenant (ADR-2) y corta la sesión sin esperar a que venza la cookie — igual prueba que
        // la etapa 2 (UsuariosYLoginTests), pero suspendiendo a través del endpoint real, no
        // escribiendo directo en la base.
        var luegoDeSuspender = await clienteAdmin.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, luegoDeSuspender.StatusCode);

        // El login también queda bloqueado mientras el tenant siga suspendido.
        using var clienteNuevoLogin = fixture.CreateClient();
        var reintentoLogin = await clienteNuevoLogin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, Password));
        Assert.Equal(HttpStatusCode.Forbidden, reintentoLogin.StatusCode);
    }

    [Fact]
    public async Task ReactivarUnTenantSuspendidoPermiteVolverAIniciarSesion()
    {
        var (tenant, _, _, mailAdmin) = await SembrarTenantAsync(
            nameof(ReactivarUnTenantSuspendidoPermiteVolverAIniciarSesion), EstadoTenant.Suspendido);

        using var clienteRoot = await ClienteComoRootAsync();

        var loginBloqueado = await fixture.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, Password));
        Assert.Equal(HttpStatusCode.Forbidden, loginBloqueado.StatusCode);

        var reactivacion = await clienteRoot.PostAsync($"/api/plataforma/tenants/{tenant.Id}/reactivar", null);
        Assert.Equal(HttpStatusCode.OK, reactivacion.StatusCode);

        var tenantReactivado = await reactivacion.Content.ReadFromJsonAsync<TenantListado>(OpcionesJson);
        Assert.NotNull(tenantReactivado);
        Assert.Equal(EstadoTenant.Activo, tenantReactivado!.Estado);

        var loginOk = await fixture.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, Password));
        Assert.Equal(HttpStatusCode.OK, loginOk.StatusCode);
    }

    [Fact]
    public async Task PlataformaEditaElNombreDeUnTenant()
    {
        var (tenant, _, _, _) = await SembrarTenantAsync(nameof(PlataformaEditaElNombreDeUnTenant));
        using var cliente = await ClienteComoRootAsync();

        var respuesta = await cliente.PutAsJsonAsync(
            $"/api/plataforma/tenants/{tenant.Id}", new TenantEdicion("Nombre editado"));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var actualizado = await respuesta.Content.ReadFromJsonAsync<TenantListado>(OpcionesJson);
        Assert.NotNull(actualizado);
        Assert.Equal("Nombre editado", actualizado!.Nombre);
    }
}
