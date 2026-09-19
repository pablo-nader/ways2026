using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/reportes/articulos</c> + su sibling <c>/export</c> (reporte de completitud de
/// catálogo, owner: "artículos sin proveedor, sin marca, sin categoría, sin grupo... para verlos
/// de un vistazo"). Tenant-wide (sin idEmpresa/idPuntoVenta: los artículos no tienen esa columna,
/// doc 10 §3) — a diferencia de todo el resto de <c>/api/reportes/*</c>. Cubre cada cláusula de
/// filtro por separado, cada variante <c>sin*</c>, los 400 de exclusión mutua, la expansión de
/// categoría por descendientes, <c>soloIncompletos</c> como OR, <c>activo</c>, exclusión de baja
/// lógica, la regla de etiqueta de proveedor, paginado, aislamiento de tenant, el tope de
/// exportación y la igualdad JSON↔XLSX fila por fila y columna por columna (mutation-proof-tests
/// regla 6/8/9).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesArticulosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string ContentTypeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record Contexto(
        int IdTenant, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdAreaA, int IdAreaB, int IdCategoriaPadre, int IdCategoriaHija, int IdCategoriaOtra,
        int IdMarcaA, int IdMarcaB, int IdGrupoA, int IdGrupoB,
        int IdProveedorConFantasia, int IdProveedorSinFantasia, int IdProveedorFantasiaEnBlanco,
        int IdAlicuotaIva);

    private async Task<Contexto> PrepararAsync(string nombre, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null)
    {
        var host = factory ?? fixture;
        var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = host.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, host, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, host, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var areaA = new Area { IdTenant = resultado.IdTenant, Nombre = "Almacén", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var areaB = new Area { IdTenant = resultado.IdTenant, Nombre = "Verdulería", Orden = 2, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.AddRange(areaA, areaB);
        await db.SaveChangesAsync();

        var categoriaPadre = new Categoria { IdTenant = resultado.IdTenant, Nombre = "Bebidas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Categorias.Add(categoriaPadre);
        await db.SaveChangesAsync();

        var categoriaHija = new Categoria
        {
            IdTenant = resultado.IdTenant, Nombre = "Gaseosas", Orden = 1, IdCategoriaPadre = categoriaPadre.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var categoriaOtra = new Categoria { IdTenant = resultado.IdTenant, Nombre = "Limpieza", Orden = 2, CreatedAt = ahora, UpdatedAt = ahora };
        db.Categorias.AddRange(categoriaHija, categoriaOtra);
        await db.SaveChangesAsync();

        var marcaA = new Marca { IdTenant = resultado.IdTenant, Nombre = "Marca A", CreatedAt = ahora, UpdatedAt = ahora };
        var marcaB = new Marca { IdTenant = resultado.IdTenant, Nombre = "Marca B", CreatedAt = ahora, UpdatedAt = ahora };
        db.Marcas.AddRange(marcaA, marcaB);
        await db.SaveChangesAsync();

        var grupoA = new Grupo { IdTenant = resultado.IdTenant, Nombre = "Grupo A", CreatedAt = ahora, UpdatedAt = ahora };
        var grupoB = new Grupo { IdTenant = resultado.IdTenant, Nombre = "Grupo B", CreatedAt = ahora, UpdatedAt = ahora };
        db.Grupos.AddRange(grupoA, grupoB);
        await db.SaveChangesAsync();

        var proveedorConFantasia = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = "Distribuidora Uno SA", NombreFantasia = "DistriUno",
            IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedorSinFantasia = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = "Distribuidora Dos SRL", NombreFantasia = null,
            IdCondicionFiscal = proveedorConFantasia.IdCondicionFiscal, CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedorFantasiaEnBlanco = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = "Distribuidora Tres SA", NombreFantasia = "   ",
            IdCondicionFiscal = proveedorConFantasia.IdCondicionFiscal, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedorConFantasia, proveedorSinFantasia, proveedorFantasiaEnBlanco);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, admin, supervisor, vendedor,
            areaA.Id, areaB.Id, categoriaPadre.Id, categoriaHija.Id, categoriaOtra.Id,
            marcaA.Id, marcaB.Id, grupoA.Id, grupoB.Id,
            proveedorConFantasia.Id, proveedorSinFantasia.Id, proveedorFantasiaEnBlanco.Id, idAlicuotaIva);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        HttpClient admin, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = host.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarArticuloAsync(
        Contexto ctx, string nombre, int? idArea = null, int? idCategoria = null, int? idMarca = null,
        int? idGrupo = null, int? idProveedorHabitual = null, bool activo = true, bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant,
            CodigoInterno = $"{nombre}-{Guid.NewGuid():N}",
            Nombre = nombre,
            IdArea = idArea ?? ctx.IdAreaA,
            IdCategoria = idCategoria,
            IdMarca = idMarca,
            IdGrupo = idGrupo,
            IdProveedorHabitual = idProveedorHabitual,
            IdAlicuotaIva = ctx.IdAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            Activo = activo,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminado ? ahora : null
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    /// <summary>Área adicional creada dentro del propio test (fuera de las dos de
    /// <see cref="Contexto"/>) — usada cuando el test necesita dar de baja un área SIN afectar los
    /// artículos que ya referencian <c>IdAreaA</c>/<c>IdAreaB</c> de otros casos.</summary>
    private async Task<int> CrearAreaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 99, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();
        return area.Id;
    }

    // ---- baja lógica estampada: la guarda de referencias rechaza la baja por endpoint -----------

    private async Task DarDeBajaAreaAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var area = await db.Areas.SingleAsync(a => a.Id == id);
        area.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaCategoriaAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var categoria = await db.Categorias.SingleAsync(c => c.Id == id);
        categoria.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaMarcaAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var marca = await db.Marcas.SingleAsync(m => m.Id == id);
        marca.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaGrupoAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var grupo = await db.Grupos.SingleAsync(g => g.Id == id);
        grupo.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaProveedorAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var proveedor = await db.Proveedores.SingleAsync(p => p.Id == id);
        proveedor.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static string ConstruirQuery(
        int? idArea = null, bool? sinArea = null, int? idCategoria = null, bool? sinCategoria = null,
        int? idMarca = null, bool? sinMarca = null, int? idGrupo = null, bool? sinGrupo = null,
        int? idProveedor = null, bool? sinProveedor = null, bool? soloIncompletos = null, bool? activo = null,
        int? pagina = null, int? tamanio = null)
    {
        var partes = new List<string>();
        if (idArea is { } a) partes.Add($"idArea={a}");
        if (sinArea is { } sa) partes.Add($"sinArea={sa}");
        if (idCategoria is { } c) partes.Add($"idCategoria={c}");
        if (sinCategoria is { } sc) partes.Add($"sinCategoria={sc}");
        if (idMarca is { } m) partes.Add($"idMarca={m}");
        if (sinMarca is { } sm) partes.Add($"sinMarca={sm}");
        if (idGrupo is { } g) partes.Add($"idGrupo={g}");
        if (sinGrupo is { } sg) partes.Add($"sinGrupo={sg}");
        if (idProveedor is { } p) partes.Add($"idProveedor={p}");
        if (sinProveedor is { } sp) partes.Add($"sinProveedor={sp}");
        if (soloIncompletos is { } si) partes.Add($"soloIncompletos={si}");
        if (activo is { } ac) partes.Add($"activo={ac}");
        if (pagina is { } pg) partes.Add($"pagina={pg}");
        if (tamanio is { } t) partes.Add($"tamanio={t}");
        return partes.Count == 0 ? string.Empty : $"?{string.Join('&', partes)}";
    }

    private static async Task<PaginaDe<ArticuloDeReporte>> ListarAsync(HttpClient cliente, string query = "")
    {
        var respuesta = await cliente.GetAsync($"/api/reportes/articulos{query}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PaginaDe<ArticuloDeReporte>>(cuerpo, OpcionesJson)!;
    }

    // ---- filtro por área --------------------------------------------------------------------------

    /// <summary>Nombra la cláusula bajo prueba: <c>a.IdArea == idAreaValor</c> en
    /// <c>ServicioDeReportesDeArticulos.ConstruirQueryDeArticulosAsync</c>.</summary>
    [Fact]
    public async Task UnArticuloSeFiltraPorArea()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSeFiltraPorArea));
        await SembrarArticuloAsync(ctx, "articulo-area-a", idArea: ctx.IdAreaA);
        await SembrarArticuloAsync(ctx, "articulo-area-b", idArea: ctx.IdAreaB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(idArea: ctx.IdAreaA));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("articulo-area-a", fila.Nombre);
        Assert.Equal("Almacén", fila.Area);
    }

    [Fact]
    public async Task IdAreaYSinAreaJuntosDevuelven400()
    {
        var ctx = await PrepararAsync(nameof(IdAreaYSinAreaJuntosDevuelven400));

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/articulos{ConstruirQuery(idArea: ctx.IdAreaA, sinArea: true)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filtro_incompatible", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Nombra la cláusula: <c>!idsDeAreasVisibles.Contains(a.IdArea)</c> en
    /// <c>ConstruirQueryDeArticulosAsync</c>. <c>IdArea</c> es NOT NULL en <c>articulos</c>
    /// (doc 10 §3) — la única forma de que un artículo quede "sin área" es que el área a la que
    /// apunta haya sido dada de baja lógica SIN guarda de uso (bug de fondo de este reporte,
    /// judgment-day ronda 1). Con la cláusula reducida a <c>a.IdArea == null</c> (comparación
    /// siempre falsa sobre una columna NOT NULL) este test falla: 0 resultados en vez de 1.</summary>
    [Fact]
    public async Task SinAreaDevuelveSoloArticulosConAreaDadaDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SinAreaDevuelveSoloArticulosConAreaDadaDeBaja));
        await SembrarArticuloAsync(ctx, "con-area-vigente", idArea: ctx.IdAreaA);
        await SembrarArticuloAsync(ctx, "con-area-dada-de-baja", idArea: ctx.IdAreaB);
        await DarDeBajaAreaAsync(ctx, ctx.IdAreaB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinArea: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("con-area-dada-de-baja", fila.Nombre);
        Assert.Null(fila.Area);
    }

    /// <summary>dangling-fk-read-models regla 2: nombra la cláusula
    /// <c>idsDeAreasVisibles.Contains(idAreaValor)</c> que decide si <c>idArea</c> matchea algo.
    /// El endpoint DELETE rechaza el área referenciada (area_en_uso), así que la baja se estampa
    /// sobre la fila real (regla 5 del mismo skill): deja el FK del artículo intacto y el área invisible. Sin la guarda de
    /// visibilidad (código previo al fix), <c>idArea=&lt;id de baja&gt;</c> devolvía la misma fila
    /// que <c>sinArea=true</c>.</summary>
    [Fact]
    public async Task IdAreaConUnIdDadoDeBajaNoMatcheaNingunaFila()
    {
        var ctx = await PrepararAsync(nameof(IdAreaConUnIdDadoDeBajaNoMatcheaNingunaFila));
        await SembrarArticuloAsync(ctx, "con-area-vigente", idArea: ctx.IdAreaA);
        await SembrarArticuloAsync(ctx, "con-area-de-baja", idArea: ctx.IdAreaB);

        var baja = await ctx.Admin.DeleteAsync($"/api/catalogos/areas/{ctx.IdAreaB}");
        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        await DarDeBajaAreaAsync(ctx, ctx.IdAreaB);

        var porIdDeBaja = await ListarAsync(ctx.Admin, ConstruirQuery(idArea: ctx.IdAreaB));
        Assert.Empty(porIdDeBaja.Items);

        var porSinArea = await ListarAsync(ctx.Admin, ConstruirQuery(sinArea: true));
        Assert.Equal("con-area-de-baja", Assert.Single(porSinArea.Items).Nombre);

        var porIdVigente = await ListarAsync(ctx.Admin, ConstruirQuery(idArea: ctx.IdAreaA));
        Assert.Equal("con-area-vigente", Assert.Single(porIdVigente.Items).Nombre);
    }

    // ---- categoría: expansión de descendientes -----------------------------------------------------

    /// <summary>Nombra la cláusula: <c>descendientes.Contains(a.IdCategoria.Value)</c> reusando
    /// <c>CadenaDeCategorias.ConstruirDescendientes</c> — filtrar por la categoría PADRE tiene que
    /// alcanzar también un artículo tageado con la categoría HIJA.</summary>
    [Fact]
    public async Task IdCategoriaEnUnPadreIncluyeArticulosDeLaCategoriaHija()
    {
        var ctx = await PrepararAsync(nameof(IdCategoriaEnUnPadreIncluyeArticulosDeLaCategoriaHija));
        await SembrarArticuloAsync(ctx, "articulo-padre", idCategoria: ctx.IdCategoriaPadre);
        await SembrarArticuloAsync(ctx, "articulo-hija", idCategoria: ctx.IdCategoriaHija);
        await SembrarArticuloAsync(ctx, "articulo-otra-categoria", idCategoria: ctx.IdCategoriaOtra);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(idCategoria: ctx.IdCategoriaPadre));

        Assert.Equal(2, pagina.Items.Count);
        Assert.Contains(pagina.Items, f => f.Nombre == "articulo-padre");
        Assert.Contains(pagina.Items, f => f.Nombre == "articulo-hija");
        Assert.DoesNotContain(pagina.Items, f => f.Nombre == "articulo-otra-categoria");
    }

    [Fact]
    public async Task SinCategoriaDevuelveSoloArticulosSinCategoriaAsignada()
    {
        var ctx = await PrepararAsync(nameof(SinCategoriaDevuelveSoloArticulosSinCategoriaAsignada));
        await SembrarArticuloAsync(ctx, "con-categoria", idCategoria: ctx.IdCategoriaPadre);
        await SembrarArticuloAsync(ctx, "sin-categoria");

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinCategoria: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("sin-categoria", fila.Nombre);
        Assert.Null(fila.Categoria);
    }

    [Fact]
    public async Task IdCategoriaYSinCategoriaJuntosDevuelven400()
    {
        var ctx = await PrepararAsync(nameof(IdCategoriaYSinCategoriaJuntosDevuelven400));

        var respuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/articulos{ConstruirQuery(idCategoria: ctx.IdCategoriaPadre, sinCategoria: true)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filtro_incompatible", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Nombra la cláusula: el segundo disyunto de <c>a.IdCategoria == null ||
    /// !idsDeCategoriasVisibles.Contains(...)</c>. Sin él (solo el chequeo de <c>null</c>, como
    /// antes del fix), un artículo con <c>IdCategoria</c> apuntando a una categoría dada de baja
    /// lógica SIN guarda de uso no aparece bajo <c>sinCategoria=true</c> pese a que la proyección
    /// ya lo muestra como "Sin asignar" (LEFT JOIN + filtro BajaLogica) — inconsistencia de fondo
    /// de este reporte (judgment-day ronda 1).</summary>
    [Fact]
    public async Task SinCategoriaTambienIncluyeUnArticuloConCategoriaDadaDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SinCategoriaTambienIncluyeUnArticuloConCategoriaDadaDeBaja));
        await SembrarArticuloAsync(ctx, "con-categoria-vigente", idCategoria: ctx.IdCategoriaPadre);
        await SembrarArticuloAsync(ctx, "con-categoria-dada-de-baja", idCategoria: ctx.IdCategoriaOtra);
        await DarDeBajaCategoriaAsync(ctx, ctx.IdCategoriaOtra);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinCategoria: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("con-categoria-dada-de-baja", fila.Nombre);
        Assert.Null(fila.Categoria);
    }

    /// <summary>Mismo objetivo de mutación que
    /// <c>IdAreaConUnIdDadoDeBajaNoMatcheaNingunaFila</c>, aplicado a categoría:
    /// nombra <c>idsDeCategoriasVisibles.Contains(idCategoriaValor)</c>, la guarda de raíz que se
    /// evalúa ANTES de expandir descendientes.</summary>
    [Fact]
    public async Task IdCategoriaConUnIdDadoDeBajaNoMatcheaNingunaFila()
    {
        var ctx = await PrepararAsync(nameof(IdCategoriaConUnIdDadoDeBajaNoMatcheaNingunaFila));
        await SembrarArticuloAsync(ctx, "con-categoria-vigente", idCategoria: ctx.IdCategoriaPadre);
        await SembrarArticuloAsync(ctx, "con-categoria-de-baja", idCategoria: ctx.IdCategoriaOtra);

        var baja = await ctx.Admin.DeleteAsync($"/api/catalogos/categorias/{ctx.IdCategoriaOtra}");
        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        await DarDeBajaCategoriaAsync(ctx, ctx.IdCategoriaOtra);

        var porIdDeBaja = await ListarAsync(ctx.Admin, ConstruirQuery(idCategoria: ctx.IdCategoriaOtra));
        Assert.Empty(porIdDeBaja.Items);

        var porSinCategoria = await ListarAsync(ctx.Admin, ConstruirQuery(sinCategoria: true));
        Assert.Equal("con-categoria-de-baja", Assert.Single(porSinCategoria.Items).Nombre);

        var porIdVigente = await ListarAsync(ctx.Admin, ConstruirQuery(idCategoria: ctx.IdCategoriaPadre));
        Assert.Equal("con-categoria-vigente", Assert.Single(porIdVigente.Items).Nombre);
    }

    // ---- marca ------------------------------------------------------------------------------------

    /// <summary>Nombra la cláusula: <c>a.IdMarca == idMarcaValor</c>.</summary>
    [Fact]
    public async Task UnArticuloSeFiltraPorMarca()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSeFiltraPorMarca));
        await SembrarArticuloAsync(ctx, "articulo-marca-a", idMarca: ctx.IdMarcaA);
        await SembrarArticuloAsync(ctx, "articulo-marca-b", idMarca: ctx.IdMarcaB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(idMarca: ctx.IdMarcaA));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("articulo-marca-a", fila.Nombre);
        Assert.Equal("Marca A", fila.Marca);
    }

    [Fact]
    public async Task SinMarcaDevuelveSoloArticulosSinMarcaAsignada()
    {
        var ctx = await PrepararAsync(nameof(SinMarcaDevuelveSoloArticulosSinMarcaAsignada));
        await SembrarArticuloAsync(ctx, "con-marca", idMarca: ctx.IdMarcaA);
        await SembrarArticuloAsync(ctx, "sin-marca");

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinMarca: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("sin-marca", fila.Nombre);
        Assert.Null(fila.Marca);
    }

    [Fact]
    public async Task IdMarcaYSinMarcaJuntosDevuelven400()
    {
        var ctx = await PrepararAsync(nameof(IdMarcaYSinMarcaJuntosDevuelven400));

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/articulos{ConstruirQuery(idMarca: ctx.IdMarcaA, sinMarca: true)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filtro_incompatible", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mismo objetivo de mutación que <c>SinCategoriaTambienIncluyeUnArticuloConCategoriaDadaDeBaja</c>,
    /// aplicado a marca.</summary>
    [Fact]
    public async Task SinMarcaTambienIncluyeUnArticuloConMarcaDadaDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SinMarcaTambienIncluyeUnArticuloConMarcaDadaDeBaja));
        await SembrarArticuloAsync(ctx, "con-marca-vigente", idMarca: ctx.IdMarcaA);
        await SembrarArticuloAsync(ctx, "con-marca-dada-de-baja", idMarca: ctx.IdMarcaB);
        await DarDeBajaMarcaAsync(ctx, ctx.IdMarcaB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinMarca: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("con-marca-dada-de-baja", fila.Nombre);
        Assert.Null(fila.Marca);
    }

    /// <summary>Mismo objetivo de mutación que
    /// <c>IdAreaConUnIdDadoDeBajaNoMatcheaNingunaFila</c>, aplicado a marca.</summary>
    [Fact]
    public async Task IdMarcaConUnIdDadoDeBajaNoMatcheaNingunaFila()
    {
        var ctx = await PrepararAsync(nameof(IdMarcaConUnIdDadoDeBajaNoMatcheaNingunaFila));
        await SembrarArticuloAsync(ctx, "con-marca-vigente", idMarca: ctx.IdMarcaA);
        await SembrarArticuloAsync(ctx, "con-marca-de-baja", idMarca: ctx.IdMarcaB);

        var baja = await ctx.Admin.DeleteAsync($"/api/catalogos/marcas/{ctx.IdMarcaB}");
        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        await DarDeBajaMarcaAsync(ctx, ctx.IdMarcaB);

        var porIdDeBaja = await ListarAsync(ctx.Admin, ConstruirQuery(idMarca: ctx.IdMarcaB));
        Assert.Empty(porIdDeBaja.Items);

        var porSinMarca = await ListarAsync(ctx.Admin, ConstruirQuery(sinMarca: true));
        Assert.Equal("con-marca-de-baja", Assert.Single(porSinMarca.Items).Nombre);

        var porIdVigente = await ListarAsync(ctx.Admin, ConstruirQuery(idMarca: ctx.IdMarcaA));
        Assert.Equal("con-marca-vigente", Assert.Single(porIdVigente.Items).Nombre);
    }

    // ---- grupo --------------------------------------------------------------------------------------

    /// <summary>Nombra la cláusula: <c>a.IdGrupo == idGrupoValor</c>.</summary>
    [Fact]
    public async Task UnArticuloSeFiltraPorGrupo()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSeFiltraPorGrupo));
        await SembrarArticuloAsync(ctx, "articulo-grupo-a", idGrupo: ctx.IdGrupoA);
        await SembrarArticuloAsync(ctx, "articulo-grupo-b", idGrupo: ctx.IdGrupoB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(idGrupo: ctx.IdGrupoA));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("articulo-grupo-a", fila.Nombre);
        Assert.Equal("Grupo A", fila.Grupo);
    }

    [Fact]
    public async Task SinGrupoDevuelveSoloArticulosSinGrupoAsignado()
    {
        var ctx = await PrepararAsync(nameof(SinGrupoDevuelveSoloArticulosSinGrupoAsignado));
        await SembrarArticuloAsync(ctx, "con-grupo", idGrupo: ctx.IdGrupoA);
        await SembrarArticuloAsync(ctx, "sin-grupo");

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinGrupo: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("sin-grupo", fila.Nombre);
        Assert.Null(fila.Grupo);
    }

    [Fact]
    public async Task IdGrupoYSinGrupoJuntosDevuelven400()
    {
        var ctx = await PrepararAsync(nameof(IdGrupoYSinGrupoJuntosDevuelven400));

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/articulos{ConstruirQuery(idGrupo: ctx.IdGrupoA, sinGrupo: true)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filtro_incompatible", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mismo objetivo de mutación que <c>SinCategoriaTambienIncluyeUnArticuloConCategoriaDadaDeBaja</c>,
    /// aplicado a grupo.</summary>
    [Fact]
    public async Task SinGrupoTambienIncluyeUnArticuloConGrupoDadoDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SinGrupoTambienIncluyeUnArticuloConGrupoDadoDeBaja));
        await SembrarArticuloAsync(ctx, "con-grupo-vigente", idGrupo: ctx.IdGrupoA);
        await SembrarArticuloAsync(ctx, "con-grupo-dado-de-baja", idGrupo: ctx.IdGrupoB);
        await DarDeBajaGrupoAsync(ctx, ctx.IdGrupoB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinGrupo: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("con-grupo-dado-de-baja", fila.Nombre);
        Assert.Null(fila.Grupo);
    }

    /// <summary>Mismo objetivo de mutación que
    /// <c>IdAreaConUnIdDadoDeBajaNoMatcheaNingunaFila</c>, aplicado a grupo.</summary>
    [Fact]
    public async Task IdGrupoConUnIdDadoDeBajaNoMatcheaNingunaFila()
    {
        var ctx = await PrepararAsync(nameof(IdGrupoConUnIdDadoDeBajaNoMatcheaNingunaFila));
        await SembrarArticuloAsync(ctx, "con-grupo-vigente", idGrupo: ctx.IdGrupoA);
        await SembrarArticuloAsync(ctx, "con-grupo-de-baja", idGrupo: ctx.IdGrupoB);

        var baja = await ctx.Admin.DeleteAsync($"/api/catalogos/grupos/{ctx.IdGrupoB}");
        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        await DarDeBajaGrupoAsync(ctx, ctx.IdGrupoB);

        var porIdDeBaja = await ListarAsync(ctx.Admin, ConstruirQuery(idGrupo: ctx.IdGrupoB));
        Assert.Empty(porIdDeBaja.Items);

        var porSinGrupo = await ListarAsync(ctx.Admin, ConstruirQuery(sinGrupo: true));
        Assert.Equal("con-grupo-de-baja", Assert.Single(porSinGrupo.Items).Nombre);

        var porIdVigente = await ListarAsync(ctx.Admin, ConstruirQuery(idGrupo: ctx.IdGrupoA));
        Assert.Equal("con-grupo-vigente", Assert.Single(porIdVigente.Items).Nombre);
    }

    // ---- proveedor habitual -------------------------------------------------------------------------

    /// <summary>Nombra la cláusula: <c>a.IdProveedorHabitual == idProveedorValor</c>.</summary>
    [Fact]
    public async Task UnArticuloSeFiltraPorProveedorHabitual()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSeFiltraPorProveedorHabitual));
        await SembrarArticuloAsync(ctx, "articulo-prov-uno", idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(ctx, "articulo-prov-dos", idProveedorHabitual: ctx.IdProveedorSinFantasia);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(idProveedor: ctx.IdProveedorConFantasia));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("articulo-prov-uno", fila.Nombre);
        Assert.Equal("DistriUno", fila.Proveedor);
    }

    [Fact]
    public async Task SinProveedorDevuelveSoloArticulosSinProveedorHabitual()
    {
        var ctx = await PrepararAsync(nameof(SinProveedorDevuelveSoloArticulosSinProveedorHabitual));
        await SembrarArticuloAsync(ctx, "con-proveedor", idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(ctx, "sin-proveedor");

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinProveedor: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("sin-proveedor", fila.Nombre);
        Assert.Null(fila.Proveedor);
    }

    [Fact]
    public async Task IdProveedorYSinProveedorJuntosDevuelven400()
    {
        var ctx = await PrepararAsync(nameof(IdProveedorYSinProveedorJuntosDevuelven400));

        var respuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/articulos{ConstruirQuery(idProveedor: ctx.IdProveedorConFantasia, sinProveedor: true)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filtro_incompatible", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mismo objetivo de mutación que <c>SinCategoriaTambienIncluyeUnArticuloConCategoriaDadaDeBaja</c>,
    /// aplicado a proveedor habitual.</summary>
    [Fact]
    public async Task SinProveedorTambienIncluyeUnArticuloConProveedorDadoDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SinProveedorTambienIncluyeUnArticuloConProveedorDadoDeBaja));
        await SembrarArticuloAsync(ctx, "con-proveedor-vigente", idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(ctx, "con-proveedor-dado-de-baja", idProveedorHabitual: ctx.IdProveedorSinFantasia);
        await DarDeBajaProveedorAsync(ctx, ctx.IdProveedorSinFantasia);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(sinProveedor: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("con-proveedor-dado-de-baja", fila.Nombre);
        Assert.Null(fila.Proveedor);
    }

    /// <summary>Mismo objetivo de mutación que
    /// <c>IdAreaConUnIdDadoDeBajaNoMatcheaNingunaFila</c>, aplicado a proveedor
    /// habitual — <c>DELETE /api/proveedores/{id}</c>, no el catálogo compartido.</summary>
    [Fact]
    public async Task IdProveedorConUnIdDadoDeBajaNoMatcheaNingunaFila()
    {
        var ctx = await PrepararAsync(nameof(IdProveedorConUnIdDadoDeBajaNoMatcheaNingunaFila));
        await SembrarArticuloAsync(ctx, "con-proveedor-vigente", idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(ctx, "con-proveedor-de-baja", idProveedorHabitual: ctx.IdProveedorSinFantasia);

        var baja = await ctx.Admin.DeleteAsync($"/api/proveedores/{ctx.IdProveedorSinFantasia}");
        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        await DarDeBajaProveedorAsync(ctx, ctx.IdProveedorSinFantasia);

        var porIdDeBaja = await ListarAsync(ctx.Admin, ConstruirQuery(idProveedor: ctx.IdProveedorSinFantasia));
        Assert.Empty(porIdDeBaja.Items);

        var porSinProveedor = await ListarAsync(ctx.Admin, ConstruirQuery(sinProveedor: true));
        Assert.Equal("con-proveedor-de-baja", Assert.Single(porSinProveedor.Items).Nombre);

        var porIdVigente = await ListarAsync(ctx.Admin, ConstruirQuery(idProveedor: ctx.IdProveedorConFantasia));
        Assert.Equal("con-proveedor-vigente", Assert.Single(porIdVigente.Items).Nombre);
    }

    // ---- etiqueta de proveedor: NombreFantasia > RazonSocial -----------------------------------------

    [Fact]
    public async Task ProveedorSinNombreDeFantasiaUsaRazonSocial()
    {
        var ctx = await PrepararAsync(nameof(ProveedorSinNombreDeFantasiaUsaRazonSocial));
        await SembrarArticuloAsync(ctx, "articulo-sin-fantasia", idProveedorHabitual: ctx.IdProveedorSinFantasia);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(idProveedor: ctx.IdProveedorSinFantasia));

        Assert.Equal("Distribuidora Dos SRL", Assert.Single(pagina.Items).Proveedor);
    }

    /// <summary>El nombre de fantasía en blanco (solo espacios) tiene que caer a razón social —
    /// <c>string.IsNullOrWhiteSpace</c>, no <c>IsNullOrEmpty</c>: una cadena de espacios no es
    /// nula ni vacía, pero tampoco es una etiqueta usable.</summary>
    [Fact]
    public async Task ProveedorConNombreDeFantasiaEnBlancoUsaRazonSocial()
    {
        var ctx = await PrepararAsync(nameof(ProveedorConNombreDeFantasiaEnBlancoUsaRazonSocial));
        await SembrarArticuloAsync(ctx, "articulo-fantasia-blanca", idProveedorHabitual: ctx.IdProveedorFantasiaEnBlanco);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(idProveedor: ctx.IdProveedorFantasiaEnBlanco));

        Assert.Equal("Distribuidora Tres SA", Assert.Single(pagina.Items).Proveedor);
    }

    // ---- soloIncompletos: OR de las cinco ausencias -------------------------------------------------
    //
    // mutation-proof-tests regla 3 (enumerar los conjuncts): CADA disyunto del OR de
    // ConstruirQueryDeArticulosAsync necesita su propio test — cubrir uno (acá, proveedor) no dice
    // nada de sus vecinos. Cada test siembra un artículo COMPLETO (las cinco clasificaciones
    // asignadas y vigentes) que NUNCA debe aparecer, más un artículo al que le falta SOLO la
    // clasificación bajo prueba. Categoría/marca/grupo/proveedor tienen dos variantes (FK null Y FK
    // colgante hacia una fila dada de baja lógica); área, al ser NOT NULL en articulos, solo tiene
    // la variante de FK colgante.

    /// <summary>Nombra el disyunto de proveedor (FK null). Si la cláusula fuera un
    /// <c>&amp;&amp;</c> (las cinco ausentes a la vez), este artículo (con área/categoría/marca/
    /// grupo asignados) no aparecería y el test fallaría.</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloAlQueLeFaltaUnaSolaClasificacion()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloAlQueLeFaltaUnaSolaClasificacion));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-sin-proveedor", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-sin-proveedor", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de proveedor (FK colgante hacia un proveedor dado de baja
    /// lógica) — la variante que el fix de judgment-day ronda 1 agrega.</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloConProveedorDadoDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloConProveedorDadoDeBaja));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-proveedor-de-baja", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA,
            idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorSinFantasia);
        await DarDeBajaProveedorAsync(ctx, ctx.IdProveedorSinFantasia);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-proveedor-de-baja", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de área (única variante posible: FK colgante — <c>IdArea</c> es
    /// NOT NULL). Sin este test, borrar <c>!idsDeAreasVisibles.Contains(a.IdArea) ||</c> entero del
    /// OR sobrevive.</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloElArea()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloElArea));
        await SembrarArticuloAsync(
            ctx, "completo", idArea: ctx.IdAreaA, idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA,
            idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-area-de-baja", idArea: ctx.IdAreaB, idCategoria: ctx.IdCategoriaPadre,
            idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia);
        await DarDeBajaAreaAsync(ctx, ctx.IdAreaB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-area-de-baja", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de categoría (FK null). Sin este test, borrar
    /// <c>a.IdCategoria == null ||</c> del OR sobrevive (hallazgo de judgment-day ronda 1: 26/26
    /// verdes con ese disyunto borrado).</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloLaCategoria()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloLaCategoria));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-sin-categoria", idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-sin-categoria", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de categoría (FK colgante hacia una categoría dada de baja
    /// lógica).</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloConCategoriaDadaDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloConCategoriaDadaDeBaja));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-categoria-de-baja", idCategoria: ctx.IdCategoriaOtra, idMarca: ctx.IdMarcaA,
            idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia);
        await DarDeBajaCategoriaAsync(ctx, ctx.IdCategoriaOtra);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-categoria-de-baja", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de marca (FK null).</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloLaMarca()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloLaMarca));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-sin-marca", idCategoria: ctx.IdCategoriaPadre, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-sin-marca", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de marca (FK colgante hacia una marca dada de baja lógica).</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloConMarcaDadaDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloConMarcaDadaDeBaja));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-marca-de-baja", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaB,
            idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia);
        await DarDeBajaMarcaAsync(ctx, ctx.IdMarcaB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-marca-de-baja", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de grupo (FK null).</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloElGrupo()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloAlQueLeFaltaSoloElGrupo));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-sin-grupo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-sin-grupo", fila.Nombre);
    }

    /// <summary>Nombra el disyunto de grupo (FK colgante hacia un grupo dado de baja lógica).</summary>
    [Fact]
    public async Task SoloIncompletosIncluyeUnArticuloConGrupoDadoDeBaja()
    {
        var ctx = await PrepararAsync(nameof(SoloIncompletosIncluyeUnArticuloConGrupoDadoDeBaja));
        await SembrarArticuloAsync(
            ctx, "completo", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA,
            idProveedorHabitual: ctx.IdProveedorConFantasia);
        await SembrarArticuloAsync(
            ctx, "incompleto-grupo-de-baja", idCategoria: ctx.IdCategoriaPadre, idMarca: ctx.IdMarcaA,
            idGrupo: ctx.IdGrupoB, idProveedorHabitual: ctx.IdProveedorConFantasia);
        await DarDeBajaGrupoAsync(ctx, ctx.IdGrupoB);

        var pagina = await ListarAsync(ctx.Admin, ConstruirQuery(soloIncompletos: true));

        var fila = Assert.Single(pagina.Items);
        Assert.Equal("incompleto-grupo-de-baja", fila.Nombre);
    }

    // ---- activo ---------------------------------------------------------------------------------------

    /// <summary>Nombra la cláusula: <c>a.Activo == activoValor</c>. Sin <c>activo</c> en la query,
    /// las dos filas (activa e inactiva) tienen que aparecer.</summary>
    [Fact]
    public async Task ActivoFiltraPorEstadoYOmitidoTraeAmbos()
    {
        var ctx = await PrepararAsync(nameof(ActivoFiltraPorEstadoYOmitidoTraeAmbos));
        await SembrarArticuloAsync(ctx, "articulo-activo", activo: true);
        await SembrarArticuloAsync(ctx, "articulo-inactivo", activo: false);

        var soloActivos = await ListarAsync(ctx.Admin, ConstruirQuery(activo: true));
        Assert.Equal("articulo-activo", Assert.Single(soloActivos.Items).Nombre);

        var soloInactivos = await ListarAsync(ctx.Admin, ConstruirQuery(activo: false));
        Assert.Equal("articulo-inactivo", Assert.Single(soloInactivos.Items).Nombre);

        var ambos = await ListarAsync(ctx.Admin);
        Assert.Equal(2, ambos.Total);
    }

    // ---- baja lógica excluida ---------------------------------------------------------------------------

    [Fact]
    public async Task UnArticuloConBajaLogicaNuncaAparece()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloConBajaLogicaNuncaAparece));
        await SembrarArticuloAsync(ctx, "articulo-eliminado", eliminado: true);
        await SembrarArticuloAsync(ctx, "articulo-vigente");

        var pagina = await ListarAsync(ctx.Admin);

        Assert.Equal("articulo-vigente", Assert.Single(pagina.Items).Nombre);
    }

    // ---- paginado -------------------------------------------------------------------------------------

    [Fact]
    public async Task ElPaginadoRespetaTamanioYReportaElTotalReal()
    {
        var ctx = await PrepararAsync(nameof(ElPaginadoRespetaTamanioYReportaElTotalReal));
        await SembrarArticuloAsync(ctx, "articulo-1");
        await SembrarArticuloAsync(ctx, "articulo-2");
        await SembrarArticuloAsync(ctx, "articulo-3");

        var primeraPagina = await ListarAsync(ctx.Admin, ConstruirQuery(pagina: 1, tamanio: 2));
        Assert.Equal(3, primeraPagina.Total);
        Assert.Equal(2, primeraPagina.Items.Count);

        var segundaPagina = await ListarAsync(ctx.Admin, ConstruirQuery(pagina: 2, tamanio: 2));
        Assert.Equal(3, segundaPagina.Total);
        Assert.Single(segundaPagina.Items);
    }

    // ---- aislamiento de tenant --------------------------------------------------------------------------

    [Fact]
    public async Task UnArticuloDeOtroTenantNuncaAparece()
    {
        var ctxA = await PrepararAsync(nameof(UnArticuloDeOtroTenantNuncaAparece) + "-A");
        var ctxB = await PrepararAsync(nameof(UnArticuloDeOtroTenantNuncaAparece) + "-B");
        await SembrarArticuloAsync(ctxB, "articulo-tenant-b");

        var pagina = await ListarAsync(ctxA.Admin);

        Assert.DoesNotContain(pagina.Items, f => f.Nombre == "articulo-tenant-b");
    }

    // ---- roles ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelReporteDeArticulos()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelReporteDeArticulos));

        var respuesta = await ctx.Vendedor.GetAsync("/api/reportes/articulos");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorLeeElReporteDeArticulos()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeElReporteDeArticulos));

        var respuesta = await ctx.Supervisor.GetAsync("/api/reportes/articulos");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // ---- export: igualdad JSON↔XLSX fila por fila y columna por columna -----------------------------------

    /// <summary>Nombra el objetivo de mutación (mutation-proof-tests regla 6/8): el call site de
    /// <c>ExportacionDeReportes.De(IReadOnlyList&lt;ArticuloDeReporte&gt;, ContextoDeExportacion)</c>
    /// dentro de <c>/articulos/export</c>. Tres filas con TODOS los campos distintos — una
    /// totalmente clasificada, otra sin ninguna clasificación, y una con el área dada de baja
    /// lógica (judgment-day ronda 2: ninguna fila de este fixture ejercía el área <c>null</c>,
    /// dejando <c>Área</c> como la única columna comparada con un <c>Assert.Equal</c> no
    /// null-safe) — con las OCHO columnas comparadas contra el workbook, más el header exacto
    /// (regla 8: el header es lo que ata cada celda a su columna).</summary>
    [Fact]
    public async Task ElExportEsIgualAlEndpointJsonParaTodasLasColumnas()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEndpointJsonParaTodasLasColumnas));
        await SembrarArticuloAsync(
            ctx, "Aceite de girasol 900ml", idArea: ctx.IdAreaA, idCategoria: ctx.IdCategoriaPadre,
            idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia, activo: true);
        await SembrarArticuloAsync(ctx, "Fideos guiseros 500g", idArea: ctx.IdAreaB, activo: false);

        var idAreaDeBaja = await CrearAreaAsync(ctx, "Freezer");
        await SembrarArticuloAsync(ctx, "Yerba mate 500g", idArea: idAreaDeBaja, activo: true);
        await DarDeBajaAreaAsync(ctx, idAreaDeBaja);

        var pagina = await ListarAsync(ctx.Admin);
        Assert.Equal(3, pagina.Items.Count);

        var exportRespuesta = await ctx.Admin.GetAsync("/api/reportes/articulos/export?formato=xlsx");
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        const int filaDeEncabezados = 6;
        Assert.Equal(
            ["Código", "Nombre", "Área", "Categoría", "Marca", "Grupo", "Proveedor", "Activo"],
            Enumerable.Range(1, 8).Select(c => hoja.Cell(filaDeEncabezados, c).GetString()));

        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < pagina.Items.Count; i++)
        {
            var esperado = pagina.Items[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(esperado.CodigoInterno, fila.Cell(1).GetString());
            Assert.Equal(esperado.Nombre, fila.Cell(2).GetString());
            AssertCeldaTextoNullable(fila.Cell(3), esperado.Area);
            AssertCeldaTextoNullable(fila.Cell(4), esperado.Categoria);
            AssertCeldaTextoNullable(fila.Cell(5), esperado.Marca);
            AssertCeldaTextoNullable(fila.Cell(6), esperado.Grupo);
            AssertCeldaTextoNullable(fila.Cell(7), esperado.Proveedor);
            Assert.Equal(esperado.Activo ? "Sí" : "No", fila.Cell(8).GetString());
        }

        // Sin fila de totales (un catálogo de artículos no suma nada) — la fila siguiente a la
        // última fila de datos tiene que estar vacía.
        Assert.True(hoja.Row(primeraFilaDeDatos + pagina.Items.Count).IsEmpty());
    }

    private static void AssertCeldaTextoNullable(IXLCell celda, string? esperado)
    {
        if (esperado is null)
        {
            Assert.True(celda.Value.IsBlank);
            return;
        }

        Assert.Equal(esperado, celda.GetString());
    }

    // ---- export: reenvío de cada filtro (mutation-proof-tests) --------------------------------------

    /// <summary>Nombra el objetivo de mutación: el reenvío de CADA parámetro de filtro desde el
    /// handler de <c>/articulos/export</c> hacia <c>ListarArticulosParaExportacionAsync</c>
    /// (<c>ReportesEndpoints.cs</c>) — un handler que ignorara un parámetro y lo reemplazara por su
    /// default (p.ej. <c>sinArea=false</c> hardcodeado, ignorando la query string) sobrevivía a
    /// toda la suite, porque ningún test comparaba el export FILTRADO contra el listado FILTRADO
    /// con la misma query string (judgment-day ronda 2). Fixture discriminante de 6 artículos donde
    /// cada uno de los doce parámetros angosta el conjunto sin vaciarlo ni devolverlo completo, y el
    /// export de cada filtro se compara contra el listado del MISMO filtro, código por código y en
    /// el mismo orden (mismo <c>OrderBy(a =&gt; a.Nombre).ThenBy(a =&gt; a.Id)</c> de ambos
    /// lados).</summary>
    [Fact]
    public async Task CadaFiltroDelExportCoincideConElListadoYAngostaElConjunto()
    {
        var ctx = await PrepararAsync(nameof(CadaFiltroDelExportCoincideConElListadoYAngostaElConjunto));

        await SembrarArticuloAsync(
            ctx, "articulo-completo-1", idArea: ctx.IdAreaA, idCategoria: ctx.IdCategoriaPadre,
            idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia, activo: true);
        await SembrarArticuloAsync(
            ctx, "articulo-completo-2", idArea: ctx.IdAreaA, idCategoria: ctx.IdCategoriaHija,
            idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia, activo: true);
        await SembrarArticuloAsync(
            ctx, "articulo-otra-area", idArea: ctx.IdAreaB, idCategoria: ctx.IdCategoriaOtra,
            idMarca: ctx.IdMarcaB, idGrupo: ctx.IdGrupoB, idProveedorHabitual: ctx.IdProveedorSinFantasia, activo: true);
        await SembrarArticuloAsync(ctx, "articulo-incompleto", idArea: ctx.IdAreaA, activo: true);

        var idAreaDeBaja = await CrearAreaAsync(ctx, "Depósito");
        await SembrarArticuloAsync(
            ctx, "articulo-area-de-baja", idArea: idAreaDeBaja, idCategoria: ctx.IdCategoriaPadre,
            idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia, activo: true);
        await DarDeBajaAreaAsync(ctx, idAreaDeBaja);

        await SembrarArticuloAsync(
            ctx, "articulo-inactivo", idArea: ctx.IdAreaA, idCategoria: ctx.IdCategoriaPadre,
            idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia, activo: false);

        var sinFiltro = await ListarAsync(ctx.Admin);
        Assert.Equal(6, sinFiltro.Total);

        var casos = new (string Nombre, string Query)[]
        {
            ("idArea", ConstruirQuery(idArea: ctx.IdAreaA)),
            ("sinArea", ConstruirQuery(sinArea: true)),
            ("idCategoria", ConstruirQuery(idCategoria: ctx.IdCategoriaPadre)),
            ("sinCategoria", ConstruirQuery(sinCategoria: true)),
            ("idMarca", ConstruirQuery(idMarca: ctx.IdMarcaA)),
            ("sinMarca", ConstruirQuery(sinMarca: true)),
            ("idGrupo", ConstruirQuery(idGrupo: ctx.IdGrupoA)),
            ("sinGrupo", ConstruirQuery(sinGrupo: true)),
            ("idProveedor", ConstruirQuery(idProveedor: ctx.IdProveedorConFantasia)),
            ("sinProveedor", ConstruirQuery(sinProveedor: true)),
            ("soloIncompletos", ConstruirQuery(soloIncompletos: true)),
            ("activo", ConstruirQuery(activo: true)),
        };

        foreach (var (nombre, query) in casos)
        {
            var pagina = await ListarAsync(ctx.Admin, query);
            Assert.True(
                pagina.Items.Count < sinFiltro.Total,
                $"{nombre}: no angostó el conjunto ({pagina.Items.Count} de {sinFiltro.Total}).");

            var exportRespuesta = await ctx.Admin.GetAsync($"/api/reportes/articulos/export{query}&formato=xlsx");
            var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
            Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, $"{nombre}: {cuerpoError}");

            using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
            var codigosExport = LeerCodigosDelExport(libro.Worksheets.First());
            var codigosEsperados = pagina.Items.Select(f => f.CodigoInterno).ToList();

            Assert.True(
                codigosExport.SequenceEqual(codigosEsperados),
                $"{nombre}: export [{string.Join(',', codigosExport)}] != listado [{string.Join(',', codigosEsperados)}].");
        }
    }

    private static List<string> LeerCodigosDelExport(IXLWorksheet hoja)
    {
        const int primeraFilaDeDatos = 7;
        var codigos = new List<string>();
        var fila = primeraFilaDeDatos;
        while (!hoja.Row(fila).IsEmpty())
        {
            codigos.Add(hoja.Cell(fila, 1).GetString());
            fila++;
        }

        return codigos;
    }

    // ---- export: formato no soportado ------------------------------------------------------------------

    [Fact]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeArticulos()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeArticulos));

        var respuesta = await ctx.Admin.GetAsync("/api/reportes/articulos/export?formato=pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- export: tope de filas — rechazo con la cantidad real, no tope+1 ------------------------------

    /// <summary>mutation-proof-tests regla 9a: siembra <c>tope + 2</c> (no <c>tope + 1</c>) — con
    /// <c>tope + 1</c> sembrado, <c>Take(tope + 1)</c> devuelve exactamente esa cantidad y el
    /// segundo <c>GuardaDeTope.Exigir</c> "de casualidad" coincide con el primero, dejando pasar un
    /// mutante que borra el PRIMER <c>Exigir</c> (el que corre sobre el <c>COUNT(*)</c> real, antes
    /// de materializar filas).</summary>
    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<Ways.Application.Exportacion.OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        for (var i = 0; i < 5; i++)
        {
            await SembrarArticuloAsync(ctx, $"articulo-tope-{i}");
        }

        var respuesta = await ctx.Admin.GetAsync("/api/reportes/articulos/export?formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 5 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>mutation-proof-tests regla 9c: discrimina el ÚNICO <c>GuardaDeTope.Exigir</c> del
    /// lado del ÉXITO — sin este test, mutar <c>Exigir(cantidad, tope)</c> a
    /// <c>Exigir(cantidad, tope - 1)</c> sobrevive (el test de arriba solo cubre el rechazo por
    /// ENCIMA del tope).</summary>
    [Fact]
    public async Task UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<Ways.Application.Exportacion.OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta), factoryBajo);
        for (var i = 0; i < 3; i++)
        {
            await SembrarArticuloAsync(ctx, $"articulo-exacto-{i}");
        }

        var respuesta = await ctx.Admin.GetAsync("/api/reportes/articulos/export?formato=xlsx");
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < 3; i++)
        {
            Assert.False(hoja.Row(primeraFilaDeDatos + i).IsEmpty());
        }
        Assert.True(hoja.Row(primeraFilaDeDatos + 3).IsEmpty());
    }

    // ---- export: backstop de carrera del `+1` (mutation-proof-tests) -----------------------------

    /// <summary>
    /// Simula la carrera que el <c>+1</c> de <c>.Take(topeDeFilas + 1)</c> existe para atrapar —
    /// mismo patrón de rendezvous que <c>VentasListadoExportTests.
    /// UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion</c>, aplicado acá a
    /// <c>articulos</c>: un <c>COUNT(*)</c> que ve <c>tope</c> filas (pasa el primer
    /// <see cref="GuardaDeTope.Exigir"/>) seguido de una fila insertada ANTES de la lectura
    /// <c>.Take(tope + 1)</c>. El <c>DbCommandInterceptor</c> intercepta la SEGUNDA sentencia que
    /// toca <c>articulos</c> (la primera es el <c>COUNT(*)</c>) e inserta la fila extra justo antes
    /// de dejarla correr. Sin el SEGUNDO <c>GuardaDeTope.Exigir</c> (el que corre sobre
    /// <c>items.Count</c>, después de materializar), esta prueba pasaría de rechazar (400) a
    /// aceptar (200) con un archivo de 4 filas por encima del tope de 3 — evidencia registrada en
    /// el resumen de apply.
    /// </summary>
    [Fact]
    public async Task UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacionDeArticulos()
    {
        var gate = new SemaphoreSlim(0, 1);
        Contexto? ctxRef = null;

        var interceptor = new InterceptorDeCarreraDeExportacionDeArticulos(async () =>
        {
            if (ctxRef is null)
            {
                return;
            }

            await SembrarArticuloAsync(ctxRef, "articulo-carrera-extra");
            gate.Release();
        });

        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.Configure<Ways.Application.Exportacion.OpcionesDeExportacion>(o => o.TopeDeFilas = 3);
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor));
            }));

        var ctx = await PrepararAsync(nameof(UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacionDeArticulos), factoryBajo);
        ctxRef = ctx;

        for (var i = 0; i < 3; i++)
        {
            await SembrarArticuloAsync(ctx, $"articulo-carrera-{i}");
        }

        var respuesta = await ctx.Admin.GetAsync("/api/reportes/articulos/export?formato=xlsx");

        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)), "El interceptor de carrera nunca insertó la fila extra.");
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 4 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Retiene la SEGUNDA sentencia que toca <c>articulos</c> (la lectura
    /// <c>.Take(tope + 1)</c> de <c>ProyectarArticulosDeReporteAsync</c> — la primera es el
    /// <c>COUNT(*)</c> de <c>ConstruirQueryDeArticulosAsync</c>) e inyecta <paramref
    /// name="alSegundaConsulta"/> antes de dejarla correr. Cubre tanto <c>ReaderExecutingAsync</c>
    /// como <c>ScalarExecutingAsync</c>: si <c>CountAsync</c> se traduce a un escalar en vez de un
    /// reader, el contador compartido sigue contando en orden.</summary>
    private sealed class InterceptorDeCarreraDeExportacionDeArticulos(Func<Task> alSegundaConsulta) : DbCommandInterceptor
    {
        private int _coincidencias;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await ConsiderarAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await ConsiderarAsync(command);
            return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task ConsiderarAsync(DbCommand command)
        {
            if (!command.CommandText.Contains("articulos", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _coincidencias) == 2)
            {
                await alSegundaConsulta();
            }
        }
    }

    // ---- orden estable: Nombre, luego Id como desempate --------------------------------------------

    /// <summary>Nombra la cláusula: <c>.ThenBy(a => a.Id)</c> del <c>OrderBy</c> que decide QUÉ
    /// filas caen en cada página en <c>ListarArticulosAsync</c> (<c>query.OrderBy(a =>
    /// a.Nombre).ThenBy(a => a.Id).Skip(...).Take(...)</c>). Confound descartado (mutation-proof-
    /// tests regla 3): pedir las 60 filas empatadas en UNA sola página no discrimina nada — el
    /// <c>orderby a.Nombre, a.Id</c> propio de <c>ProyectarArticulosDeReporteAsync</c> reordena
    /// igual el resultado ya elegido, enmascarando cualquier desempate faltante en el paginado
    /// (confirmado: la mutación sobrevive con <c>tamanio=100</c>, sin truncar). El test se
    /// reubica DEBAJO de ese confound: dos páginas de 30 sobre 60 filas empatadas, de manera que
    /// el desempate del PAGINADO decide qué 30 ids caen en cada una — el reorden de la proyección
    /// ya no puede disimular una partición Skip/Take incorrecta. Confirmado corriendo la mutación
    /// (quitar <c>.ThenBy(a => a.Id)</c> del paginado): este test pasa de FALLAR (páginas con ids
    /// mezclados) a pasar al revertir.</summary>
    [Fact]
    public async Task ElOrdenEsPorNombreYLuegoPorIdComoDesempate()
    {
        var ctx = await PrepararAsync(nameof(ElOrdenEsPorNombreYLuegoPorIdComoDesempate));

        var ids = new List<int>();
        for (var i = 0; i < 60; i++)
        {
            ids.Add(await SembrarArticuloAsync(ctx, "Empate"));
        }

        var pagina1 = await ListarAsync(ctx.Admin, ConstruirQuery(pagina: 1, tamanio: 30));
        var pagina2 = await ListarAsync(ctx.Admin, ConstruirQuery(pagina: 2, tamanio: 30));

        Assert.Equal(60, pagina1.Total);
        Assert.Equal(ids.Take(30).ToList(), pagina1.Items.Select(f => f.Id).ToList());
        Assert.Equal(ids.Skip(30).Take(30).ToList(), pagina2.Items.Select(f => f.Id).ToList());
    }
}
