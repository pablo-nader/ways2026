using System.Net;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;
using static Ways.IntegrationTests.ApoyoDePadrones;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/usuarios</c> con filtros por columna y orden contra Postgres real: el filtro y el orden corren en el
/// servidor sobre TODO el conjunto, antes de paginar. Cada caso de orden recorre las páginas de a dos filas y compara la
/// secuencia completa. Los datos están sembrados para que el orden de cada columna difiera del orden de alta, con
/// "sin última conexión". El admin que el aprovisionamiento crea cuenta como la fila 0: el test le fija usuario, mail y
/// conexión después de iniciar sesión.
///
/// Índices vistos por el admin del tenant (0 = admin, 1 a 5 = sembrados en orden de alta, es decir de id):
/// <code>
/// i  usuario  mail  rol         estado     última conexión
/// 0  omar     m     admin       Activo     hace 5 días
/// 1  mario    d     admin       Activo     hace 3 días
/// 2  lucia    z     supervisor  Activo     -
/// 3  juan     b     vendedor    Bloqueado  -
/// 4  ana      y     supervisor  Inactivo   hace 1 día
/// 5  pedro    c     admin       Activo     hace 2 días
/// </code>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class UsuariosGrillaEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/usuarios";
    private static readonly DateTimeOffset Momento = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static Usuario NuevoUsuario(
        int? idTenant, string usuario, string mail, RolConocido rol, EstadoUsuario estado = EstadoUsuario.Activo,
        DateTimeOffset? ultimaConexion = null)
    {
        var hasheador = new HasheadorPbkdf2();

        return new Usuario
        {
            IdTenant = idTenant, NombreUsuario = usuario, Mail = mail, RolId = (int)rol, Estado = estado,
            UltimaConexion = ultimaConexion, PasswordHash = hasheador.Hashear("una-contraseña-larga"),
            PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = Momento, CreatedAt = Momento, UpdatedAt = Momento
        };
    }

    /// <summary>El admin inicia sesión primero (el login le pisa la última conexión) y recién después se le fijan los
    /// datos de la fila 0; devuelve los ids con el admin en el índice 0.</summary>
    private async Task<(HttpClient Admin, int[] Ids)> SembrarYEntrarAsync(Escenario e)
    {
        var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var propio = await db.Usuarios.SingleAsync(u => u.Id == e.IdAdmin);
        propio.NombreUsuario = "omar";
        propio.Mail = $"m.{e.IdTenant}@ways.test";
        propio.UltimaConexion = Momento.AddDays(-5);

        var sembrados = new[]
        {
            NuevoUsuario(e.IdTenant, "mario", $"d.{e.IdTenant}@ways.test", RolConocido.Admin, ultimaConexion: Momento.AddDays(-3)),
            NuevoUsuario(e.IdTenant, "lucia", $"z.{e.IdTenant}@ways.test", RolConocido.Supervisor),
            NuevoUsuario(e.IdTenant, "juan", $"b.{e.IdTenant}@ways.test", RolConocido.Vendedor, EstadoUsuario.Bloqueado),
            NuevoUsuario(e.IdTenant, "ana", $"y.{e.IdTenant}@ways.test", RolConocido.Supervisor, EstadoUsuario.Inactivo, Momento.AddDays(-1)),
            NuevoUsuario(e.IdTenant, "pedro", $"c.{e.IdTenant}@ways.test", RolConocido.Admin, ultimaConexion: Momento.AddDays(-2)),
        };
        foreach (var usuario in sembrados)
        {
            db.Usuarios.Add(usuario);
            await db.SaveChangesAsync();
        }

        return (admin, [e.IdAdmin, .. sembrados.Select(u => u.Id)]);
    }

    [Theory]
    [InlineData("id", "ascendente", "0,1,2,3,4,5")]
    [InlineData("id", "descendente", "5,4,3,2,1,0")]
    [InlineData("usuario", "ascendente", "4,3,2,1,0,5")]
    [InlineData("usuario", "descendente", "5,0,1,2,3,4")]
    [InlineData("mail", "ascendente", "3,5,1,0,4,2")]
    [InlineData("mail", "descendente", "2,4,0,1,5,3")]
    [InlineData("rol", "ascendente", "0,1,5,2,4,3")]
    [InlineData("rol", "descendente", "3,2,4,0,1,5")]
    [InlineData("estado", "ascendente", "0,1,2,5,3,4")]
    [InlineData("estado", "descendente", "4,3,0,1,2,5")]
    [InlineData("ultimaConexion", "ascendente", "0,1,5,4,2,3")]
    [InlineData("ultimaConexion", "descendente", "4,5,1,0,2,3")]
    public async Task CadaColumnaOrdenaTodoElConjuntoAtravesDeLasPaginas(string orden, string direccion, string esperado)
    {
        var e = await AprovisionarAsync($"UsrOrden{orden}{direccion}", fixture);
        var (admin, ids) = await SembrarYEntrarAsync(e);
        using var _ = admin;

        var obtenido = await RecorrerAsync(admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(PorIndice(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsRolYLuegoUsuario()
    {
        var e = await AprovisionarAsync(nameof(SinOrdenSigueElOrdenDeSiempreQueEsRolYLuegoUsuario), fixture);
        var (admin, ids) = await SembrarYEntrarAsync(e);
        using var _ = admin;

        Assert.Equal(PorIndice(ids, "1,0,5,4,2,3"), await RecorrerAsync(admin, Ruta, string.Empty));
    }

    [Theory]
    [InlineData("usuario=ar", "1,0")]
    [InlineData("usuario=LUC", "2")]
    [InlineData("mail=b.", "3")]
    [InlineData("rolId=2", "1,0,5")]
    [InlineData("estado=Bloqueado", "3")]
    [InlineData("estado=Inactivo", "4")]
    [InlineData("conectado=true", "1,0,5,4")]
    [InlineData("conectado=false", "2,3")]
    [InlineData("rolId=2&estado=Activo", "1,0,5")]
    [InlineData("rolId=3&conectado=false", "2")]
    [InlineData("busqueda=juan&estado=Activo", "")]
    [InlineData("busqueda=juan&estado=Bloqueado", "3")]
    public async Task CadaFiltroDeColumnaAcotaElConjuntoYElTotalEsElDelFiltro(string query, string esperado)
    {
        var e = await AprovisionarAsync($"UsrFiltro{query.Replace("=", string.Empty).Replace("&", string.Empty).Replace(".", string.Empty)}", fixture);
        var (admin, ids) = await SembrarYEntrarAsync(e);
        using var _ = admin;

        var pagina = await PedirAsync(admin, Ruta, query);

        Assert.Equal(esperado.Length == 0 ? new List<int>() : PorIndice(ids, esperado), IdsDe(pagina));
        Assert.Equal(esperado.Length == 0 ? 0 : esperado.Split(',').Length, pagina.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ElFiltroPorIdEsExacto()
    {
        var e = await AprovisionarAsync(nameof(ElFiltroPorIdEsExacto), fixture);
        var (admin, ids) = await SembrarYEntrarAsync(e);
        using var _ = admin;

        var pagina = await PedirAsync(admin, Ruta, $"id={ids[3]}");

        Assert.Equal(PorIndice(ids, "3"), IdsDe(pagina));
    }

    [Fact]
    public async Task ElFiltroConviveConElOrdenYSePaginaSobreElConjuntoFiltrado()
    {
        var e = await AprovisionarAsync(nameof(ElFiltroConviveConElOrdenYSePaginaSobreElConjuntoFiltrado), fixture);
        var (admin, ids) = await SembrarYEntrarAsync(e);
        using var _ = admin;

        var primera = await PedirAsync(admin, Ruta, "rolId=2&orden=usuario&direccion=descendente&tamanio=2");
        var recorrido = await RecorrerAsync(admin, Ruta, "rolId=2&orden=usuario&direccion=descendente");

        Assert.Equal(3, primera.GetProperty("total").GetInt32());
        Assert.Equal(PorIndice(ids, "5,0"), IdsDe(primera));
        Assert.Equal(PorIndice(ids, "5,0,1"), recorrido);
    }

    [Theory]
    [InlineData("orden=acciones", "orden_invalido")]
    [InlineData("orden=Usuario", "orden_invalido")]
    [InlineData("orden=usuario&direccion=asc", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    [InlineData("orden=tenant", "orden_no_permitido")]
    [InlineData("tenant=1", "filtro_tenant_no_permitido")]
    [InlineData("tenant=sin-tenant", "filtro_tenant_no_permitido")]
    [InlineData("estado=activo", "estado_invalido")]
    [InlineData("estado=0", "estado_invalido")]
    public async Task UnPedidoFueraDeLoPermitidoSeRechazaConCuatrocientos(string query, string codigoEsperado)
    {
        var e = await AprovisionarAsync($"UsrRechazo{codigoEsperado}{query.Length}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(codigoEsperado, await CodigoDeRechazoAsync(respuesta));
    }

    [Theory]
    [InlineData("rolId=abc")]
    [InlineData("conectado=quizas")]
    [InlineData("id=1,5")]
    public async Task UnFiltroConUnValorQueNoEsDelTipoSeRechazaConCuatrocientos(string query)
    {
        var e = await AprovisionarAsync($"UsrFiltroMalo{query.Length}{query[0]}", fixture);
        using var admin = await ClienteAsync(e.MailAdmin, e.PasswordAdmin, fixture);

        var respuesta = await admin.GetAsync($"{Ruta}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElSupervisorYElVendedorNoAccedenAlPadronNiConFiltrosNiConOrden()
    {
        var e = await AprovisionarAsync(nameof(ElSupervisorYElVendedorNoAccedenAlPadronNiConFiltrosNiConOrden), fixture);
        using var supervisor = await ClienteConRolAsync(e, "supervisor", RolConocido.Supervisor, fixture);
        using var vendedor = await ClienteConRolAsync(e, "vendedor", RolConocido.Vendedor, fixture);

        var delSupervisor = await supervisor.GetAsync($"{Ruta}?orden=usuario&estado=Activo");
        var delVendedor = await vendedor.GetAsync($"{Ruta}?orden=usuario&estado=Activo");

        Assert.Equal(HttpStatusCode.Forbidden, delSupervisor.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delVendedor.StatusCode);
    }

    [Fact]
    public async Task ElOrdenYElFiltroNoMezclanUsuariosDeOtroTenant()
    {
        var propio = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanUsuariosDeOtroTenant)}A", fixture);
        var ajeno = await AprovisionarAsync($"{nameof(ElOrdenYElFiltroNoMezclanUsuariosDeOtroTenant)}B", fixture);
        var (admin, idsPropios) = await SembrarYEntrarAsync(propio);
        using var _ = admin;
        var (adminAjeno, idsAjenos) = await SembrarYEntrarAsync(ajeno);
        adminAjeno.Dispose();

        var ordenado = await RecorrerAsync(admin, Ruta, "orden=usuario&direccion=ascendente");
        var filtrado = await PedirAsync(admin, Ruta, "usuario=a");

        Assert.Equal(PorIndice(idsPropios, "4,3,2,1,0,5"), ordenado);
        Assert.Empty(ordenado.Intersect(idsAjenos));
        Assert.Empty(IdsDe(filtrado).Intersect(idsAjenos));
    }

    /// <summary>Tres tenants posibles para una cuenta (uno que empieza con A, uno con Z y la plataforma, sin tenant):
    /// el orden por tenant es el del texto que muestra la columna, y "Plataforma" cae entre los dos.</summary>
    private async Task<(int IdAlfa, int IdZeta, int[] Ids, string Marca)> SembrarCuentasEnVariosTenantsAsync(string marca)
    {
        var alfa = await AprovisionarAsync($"Alfa{marca}", fixture);
        var zeta = await AprovisionarAsync($"Zeta{marca}", fixture);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var cuentas = new[]
        {
            NuevoUsuario(zeta.IdTenant, $"{marca}-u1", $"u1.{marca}@ways.test", RolConocido.Vendedor),
            NuevoUsuario(null, $"{marca}-u2", $"u2.{marca}@ways.test", RolConocido.Root),
            NuevoUsuario(alfa.IdTenant, $"{marca}-u3", $"u3.{marca}@ways.test", RolConocido.Vendedor),
            NuevoUsuario(alfa.IdTenant, $"{marca}-u4", $"u4.{marca}@ways.test", RolConocido.Vendedor),
        };
        foreach (var cuenta in cuentas)
        {
            db.Usuarios.Add(cuenta);
            await db.SaveChangesAsync();
        }

        return (alfa.IdTenant, zeta.IdTenant, [.. cuentas.Select(c => c.Id)], marca);
    }

    /// <summary><c>usuario</c> es único por tenant, no global: dos admins llamados igual en tenants distintos empatan en
    /// el orden de siempre (rol y usuario) y solo el id los separa. Con páginas de una fila, sin el desempate el
    /// empate puede repetir o saltear filas.</summary>
    [Fact]
    public async Task ElOrdenDeSiempreDesempataPorIdEntreCuentasDeTenantsDistintosConElMismoUsuario()
    {
        var alfa = await AprovisionarAsync($"{nameof(ElOrdenDeSiempreDesempataPorIdEntreCuentasDeTenantsDistintosConElMismoUsuario)}A", fixture);
        var beta = await AprovisionarAsync($"{nameof(ElOrdenDeSiempreDesempataPorIdEntreCuentasDeTenantsDistintosConElMismoUsuario)}B", fixture);
        using var root = await ClienteRootAsync(fixture);

        // Los dos admins (mismo rol) pasan a llamarse igual; el nombre único aísla estas filas del resto de la base.
        const string Nombre = "gemelo-desempate";
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            foreach (var u in await db.Usuarios.Where(u => u.Id == alfa.IdAdmin || u.Id == beta.IdAdmin).ToListAsync())
            {
                u.NombreUsuario = Nombre;
            }

            await db.SaveChangesAsync();
        }

        var recorrido = await RecorrerAsync(root, Ruta, $"usuario={Nombre}", tamanio: 1);

        Assert.Equal(new[] { alfa.IdAdmin, beta.IdAdmin }.Order().ToList(), recorrido);
    }

    [Theory]
    [InlineData("ascendente", "2,3,1,0")]
    [InlineData("descendente", "0,1,2,3")]
    public async Task ElActorDePlataformaOrdenaPorElTextoDeLaColumnaTenant(string direccion, string esperado)
    {
        var (_, _, ids, marca) = await SembrarCuentasEnVariosTenantsAsync($"Mt{direccion[0]}");
        using var root = await ClienteRootAsync(fixture);

        var obtenido = await RecorrerAsync(root, Ruta, $"usuario={marca}-&orden=tenant&direccion={direccion}");

        Assert.Equal(PorIndice(ids, esperado), obtenido);
    }

    [Fact]
    public async Task ElActorDePlataformaFiltraPorTenantYPorLaCuentaSinTenant()
    {
        var (idAlfa, idZeta, ids, marca) = await SembrarCuentasEnVariosTenantsAsync("Mf");
        using var root = await ClienteRootAsync(fixture);

        var deAlfa = await PedirAsync(root, Ruta, $"usuario={marca}-&tenant={idAlfa}");
        var deZeta = await PedirAsync(root, Ruta, $"usuario={marca}-&tenant={idZeta}");
        var deLaPlataforma = await PedirAsync(root, Ruta, $"usuario={marca}-&tenant=sin-tenant");
        var deNadie = await PedirAsync(root, Ruta, $"usuario={marca}-&tenant={int.MaxValue}");
        var ordenInvalido = await root.GetAsync($"{Ruta}?tenant=abc");

        Assert.Equal(PorIndice(ids, "2,3"), IdsDe(deAlfa));
        Assert.Equal(PorIndice(ids, "0"), IdsDe(deZeta));
        Assert.Equal(PorIndice(ids, "1"), IdsDe(deLaPlataforma));
        Assert.Empty(IdsDe(deNadie));
        Assert.Equal("tenant_invalido", await CodigoDeRechazoAsync(ordenInvalido));
    }
}
