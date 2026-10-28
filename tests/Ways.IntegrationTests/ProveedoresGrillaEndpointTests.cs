using System.Net;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDePadrones;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/proveedores</c> con filtros por columna y orden contra Postgres real: el filtro y el orden corren en el
/// servidor sobre TODO el conjunto, antes de paginar. Cada caso de orden recorre las páginas de a dos filas y compara la
/// secuencia completa. Los datos están sembrados para que el orden de cada columna difiera del orden de alta y del orden
/// por razón social, con celdas vacías.
///
/// Índices (0 a 4, en orden de alta, es decir de id):
/// <code>
/// i  razón social  fantasía  CUIT  teléfono  email  margen  activo
/// 0  Delta         Ñandú     30-2  5         -      10      no
/// 1  Alfa          -         -     3         z@x    -       sí
/// 2  Eco           -         30-5  -         y@x    30      sí
/// 3  Beta          -         30-1  1         x@x    20      no
/// 4  Cima          -         30-4  4         -      -       sí
/// </code>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ProveedoresGrillaEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/proveedores";

    private async Task<int[]> SembrarAsync(Escenario e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var ids = new List<int>();

        (string Razon, string? Fantasia, string? Cuit, string? Telefono, string? Email, decimal? Margen, bool Activo)[] filas =
        [
            ("Delta", "Ñandú", "30-2", "5", null, 10m, false),
            ("Alfa", null, null, "3", "z@x", null, true),
            ("Eco", null, "30-5", null, "y@x", 30m, true),
            ("Beta", null, "30-1", "1", "x@x", 20m, false),
            ("Cima", null, "30-4", "4", null, null, true),
        ];
        foreach (var f in filas)
        {
            var proveedor = new Proveedor
            {
                IdTenant = e.IdTenant, RazonSocial = f.Razon, NombreFantasia = f.Fantasia, Cuit = f.Cuit,
                Telefono = f.Telefono, Email = f.Email, Margen = f.Margen, Activo = f.Activo,
                IdCondicionFiscal = e.IdCondicionFiscalCf, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Proveedores.Add(proveedor);
            await db.SaveChangesAsync();
            ids.Add(proveedor.Id);
        }

        return [.. ids];
    }

    [Theory]
    [InlineData("razonSocial", "ascendente", "1,3,4,0,2")]
    [InlineData("razonSocial", "descendente", "2,0,4,3,1")]
    [InlineData("cuit", "ascendente", "3,0,4,2,1")]
    [InlineData("cuit", "descendente", "2,4,0,3,1")]
    [InlineData("telefono", "ascendente", "3,1,4,0,2")]
    [InlineData("telefono", "descendente", "0,4,1,3,2")]
    [InlineData("email", "ascendente", "3,2,1,0,4")]
    [InlineData("email", "descendente", "1,2,3,0,4")]
    [InlineData("margen", "ascendente", "0,3,2,1,4")]
    [InlineData("margen", "descendente", "2,3,0,1,4")]
    [InlineData("estado", "ascendente", "1,2,4,0,3")]
    [InlineData("estado", "descendente", "0,3,1,2,4")]
    public async Task CadaColumnaOrdenaTodoElConjuntoAtravesDeLasPaginas(string orden, string direccion, string esperado)
    {
        var e = await AprovisionarAsync($"ProvOrden{orden}{direccion}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        var obtenido = await RecorrerAsync(admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(PorIndice(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsLaRazonSocial()
    {
        var e = await AprovisionarAsync(nameof(SinOrdenSigueElOrdenDeSiempreQueEsLaRazonSocial), fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        Assert.Equal(PorIndice(ids, "1,3,4,0,2"), await RecorrerAsync(admin, Ruta, string.Empty));
    }

    [Theory]
    [InlineData("razonSocial=nandu", "0")]
    [InlineData("razonSocial=ECO", "2")]
    [InlineData("cuitContiene=30-", "3,4,0,2")]
    [InlineData("cuitContiene=-5", "2")]
    [InlineData("telefono=1", "3")]
    [InlineData("telefono=5", "0")]
    [InlineData("email=%40x", "1,3,2")]
    [InlineData("email=x%40", "3")]
    [InlineData("margenDesde=15", "3,2")]
    [InlineData("margenHasta=10", "0")]
    [InlineData("margenDesde=10&margenHasta=20", "3,0")]
    [InlineData("activo=true", "1,4,2")]
    [InlineData("activo=false", "3,0")]
    [InlineData("razonSocial=a&activo=true", "1,4")]
    [InlineData("busqueda=eco&activo=true", "2")]
    [InlineData("busqueda=eco&activo=false", "")]
    public async Task CadaFiltroDeColumnaAcotaElConjuntoYElTotalEsElDelFiltro(string query, string esperado)
    {
        var e = await AprovisionarAsync($"ProvFiltro{query.Replace("=", string.Empty).Replace("&", string.Empty).Replace("%", string.Empty)}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        var pagina = await PedirAsync(admin, Ruta, query);

        Assert.Equal(esperado.Length == 0 ? new List<int>() : PorIndice(ids, esperado), IdsDe(pagina));
        Assert.Equal(esperado.Length == 0 ? 0 : esperado.Split(',').Length, pagina.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ElFiltroConviveConElOrdenYSePaginaSobreElConjuntoFiltrado()
    {
        var e = await AprovisionarAsync(nameof(ElFiltroConviveConElOrdenYSePaginaSobreElConjuntoFiltrado), fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        var primera = await PedirAsync(admin, Ruta, "margenDesde=10&orden=margen&direccion=descendente&tamanio=2");
        var recorrido = await RecorrerAsync(admin, Ruta, "margenDesde=10&orden=margen&direccion=descendente");

        Assert.Equal(3, primera.GetProperty("total").GetInt32());
        Assert.Equal(PorIndice(ids, "2,3"), IdsDe(primera));
        Assert.Equal(PorIndice(ids, "2,3,0"), recorrido);
    }

    [Theory]
    [InlineData("orden=acciones", "orden_invalido")]
    [InlineData("orden=RazonSocial", "orden_invalido")]
    [InlineData("orden=margen&direccion=asc", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionFueraDeLaListaSeRechazaConCuatrocientos(string query, string codigoEsperado)
    {
        var e = await AprovisionarAsync($"ProvRechazo{codigoEsperado}{query.Length}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(codigoEsperado, await CodigoDeRechazoAsync(respuesta));
    }

    [Theory]
    [InlineData("margenDesde=abc")]
    [InlineData("activo=quizas")]
    public async Task UnFiltroConUnValorQueNoEsDelTipoSeRechazaConCuatrocientos(string query)
    {
        var e = await AprovisionarAsync($"ProvFiltroMalo{query.Length}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElVendedorNoAccedeAlPadronNiConFiltrosNiConOrden()
    {
        var e = await AprovisionarAsync(nameof(ElVendedorNoAccedeAlPadronNiConFiltrosNiConOrden), fixture);
        await SembrarAsync(e);
        using var vendedor = await ClienteConRolAsync(e, "vendedor", RolConocido.Vendedor, fixture);

        var respuesta = await vendedor.GetAsync($"{Ruta}?orden=razonSocial&activo=true");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElOrdenYElFiltroNoMezclanProveedoresDeOtroTenant()
    {
        var propio = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanProveedoresDeOtroTenant)}A", fixture);
        var ajeno = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanProveedoresDeOtroTenant)}B", fixture);
        var idsPropios = await SembrarAsync(propio);
        var idsAjenos = await SembrarAsync(ajeno);
        using var admin = await ClienteAsync(propio.MailAdmin, propio.PasswordAdmin, fixture);

        var ordenado = await RecorrerAsync(admin, Ruta, "orden=cuit&direccion=descendente");
        var filtrado = await PedirAsync(admin, Ruta, "activo=true");

        Assert.Equal(PorIndice(idsPropios, "2,4,0,3,1"), ordenado);
        Assert.Empty(ordenado.Intersect(idsAjenos));
        Assert.Empty(IdsDe(filtrado).Intersect(idsAjenos));
    }
}
