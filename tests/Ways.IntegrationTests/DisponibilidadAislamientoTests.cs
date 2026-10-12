using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Las cuatro policies <c>*_publico_lectura</c> que suma la disponibilidad online (<c>ofertas</c>, <c>ofertas_listas</c>,
/// <c>combos</c> y <c>combos_componentes</c>) contra Postgres real, con el rol <c>ways_app</c> (sin <c>BYPASSRLS</c>) y
/// SIN el filtro de EF de por medio: conexión cruda. Un test que pasara por el endpoint no probaría la policy, porque
/// el filtro de EF y los <c>JOIN</c> de la consulta dan el mismo resultado aunque la policy no existiera. (La cláusula
/// <c>disponible_venta</c> de <c>articulos</c> está en <see cref="CatalogoOnlineAislamientoTests"/>.)
///
/// Cada test nombra la cláusula que mata: <c>id_tenant = app_tenant_publico()</c>, <c>app_modo() = 'publico'</c>,
/// <c>disponible_online</c>, <c>activo</c> y <c>deleted_at IS NULL</c> en <c>ofertas</c> y <c>combos</c>. Las dos tablas
/// hijas (<c>ofertas_listas</c>, <c>combos_componentes</c>) solo llevan el tenant: no tienen baja lógica y el padre es
/// quien decide qué se ve, así que la fila de una oferta o un combo que NO se ve SÍ se lee a nivel de policy, y
/// queda fijado para que nadie asuma lo contrario.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DisponibilidadAislamientoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly string[] CuatroTablas = ["ofertas", "ofertas_listas", "combos", "combos_componentes"];

    private readonly ApoyoDeCatalogoOnline apoyo = new(fixture);

    private sealed record Escenario(
        ApoyoDeCatalogoOnline.Tienda A,
        ApoyoDeCatalogoOnline.Tienda B,
        int OfertaVisibleDeA,
        int OfertaFueraDeLaTiendaDeA,
        int OfertaInactivaDeA,
        int OfertaEliminadaDeA,
        int OfertaDeB,
        int ComboVisibleDeA,
        int ComboFueraDeLaTiendaDeA,
        int ComboInactivoDeA,
        int ComboEliminadoDeA,
        int ComboDeB,
        int ArticuloDeA,
        int ArticuloDeB);

    private async Task<Escenario> SembrarEscenarioAsync(string prefijo)
    {
        var a = await apoyo.SembrarTiendaAsync($"{prefijo}-a");
        var b = await apoyo.SembrarTiendaAsync($"{prefijo}-b");
        var articuloA = await apoyo.SembrarVendibleAsync(a, "Artículo A", 10m);
        var articuloB = await apoyo.SembrarVendibleAsync(b, "Artículo B", 10m);

        var ofertaVisible = await apoyo.SembrarOfertaAsync(a.IdTenant, "Visible A", idArticulo: articuloA);
        var ofertaFuera = await apoyo.SembrarOfertaAsync(a.IdTenant, "Fuera de la tienda A", idArticulo: articuloA, disponibleOnline: false);
        var ofertaInactiva = await apoyo.SembrarOfertaAsync(a.IdTenant, "Inactiva A", idArticulo: articuloA, activo: false);
        var ofertaEliminada = await apoyo.SembrarOfertaAsync(a.IdTenant, "Eliminada A", idArticulo: articuloA, eliminada: true);
        var ofertaDeB = await apoyo.SembrarOfertaAsync(b.IdTenant, "Visible B", idArticulo: articuloB);

        // Una fila de targeting por oferta: tres de A (incluida la de la oferta que la policy de ofertas esconde) y una de B.
        foreach (var idOferta in new[] { ofertaVisible, ofertaFuera, ofertaInactiva })
        {
            await apoyo.SembrarOfertaListaAsync(a.IdTenant, idOferta, a.IdListaDefault);
        }

        await apoyo.SembrarOfertaListaAsync(b.IdTenant, ofertaDeB, b.IdListaDefault);

        var par = new (int? IdArticulo, int? IdFamilia, decimal Cantidad)[] { (articuloA, null, 2m) };
        var comboVisible = await apoyo.SembrarComboAsync(a.IdTenant, "Visible A", 15m, par);
        var comboFuera = await apoyo.SembrarComboAsync(a.IdTenant, "Fuera de la tienda A", 16m, par, disponibleOnline: false);
        var comboInactivo = await apoyo.SembrarComboAsync(a.IdTenant, "Inactivo A", 17m, par, activo: false);
        var comboEliminado = await apoyo.SembrarComboAsync(a.IdTenant, "Eliminado A", 18m, par, eliminado: true);
        var comboDeB = await apoyo.SembrarComboAsync(
            b.IdTenant, "Visible B", 19m, new (int? IdArticulo, int? IdFamilia, decimal Cantidad)[] { (articuloB, null, 2m) });

        return new Escenario(
            a, b, ofertaVisible, ofertaFuera, ofertaInactiva, ofertaEliminada, ofertaDeB,
            comboVisible, comboFuera, comboInactivo, comboEliminado, comboDeB, articuloA, articuloB);
    }

    private static async Task<int[]> IdsAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var ids = new List<int>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            ids.Add(lector.GetInt32(0));
        }

        return [.. ids.Order()];
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla}";

        return (long)(await comando.ExecuteScalarAsync())!;
    }

    // ---- qué se ve en modo publico ----------------------------------------------------------------

    /// <summary>La lista completa de ofertas, sin WHERE: cada fila de más rompe la igualdad. La de B mata
    /// <c>id_tenant = app_tenant_publico()</c>; la fuera de la tienda, <c>disponible_online</c>; la inactiva,
    /// <c>activo</c>; la dada de baja, <c>deleted_at IS NULL</c>.</summary>
    [Fact]
    public async Task LaPolicyDeOfertasDejaVerSoloLasDisponiblesOnlineActivasYVivasDelTenantDelGuc()
    {
        var e = await SembrarEscenarioAsync("rls-ofertas");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(new[] { e.OfertaVisibleDeA }, await IdsAsync(comoA, "SELECT id_oferta FROM ofertas"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal(new[] { e.OfertaDeB }, await IdsAsync(comoB, "SELECT id_oferta FROM ofertas"));
    }

    /// <summary>Solo el tenant: la fila de una oferta fuera de la tienda o inactiva SÍ se lee (quién la ve lo decide
    /// el padre), la de B no. No tiene <c>deleted_at</c>.</summary>
    [Fact]
    public async Task LaPolicyDeOfertasListasDejaVerTodasLasFilasDelTenantDelGucAunDeOfertasQueLaTiendaNoVe()
    {
        var e = await SembrarEscenarioAsync("rls-ofertas-listas");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(
            new[] { e.OfertaVisibleDeA, e.OfertaFueraDeLaTiendaDeA, e.OfertaInactivaDeA }.Order().ToArray(),
            await IdsAsync(comoA, "SELECT id_oferta FROM ofertas_listas"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal(new[] { e.OfertaDeB }, await IdsAsync(comoB, "SELECT id_oferta FROM ofertas_listas"));
    }

    [Fact]
    public async Task LaPolicyDeCombosDejaVerSoloLosDisponiblesOnlineActivosYVivosDelTenantDelGuc()
    {
        var e = await SembrarEscenarioAsync("rls-combos");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(new[] { e.ComboVisibleDeA }, await IdsAsync(comoA, "SELECT id_combo FROM combos"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal(new[] { e.ComboDeB }, await IdsAsync(comoB, "SELECT id_combo FROM combos"));
    }

    /// <summary>Solo el tenant: los componentes de un combo fuera de la tienda, inactivo o dado de baja SÍ se leen
    /// (el borrado físico de los de un combo dado de baja se hace al darlo de baja, pero no es lo que la policy
    /// decide); los de B no.</summary>
    [Fact]
    public async Task LaPolicyDeCombosComponentesDejaVerTodasLasFilasDelTenantDelGucAunDeCombosQueLaTiendaNoVe()
    {
        var e = await SembrarEscenarioAsync("rls-combos-comp");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(
            new[] { e.ComboVisibleDeA, e.ComboFueraDeLaTiendaDeA, e.ComboInactivoDeA, e.ComboEliminadoDeA }.Order().ToArray(),
            await IdsAsync(comoA, "SELECT id_combo FROM combos_componentes"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal(new[] { e.ComboDeB }, await IdsAsync(comoB, "SELECT id_combo FROM combos_componentes"));
    }

    // ---- modo y tenant ----------------------------------------------------------------------------

    /// <summary>Las policies leen <c>app_modo() = 'publico'</c> y no solo el GUC del tenant: con el GUC público
    /// puesto pero cualquier otro modo, las cuatro tablas no se ven. Mata la cláusula de modo en cada una.</summary>
    [Theory]
    [InlineData("login")]
    [InlineData("")]
    [InlineData("ninguno")]
    public async Task ElGucPublicoSinElModoPublicoNoAbreNinguna(string modo)
    {
        var e = await SembrarEscenarioAsync($"rls-modo-{(modo == "" ? "vacio" : modo)}");

        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant, modo);

        foreach (var tabla in CuatroTablas)
        {
            Assert.Equal(0L, await ContarAsync(conexion, tabla));
        }
    }

    [Fact]
    public async Task EnModoPublicoSinTenantNoSeVeNingunaDeLasCuatro()
    {
        await SembrarEscenarioAsync("rls-sin-tenant");

        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(idTenantPublico: null);

        foreach (var tabla in CuatroTablas)
        {
            Assert.Equal(0L, await ContarAsync(conexion, tabla));
        }
    }

    // ---- solo lectura -----------------------------------------------------------------------------

    [Fact]
    public async Task EnModoPublicoNoSePuedeEscribirNadaEnLasCuatroTablas()
    {
        var e = await SembrarEscenarioAsync("rls-escritura");
        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);

        // UPDATE y DELETE: RLS filtra las filas antes de tocarlas, 0 filas y sin excepción.
        foreach (var sql in new[]
        {
            "UPDATE ofertas SET nombre = 'hackeada'",
            "UPDATE ofertas_listas SET id_lista_precio = id_lista_precio",
            "UPDATE combos SET nombre = 'hackeado'",
            "UPDATE combos_componentes SET cantidad = 99",
            "DELETE FROM ofertas",
            "DELETE FROM ofertas_listas",
            "DELETE FROM combos",
            "DELETE FROM combos_componentes"
        })
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = sql;

            Assert.Equal(0, await comando.ExecuteNonQueryAsync());
        }

        var idTenant = e.A.IdTenant;

        // INSERT: rechazado por el WITH CHECK de la policy estándar (ninguna policy de insert aplica).
        foreach (var sql in new[]
        {
            $"INSERT INTO ofertas (id_tenant, nombre, id_articulo, porcentaje, prioridad, acumulable, activo, created_at, updated_at) " +
                $"VALUES ({idTenant}, 'hack', {e.ArticuloDeA}, 10, 0, false, true, now(), now())",
            $"INSERT INTO ofertas_listas (id_oferta, id_lista_precio, id_tenant) VALUES ({e.OfertaVisibleDeA}, {e.A.IdListaDefault}, {idTenant})",
            $"INSERT INTO combos (id_tenant, codigo, nombre, precio, activo, created_at, updated_at) " +
                $"VALUES ({idTenant}, 987654, 'hack', 10, true, now(), now())",
            $"INSERT INTO combos_componentes (id_tenant, id_combo, id_articulo, cantidad) " +
                $"VALUES ({idTenant}, {e.ComboVisibleDeA}, {e.ArticuloDeA}, 1)"
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var comando = conexion.CreateCommand();
                comando.CommandText = sql;
                await comando.ExecuteNonQueryAsync();
            });

            Assert.Equal("42501", error.SqlState);
        }

        // Nada cambió en la base: ninguna de las cuatro tablas perdió ni ganó filas de A.
        await using var comoTenant = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        Assert.Equal(4L, await ContarAsync(comoTenant, "ofertas"));
        Assert.Equal(3L, await ContarAsync(comoTenant, "ofertas_listas"));
        Assert.Equal(4L, await ContarAsync(comoTenant, "combos"));
        Assert.Equal(4L, await ContarAsync(comoTenant, "combos_componentes"));
    }

    // ---- el contexto de EF ------------------------------------------------------------------------

    /// <summary>El contexto de EF en modo público (el camino real de la tienda) lee las cuatro tablas del tenant del
    /// host y solo eso, aun sin el filtro de EF: lo decide la policy.</summary>
    [Fact]
    public async Task ElContextoPublicoDeEfLeeLasCuatroTablasDelTenantDelHostYNadaMas()
    {
        var e = await SembrarEscenarioAsync("rls-ef");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Publico, e.A.IdTenant));

        Assert.Equal(
            new[] { e.OfertaVisibleDeA },
            await db.Ofertas.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(x => x.Id).ToArrayAsync());
        Assert.Equal(
            3,
            await db.OfertasListas.IgnoreQueryFilters(["Tenant"]).CountAsync());
        Assert.Equal(
            new[] { e.ComboVisibleDeA },
            await db.Combos.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(x => x.Id).ToArrayAsync());
        Assert.Equal(
            4,
            await db.CombosComponentes.IgnoreQueryFilters(["Tenant"]).CountAsync());
    }
}
