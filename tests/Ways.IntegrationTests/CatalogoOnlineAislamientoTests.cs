using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Las seis policies <c>*_publico_lectura</c> que la tienda online agrega al modo <c>publico</c> (<c>articulos</c>,
/// <c>articulos_empresas</c>, <c>listas_precio</c>, <c>precios</c>, <c>categorias</c> e <c>imagenes_articulo</c>)
/// contra Postgres real, con el rol <c>ways_app</c> (sin <c>BYPASSRLS</c>) y SIN el filtro de EF de por medio:
/// conexión cruda. Un test que pasara por el endpoint no probaría la policy, porque el filtro de EF
/// (<c>Tenant</c> y <c>BajaLogica</c>) y los <c>JOIN</c> de la consulta dan el mismo resultado aunque la policy no
/// existiera.
///
/// Cada test nombra la cláusula que mata: <c>id_tenant = app_tenant_publico()</c>, <c>app_modo() = 'publico'</c>,
/// <c>disponible_online</c>, <c>disponible_venta</c>, <c>activo</c> y <c>deleted_at IS NULL</c> en <c>articulos</c>, y <c>deleted_at IS NULL</c> en
/// las otras cuatro que lo llevan. Lo que las policies NO deciden (que un precio esté vigente, que una lista esté
/// activa, que una imagen sea de un artículo publicado) queda fijado también, para que nadie asuma que lo hacen.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogoOnlineAislamientoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly string[] SeisTablas =
        ["articulos", "articulos_empresas", "listas_precio", "precios", "categorias", "imagenes_articulo"];

    private readonly ApoyoDeCatalogoOnline apoyo = new(fixture);

    /// <summary>Dos comercios. En el A, filas de cada tabla que ESTA prueba tiene que ver o no ver; en el B, una
    /// de cada una, que el A nunca ve.</summary>
    private sealed record Escenario(
        ApoyoDeCatalogoOnline.Tienda A,
        ApoyoDeCatalogoOnline.Tienda B,
        int ArticuloVisibleDeA,
        int ArticuloRestringidoDeA,
        int ArticuloSinPublicarDeA,
        int ArticuloInactivoDeA,
        int ArticuloEliminadoDeA,
        int ArticuloDeB,
        int[] ListasVisiblesDeA,
        int ListaEliminadaDeA,
        int ListaDeB,
        int[] PreciosVisiblesDeA,
        int PrecioEliminadoDeA,
        int PrecioDeB,
        int[] CategoriasVisiblesDeA,
        int CategoriaEliminadaDeA,
        int CategoriaDeB,
        int[] ImagenesVisiblesDeA,
        int ImagenEliminadaDeA,
        int ImagenDeB,
        int ArticuloSinVenderDeA);

    private async Task<Escenario> SembrarEscenarioAsync(string prefijo)
    {
        var a = await apoyo.SembrarTiendaAsync($"{prefijo}-a");
        var b = await apoyo.SembrarTiendaAsync($"{prefijo}-b");

        var visible = await apoyo.SembrarVendibleAsync(a, "Visible A", 4.5m);
        var restringido = await apoyo.SembrarArticuloAsync(a, "Restringido A", idEmpresaDisponible: a.IdEmpresa);
        var sinPublicar = await apoyo.SembrarArticuloAsync(a, "Sin publicar A", disponibleOnline: false);
        var sinVender = await apoyo.SembrarArticuloAsync(a, "Sin vender A", disponibleVenta: false);
        var inactivo = await apoyo.SembrarArticuloAsync(a, "Inactivo A", activo: false);
        var eliminado = await apoyo.SembrarArticuloAsync(a, "Eliminado A", eliminado: true);
        var deB = await apoyo.SembrarVendibleAsync(b, "Visible B", 8m);

        var listaInactiva = await apoyo.SembrarListaAsync(a.IdTenant, "Inactiva", activo: false);
        var listaEliminada = await apoyo.SembrarListaAsync(a.IdTenant, "Eliminada", eliminada: true);

        var ahora = DateTimeOffset.UtcNow;
        var precioVivo = await apoyo.SembrarPrecioAsync(a.IdTenant, restringido, a.IdListaDefault, 10m);
        var precioVencido = await apoyo.SembrarPrecioAsync(
            a.IdTenant, visible, a.IdListaDefault, 5m, desde: ahora.AddDays(-30), hasta: ahora.AddDays(-20));
        var precioEliminado = await apoyo.SembrarPrecioAsync(a.IdTenant, visible, a.IdListaDefault, 7m, eliminado: true);
        var precioDeA = await PrecioDeAsync(a.IdTenant, visible, a.IdListaDefault);
        var precioDeB = await PrecioDeAsync(b.IdTenant, deB, b.IdListaDefault);

        var categoriaViva = await apoyo.SembrarCategoriaAsync(a.IdTenant, "Viva");
        var categoriaInactiva = await apoyo.SembrarCategoriaAsync(a.IdTenant, "Inactiva", activo: false);
        var categoriaEliminada = await apoyo.SembrarCategoriaAsync(a.IdTenant, "Eliminada", eliminada: true);
        var categoriaDeB = await apoyo.SembrarCategoriaAsync(b.IdTenant, "De B");

        var bytes = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng);
        var imagenVisible = await apoyo.SembrarImagenAsync(a.IdTenant, visible, bytes);
        var imagenDeArticuloSinPublicar = await apoyo.SembrarImagenAsync(a.IdTenant, sinPublicar, bytes);
        var imagenEliminada = await apoyo.SembrarImagenAsync(a.IdTenant, visible, bytes, eliminada: true);
        var imagenDeB = await apoyo.SembrarImagenAsync(b.IdTenant, deB, bytes);

        return new Escenario(
            a, b, visible, restringido, sinPublicar, inactivo, eliminado, deB,
            ListasVisiblesDeA: [.. new[] { a.IdListaDefault, listaInactiva }.Order()],
            listaEliminada,
            b.IdListaDefault,
            PreciosVisiblesDeA: [.. new[] { precioVivo, precioVencido, precioDeA }.Order()],
            precioEliminado,
            precioDeB,
            CategoriasVisiblesDeA: [.. new[] { categoriaViva, categoriaInactiva }.Order()],
            categoriaEliminada,
            categoriaDeB,
            ImagenesVisiblesDeA: [.. new[] { imagenVisible, imagenDeArticuloSinPublicar }.Order()],
            imagenEliminada,
            imagenDeB,
            sinVender);
    }

    private static int[] ArticulosVisiblesDeA(Escenario e) =>
        [.. new[] { e.ArticuloVisibleDeA, e.ArticuloRestringidoDeA }.Order()];

    private async Task<int> PrecioDeAsync(int idTenant, int idArticulo, int idLista)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Precios
            .Where(p => p.IdTenant == idTenant && p.IdArticulo == idArticulo && p.IdListaPrecio == idLista)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .FirstAsync();
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

    /// <summary>La lista completa de artículos, sin WHERE: cada fila de más rompe la igualdad. El de B mata
    /// <c>id_tenant = app_tenant_publico()</c>; el fuera de la tienda, <c>disponible_online</c>; el de uso interno,
    /// <c>disponible_venta</c>; el inactivo, <c>activo</c>; el dado de baja, <c>deleted_at IS NULL</c>. Un artículo restringido a otra empresa SÍ se ve a nivel de
    /// policy: la disponibilidad por empresa la decide la consulta, no RLS.</summary>
    [Fact]
    public async Task LaPolicyDeArticulosDejaVerSoloLosDisponiblesOnlineYParaLaVentaActivosYVivosDelTenantDelGuc()
    {
        var e = await SembrarEscenarioAsync("rls-articulos");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(
            ArticulosVisiblesDeA(e),
            await IdsAsync(comoA, "SELECT id_articulo FROM articulos"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal([e.ArticuloDeB], await IdsAsync(comoB, "SELECT id_articulo FROM articulos"));
    }

    [Fact]
    public async Task LaPolicyDeArticulosEmpresasDejaVerSoloLasFilasDelTenantDelGuc()
    {
        var e = await SembrarEscenarioAsync("rls-art-emp");
        await apoyo.SembrarArticuloAsync(e.B, "Restringido B", idEmpresaDisponible: e.B.IdEmpresa);

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal([e.ArticuloRestringidoDeA], await IdsAsync(comoA, "SELECT id_articulo FROM articulos_empresas"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Single(await IdsAsync(comoB, "SELECT id_articulo FROM articulos_empresas"));
    }

    /// <summary>La baja mata <c>deleted_at IS NULL</c>; la lista de otro tenant, el tenant. Una lista inactiva SÍ
    /// se ve: que esté activa lo decide la consulta.</summary>
    [Fact]
    public async Task LaPolicyDeListasPrecioDejaVerLasVivasDelTenantDelGucSeanActivasONo()
    {
        var e = await SembrarEscenarioAsync("rls-listas");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(e.ListasVisiblesDeA, await IdsAsync(comoA, "SELECT id_lista_precio FROM listas_precio"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal([e.ListaDeB], await IdsAsync(comoB, "SELECT id_lista_precio FROM listas_precio"));
    }

    /// <summary>Un precio vencido SÍ se ve: su vigencia la decide la consulta (<c>vigente_desde</c> y
    /// <c>vigente_hasta</c>), no RLS. El dado de baja mata <c>deleted_at IS NULL</c>.</summary>
    [Fact]
    public async Task LaPolicyDePreciosDejaVerLosVivosDelTenantDelGucEstenVigentesONo()
    {
        var e = await SembrarEscenarioAsync("rls-precios");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(e.PreciosVisiblesDeA, await IdsAsync(comoA, "SELECT id_precio FROM precios"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal([e.PrecioDeB], await IdsAsync(comoB, "SELECT id_precio FROM precios"));
    }

    [Fact]
    public async Task LaPolicyDeCategoriasDejaVerLasVivasDelTenantDelGucSeanActivasONo()
    {
        var e = await SembrarEscenarioAsync("rls-categorias");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(e.CategoriasVisiblesDeA, await IdsAsync(comoA, "SELECT id_categoria FROM categorias"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal([e.CategoriaDeB], await IdsAsync(comoB, "SELECT id_categoria FROM categorias"));
    }

    /// <summary>La imagen de un artículo sin publicar SÍ se ve a nivel de policy: que el artículo esté publicado y
    /// disponible lo decide la consulta con un <c>JOIN</c> a <c>articulos</c>, que sí tiene esa policy.</summary>
    [Fact]
    public async Task LaPolicyDeImagenesDejaVerLasVivasDelTenantDelGucAunDeArticulosSinPublicar()
    {
        var e = await SembrarEscenarioAsync("rls-imagenes");

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(e.ImagenesVisiblesDeA, await IdsAsync(comoA, "SELECT id_imagen_articulo FROM imagenes_articulo"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal([e.ImagenDeB], await IdsAsync(comoB, "SELECT id_imagen_articulo FROM imagenes_articulo"));
    }

    // ---- modo y tenant ----------------------------------------------------------------------------

    /// <summary>Las policies leen <c>app_modo() = 'publico'</c> y no solo el GUC del tenant: con el GUC público
    /// puesto pero cualquier otro modo, las seis tablas no se ven. Mata la cláusula de modo en cada una.</summary>
    [Theory]
    [InlineData("login")]
    [InlineData("")]
    [InlineData("ninguno")]
    public async Task ElGucPublicoSinElModoPublicoNoAbreNinguna(string modo)
    {
        var e = await SembrarEscenarioAsync($"rls-modo-{(modo == "" ? "vacio" : modo)}");

        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant, modo);

        foreach (var tabla in SeisTablas)
        {
            Assert.Equal(0L, await ContarAsync(conexion, tabla));
        }
    }

    [Fact]
    public async Task EnModoPublicoSinTenantNoSeVeNingunaDeLasSeis()
    {
        await SembrarEscenarioAsync("rls-sin-tenant");

        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(idTenantPublico: null);

        foreach (var tabla in SeisTablas)
        {
            Assert.Equal(0L, await ContarAsync(conexion, tabla));
        }
    }

    // ---- solo lectura -----------------------------------------------------------------------------

    [Fact]
    public async Task EnModoPublicoNoSePuedeEscribirNadaEnLasSeisTablas()
    {
        var e = await SembrarEscenarioAsync("rls-escritura");
        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);

        // UPDATE y DELETE: RLS filtra las filas antes de tocarlas, 0 filas y sin excepción. Las policies de
        // lectura solo cubren SELECT, y ninguna otra policy aplica en este modo.
        foreach (var sql in new[]
        {
            "UPDATE articulos SET nombre = 'hackeado'",
            "UPDATE articulos_empresas SET id_empresa = id_empresa",
            "UPDATE listas_precio SET nombre = 'hackeada'",
            "UPDATE precios SET precio = 0",
            "UPDATE categorias SET nombre = 'hackeada'",
            "UPDATE imagenes_articulo SET content_type = 'image/png'",
            "DELETE FROM articulos",
            "DELETE FROM articulos_empresas",
            "DELETE FROM listas_precio",
            "DELETE FROM precios",
            "DELETE FROM categorias",
            "DELETE FROM imagenes_articulo"
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
            $"INSERT INTO articulos (id_tenant, codigo_interno, nombre, id_area, id_alicuota_iva, unidad_venta, es_producto, created_at, updated_at) " +
                $"VALUES ({idTenant}, 'hack', 'hack', {e.A.IdArea}, {e.A.IdAlicuotaIva}, 'unidad', true, now(), now())",
            $"INSERT INTO articulos_empresas (id_articulo, id_empresa, id_tenant) VALUES ({e.ArticuloVisibleDeA}, {e.A.IdEmpresa}, {idTenant})",
            $"INSERT INTO listas_precio (id_tenant, nombre, es_default, modo, activo, created_at, updated_at) " +
                $"VALUES ({idTenant}, 'hack', false, 'fija', true, now(), now())",
            $"INSERT INTO precios (id_tenant, id_articulo, id_lista_precio, precio, vigente_desde, created_at, updated_at) " +
                $"VALUES ({idTenant}, {e.ArticuloVisibleDeA}, {e.A.IdListaDefault}, 1, now(), now(), now())",
            $"INSERT INTO categorias (id_tenant, nombre, orden, activo, created_at, updated_at) " +
                $"VALUES ({idTenant}, 'hack', 1, true, now(), now())",
            $"INSERT INTO imagenes_articulo (id_tenant, id_articulo, contenido, content_type, actualizado_en, created_at, updated_at) " +
                $"VALUES ({idTenant}, {e.ArticuloRestringidoDeA}, '\\x89504e470d0a1a0a', 'image/png', now(), now(), now())"
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

        // Nada cambió en la base.
        Assert.Equal("Visible A", (await apoyo.LeerArticuloAsync(e.ArticuloVisibleDeA)).Nombre);
        Assert.Equal(2, (await apoyo.LeerImagenesAsync(e.ArticuloVisibleDeA)).Count);
    }

    // ---- lo que el modo público sigue sin poder leer -----------------------------------------------

    /// <summary>
    /// Ni los clientes, ni los usuarios, ni el stock, ni los códigos de barra, ni el costo ni ningún otro dato
    /// del comercio. Es la prueba de que sumar seis tablas no abrió el resto: cada tabla tiene filas del tenant A
    /// (se comprueba antes, en modo tenant, para que el cero no sea vacío).
    /// </summary>
    [Fact]
    public async Task LasSeisTablasNuevasNoAbrenClientesUsuariosNiStock()
    {
        var e = await SembrarEscenarioAsync("rls-resto");
        await SembrarRestoDelComercioAsync(e.A, e.ArticuloVisibleDeA);
        string[] otras = ["clientes", "usuarios", "stock", "codigos_barra", "areas", "dominios", "tenants"];

        await using (var comoTenant = await fixture.AbrirConexionCrudaAsync("tenant", e.A.IdTenant))
        {
            foreach (var tabla in otras)
            {
                Assert.True(await ContarAsync(comoTenant, tabla) > 0, $"{tabla}: el tenant A debería tener filas.");
            }
        }

        await using var comoPublico = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        foreach (var tabla in otras)
        {
            Assert.Equal(0L, await ContarAsync(comoPublico, tabla));
        }
    }

    private async Task SembrarRestoDelComercioAsync(ApoyoDeCatalogoOnline.Tienda tienda, int idArticulo)
    {
        var ahora = DateTimeOffset.UtcNow;
        var idPuntoVenta = await apoyo.Vitrinas.SembrarPuntoVentaAsync(tienda.IdTenant, tienda.IdEmpresa, "Stock privado");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        db.Clientes.Add(new Ways.Domain.Clientes.Cliente
        {
            IdTenant = tienda.IdTenant,
            Numero = 10_000 + Random.Shared.Next(1, 1_000_000),
            Nombre = "Cliente privado",
            IdCondicionFiscal = idCondicionFiscal,
            IdListaPrecio = tienda.IdListaDefault,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        db.CodigosBarra.Add(new Ways.Domain.Articulos.CodigoBarra
        {
            IdTenant = tienda.IdTenant,
            IdArticulo = idArticulo,
            Codigo = $"779{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}",
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = tienda.IdTenant,
            IdArticulo = idArticulo,
            IdSucursal = await SucursalDePrueba.DeAsync(db, idPuntoVenta),
            Cantidad = 7m
        });
        await db.SaveChangesAsync();
    }

    // ---- el contexto de EF ------------------------------------------------------------------------

    /// <summary>El contexto de EF en modo público (el camino real del catálogo) lee las seis tablas del tenant del
    /// host y solo eso, aun sin el filtro de EF: lo decide la policy. Es la comprobación de que el interceptor
    /// deja el tenant en el GUC público y no en <c>app.tenant_id</c>, cuyas policies estándar no miran el modo.</summary>
    [Fact]
    public async Task ElContextoPublicoDeEfLeeLasSeisTablasDelTenantDelHostYNadaMas()
    {
        var e = await SembrarEscenarioAsync("rls-ef");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Publico, e.A.IdTenant));

        Assert.Equal(
            ArticulosVisiblesDeA(e),
            await db.Articulos.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(a => a.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(
            [e.ArticuloRestringidoDeA],
            await db.ArticulosEmpresas.IgnoreQueryFilters(["Tenant"]).Select(x => x.IdArticulo).ToArrayAsync());
        Assert.Equal(
            e.ListasVisiblesDeA,
            await db.ListasPrecio.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(x => x.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(
            e.PreciosVisiblesDeA,
            await db.Precios.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(x => x.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(
            e.CategoriasVisiblesDeA,
            await db.Categorias.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(x => x.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(
            e.ImagenesVisiblesDeA,
            await db.ImagenesArticulo.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(x => x.Id).OrderBy(id => id).ToArrayAsync());

        Assert.Empty(await db.Clientes.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.Usuarios.IgnoreQueryFilters().ToListAsync());
    }
}
