using System.Net;
using System.Net.Http.Json;
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

    private async Task<(int IdTenant, int IdEmpresa)> AprovisionarTenantAsync(string nombre)
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

        return (resultado!.IdTenant, resultado.IdEmpresa);
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

    [Fact]
    public async Task UnVendedorPuedeListarArticulos()
    {
        var (idTenant, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarArticulos));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarArticulos));

        var respuesta = await vendedor.GetAsync("/api/articulos");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorPuedeListarClientes()
    {
        var (idTenant, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarClientes));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarClientes));

        var respuesta = await vendedor.GetAsync("/api/clientes");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorPuedeListarUnCatalogoDeTenant()
    {
        var (idTenant, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarUnCatalogoDeTenant));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeListarUnCatalogoDeTenant));

        var respuesta = await vendedor.GetAsync("/api/catalogos/areas");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorPuedeListarParametros()
    {
        var (idTenant, idEmpresa) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeListarParametros));
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
        var (idTenant, _) = await AprovisionarTenantAsync(nameof(UnVendedorPuedeResolverOfertas));
        using var vendedor = await VendedorLogueadoAsync(idTenant, nameof(UnVendedorPuedeResolverOfertas));

        var respuesta = await vendedor.PostAsJsonAsync(
            "/api/ofertas/resolver", new SolicitudDeResolucion(Lineas: []));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }
}
