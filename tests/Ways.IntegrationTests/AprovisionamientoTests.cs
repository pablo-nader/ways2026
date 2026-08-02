using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 3, sección 3F (task 3.27): <c>ServicioDeAprovisionamiento</c> punta a punta —
/// transacción atómica (ADR-16), plantilla V1, y que solo la plataforma pueda invocarlo.
/// Corre contra Postgres real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AprovisionamientoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private async Task<HttpClient> ClienteComoRootAsync()
    {
        var cliente = fixture.CreateClient(); // arranca el host: siembra root
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact]
    public async Task AprovisionaUnTenantDePuntaAPuntaConLaPlantillaV1YElAdminPuedeIniciarSesion()
    {
        using var cliente = await ClienteComoRootAsync();

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nameof(AprovisionaUnTenantDePuntaAPuntaConLaPlantillaV1YElAdminPuedeIniciarSesion),
            RazonSocialEmpresa: "Empresa de prueba",
            NombrePuntoVenta: "Local 1",
            MailAdmin: $"{nameof(AprovisionaUnTenantDePuntaAPuntaConLaPlantillaV1YElAdminPuedeIniciarSesion)}@ways.test");

        var respuesta = await cliente.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);
        Assert.NotEmpty(resultado!.PasswordTemporal);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == resultado.IdTenant);
        Assert.NotNull(tenant);
        Assert.Equal(EstadoTenant.Activo, tenant!.Estado);

        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == resultado.IdEmpresa);
        Assert.NotNull(empresa);
        Assert.Equal(resultado.IdTenant, empresa!.IdTenant);

        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == resultado.IdPuntoVenta);
        Assert.NotNull(puntoVenta);
        Assert.Equal(resultado.IdTenant, puntoVenta!.IdTenant);

        var areas = await db.Areas.Where(a => a.IdTenant == resultado.IdTenant).ToListAsync();
        Assert.Single(areas);
        Assert.Equal(PlantillaDeAprovisionamiento.V1.Area, areas[0].Nombre);

        var medios = await db.MediosPago.Where(m => m.IdTenant == resultado.IdTenant).ToListAsync();
        Assert.Equal(2, medios.Count);
        Assert.Contains(medios, m => m.Nombre == "Efectivo");
        Assert.Contains(medios, m => m.Nombre == "Transferencia");

        var admin = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == resultado.IdUsuarioAdmin);
        Assert.NotNull(admin);
        Assert.Equal(resultado.IdTenant, admin!.IdTenant);
        Assert.Equal((int)RolConocido.Admin, admin.RolId);

        // El password temporal devuelto una sola vez funciona de verdad.
        using var clienteAdmin = fixture.CreateClient();
        var loginAdmin = await clienteAdmin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(solicitud.MailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);
    }

    [Fact]
    public async Task UnaFallaAMitadDeCaminoNoDejaNadaCreado()
    {
        using var cliente = await ClienteComoRootAsync();

        var mailCompartido = $"{nameof(UnaFallaAMitadDeCaminoNoDejaNadaCreado)}@ways.test";

        var primeraSolicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nameof(UnaFallaAMitadDeCaminoNoDejaNadaCreado) + "-1",
            RazonSocialEmpresa: "Empresa 1",
            NombrePuntoVenta: "Local 1",
            MailAdmin: mailCompartido);

        var primeraRespuesta = await cliente.PostAsJsonAsync("/api/plataforma/tenants", primeraSolicitud);
        Assert.Equal(HttpStatusCode.Created, primeraRespuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantsAntes = await db.Tenants.CountAsync();

        // El mail del admin ya está en uso (ux_usuarios_mail es global, ADR-7): el último paso
        // de la transacción (crear el usuario admin) falla — todo lo anterior en esa MISMA
        // transacción (tenant, empresa, punto de venta, plantilla) tiene que desaparecer con
        // ella, no quedar huérfano.
        var segundaSolicitud = primeraSolicitud with
        {
            NombreTenant = nameof(UnaFallaAMitadDeCaminoNoDejaNadaCreado) + "-2",
            RazonSocialEmpresa = "Empresa 2",
            NombrePuntoVenta = "Local 2"
        };

        var segundaRespuesta = await cliente.PostAsJsonAsync("/api/plataforma/tenants", segundaSolicitud);
        Assert.NotEqual(HttpStatusCode.Created, segundaRespuesta.StatusCode);

        var tenantsDespues = await db.Tenants.CountAsync();
        Assert.Equal(tenantsAntes, tenantsDespues);

        var tenantHuerfano = await db.Tenants.AnyAsync(
            t => t.Nombre == nameof(UnaFallaAMitadDeCaminoNoDejaNadaCreado) + "-2");
        Assert.False(tenantHuerfano);
    }

    [Fact]
    public async Task UnActorDeTenantRecibe403AlIntentarAprovisionar()
    {
        using var _ = fixture.CreateClient();

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Nombre = nameof(UnActorDeTenantRecibe403AlIntentarAprovisionar),
            Estado = EstadoTenant.Activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        const string password = "una-contraseña-larga";
        var mail = $"{nameof(UnActorDeTenantRecibe403AlIntentarAprovisionar)}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mail,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        using var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var intento = await cliente.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento("Otro tenant", "Otra razón social", "Otro local", "otro@ways.test"));

        Assert.Equal(HttpStatusCode.Forbidden, intento.StatusCode);
    }
}
