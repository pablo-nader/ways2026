using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/articulos/grilla?orden=&amp;direccion=</c> contra Postgres real: el orden corre en el
/// servidor sobre TODO el conjunto filtrado, antes de paginar. Cada caso recorre las páginas de a dos
/// filas y compara la secuencia completa: un <c>OrderBy</c> aplicado después del <c>Skip/Take</c> (o
/// ausente) la rompe. Los datos están sembrados para que el orden de cada columna difiera del orden
/// por id y del orden por nombre, con vacíos y un proveedor de baja lógica (FK colgante).
///
/// Posiciones sembradas (1 a 5, en orden de alta, es decir de id):
/// <code>
/// pos nombre codigo costo prov     activo venta online precio
///  1  CC     K3     20    PB       sí     sí    no     300
///  2  AA     K5     -     -        no     no    no     -
///  3  EE     K1     40    PA       sí     sí    sí     100
///  4  BB     K4     10    (baja)   sí     sí    no     200
///  5  DD     K2     -     PC       no     sí    sí     400
/// </code>
/// La posición 5 tiene online pero está inactiva: no muestra la insignia "Online".
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosGrillaOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordSecundario = "una-contraseña-larga";

    private static readonly DateTimeOffset MomentoDePrecioFijo = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record Escenario(
        int IdTenant, int IdArea, int IdAlicuotaIva, int IdCondicionFiscalCf, int IdListaGeneral,
        string MailAdmin, string PasswordAdmin);

    private static async Task<Escenario> AprovisionarAsync(
        string nombre, WaysApiFixture fixture, WebApplicationFactory<Program> factory)
    {
        using var root = factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Escenario(
            resultado.IdTenant,
            area.Id,
            await db.AlicuotasIva.Select(a => a.Id).FirstAsync(),
            await db.CondicionesFiscales.Where(c => c.Codigo == "CF").Select(c => c.Id).SingleAsync(),
            await db.ListasPrecio.Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault).Select(l => l.Id).SingleAsync(),
            mailAdmin,
            resultado.PasswordTemporal);
    }

    private static async Task<HttpClient> ClienteAsync(string mail, string password, WebApplicationFactory<Program> factory)
    {
        var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<HttpClient> ClienteConRolAsync(Escenario e, string sufijo, RolConocido rol, WebApplicationFactory<Program> factory)
    {
        var mail = $"{sufijo.ToLowerInvariant()}-{e.IdTenant}@ways.test";
        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = e.IdTenant,
                NombreUsuario = rol.ToString().ToLowerInvariant(),
                Mail = mail,
                RolId = (int)rol,
                PasswordHash = hasheador.Hashear(PasswordSecundario),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        return await ClienteAsync(mail, PasswordSecundario, factory);
    }

    private async Task<int> SembrarProveedorAsync(Escenario e, string razonSocial, bool dadoDeBaja = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var proveedor = new Proveedor
        {
            IdTenant = e.IdTenant, RazonSocial = razonSocial, IdCondicionFiscal = e.IdCondicionFiscalCf,
            DeletedAt = dadoDeBaja ? ahora : null, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();
        return proveedor.Id;
    }

    private async Task<int> SembrarArticuloAsync(
        Escenario e, string nombre, string codigo, decimal? costo, int? idProveedor, bool activo,
        bool venta, bool online, decimal? precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var articulo = new Articulo
        {
            IdTenant = e.IdTenant, CodigoInterno = codigo, Nombre = nombre, IdArea = e.IdArea, IdAlicuotaIva = e.IdAlicuotaIva,
            IdProveedorHabitual = idProveedor, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, Activo = activo,
            CostoNominal = costo, DisponibleVenta = venta, DisponibleOnline = online, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (precio is { } monto)
        {
            db.Precios.Add(new Precio
            {
                IdTenant = e.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = e.IdListaGeneral, Monto = monto,
                VigenteDesde = MomentoDePrecioFijo, CreatedAt = MomentoDePrecioFijo, UpdatedAt = MomentoDePrecioFijo
            });
            await db.SaveChangesAsync();
        }

        return articulo.Id;
    }

    /// <summary>Siembra las cinco posiciones del resumen de la clase y devuelve sus ids por posición (índice 0 = pos. 1).</summary>
    private async Task<int[]> SembrarCincoAsync(Escenario e)
    {
        var pa = await SembrarProveedorAsync(e, "PA");
        var pb = await SembrarProveedorAsync(e, "PB");
        var pc = await SembrarProveedorAsync(e, "PC");
        var baja = await SembrarProveedorAsync(e, "ZZ-baja");

        var ids = new[]
        {
            await SembrarArticuloAsync(e, "CC", "K3", 20m, pb, true, true, false, 300m),
            await SembrarArticuloAsync(e, "AA", "K5", null, null, false, false, false, null),
            await SembrarArticuloAsync(e, "EE", "K1", 40m, pa, true, true, true, 100m),
            await SembrarArticuloAsync(e, "BB", "K4", 10m, baja, true, true, false, 200m),
            await SembrarArticuloAsync(e, "DD", "K2", null, pc, false, true, true, 400m),
        };

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var proveedorDeBaja = await db.Proveedores.SingleAsync(p => p.Id == baja);
        proveedorDeBaja.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return ids;
    }

    private static async Task<List<int>> RecorrerTodasLasPaginasAsync(HttpClient cliente, string query, int tamanio = 2)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 10; pagina++)
        {
            var p = await cliente.GetFromJsonAsync<PaginaDeArticulosGrilla>(
                $"/api/articulos/grilla?{query}&pagina={pagina}&tamanio={tamanio}", OpcionesJson);
            Assert.NotNull(p);
            ids.AddRange(p!.Items.Select(i => i.Id));
            if (pagina * tamanio >= p.Total) break;
        }

        return ids;
    }

    private static List<int> PorPosicion(int[] ids, string posiciones) =>
        [.. posiciones.Split(',').Select(p => ids[int.Parse(p) - 1])];

    [Theory]
    [InlineData("nombre", "ascendente", "2,4,1,5,3")]
    [InlineData("nombre", "descendente", "3,5,1,4,2")]
    [InlineData("codigo", "ascendente", "3,5,1,4,2")]
    [InlineData("codigo", "descendente", "2,4,1,5,3")]
    [InlineData("costo", "ascendente", "4,1,3,2,5")]
    [InlineData("costo", "descendente", "3,1,4,2,5")]
    [InlineData("precio", "ascendente", "3,4,1,5,2")]
    [InlineData("precio", "descendente", "5,1,4,3,2")]
    [InlineData("proveedor", "ascendente", "3,1,5,2,4")]
    [InlineData("proveedor", "descendente", "5,1,3,2,4")]
    [InlineData("estado", "ascendente", "2,5,1,3,4")]
    [InlineData("estado", "descendente", "1,3,4,2,5")]
    [InlineData("disponibilidad", "ascendente", "2,1,4,5,3")]
    [InlineData("disponibilidad", "descendente", "3,1,4,5,2")]
    public async Task CadaColumnaOrdenaTodoElConjuntoAtravesDeLasPaginas(string orden, string direccion, string esperadoPorPosicion)
    {
        var e = await AprovisionarAsync($"Orden{orden}{direccion}", fixture, fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarCincoAsync(e);

        var obtenido = await RecorrerTodasLasPaginasAsync(admin, $"orden={orden}&direccion={direccion}");

        Assert.Equal(PorPosicion(ids, esperadoPorPosicion), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreNombreYLuegoId()
    {
        var e = await AprovisionarAsync(nameof(SinOrdenSigueElOrdenDeSiempreNombreYLuegoId), fixture, fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarCincoAsync(e);

        var obtenido = await RecorrerTodasLasPaginasAsync(admin, "pagina=1");

        Assert.Equal(PorPosicion(ids, "2,4,1,5,3"), obtenido);
    }

    [Fact]
    public async Task ElOrdenConviveConLosFiltrosYElTotalNoCambia()
    {
        var e = await AprovisionarAsync(nameof(ElOrdenConviveConLosFiltrosYElTotalNoCambia), fixture, fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarCincoAsync(e);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            "/api/articulos/grilla?activo=true&orden=codigo&direccion=ascendente&tamanio=2", OpcionesJson);

        Assert.Equal(3, pagina!.Total);
        Assert.Equal(PorPosicion(ids, "3,1"), pagina.Items.Select(i => i.Id).ToList());
    }

    [Fact]
    public async Task OrdenarPorPrecioConFiltroDePrecioTambienOrdenaYPagina()
    {
        var e = await AprovisionarAsync(nameof(OrdenarPorPrecioConFiltroDePrecioTambienOrdenaYPagina), fixture, fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarCincoAsync(e);

        var obtenido = await RecorrerTodasLasPaginasAsync(admin, "precioDesde=150&orden=precio&direccion=descendente");

        Assert.Equal(PorPosicion(ids, "5,1,4"), obtenido);
    }

    [Fact]
    public async Task OrdenarPorPrecioRechazaCuandoLosCandidatosExcedenElTope()
    {
        using var factoryConTopeBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeGrillaDeArticulos>(o => o.TopeDeCandidatosPorFiltroDePrecio = 3)));
        var e = await AprovisionarAsync(nameof(OrdenarPorPrecioRechazaCuandoLosCandidatosExcedenElTope), fixture, factoryConTopeBajo);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, factoryConTopeBajo);
        await SembrarCincoAsync(e);

        var respuesta = await admin.GetAsync("/api/articulos/grilla?orden=precio&direccion=ascendente");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("orden_precio_excede_tope", problema.GetProperty("codigo").GetString());

        var porNombre = await admin.GetAsync("/api/articulos/grilla?orden=nombre&direccion=ascendente");
        Assert.Equal(HttpStatusCode.OK, porNombre.StatusCode);
    }

    [Theory]
    [InlineData("orden=acciones", "orden_invalido")]
    [InlineData("orden=Nombre", "orden_invalido")]
    [InlineData("orden=nombre&direccion=asc", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionFueraDeLaListaSeRechazaConCuatrocientos(string query, string codigoEsperado)
    {
        var e = await AprovisionarAsync($"Rechazo{codigoEsperado}{query.Length}", fixture, fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"/api/articulos/grilla?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(codigoEsperado, problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElVendedorNoPuedeOrdenarPorCostoPeroSiPorLasDemasColumnas()
    {
        var e = await AprovisionarAsync(nameof(ElVendedorNoPuedeOrdenarPorCostoPeroSiPorLasDemasColumnas), fixture, fixture);
        var ids = await SembrarCincoAsync(e);
        using var vendedor = await ClienteConRolAsync(e, "vendedor", RolConocido.Vendedor, fixture);

        var porCosto = await vendedor.GetAsync("/api/articulos/grilla?orden=costo&direccion=ascendente");
        Assert.Equal(HttpStatusCode.BadRequest, porCosto.StatusCode);
        var problema = await porCosto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("orden_no_permitido", problema.GetProperty("codigo").GetString());

        var porPrecio = await RecorrerTodasLasPaginasAsync(vendedor, "orden=precio&direccion=ascendente");
        Assert.Equal(PorPosicion(ids, "3,4,1,5,2"), porPrecio);
    }

    [Fact]
    public async Task ElSupervisorSiPuedeOrdenarPorCosto()
    {
        var e = await AprovisionarAsync(nameof(ElSupervisorSiPuedeOrdenarPorCosto), fixture, fixture);
        var ids = await SembrarCincoAsync(e);
        using var supervisor = await ClienteConRolAsync(e, "supervisor", RolConocido.Supervisor, fixture);

        var obtenido = await RecorrerTodasLasPaginasAsync(supervisor, "orden=costo&direccion=ascendente");

        Assert.Equal(PorPosicion(ids, "4,1,3,2,5"), obtenido);
    }

    [Fact]
    public async Task ElOrdenNoMezclaArticulosDeOtroTenant()
    {
        var propio = await AprovisionarAsync($"{nameof(ElOrdenNoMezclaArticulosDeOtroTenant)}A", fixture, fixture);
        var ajeno = await AprovisionarAsync($"{nameof(ElOrdenNoMezclaArticulosDeOtroTenant)}B", fixture, fixture);
        var idsPropios = await SembrarCincoAsync(propio);
        var idsAjenos = await SembrarCincoAsync(ajeno);
        using var admin = await ClienteAsync(propio.MailAdmin, propio.PasswordAdmin, fixture);

        var obtenido = await RecorrerTodasLasPaginasAsync(admin, "orden=precio&direccion=descendente");

        Assert.Equal(PorPosicion(idsPropios, "5,1,4,3,2"), obtenido);
        Assert.Empty(obtenido.Intersect(idsAjenos));
    }
}
