using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Abstracciones;
using Ways.Application.Combos;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/combos/{id}/cargable</c> contra Postgres real: lo que el POS necesita para cargar un combo como líneas de
/// artículo. Cubre los roles, el 404 antes de todo, el aislamiento entre tenants y de empresa, los miembros de una
/// familia (excluye pesables, inactivos y dados de baja), el componente colgado con la lista vacía y el techo de consultas.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosCargablesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private sealed class ContadorDeConsultas : DbCommandInterceptor
    {
        public int Total { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Total++;

            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Total++;

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static ComponenteDeSolicitudDeCombo Art(int id, decimal cantidad = 1m) => new(id, null, cantidad);

    private static ComponenteDeSolicitudDeCombo Fam(int id, decimal cantidad = 1m) => new(null, id, cantidad);

    private async Task<int> ArticuloAsync(
        Entorno e, string nombre, UnidadVenta unidad = UnidadVenta.Unidad, int? idFamilia = null, bool activo = true,
        bool dadoDeBaja = false) =>
        await apoyo.SembrarArticuloAsync(
            e, nombre, ValoresBase(e) with { UnidadVenta = unidad }, idFamilia, dadoDeBaja: dadoDeBaja, activo: activo);

    private static async Task<int> ComboAsync(
        HttpClient admin, IReadOnlyList<ComponenteDeSolicitudDeCombo> componentes, bool activo = true, int? idEmpresa = null)
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/combos", new AltaCombo(null, "Combo", 5000m, idEmpresa, componentes, activo), OpcionesJson);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<ComboListado>(OpcionesJson))!.Id;
    }

    private static Task<HttpResponseMessage> CargableAsync(HttpClient cliente, int id, int? idEmpresa = null) =>
        cliente.GetAsync($"/api/combos/{id}/cargable{(idEmpresa is { } e ? $"?idEmpresa={e}" : "")}");

    private static async Task<ComboCargable> LeerAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<ComboCargable>(OpcionesJson))!;
    }

    [Fact]
    public async Task UnComponenteDeArticuloDevuelveEseArticuloConLaFormaDelEscaneo()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnComponenteDeArticuloDevuelveEseArticuloConLaFormaDelEscaneo));
        var pancho = await ArticuloAsync(e, "pancho");
        var coca = await ArticuloAsync(e, "coca");
        var id = await ComboAsync(e.Admin, [Art(pancho, 2m), Art(coca)]);

        var combo = await LeerAsync(await CargableAsync(e.Admin, id, e.IdEmpresa));

        Assert.Equal(id, combo.Id);
        Assert.Equal(5000m, combo.Precio);
        Assert.Equal([pancho, coca], combo.Componentes.Select(c => c.IdArticulo));
        Assert.Equal([2m, 1m], combo.Componentes.Select(c => c.Cantidad));
        var candidato = Assert.Single(combo.Componentes[0].Candidatos);
        Assert.Equal(pancho, candidato.IdArticulo);
        Assert.Equal("pancho", candidato.Nombre);
        Assert.StartsWith("pancho-", candidato.CodigoInterno);
        Assert.Equal(1m, candidato.Cantidad);
        Assert.Null(candidato.CodigoBarra);
        Assert.Equal(UnidadVenta.Unidad, candidato.UnidadVenta);
        Assert.True(candidato.AcumulaEnVenta);
    }

    [Fact]
    public async Task UnComponenteDeFamiliaDevuelveSoloSusMiembrosVivosActivosYPorUnidad()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnComponenteDeFamiliaDevuelveSoloSusMiembrosVivosActivosYPorUnidad));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var sprite = await ArticuloAsync(e, "sprite", idFamilia: familia);
        var coca = await ArticuloAsync(e, "coca", idFamilia: familia);
        await ArticuloAsync(e, "fanta-inactiva", idFamilia: familia, activo: false);
        await ArticuloAsync(e, "caida", idFamilia: familia, dadoDeBaja: true);
        await ArticuloAsync(e, "ajena-a-la-familia");
        var pancho = await ArticuloAsync(e, "pancho");
        var id = await ComboAsync(e.Admin, [Art(pancho), Fam(familia, 2m)]);

        // El ABM rechaza una familia con un pesable, pero nada impide sumarlo después: la lectura tiene que dejarlo afuera.
        await ArticuloAsync(e, "pesable", UnidadVenta.Peso, familia);

        var combo = await LeerAsync(await CargableAsync(e.Admin, id, e.IdEmpresa));

        var deLaFamilia = combo.Componentes[1];
        Assert.Equal(familia, deLaFamilia.IdFamilia);
        Assert.Equal("Gaseosas", deLaFamilia.Nombre);
        Assert.Equal(2m, deLaFamilia.Cantidad);
        Assert.Equal([coca, sprite], deLaFamilia.Candidatos.Select(c => c.IdArticulo));
    }

    [Fact]
    public async Task UnArticuloComponenteInactivoODadoDeBajaQuedaSinCandidatosYElComponenteSigueEnElCombo()
    {
        using var e = await apoyo.PrepararAsync(
            nameof(UnArticuloComponenteInactivoODadoDeBajaQuedaSinCandidatosYElComponenteSigueEnElCombo));
        var vivo = await ArticuloAsync(e, "vivo");
        var inactivo = await ArticuloAsync(e, "inactivo");
        var caido = await ArticuloAsync(e, "caido");
        var id = await ComboAsync(e.Admin, [Art(vivo), Art(inactivo), Art(caido)]);
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var fila = db.Articulos.Single(a => a.Id == inactivo);
            fila.Activo = false;
            await db.SaveChangesAsync();
        }

        await apoyo.DarDeBajaAsync("articulos", "id_articulo", caido);

        var combo = await LeerAsync(await CargableAsync(e.Admin, id, e.IdEmpresa));

        Assert.Equal([vivo, inactivo, caido], combo.Componentes.Select(c => c.IdArticulo));
        Assert.Single(combo.Componentes[0].Candidatos);
        Assert.Empty(combo.Componentes[1].Candidatos);
        Assert.Equal("inactivo", combo.Componentes[1].Nombre);
        Assert.Empty(combo.Componentes[2].Candidatos);
        Assert.Null(combo.Componentes[2].Nombre);
    }

    [Fact]
    public async Task UnaFamiliaDadaDeBajaOSinMiembrosVendiblesQuedaSinCandidatos()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaDadaDeBajaOSinMiembrosVendiblesQuedaSinCandidatos));
        var vacia = await apoyo.SembrarFamiliaAsync(e, "Vacia");
        var caida = await apoyo.SembrarFamiliaAsync(e, "Caida");
        await ArticuloAsync(e, "miembro-de-la-caida", idFamilia: caida);
        var id = await ComboAsync(e.Admin, [Fam(vacia), Fam(caida)]);
        await apoyo.DarDeBajaAsync("familias", "id_familia", caida);

        var combo = await LeerAsync(await CargableAsync(e.Admin, id, e.IdEmpresa));

        Assert.All(combo.Componentes, c => Assert.Empty(c.Candidatos));
        Assert.Equal("Vacia", combo.Componentes[0].Nombre);
        Assert.Null(combo.Componentes[1].Nombre);
    }

    [Theory]
    [InlineData(RolConocido.Supervisor)]
    [InlineData(RolConocido.Vendedor)]
    public async Task LosRolesDelPosPuedenLeerloYElAdminTambien(RolConocido rol)
    {
        using var e = await apoyo.PrepararAsync(nameof(LosRolesDelPosPuedenLeerloYElAdminTambien) + rol);
        var id = await ComboAsync(e.Admin, [Art(await ArticuloAsync(e, "a")), Art(await ArticuloAsync(e, "b"))]);
        using var cliente = await apoyo.ClienteConRolAsync(e, rol);

        Assert.Equal(id, (await LeerAsync(await CargableAsync(cliente, id, e.IdEmpresa))).Id);
        Assert.Equal(id, (await LeerAsync(await CargableAsync(e.Admin, id, e.IdEmpresa))).Id);
    }

    [Fact]
    public async Task SinSesionDa401()
    {
        using var e = await apoyo.PrepararAsync(nameof(SinSesionDa401));
        var id = await ComboAsync(e.Admin, [Art(await ArticuloAsync(e, "a")), Art(await ArticuloAsync(e, "b"))]);
        using var anonimo = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await CargableAsync(anonimo, id, e.IdEmpresa)).StatusCode);
    }

    [Fact]
    public async Task UnComboInexistenteInactivoDadoDeBajaODeOtroTenantODeOtraEmpresaDa404()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnComboInexistenteInactivoDadoDeBajaODeOtroTenantODeOtraEmpresaDa404));
        using var otro = await apoyo.PrepararAsync(
            nameof(UnComboInexistenteInactivoDadoDeBajaODeOtroTenantODeOtraEmpresaDa404) + "-ajeno");
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var inactivo = await ComboAsync(e.Admin, [Art(a), Art(b)], activo: false);
        var baja = await ComboAsync(e.Admin, [Art(a), Art(b)]);
        await e.Admin.DeleteAsync($"/api/combos/{baja}");
        var deOtraEmpresa = await ComboAsync(e.Admin, [Art(a), Art(b)], idEmpresa: e.IdEmpresa);
        var ajeno = await ComboAsync(
            otro.Admin, [Art(await ArticuloAsync(otro, "x")), Art(await ArticuloAsync(otro, "y"))]);

        foreach (var id in new[] { 999_999_999, inactivo, baja, ajeno })
        {
            var respuesta = await CargableAsync(e.Admin, id, e.IdEmpresa);

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await CargableAsync(e.Admin, deOtraEmpresa, e.IdEmpresa + 1_000)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CargableAsync(e.Admin, deOtraEmpresa)).StatusCode);
        Assert.Equal(deOtraEmpresa, (await LeerAsync(await CargableAsync(e.Admin, deOtraEmpresa, e.IdEmpresa))).Id);
    }

    [Fact]
    public async Task ElComboDeTodoElTenantSeCargaConCualquierEmpresaOSinElla()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElComboDeTodoElTenantSeCargaConCualquierEmpresaOSinElla));
        var id = await ComboAsync(e.Admin, [Art(await ArticuloAsync(e, "a")), Art(await ArticuloAsync(e, "b"))]);

        Assert.Equal(id, (await LeerAsync(await CargableAsync(e.Admin, id))).Id);
        Assert.Equal(id, (await LeerAsync(await CargableAsync(e.Admin, id, e.IdEmpresa))).Id);
    }

    [Fact]
    public async Task LeerUnComboCuestaTresConsultasSinImportarCuantosMiembrosHay()
    {
        using var e = await apoyo.PrepararAsync(nameof(LeerUnComboCuestaTresConsultasSinImportarCuantosMiembrosHay));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Muchas");
        foreach (var i in Enumerable.Range(0, 12))
        {
            await ArticuloAsync(e, $"miembro-{i}", idFamilia: familia);
        }

        var pancho = await ArticuloAsync(e, "pancho");
        var id = await ComboAsync(e.Admin, [Art(pancho), Fam(familia, 3m)]);
        var contador = new ContadorDeConsultas();
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), contador);

        var combo = await ServicioDeCombos.CargarParaPosAsync(db, id, e.IdEmpresa);

        Assert.Equal(12, combo.Componentes[1].Candidatos.Count);
        Assert.Equal(3, contador.Total);
    }
}
