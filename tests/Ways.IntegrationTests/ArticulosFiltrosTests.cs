using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-18-etiquetas-y-consulta, Slice 2 (tasks 2.7-2.11; design.md:219-224, Reconciliación 4 de
/// tasks.md): los tres filtros aditivos de <c>GET /api/articulos</c> (<c>idArea</c>/
/// <c>idCategoria</c>/<c>idMarca</c>) contra Postgres real — cada uno solo, el AND de los cuatro
/// conjuncts (<c>busqueda</c> incluido), y la regresión byte-idéntica sin ningún filtro.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosFiltrosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(int IdTenant, int IdArea, int IdAlicuotaIva, string MailAdmin, string PasswordAdmin)>
        AprovisionarTenantAsync(string nombre)
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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado!.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        return (resultado.IdTenant, area.Id, idAlicuotaIva, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarMarcaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var marca = new Marca { IdTenant = idTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Marcas.Add(marca);
        await db.SaveChangesAsync();

        return marca.Id;
    }

    private async Task<int> SembrarAreaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = idTenant, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return area.Id;
    }

    private async Task<int> SembrarCategoriaAsync(int idTenant, string nombre, int? idCategoriaPadre = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var categoria = new Categoria
        {
            IdTenant = idTenant, Nombre = nombre, Orden = 1, IdCategoriaPadre = idCategoriaPadre,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();

        return categoria.Id;
    }

    private async Task<int> SembrarArticuloAsync(
        int idTenant, string nombre, int idArea, int idAlicuotaIva,
        int? idCategoria = null, int? idMarca = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"{nombre}-{Guid.NewGuid():N}",
            Nombre = nombre,
            IdArea = idArea,
            IdCategoria = idCategoria,
            IdMarca = idMarca,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    // ---- task 2.9: idMarca solo, seed asimétrico -----------------------------------------------

    /// <summary>mutation target 26: borrar el `if (idMarca is { } x)` de `ListarAsync` hace que
    /// este filtro deje de aplicarse — el seed asimétrico (12 de marca A, 28 de otras dos marcas)
    /// asegura que un `ListarAsync` sin el filtro devuelva un total DISTINTO.</summary>
    [Fact]
    public async Task FiltrarPorIdMarcaDevuelveSoloLosArticulosDeEsaMarca()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(FiltrarPorIdMarcaDevuelveSoloLosArticulosDeEsaMarca));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var marcaA = await SembrarMarcaAsync(idTenant, "Marca A");
        var marcaB = await SembrarMarcaAsync(idTenant, "Marca B");

        for (var i = 0; i < 12; i++)
        {
            await SembrarArticuloAsync(idTenant, $"a-marca-a-{i}", idArea, idAlicuotaIva, idMarca: marcaA);
        }

        for (var i = 0; i < 28; i++)
        {
            await SembrarArticuloAsync(idTenant, $"a-marca-b-{i}", idArea, idAlicuotaIva, idMarca: marcaB);
        }

        var listado = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>(
            $"/api/articulos?idMarca={marcaA}&tamanio=50", OpcionesJson);

        Assert.NotNull(listado);
        Assert.Equal(12, listado!.Total);
        Assert.All(listado.Items, a => Assert.Equal(marcaA, a.IdMarca));
    }

    // ---- task 2.7: idArea solo, seed asimétrico ------------------------------------------------

    [Fact]
    public async Task FiltrarPorIdAreaDevuelveSoloLosArticulosDeEsaArea()
    {
        var (idTenant, _, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(FiltrarPorIdAreaDevuelveSoloLosArticulosDeEsaArea));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var areaA = await SembrarAreaAsync(idTenant, "Area A");
        var areaB = await SembrarAreaAsync(idTenant, "Area B");

        for (var i = 0; i < 7; i++)
        {
            await SembrarArticuloAsync(idTenant, $"a-area-a-{i}", areaA, idAlicuotaIva);
        }

        for (var i = 0; i < 19; i++)
        {
            await SembrarArticuloAsync(idTenant, $"a-area-b-{i}", areaB, idAlicuotaIva);
        }

        var listado = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>(
            $"/api/articulos?idArea={areaA}&tamanio=50", OpcionesJson);

        Assert.NotNull(listado);
        Assert.Equal(7, listado!.Total);
        Assert.All(listado.Items, a => Assert.Equal(areaA, a.IdArea));
    }

    // ---- task 2.8: idCategoria en un abuelo devuelve al nieto (fixture de tres niveles) --------

    /// <summary>articulos/spec.md: "idCategoria on a parent returns descendant artículos too" —
    /// Bebidas (abuelo) → Gaseosas (padre) → Cola (nieto, el artículo real vive acá). Sibling
    /// asimétrico (Limpieza) prueba que el filtro no devuelve de más.</summary>
    [Fact]
    public async Task FiltrarPorIdCategoriaEnUnAbueloDevuelveElArticuloDelNieto()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(FiltrarPorIdCategoriaEnUnAbueloDevuelveElArticuloDelNieto));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idBebidas = await SembrarCategoriaAsync(idTenant, "Bebidas");
        var idGaseosas = await SembrarCategoriaAsync(idTenant, "Gaseosas", idBebidas);
        var idCola = await SembrarCategoriaAsync(idTenant, "Cola", idGaseosas);
        var idLimpieza = await SembrarCategoriaAsync(idTenant, "Limpieza");

        var articuloCola = await SembrarArticuloAsync(idTenant, "cola-500ml", idArea, idAlicuotaIva, idCategoria: idCola);
        var articuloDirecto = await SembrarArticuloAsync(idTenant, "gaseosa-generica", idArea, idAlicuotaIva, idCategoria: idGaseosas);
        var articuloLimpieza = await SembrarArticuloAsync(idTenant, "detergente", idArea, idAlicuotaIva, idCategoria: idLimpieza);

        var listado = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>(
            $"/api/articulos?idCategoria={idBebidas}&tamanio=50", OpcionesJson);

        Assert.NotNull(listado);
        Assert.Equal(2, listado!.Total);
        Assert.Contains(listado.Items, a => a.Id == articuloCola);
        Assert.Contains(listado.Items, a => a.Id == articuloDirecto);
        Assert.DoesNotContain(listado.Items, a => a.Id == articuloLimpieza);
    }

    // ---- task 2.10: AND de los cuatro conjuncts ------------------------------------------------

    /// <summary>Reconciliación 4 de tasks.md: pairwise + los cuatro juntos, sobre seeds disjuntos
    /// que se moverían si cualquier filtro se comportara como OR o se ignorara. Mutation target 26
    /// (cada guarda es un conjunct independiente).</summary>
    [Fact]
    public async Task LosCuatroFiltrosComponenComoAnd()
    {
        var (idTenant, _, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LosCuatroFiltrosComponenComoAnd));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var areaA = await SembrarAreaAsync(idTenant, "Area A");
        var areaB = await SembrarAreaAsync(idTenant, "Area B");
        var marcaA = await SembrarMarcaAsync(idTenant, "Marca A");
        var marcaB = await SembrarMarcaAsync(idTenant, "Marca B");
        var categoriaA = await SembrarCategoriaAsync(idTenant, "Categoria A");
        var categoriaB = await SembrarCategoriaAsync(idTenant, "Categoria B");

        // El único artículo que matchea LOS CUATRO conjuncts a la vez (area A, marca A,
        // categoría A, nombre "buscable-and").
        var elegido = await SembrarArticuloAsync(idTenant, "buscable-and", areaA, idAlicuotaIva, categoriaA, marcaA);

        // Comparte exactamente tres de los cuatro criterios cada uno — si el AND se rompiera en
        // OR, cualquiera de estos aparecería también.
        await SembrarArticuloAsync(idTenant, "buscable-and", areaB, idAlicuotaIva, categoriaA, marcaA); // área distinta
        await SembrarArticuloAsync(idTenant, "buscable-and", areaA, idAlicuotaIva, categoriaB, marcaA); // categoría distinta
        await SembrarArticuloAsync(idTenant, "buscable-and", areaA, idAlicuotaIva, categoriaA, marcaB); // marca distinta
        await SembrarArticuloAsync(idTenant, "otro-nombre", areaA, idAlicuotaIva, categoriaA, marcaA);  // búsqueda distinta

        // area + marca (pairwise) — matchea el elegido, el de categoría distinta y el de nombre
        // distinto (ninguno de los dos participa en ESTE par de conjuncts): 3, no 1 ni 4 (el de
        // área distinta SÍ queda afuera).
        var pairwiseAreaMarca = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>(
            $"/api/articulos?idArea={areaA}&idMarca={marcaA}&tamanio=50", OpcionesJson);
        Assert.Equal(3, pairwiseAreaMarca!.Total);

        // Los cuatro juntos: exactamente el elegido.
        var todosJuntos = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>(
            $"/api/articulos?idArea={areaA}&idMarca={marcaA}&idCategoria={categoriaA}&busqueda=buscable-and&tamanio=50",
            OpcionesJson);

        Assert.NotNull(todosJuntos);
        Assert.Equal(1, todosJuntos!.Total);
        Assert.Equal(elegido, Assert.Single(todosJuntos.Items).Id);
    }

    // ---- task 2.11: regresión byte-idéntica sin ningún filtro ----------------------------------

    /// <summary>articulos/spec.md: "Absent filters leave the listing byte-identical" — con los
    /// tres filtros ausentes, el listado (items, orden, total, paging) es idéntico al camino
    /// anterior a esta slice, sobre un seed que se movería si cualquiera de los tres defaulteara a
    /// un valor. Mutation target 27.</summary>
    [Fact]
    public async Task SinFiltrosElListadoQuedaByteIdenticoAlCaminoPrevio()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(SinFiltrosElListadoQuedaByteIdenticoAlCaminoPrevio));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var otraArea = await SembrarAreaAsync(idTenant, "Otra area");
        var marca = await SembrarMarcaAsync(idTenant, "Marca cualquiera");
        var categoria = await SembrarCategoriaAsync(idTenant, "Categoria cualquiera");

        var idB = await SembrarArticuloAsync(idTenant, "Zapallo", otraArea, idAlicuotaIva, categoria, marca);
        var idA = await SembrarArticuloAsync(idTenant, "Arroz", idArea, idAlicuotaIva);

        var listado = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>("/api/articulos", OpcionesJson);

        Assert.NotNull(listado);
        Assert.Equal(2, listado!.Total);
        Assert.Equal(1, listado.Pagina);
        Assert.Equal(25, listado.Tamanio);
        // OrderBy(a => a.Nombre) sigue intacto: "Arroz" antes que "Zapallo".
        Assert.Equal([idA, idB], listado.Items.Select(a => a.Id).ToList());
    }
}
