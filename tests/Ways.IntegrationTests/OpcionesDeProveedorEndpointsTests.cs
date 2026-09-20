using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Proveedores;
using Ways.Application.Usuarios; // SolicitudDeLogin
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// JD-A1 (judgment-day): <c>GET /api/proveedores/opciones</c> punta a punta — la proyección
/// MÍNIMA (<see cref="OpcionDeProveedor"/>) que reemplaza el uso indebido del listado completo
/// (<c>ProveedorListado</c>, con margen/cuit/contacto) como fuente del selector opcional de
/// proveedor del formulario de gastos del turno. <c>Politicas.OperacionDePos</c> (un Vendedor
/// tiene que poder leerla, mismo criterio que el saldo derivado), activos/no-eliminados
/// únicamente, y sin fuga de otro tenant.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OpcionesDeProveedorEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private async Task<(int IdCondicionFiscalCf, string MailAdmin, string PasswordAdmin, int IdTenant)>
        AprovisionarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);

        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idCondicionFiscalCf = await db.CondicionesFiscales
            .Where(c => c.Codigo == "CF")
            .Select(c => c.Id)
            .SingleAsync();

        return (idCondicionFiscalCf, mailAdmin, resultado!.PasswordTemporal, resultado.IdTenant);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<string> SembrarVendedorAsync(int idTenant, string nombre)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";

        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = "vendedor",
            Mail = mail,
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
    }

    private static AltaProveedor AltaValida(int idCondicionFiscal, string razonSocial, bool activo = true) =>
        new(razonSocial, null, null, idCondicionFiscal, null, null, null, null, null, null, null, null, null,
            IdEmpresa: null, Activo: activo);

    /// <summary>Cláusula bajo prueba: <c>ServicioDeProveedores.ListarOpcionesAsync</c> proyecta
    /// SOLO id/razonSocial/nombreFantasia — nunca margen, cuit ni ningún dato de contacto. Mutación
    /// verificada (mutation-proof-tests): reemplazando la proyección por <c>Proyectar(p)</c>
    /// (el <c>ProveedorListado</c> completo), este test falla porque <c>margen</c>/<c>cuit</c>/
    /// <c>celularVendedor</c> pasan a estar presentes; restaurada la proyección mínima, vuelve a
    /// verde.</summary>
    [Fact]
    public async Task UnVendedorObtieneSoloIdRazonSocialYNombreFantasia()
    {
        var (idCondicionFiscalCf, mailAdmin, passwordAdmin, idTenant) =
            await AprovisionarTenantAsync(nameof(UnVendedorObtieneSoloIdRazonSocialYNombreFantasia));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var alta = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Distribuidora Visible"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorObtieneSoloIdRazonSocialYNombreFantasia));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.GetAsync("/api/proveedores/opciones");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var items = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        var item = items.EnumerateArray().Single(e => e.GetProperty("razonSocial").GetString() == "Distribuidora Visible");

        Assert.True(item.TryGetProperty("id", out _));
        Assert.True(item.TryGetProperty("razonSocial", out _));
        Assert.True(item.TryGetProperty("nombreFantasia", out _));
        Assert.False(item.TryGetProperty("margen", out _));
        Assert.False(item.TryGetProperty("cuit", out _));
        Assert.False(item.TryGetProperty("celularVendedor", out _));
        Assert.False(item.TryGetProperty("domicilio", out _));
        Assert.False(item.TryGetProperty("observaciones", out _));
    }

    /// <summary>Cláusula bajo prueba: <c>.Where(p => p.Activo)</c> en
    /// <c>ServicioDeProveedores.ListarOpcionesAsync</c>. Mutación verificada (mutation-proof-tests):
    /// borrando ese <c>Where</c>, este test falla porque el proveedor inactivo vuelve a aparecer;
    /// restaurado, vuelve a verde.</summary>
    [Fact]
    public async Task ExcluyeUnProveedorInactivo()
    {
        var (idCondicionFiscalCf, mailAdmin, passwordAdmin, idTenant) =
            await AprovisionarTenantAsync(nameof(ExcluyeUnProveedorInactivo));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var activo = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Proveedor activo"));
        Assert.Equal(HttpStatusCode.Created, activo.StatusCode);

        var inactivo = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Proveedor inactivo", activo: false));
        Assert.Equal(HttpStatusCode.Created, inactivo.StatusCode);

        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(ExcluyeUnProveedorInactivo));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var opciones = await vendedor.GetFromJsonAsync<List<OpcionDeProveedor>>("/api/proveedores/opciones");

        Assert.Contains(opciones!, o => o.RazonSocial == "Proveedor activo");
        Assert.DoesNotContain(opciones!, o => o.RazonSocial == "Proveedor inactivo");
    }

    /// <summary>Cláusula bajo prueba: la baja lógica (filtro global "BajaLogica",
    /// <c>deleted_at IS NULL</c>) sigue aplicada — <c>ListarOpcionesAsync</c> no llama
    /// <c>IgnoreQueryFilters</c>. Mutación verificada (mutation-proof-tests): agregando
    /// <c>IgnoreQueryFilters(["BajaLogica"])</c> a la query, este test falla porque el proveedor
    /// dado de baja vuelve a aparecer; sin ese ignore, vuelve a verde.</summary>
    [Fact]
    public async Task ExcluyeUnProveedorDadoDeBaja()
    {
        var (idCondicionFiscalCf, mailAdmin, passwordAdmin, idTenant) =
            await AprovisionarTenantAsync(nameof(ExcluyeUnProveedorDadoDeBaja));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var vigente = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Proveedor vigente"));
        Assert.Equal(HttpStatusCode.Created, vigente.StatusCode);

        var dadoDeBaja = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Proveedor de baja"));
        Assert.Equal(HttpStatusCode.Created, dadoDeBaja.StatusCode);
        var creado = await dadoDeBaja.Content.ReadFromJsonAsync<ProveedorListado>();
        var baja = await admin.DeleteAsync($"/api/proveedores/{creado!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(ExcluyeUnProveedorDadoDeBaja));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var opciones = await vendedor.GetFromJsonAsync<List<OpcionDeProveedor>>("/api/proveedores/opciones");

        Assert.Contains(opciones!, o => o.RazonSocial == "Proveedor vigente");
        Assert.DoesNotContain(opciones!, o => o.RazonSocial == "Proveedor de baja");
    }

    /// <summary>Cláusula bajo prueba: el alcance de tenant (RLS) que ya filtra <c>db.Proveedores</c>
    /// — sin <c>IgnoreQueryFilters</c> de por medio, el proveedor de OTRO tenant nunca debería
    /// aparecer acá.</summary>
    [Fact]
    public async Task NoListaProveedoresDeOtroTenant()
    {
        var (idCondicionFiscalA, mailAdminA, passwordAdminA, idTenantA) =
            await AprovisionarTenantAsync(nameof(NoListaProveedoresDeOtroTenant) + "-A");
        var (idCondicionFiscalB, mailAdminB, passwordAdminB, _) =
            await AprovisionarTenantAsync(nameof(NoListaProveedoresDeOtroTenant) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var altaA = await adminA.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalA, "Proveedor del tenant A"));
        Assert.Equal(HttpStatusCode.Created, altaA.StatusCode);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var altaB = await adminB.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalB, "Proveedor del tenant B"));
        Assert.Equal(HttpStatusCode.Created, altaB.StatusCode);

        var mailVendedorA = await SembrarVendedorAsync(idTenantA, nameof(NoListaProveedoresDeOtroTenant) + "-A");
        using var vendedorA = await ClienteLogueadoAsync(mailVendedorA, PasswordVendedor);

        var opciones = await vendedorA.GetFromJsonAsync<List<OpcionDeProveedor>>("/api/proveedores/opciones");

        Assert.Contains(opciones!, o => o.RazonSocial == "Proveedor del tenant A");
        Assert.DoesNotContain(opciones!, o => o.RazonSocial == "Proveedor del tenant B");
    }
}
