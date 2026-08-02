using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Proveedores;
using Ways.Application.Usuarios; // PaginaDe<T>, SolicitudDeLogin
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 3 (tasks 3.4-3.5, db-error-backstops skill): <c>ServicioDeProveedores</c>/
/// <c>ProveedoresEndpoints</c> punta a punta contra Postgres real — unicidad de <c>cuit</c>
/// tenant-wide bajo concurrencia genuina (a diferencia de <c>ux_clientes_numero</c>, acá SÍ es
/// un valor provisto por el cliente HTTP, sin contador atómico que serialice la carrera), ABM
/// completo con la policy <c>GestionDeCatalogo</c> (admin-only), y el 404 uniforme cross-tenant
/// (ADR-8).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ProveedoresEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);

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

    private static AltaProveedor AltaValida(int idCondicionFiscal, string razonSocial, string? cuit = null) =>
        new(razonSocial, null, cuit, idCondicionFiscal, null, null, null, null, null, null, null, null, null);

    /// <summary>Spec: Concurrent creation race yields exactly one winner. A diferencia de
    /// <c>ClientesEndpointsTests.LaCreacionConcurrenteAsignaNumerosSecuencialesSinExponerElBackstop</c>,
    /// acá no hay ningún contador con lock de fila que serialice la carrera: <c>cuit</c> es un
    /// valor provisto por el cliente HTTP y <c>ServicioDeProveedores.CrearAsync</c> es un
    /// INSERT incondicional con pre-chequeo (mismo shape que
    /// <c>ServicioDeCatalogo.CrearAsync</c>) — dos POST lanzados con <c>Task.WhenAll</c> ya
    /// compiten de verdad por el mismo <c>ux_proveedores_cuit</c>, sin necesitar el
    /// interceptor de rendezvous de <c>ParametrosTests</c> (mismo criterio, sin forzar nada,
    /// que <c>CatalogosTests.DosAltasConcurrentesConElMismoNombreEnElMismoAlcanceDisparanElBackstopDelSaveChanges</c>).</summary>
    [Fact]
    public async Task LaCreacionConcurrenteConElMismoCuitDaExactamenteUnGanador()
    {
        var (idCondicionFiscalCf, mailAdmin, passwordAdmin, _) =
            await AprovisionarTenantAsync(nameof(LaCreacionConcurrenteConElMismoCuitDaExactamenteUnGanador));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        const string cuitCompartido = "30712345678";
        var altaA = AltaValida(idCondicionFiscalCf, "Proveedor A", cuitCompartido);
        var altaB = AltaValida(idCondicionFiscalCf, "Proveedor B", cuitCompartido);

        var tareaA = admin.PostAsJsonAsync("/api/proveedores", altaA);
        var tareaB = admin.PostAsJsonAsync("/api/proveedores", altaB);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cuit_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Spec: Same cuit across different tenants is allowed.</summary>
    [Fact]
    public async Task ElMismoCuitEnDosTenantsEsPermitido()
    {
        var (idCondicionFiscalA, mailAdminA, passwordAdminA, _) =
            await AprovisionarTenantAsync(nameof(ElMismoCuitEnDosTenantsEsPermitido) + "-A");
        var (idCondicionFiscalB, mailAdminB, passwordAdminB, _) =
            await AprovisionarTenantAsync(nameof(ElMismoCuitEnDosTenantsEsPermitido) + "-B");

        const string cuitCompartido = "30712345678";

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var respuestaA = await adminA.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalA, "Proveedor de A", cuitCompartido));
        Assert.Equal(HttpStatusCode.Created, respuestaA.StatusCode);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuestaB = await adminB.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalB, "Proveedor de B", cuitCompartido));
        Assert.Equal(HttpStatusCode.Created, respuestaB.StatusCode);
    }

    /// <summary>Spec: NULL cuit never collides.</summary>
    [Fact]
    public async Task DosProveedoresSinCuitEnElMismoTenantSonAceptados()
    {
        var (idCondicionFiscalCf, mailAdmin, passwordAdmin, _) =
            await AprovisionarTenantAsync(nameof(DosProveedoresSinCuitEnElMismoTenantSonAceptados));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var primero = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Sin cuit uno"));
        var segundo = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Sin cuit dos"));

        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);
        Assert.Equal(HttpStatusCode.Created, segundo.StatusCode);
    }

    /// <summary>Spec: Soft-deleted cuit is reusable.</summary>
    [Fact]
    public async Task ElCuitDeUnProveedorDadoDeBajaEsReutilizable()
    {
        var (idCondicionFiscalCf, mailAdmin, passwordAdmin, _) =
            await AprovisionarTenantAsync(nameof(ElCuitDeUnProveedorDadoDeBajaEsReutilizable));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        const string cuit = "30712345678";
        var alta = await admin.PostAsJsonAsync("/api/proveedores", AltaValida(idCondicionFiscalCf, "Original", cuit));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var creado = await alta.Content.ReadFromJsonAsync<ProveedorListado>();

        var baja = await admin.DeleteAsync($"/api/proveedores/{creado!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var reintento = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Reemplazo", cuit));
        Assert.Equal(HttpStatusCode.Created, reintento.StatusCode);
    }

    /// <summary>Spec: Admin creates and soft-deletes a proveedor.</summary>
    [Fact]
    public async Task UnAdminCreaYDaDeBajaUnProveedor()
    {
        var (idCondicionFiscalCf, mailAdmin, passwordAdmin, _) =
            await AprovisionarTenantAsync(nameof(UnAdminCreaYDaDeBajaUnProveedor));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = await admin.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "De alta y baja"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var creado = await alta.Content.ReadFromJsonAsync<ProveedorListado>();

        var baja = await admin.DeleteAsync($"/api/proveedores/{creado!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var listado = await admin.GetFromJsonAsync<PaginaDe<ProveedorListado>>(
            "/api/proveedores?busqueda=De+alta+y+baja");
        Assert.DoesNotContain(listado!.Items, p => p.Id == creado.Id);
    }

    [Fact]
    public async Task UnVendedorNoPuedeCrearProveedores()
    {
        var (idCondicionFiscalCf, _, _, idTenant) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeCrearProveedores));
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeCrearProveedores));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalCf, "Intento de vendedor"));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant".</summary>
    [Fact]
    public async Task UnProveedorDeOtroTenantDevuelve404()
    {
        var (idCondicionFiscalA, mailAdminA, passwordAdminA, _) =
            await AprovisionarTenantAsync(nameof(UnProveedorDeOtroTenantDevuelve404) + "-A");
        var (_, mailAdminB, passwordAdminB, _) =
            await AprovisionarTenantAsync(nameof(UnProveedorDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var respuestaAlta = await adminA.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalA, "De tenant A"));
        var proveedorDeA = await respuestaAlta.Content.ReadFromJsonAsync<ProveedorListado>();

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.GetAsync($"/api/proveedores/{proveedorDeA!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Mismo criterio que el GET de arriba (ADR-8), para el PUT.</summary>
    [Fact]
    public async Task UnPutSobreUnProveedorDeOtroTenantDevuelve404()
    {
        var (idCondicionFiscalA, mailAdminA, passwordAdminA, _) =
            await AprovisionarTenantAsync(nameof(UnPutSobreUnProveedorDeOtroTenantDevuelve404) + "-A");
        var (idCondicionFiscalB, mailAdminB, passwordAdminB, _) =
            await AprovisionarTenantAsync(nameof(UnPutSobreUnProveedorDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var respuestaAlta = await adminA.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalA, "De tenant A"));
        var proveedorDeA = await respuestaAlta.Content.ReadFromJsonAsync<ProveedorListado>();

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var edicion = new EdicionProveedor(
            RazonSocial: "Intento de edición",
            NombreFantasia: null,
            Cuit: null,
            IdCondicionFiscal: idCondicionFiscalB,
            Domicilio: null,
            Telefono: null,
            Email: null,
            Vendedor: null,
            CelularVendedor: null,
            Supervisor: null,
            CelularSupervisor: null,
            Margen: null,
            Observaciones: null,
            IdEmpresa: null,
            Activo: true);
        var respuesta = await adminB.PutAsJsonAsync($"/api/proveedores/{proveedorDeA!.Id}", edicion);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Mismo criterio que el GET de arriba (ADR-8), para el DELETE.</summary>
    [Fact]
    public async Task UnDeleteSobreUnProveedorDeOtroTenantDevuelve404()
    {
        var (idCondicionFiscalA, mailAdminA, passwordAdminA, _) =
            await AprovisionarTenantAsync(nameof(UnDeleteSobreUnProveedorDeOtroTenantDevuelve404) + "-A");
        var (_, mailAdminB, passwordAdminB, _) =
            await AprovisionarTenantAsync(nameof(UnDeleteSobreUnProveedorDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var respuestaAlta = await adminA.PostAsJsonAsync(
            "/api/proveedores", AltaValida(idCondicionFiscalA, "De tenant A"));
        var proveedorDeA = await respuestaAlta.Content.ReadFromJsonAsync<ProveedorListado>();

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.DeleteAsync($"/api/proveedores/{proveedorDeA!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
