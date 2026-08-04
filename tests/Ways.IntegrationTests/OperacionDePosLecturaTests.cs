using System.Net;
using System.Net.Http.Json;
using Ways.Api.Seguridad;
using Ways.Application.Catalogos;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios; // SolicitudDeLogin
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Stage-5-pos-ventas, Slice 1 (task 1.9, design: Authorization Surface — decisión 6, spec:
/// articulos/clientes/codigos-barra/parametros-operativos "Vendedor can …" scenarios): prueba
/// positiva, punta a punta, de que un Vendedor lee todas las superficies re-gateadas a
/// <c>Politicas.OperacionDePos</c> — el complemento de los <c>UnVendedorNoPuede…</c> existentes
/// (que siguen en rojo para toda escritura, sin tocar). Cada resurso re-gateado se prueba una
/// vez acá; los <c>*EndpointsTests</c> ya cubren la variante negativa (escritura) por resurso.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OperacionDePosLecturaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";
    private const string PasswordSupervisor = "una-contraseña-larga";

    private async Task<(int IdTenant, int IdEmpresa, int IdPuntoVenta)> AprovisionarTenantAsync(string nombre)
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

        return (resultado!.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta);
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

    private async Task<HttpClient> VendedorLogueadoAsync(int idTenant, string nombre)
    {
        var mailVendedor = await SembrarVendedorAsync(idTenant, nombre);
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<string> SembrarSupervisorAsync(int idTenant, string nombre)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombre.ToLowerInvariant()}-supervisor@ways.test";

        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = "supervisor",
            Mail = mail,
            RolId = (int)RolConocido.Supervisor,
            PasswordHash = hasheador.Hashear(PasswordSupervisor),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
    }

    private async Task<HttpClient> SupervisorLogueadoAsync(int idTenant, string nombre)
    {
        var mailSupervisor = await SembrarSupervisorAsync(idTenant, nombre);
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailSupervisor, PasswordSupervisor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact]
    public async Task UnVendedorPuedeListarArticulos()
    {
        var (idTenant, _, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarArticulos));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarArticulos));

        var respuesta = await vendedor.GetAsync("/api/articulos");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorPuedeListarClientes()
    {
        var (idTenant, _, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarClientes));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarClientes));

        var respuesta = await vendedor.GetAsync("/api/clientes");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorPuedeListarUnCatalogoDeTenant()
    {
        var (idTenant, _, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarUnCatalogoDeTenant));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarUnCatalogoDeTenant));

        var respuesta = await vendedor.GetAsync("/api/catalogos/areas");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorPuedeListarParametros()
    {
        var (idTenant, idEmpresa, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarParametros));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarParametros));

        var respuesta = await vendedor.GetAsync($"/api/parametros?idEmpresa={idEmpresa}");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Cierra el carryover de verify de la etapa 4 (spec: resolucion-de-ofertas /
    /// OperacionDePos Authorization For POST /api/ofertas/resolver). Lote vacío: el servicio
    /// responde <c>[]</c> sin tocar la base (batch 3.11 de la etapa 4), así que esta prueba
    /// aísla la autorización sin depender de datos de catálogo.</summary>
    [Fact]
    public async Task UnVendedorPuedeResolverOfertas()
    {
        var (idTenant, _, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeResolverOfertas));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeResolverOfertas));

        var respuesta = await vendedor.PostAsJsonAsync(
            "/api/ofertas/resolver", new SolicitudDeResolucion(Lineas: []));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Confirmed issue (judgment-day, stage-5-pos-ventas Slice 1): sin token, la
    /// policy OperacionDePos exige <c>RequireAuthenticatedUser()</c> antes que nada — 401, no
    /// 403 (la distinción importa: 403 implica una sesión ya autenticada sin el rol correcto).</summary>
    [Fact]
    public async Task ResolverOfertasSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/ofertas/resolver", new SolicitudDeResolucion(Lineas: []));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    /// <summary>ORCHESTRATOR DECISION (recorded en el spec de main, paridad legacy): Supervisor
    /// se suma al conjunto de roles de <see cref="Politicas.OperacionDePos"/> — mismo acceso de
    /// lectura que Vendedor sobre las superficies re-gateadas.</summary>
    [Fact]
    public async Task UnSupervisorPuedeListarArticulos()
    {
        var (idTenant, _, _) = await AprovisionarTenantAsync(nameof(UnSupervisorPuedeListarArticulos));
        using var supervisor = await SupervisorLogueadoAsync(idTenant, nameof(UnSupervisorPuedeListarArticulos));

        var respuesta = await supervisor.GetAsync("/api/articulos");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Companion del test de arriba: sumar Supervisor a OperacionDePos no relaja el
    /// ABM de catálogos, que sigue admin-only (<see cref="Politicas.GestionDeCatalogo"/>).</summary>
    [Fact]
    public async Task UnSupervisorNoPuedeCrearUnCatalogoDeTenant()
    {
        var (idTenant, _, _) = await AprovisionarTenantAsync(nameof(UnSupervisorNoPuedeCrearUnCatalogoDeTenant));
        using var supervisor = await SupervisorLogueadoAsync(idTenant, nameof(UnSupervisorNoPuedeCrearUnCatalogoDeTenant));

        var respuesta = await supervisor.PostAsJsonAsync(
            "/api/catalogos/areas", new AreaAlta("Intrusa", IdEmpresa: null, Orden: 1));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>Judgment-day slice-6 CRITICAL: <c>GET /api/puntos-venta</c> (listado) quedaba
    /// bajo <see cref="Politicas.GestionDeOrganizacion"/> (Root/Admin), lo que le bloqueaba al
    /// selector de PV del POS el acceso de Vendedor/Supervisor. Se re-gateó solo esta ruta a
    /// <see cref="Politicas.LecturaDePuntosVenta"/>; el resto de <c>OrganizacionEndpoints</c>
    /// (obtener por id, editar) sigue exclusivamente bajo GestionDeOrganizacion.</summary>
    [Fact]
    public async Task UnVendedorPuedeListarPuntosDeVenta()
    {
        var (idTenant, _, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarPuntosDeVenta));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarPuntosDeVenta));

        var respuesta = await vendedor.GetAsync("/api/puntos-venta");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Companion del test de arriba: el listado se relaja, pero la edición de un punto
    /// de venta sigue exclusivamente admin/root (<see cref="Politicas.GestionDeOrganizacion"/>).</summary>
    [Fact]
    public async Task UnVendedorNoPuedeEditarUnPuntoDeVenta()
    {
        var (idTenant, _, idPuntoVenta) = await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeEditarUnPuntoDeVenta));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorNoPuedeEditarUnPuntoDeVenta));

        var respuesta = await vendedor.PutAsJsonAsync(
            $"/api/puntos-venta/{idPuntoVenta}",
            new PuntoVentaEdicion("Intento vendedor", null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>El re-gateo del listado a LecturaDePuntosVenta no le rompe a Root el acceso que
    /// ya tenía vía GestionDeOrganizacion — <c>PuntosVenta.tsx</c> (admin) sigue funcionando.</summary>
    [Fact]
    public async Task RootPuedeListarPuntosDeVenta()
    {
        await AprovisionarTenantAsync(nameof(RootPuedeListarPuntosDeVenta));

        using var root = fixture.CreateClient();
        var login = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await root.GetAsync("/api/puntos-venta");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }
}
