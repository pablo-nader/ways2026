using System.Net;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Clientes;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDePadrones;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/clientes</c> con filtros por columna y orden contra Postgres real: el filtro y el orden corren en el
/// servidor sobre TODO el conjunto, antes de paginar. Cada caso de orden recorre las páginas de a dos filas y compara la
/// secuencia completa. Los datos están sembrados para que el orden de cada columna difiera del orden de alta y del orden
/// por número, con celdas vacías, y el Consumidor Final que el aprovisionamiento crea cuenta como la fila 0.
///
/// Índices (0 = Consumidor Final, 1 a 5 = sembrados en orden de alta, es decir de id):
/// <code>
/// i  numero nombre         documento teléfono       email  activo
/// 0  1      Consumidor F.  -         -              -      sí
/// 1  20     Carla Zeta     300       33             d@x    sí
/// 2  50     Ana            -         - (celular 22) -      no
/// 3  30     Eva Gómez      100       11             e@x    sí
/// 4  10     Beto           -         -              b@x    sí
/// 5  40     Dora Paz       200       44             c@x    no
/// </code>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ClientesGrillaEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/clientes";

    private async Task<int[]> SembrarAsync(Escenario e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var ids = new List<int> { await db.Clientes.Where(c => c.IdTenant == e.IdTenant && c.Numero == 1).Select(c => c.Id).SingleAsync() };

        (int Numero, string Nombre, string? Apellido, string? Documento, string? Telefono, string? Celular, string? Email, bool Activo)[] filas =
        [
            (20, "Carla", "Zeta", "300", "33", null, "d@x", true),
            (50, "Ana", null, null, null, "22", null, false),
            (30, "Eva", "Gómez", "100", "11", null, "e@x", true),
            (10, "Beto", null, null, null, null, "b@x", true),
            (40, "Dora", "Paz", "200", "44", null, "c@x", false),
        ];
        foreach (var f in filas)
        {
            var cliente = new Cliente
            {
                IdTenant = e.IdTenant, Numero = f.Numero, Nombre = f.Nombre, Apellido = f.Apellido,
                TipoDocumento = f.Documento is null ? null : TipoDocumento.Dni, NumeroDocumento = f.Documento,
                Telefono = f.Telefono, Celular = f.Celular, Email = f.Email, Activo = f.Activo,
                IdCondicionFiscal = e.IdCondicionFiscalCf, IdListaPrecio = e.IdListaGeneral, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();
            ids.Add(cliente.Id);
        }

        return [.. ids];
    }

    [Theory]
    [InlineData("numero", "ascendente", "0,4,1,3,5,2")]
    [InlineData("numero", "descendente", "2,5,3,1,4,0")]
    [InlineData("nombre", "ascendente", "2,4,1,0,5,3")]
    [InlineData("nombre", "descendente", "3,5,0,1,4,2")]
    [InlineData("documento", "ascendente", "3,5,1,0,2,4")]
    [InlineData("documento", "descendente", "1,5,3,0,2,4")]
    [InlineData("telefono", "ascendente", "3,2,1,5,0,4")]
    [InlineData("telefono", "descendente", "5,1,2,3,0,4")]
    [InlineData("email", "ascendente", "4,5,1,3,0,2")]
    [InlineData("email", "descendente", "3,1,5,4,0,2")]
    [InlineData("estado", "ascendente", "0,1,3,4,2,5")]
    [InlineData("estado", "descendente", "2,5,0,1,3,4")]
    public async Task CadaColumnaOrdenaTodoElConjuntoAtravesDeLasPaginas(string orden, string direccion, string esperado)
    {
        var e = await AprovisionarAsync($"CliOrden{orden}{direccion}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        var obtenido = await RecorrerAsync(admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(PorIndice(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsElNumero()
    {
        var e = await AprovisionarAsync(nameof(SinOrdenSigueElOrdenDeSiempreQueEsElNumero), fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        Assert.Equal(PorIndice(ids, "0,4,1,3,5,2"), await RecorrerAsync(admin, Ruta, string.Empty));
    }

    [Theory]
    [InlineData("nombre=gomez", "3")]
    [InlineData("nombre=EVA", "3")]
    [InlineData("nombre=zeta", "1")]
    [InlineData("nombre=carla%20zeta", "1")]
    [InlineData("documento=0", "1,3,5")]
    [InlineData("documento=30", "1")]
    [InlineData("telefono=2", "2")]
    [InlineData("telefono=1", "3")]
    [InlineData("email=%40x", "4,1,3,5")]
    [InlineData("email=E%40", "3")]
    [InlineData("activo=true", "0,4,1,3")]
    [InlineData("activo=false", "5,2")]
    [InlineData("numero=40", "5")]
    [InlineData("nombre=a&activo=false", "5,2")]
    [InlineData("busqueda=zeta&activo=true", "1")]
    [InlineData("busqueda=zeta&activo=false", "")]
    public async Task CadaFiltroDeColumnaAcotaElConjuntoYElTotalEsElDelFiltro(string query, string esperado)
    {
        var e = await AprovisionarAsync($"CliFiltro{query.Replace("=", string.Empty).Replace("&", string.Empty).Replace("%", string.Empty)}", fixture);
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

        var primera = await PedirAsync(admin, Ruta, "activo=true&orden=nombre&direccion=descendente&tamanio=2");
        var recorrido = await RecorrerAsync(admin, Ruta, "activo=true&orden=nombre&direccion=descendente");

        Assert.Equal(4, primera.GetProperty("total").GetInt32());
        Assert.Equal(PorIndice(ids, "3,0"), IdsDe(primera));
        Assert.Equal(PorIndice(ids, "3,0,1,4"), recorrido);
    }

    [Theory]
    [InlineData("orden=acciones", "orden_invalido")]
    [InlineData("orden=Numero", "orden_invalido")]
    [InlineData("orden=numero&direccion=asc", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionFueraDeLaListaSeRechazaConCuatrocientos(string query, string codigoEsperado)
    {
        var e = await AprovisionarAsync($"CliRechazo{codigoEsperado}{query.Length}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(codigoEsperado, await CodigoDeRechazoAsync(respuesta));
    }

    [Theory]
    [InlineData("numero=abc")]
    [InlineData("activo=quizas")]
    public async Task UnFiltroConUnValorQueNoEsDelTipoSeRechazaConCuatrocientos(string query)
    {
        var e = await AprovisionarAsync($"CliFiltroMalo{query.Length}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElVendedorListaFiltraYOrdenaPorqueElPadronLoLeeElPos()
    {
        var e = await AprovisionarAsync(nameof(ElVendedorListaFiltraYOrdenaPorqueElPadronLoLeeElPos), fixture);
        var ids = await SembrarAsync(e);
        using var vendedor = await ClienteConRolAsync(e, "vendedor", RolConocido.Vendedor, fixture);

        var ordenado = await RecorrerAsync(vendedor, Ruta, "orden=nombre&direccion=ascendente");
        var filtrado = await PedirAsync(vendedor, Ruta, "nombre=gomez");

        Assert.Equal(PorIndice(ids, "2,4,1,0,5,3"), ordenado);
        Assert.Equal(PorIndice(ids, "3"), IdsDe(filtrado));
    }

    [Fact]
    public async Task ElOrdenYElFiltroNoMezclanClientesDeOtroTenant()
    {
        var propio = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanClientesDeOtroTenant)}A", fixture);
        var ajeno = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanClientesDeOtroTenant)}B", fixture);
        var idsPropios = await SembrarAsync(propio);
        var idsAjenos = await SembrarAsync(ajeno);
        using var admin = await ClienteAsync(propio.MailAdmin, propio.PasswordAdmin, fixture);

        var ordenado = await RecorrerAsync(admin, Ruta, "orden=nombre&direccion=descendente");
        var filtrado = await PedirAsync(admin, Ruta, "nombre=a");

        Assert.Equal(PorIndice(idsPropios, "3,5,0,1,4,2"), ordenado);
        Assert.Empty(ordenado.Intersect(idsAjenos));
        Assert.Empty(IdsDe(filtrado).Intersect(idsAjenos));
    }
}
