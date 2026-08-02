using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ways.Application.Catalogos;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 3 (tasks 3.19): CRUD de los catálogos de tenant a través de la ruta compartida
/// (ADR-11 — la máquina genérica se ejerce una vez por catálogo, no cinco veces a mano),
/// aislamiento cross-tenant, la regla de profundidad de <c>categorias</c> a través del
/// servicio real (CTE contra Postgres, no la regla pura), y los catálogos fiscales
/// (solo lectura, gate #4). Corre contra Postgres real, migraciones 1-5 aplicadas.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";

    // El servidor serializa enums como texto (Program.cs, JsonStringEnumConverter) — el
    // HttpClient de prueba, a diferencia del servidor, no hereda esa configuración: hay que
    // repetirla acá para los DTOs que llevan un enum (TipoComprobanteListado.Clase).
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(int IdTenant, string Mail)> SembrarTenantConAdminAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mail,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (tenant.Id, mail);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact]
    public async Task CrudCompletoDeUnCatalogoDeTenantAtravesDeLaRutaCompartida()
    {
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(CrudCompletoDeUnCatalogoDeTenantAtravesDeLaRutaCompartida));
        using var cliente = await ClienteLogueadoAsync(mail);

        var alta = new AreaAlta("Almacén", IdEmpresa: null, Orden: 1);
        var creacion = await cliente.PostAsJsonAsync("/api/catalogos/areas", alta);
        Assert.Equal(HttpStatusCode.Created, creacion.StatusCode);
        var creada = await creacion.Content.ReadFromJsonAsync<AreaListado>();
        Assert.NotNull(creada);
        Assert.Equal("Almacén", creada!.Nombre);

        var obtenida = await cliente.GetFromJsonAsync<AreaListado>($"/api/catalogos/areas/{creada.Id}");
        Assert.Equal(creada.Id, obtenida!.Id);

        var listado = await cliente.GetFromJsonAsync<List<AreaListado>>("/api/catalogos/areas");
        Assert.Contains(listado!, a => a.Id == creada.Id);

        var edicion = await cliente.PutAsJsonAsync(
            $"/api/catalogos/areas/{creada.Id}", alta with { Nombre = "Almacén General", Orden = 2 });
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        var editada = await edicion.Content.ReadFromJsonAsync<AreaListado>();
        Assert.Equal("Almacén General", editada!.Nombre);

        var baja = await cliente.DeleteAsync($"/api/catalogos/areas/{creada.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var listadoTrasBaja = await cliente.GetFromJsonAsync<List<AreaListado>>("/api/catalogos/areas");
        Assert.DoesNotContain(listadoTrasBaja!, a => a.Id == creada.Id);
    }

    [Fact]
    public async Task UnIdDeCatalogoDeOtroTenantDevuelve404()
    {
        var (_, mailA) = await SembrarTenantConAdminAsync(nameof(UnIdDeCatalogoDeOtroTenantDevuelve404) + "A");
        var (_, mailB) = await SembrarTenantConAdminAsync(nameof(UnIdDeCatalogoDeOtroTenantDevuelve404) + "B");

        using var clienteA = await ClienteLogueadoAsync(mailA);
        var creacion = await clienteA.PostAsJsonAsync(
            "/api/catalogos/marcas", new MarcaAlta("Marca de A", IdEmpresa: null));
        var creada = await creacion.Content.ReadFromJsonAsync<MarcaListado>();

        using var clienteB = await ClienteLogueadoAsync(mailB);
        var respuesta = await clienteB.GetAsync($"/api/catalogos/marcas/{creada!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaCategoriaEnElCuartoNivelDevuelve400()
    {
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(UnaCategoriaEnElCuartoNivelDevuelve400));
        using var cliente = await ClienteLogueadoAsync(mail);

        var nivel1 = await CrearCategoriaAsync(cliente, "Bebidas", null);
        var nivel2 = await CrearCategoriaAsync(cliente, "Gaseosas", nivel1.Id);
        var nivel3 = await CrearCategoriaAsync(cliente, "Cola", nivel2.Id);

        var intentoNivel4 = await cliente.PostAsJsonAsync(
            "/api/catalogos/categorias", new CategoriaAlta("Cola 500ml", null, 1, nivel3.Id));

        Assert.Equal(HttpStatusCode.BadRequest, intentoNivel4.StatusCode);
    }

    private static async Task<CategoriaListado> CrearCategoriaAsync(HttpClient cliente, string nombre, int? idPadre)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/categorias", new CategoriaAlta(nombre, null, 1, idPadre));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CategoriaListado>())!;
    }

    [Fact]
    public async Task LosCatalogosFiscalesSonDeSoloLecturaParaUnTenant()
    {
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(LosCatalogosFiscalesSonDeSoloLecturaParaUnTenant));
        using var cliente = await ClienteLogueadoAsync(mail);

        var lectura = await cliente.GetAsync("/api/catalogos-fiscales/condiciones-fiscales");
        Assert.Equal(HttpStatusCode.OK, lectura.StatusCode);
        var condiciones = await lectura.Content.ReadFromJsonAsync<List<CondicionFiscalListado>>();
        Assert.NotEmpty(condiciones!);

        var alicuotas = await cliente.GetFromJsonAsync<List<AlicuotaIvaListado>>("/api/catalogos-fiscales/alicuotas-iva");
        Assert.NotEmpty(alicuotas!);

        var tipos = await cliente.GetFromJsonAsync<List<TipoComprobanteListado>>(
            "/api/catalogos-fiscales/tipos-comprobante", OpcionesJson);
        Assert.NotEmpty(tipos!);

        // ADR-11 (override de gate #4, ver design.md): no hay ningún POST/PUT/DELETE mapeado
        // para los catálogos fiscales — a propósito, no por un 403 de policy. La ausencia de
        // ruta es la superficie de API; RLS (HabilitarRlsDeCatalogoGlobal) es la segunda capa
        // detrás. Por eso este caso devuelve 404 (ruta inexistente), no 403.
        var intentoDeEscritura = await cliente.PostAsJsonAsync(
            "/api/catalogos-fiscales/condiciones-fiscales", new { });
        Assert.Equal(HttpStatusCode.NotFound, intentoDeEscritura.StatusCode);
    }
}
