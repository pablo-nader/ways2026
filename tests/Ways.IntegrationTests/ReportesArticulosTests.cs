using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
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

    private static string ConstruirQuery(
        int? idArea = null, int? idCategoria = null, bool? sinCategoria = null, int? idMarca = null,
        bool? sinMarca = null, int? idGrupo = null, bool? sinGrupo = null, int? idProveedor = null,
        bool? sinProveedor = null, bool? soloIncompletos = null, bool? activo = null, int? pagina = null,
        int? tamanio = null)
    {
        var partes = new List<string>();
        if (idArea is { } a) partes.Add($"idArea={a}");
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

    // ---- soloIncompletos: OR de las cuatro ausencias -------------------------------------------------

    /// <summary>Nombra la cláusula: el <c>||</c> de <c>ConstruirQueryDeArticulosAsync</c> — un
    /// artículo al que le falta UNA sola de las cuatro clasificaciones (acá, proveedor) ya tiene
    /// que aparecer. Si la cláusula fuera un <c>&amp;&amp;</c> (las cuatro ausentes a la vez), este
    /// artículo (con categoría/marca/grupo asignados) no aparecería y el test fallaría.</summary>
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
    /// dentro de <c>/articulos/export</c>. Dos filas con TODOS los campos distintos — una totalmente
    /// clasificada, otra sin ninguna clasificación — con las OCHO columnas comparadas contra el
    /// workbook, más el header exacto (regla 8: el header es lo que ata cada celda a su columna).</summary>
    [Fact]
    public async Task ElExportEsIgualAlEndpointJsonParaTodasLasColumnas()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEndpointJsonParaTodasLasColumnas));
        await SembrarArticuloAsync(
            ctx, "Aceite de girasol 900ml", idArea: ctx.IdAreaA, idCategoria: ctx.IdCategoriaPadre,
            idMarca: ctx.IdMarcaA, idGrupo: ctx.IdGrupoA, idProveedorHabitual: ctx.IdProveedorConFantasia, activo: true);
        await SembrarArticuloAsync(ctx, "Fideos guiseros 500g", idArea: ctx.IdAreaB, activo: false);

        var pagina = await ListarAsync(ctx.Admin);
        Assert.Equal(2, pagina.Items.Count);

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
            Assert.Equal(esperado.Area, fila.Cell(3).GetString());
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
}
