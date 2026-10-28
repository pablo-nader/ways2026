using System.Net;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Clientes;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;
using static Ways.IntegrationTests.ApoyoDePadrones;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/personal</c> con filtros por columna y orden contra Postgres real: el filtro y el orden corren en el
/// servidor sobre TODO el conjunto, antes de paginar. Cada caso de orden recorre las páginas de a dos filas y compara la
/// secuencia completa. Los datos están sembrados para que el orden de cada columna difiera del orden de alta y del orden
/// por nombre, con celdas vacías y con un usuario y un cliente de baja lógica (vínculo colgante).
///
/// Índices (0 a 4, en orden de alta, es decir de id):
/// <code>
/// i  nombre  documento  valor hora  saldo  usuario   cliente
/// 0  Dora    20-111     100         50     zoe       Acme Q
/// 1  Ana      -         300         -10    -         -
/// 2  Eva     27-222     200         0      ana-u     Zulu SA (razón social; su nombre es Aaron Zz)
/// 3  Beto     -         500         -30    (baja)    (baja)
/// 4  Cira     -         400         20     bob       Beto
/// </code>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PersonalGrillaEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/personal";

    private async Task<int[]> SembrarAsync(Escenario e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var hasheador = new HasheadorPbkdf2();

        Usuario NuevoUsuario(string nombre, bool baja = false) => new()
        {
            IdTenant = e.IdTenant, NombreUsuario = nombre, Mail = $"{nombre}-{e.IdTenant}@ways.test", RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear("una-contraseña-larga"), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, DeletedAt = baja ? ahora : null, CreatedAt = ahora, UpdatedAt = ahora
        };
        var numero = 100;
        Cliente NuevoCliente(string nombre, string? apellido, string? razonSocial = null, bool baja = false) => new()
        {
            IdTenant = e.IdTenant, Numero = numero++, Nombre = nombre, Apellido = apellido, RazonSocial = razonSocial,
            IdCondicionFiscal = e.IdCondicionFiscalCf, IdListaPrecio = e.IdListaGeneral, DeletedAt = baja ? ahora : null,
            CreatedAt = ahora, UpdatedAt = ahora
        };

        var zoe = NuevoUsuario("zoe");
        var anaU = NuevoUsuario("ana-u");
        var usuarioDeBaja = NuevoUsuario("aaa-baja", baja: true);
        var bob = NuevoUsuario("bob");
        var acme = NuevoCliente("Acme", "Q");
        var zulu = NuevoCliente("Aaron", "Zz", razonSocial: "Zulu SA");
        var clienteDeBaja = NuevoCliente("Aaa", null, baja: true);
        var beto = NuevoCliente("Beto", null);
        db.AddRange(zoe, anaU, usuarioDeBaja, bob, acme, zulu, clienteDeBaja, beto);
        await db.SaveChangesAsync();

        (string Nombre, string? Documento, decimal ValorHora, decimal Saldo, int? Usuario, int? Cliente)[] filas =
        [
            ("Dora", "20-111", 100m, 50m, zoe.Id, acme.Id),
            ("Ana", null, 300m, -10m, null, null),
            ("Eva", "27-222", 200m, 0m, anaU.Id, zulu.Id),
            ("Beto", null, 500m, -30m, usuarioDeBaja.Id, clienteDeBaja.Id),
            ("Cira", null, 400m, 20m, bob.Id, beto.Id),
        ];

        var ids = new List<int>();
        foreach (var f in filas)
        {
            var personal = new Personal
            {
                IdTenant = e.IdTenant, Nombre = f.Nombre, Documento = f.Documento, ValorHora = f.ValorHora, Saldo = f.Saldo,
                IdUsuario = f.Usuario, IdCliente = f.Cliente, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Personal.Add(personal);
            await db.SaveChangesAsync();
            ids.Add(personal.Id);
        }

        return [.. ids];
    }

    [Theory]
    [InlineData("nombre", "ascendente", "1,3,4,0,2")]
    [InlineData("nombre", "descendente", "2,0,4,3,1")]
    [InlineData("valorHora", "ascendente", "0,2,1,4,3")]
    [InlineData("valorHora", "descendente", "3,4,1,2,0")]
    [InlineData("saldo", "ascendente", "3,1,2,4,0")]
    [InlineData("saldo", "descendente", "0,4,2,1,3")]
    [InlineData("usuario", "ascendente", "2,4,0,1,3")]
    [InlineData("usuario", "descendente", "0,4,2,1,3")]
    [InlineData("cliente", "ascendente", "0,4,2,1,3")]
    [InlineData("cliente", "descendente", "2,4,0,1,3")]
    public async Task CadaColumnaOrdenaTodoElConjuntoAtravesDeLasPaginas(string orden, string direccion, string esperado)
    {
        var e = await AprovisionarAsync($"PerOrden{orden}{direccion}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        var obtenido = await RecorrerAsync(admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(PorIndice(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsElNombre()
    {
        var e = await AprovisionarAsync(nameof(SinOrdenSigueElOrdenDeSiempreQueEsElNombre), fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        var ids = await SembrarAsync(e);

        Assert.Equal(PorIndice(ids, "1,3,4,0,2"), await RecorrerAsync(admin, Ruta, string.Empty));
    }

    [Theory]
    [InlineData("nombre=eva", "2")]
    [InlineData("nombre=ANA", "1")]
    [InlineData("nombre=27-222", "2")]
    [InlineData("valorHoraDesde=200&valorHoraHasta=400", "1,4,2")]
    [InlineData("saldoDesde=-10&saldoHasta=20", "1,4,2")]
    [InlineData("saldoDesde=-10", "1,4,0,2")]
    [InlineData("usuario=zo", "0")]
    [InlineData("usuario=b", "4")]
    [InlineData("usuario=aaa", "")]
    [InlineData("cliente=acme", "0")]
    [InlineData("cliente=zulu", "2")]
    [InlineData("cliente=aaron", "")]
    [InlineData("cliente=aaa", "")]
    [InlineData("cliente=beto", "4")]
    [InlineData("valorHoraDesde=200&usuario=o", "4")]
    [InlineData("busqueda=eva&saldoDesde=0", "2")]
    [InlineData("busqueda=eva&saldoDesde=1", "")]
    public async Task CadaFiltroDeColumnaAcotaElConjuntoYElTotalEsElDelFiltro(string query, string esperado)
    {
        var e = await AprovisionarAsync($"PerFiltro{query.Replace("=", string.Empty).Replace("&", string.Empty).Replace("-", string.Empty)}", fixture);
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

        var primera = await PedirAsync(admin, Ruta, "valorHoraDesde=200&orden=valorHora&direccion=descendente&tamanio=2");
        var recorrido = await RecorrerAsync(admin, Ruta, "valorHoraDesde=200&orden=valorHora&direccion=descendente");

        Assert.Equal(4, primera.GetProperty("total").GetInt32());
        Assert.Equal(PorIndice(ids, "3,4"), IdsDe(primera));
        Assert.Equal(PorIndice(ids, "3,4,1,2"), recorrido);
    }

    [Fact]
    public async Task ElListadoMuestraElNombreDelVinculoVivoYNadaDelDadoDeBaja()
    {
        var e = await AprovisionarAsync(nameof(ElListadoMuestraElNombreDelVinculoVivoYNadaDelDadoDeBaja), fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);
        await SembrarAsync(e);

        var pagina = await PedirAsync(admin, Ruta, "orden=cliente&direccion=ascendente&tamanio=5");

        var filas = pagina.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["Acme Q", "Beto", "Zulu SA", null, null],
            filas.Select(f => f.GetProperty("nombreCliente").GetString()).ToList());
        Assert.Equal(
            ["zoe", "bob", "ana-u", null, null],
            filas.Select(f => f.GetProperty("nombreUsuario").GetString()).ToList());
    }

    [Theory]
    [InlineData("orden=acciones", "orden_invalido")]
    [InlineData("orden=valorhora", "orden_invalido")]
    [InlineData("orden=saldo&direccion=asc", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionFueraDeLaListaSeRechazaConCuatrocientos(string query, string codigoEsperado)
    {
        var e = await AprovisionarAsync($"PerRechazo{codigoEsperado}{query.Length}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(codigoEsperado, await CodigoDeRechazoAsync(respuesta));
    }

    [Theory]
    [InlineData("saldoDesde=abc")]
    [InlineData("valorHoraHasta=1,5x")]
    public async Task UnFiltroConUnValorQueNoEsDelTipoSeRechazaConCuatrocientos(string query)
    {
        var e = await AprovisionarAsync($"PerFiltroMalo{query.Length}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElSupervisorLeeFiltraYOrdenaPeroElVendedorNoAccede()
    {
        var e = await AprovisionarAsync(nameof(ElSupervisorLeeFiltraYOrdenaPeroElVendedorNoAccede), fixture);
        var ids = await SembrarAsync(e);
        using var supervisor = await ClienteConRolAsync(e, "supervisor", RolConocido.Supervisor, fixture);
        using var vendedor = await ClienteConRolAsync(e, "vendedor", RolConocido.Vendedor, fixture);

        var ordenado = await RecorrerAsync(supervisor, Ruta, "orden=saldo&direccion=ascendente");
        var filtrado = await PedirAsync(supervisor, Ruta, "cliente=acme");
        var rechazado = await vendedor.GetAsync($"{Ruta}?orden=saldo&nombre=eva");

        Assert.Equal(PorIndice(ids, "3,1,2,4,0"), ordenado);
        Assert.Equal(PorIndice(ids, "0"), IdsDe(filtrado));
        Assert.Equal(HttpStatusCode.Forbidden, rechazado.StatusCode);
    }

    [Fact]
    public async Task ElOrdenYElFiltroNoMezclanPersonalDeOtroTenant()
    {
        var propio = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanPersonalDeOtroTenant)}A", fixture);
        var ajeno = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanPersonalDeOtroTenant)}B", fixture);
        var idsPropios = await SembrarAsync(propio);
        var idsAjenos = await SembrarAsync(ajeno);
        using var admin = await ClienteAsync(propio.MailAdmin, propio.PasswordAdmin, fixture);

        var ordenado = await RecorrerAsync(admin, Ruta, "orden=valorHora&direccion=descendente");
        var filtrado = await PedirAsync(admin, Ruta, "usuario=o");

        Assert.Equal(PorIndice(idsPropios, "3,4,1,2,0"), ordenado);
        Assert.Empty(ordenado.Intersect(idsAjenos));
        Assert.Empty(IdsDe(filtrado).Intersect(idsAjenos));
    }
}
