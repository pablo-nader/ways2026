using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/reportes/ventas/por-articulo</c> (+ <c>/export</c>) punta a punta: cantidades y
/// totales netos por artículo, filtrables por la clasificación ACTUAL (área, grupo, categoría con
/// descendientes) o por artículo. Siembra directa, sin <c>ServicioDeVentas</c> (que exige turno
/// abierto), con fechas fijas a mediodía UTC.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesVentasPorArticuloTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string ContentTypeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateOnly Dia = new(2026, 8, 5);
    private static readonly DateTimeOffset Mediodia = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdCliente, int IdEmpleadoAdmin, int IdTipoTx, int IdTipoNcx, int IdTipoFa, int IdListaPrecio,
        int IdAlicuotaIva, int IdAreaA, int IdAreaB, int IdGrupoA, int IdGrupoB,
        int IdCategoriaPadre, int IdCategoriaHija, int IdCategoriaOtra);

    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? host = null)
    {
        host ??= fixture;
        var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
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
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idListaPrecio = await db.ListasPrecio.Select(l => l.Id).FirstAsync();
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var areaA = new Area { IdTenant = resultado.IdTenant, Nombre = "Area A", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var areaB = new Area { IdTenant = resultado.IdTenant, Nombre = "Area B", Orden = 2, CreatedAt = ahora, UpdatedAt = ahora };
        var grupoA = new Grupo { IdTenant = resultado.IdTenant, Nombre = "Cigarrillos", CreatedAt = ahora, UpdatedAt = ahora };
        var grupoB = new Grupo { IdTenant = resultado.IdTenant, Nombre = "Golosinas", CreatedAt = ahora, UpdatedAt = ahora };
        var catPadre = new Categoria { IdTenant = resultado.IdTenant, Nombre = "Bebidas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var catOtra = new Categoria { IdTenant = resultado.IdTenant, Nombre = "Limpieza", Orden = 2, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.AddRange(areaA, areaB);
        db.Grupos.AddRange(grupoA, grupoB);
        db.Categorias.AddRange(catPadre, catOtra);
        await db.SaveChangesAsync();

        var catHija = new Categoria
        {
            IdTenant = resultado.IdTenant, Nombre = "Gaseosas", Orden = 1, IdCategoriaPadre = catPadre.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Categorias.Add(catHija);
        await db.SaveChangesAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();
        var idFa = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor,
            idCliente, resultado.IdUsuarioAdmin, idTx, idNcx, idFa, idListaPrecio, idAlicuotaIva,
            areaA.Id, areaB.Id, grupoA.Id, grupoB.Id, catPadre.Id, catHija.Id, catOtra.Id);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        HttpClient admin, WebApplicationFactory<Program> host, string nombre, string sufijo, RolConocido rol)
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
        Contexto ctx, string nombre, int? idArea = null, int? idGrupo = null, int? idCategoria = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = idArea ?? ctx.IdAreaA, IdGrupo = idGrupo, IdCategoria = idCategoria,
            IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    /// <summary>Un comprobante con un único ítem; <paramref name="cantidad"/> y
    /// <paramref name="total"/> llevan el signo (negativos en una NCX). Un espejo fiscal lleva
    /// tipo FA, CAE aprobado y <c>IdVentaOrigen</c>.</summary>
    private async Task SembrarLineaAsync(
        Contexto ctx, int idArticulo, decimal cantidad, decimal total, DateTimeOffset? fecha = null,
        bool esNcx = false, EstadoComprobante estado = EstadoComprobante.Emitido, int? idVentaOrigenDeEspejo = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var espejo = idVentaOrigenDeEspejo is not null;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = espejo ? ctx.IdTipoFa : esNcx ? ctx.IdTipoNcx : ctx.IdTipoTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha ?? Mediodia,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            Cae = espejo ? "12345678901234" : null,
            CaeVencimiento = espejo ? new DateOnly(2026, 9, 20) : null,
            ResultadoFiscal = espejo ? ResultadoFiscal.Aprobado : null,
            IdVentaOrigen = idVentaOrigenDeEspejo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = 1, IdArticulo = idArticulo,
            Descripcion = "snapshot", IdArea = ctx.IdAreaA, IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = cantidad,
            PrecioUnitario = total / cantidad, Descuento = 0m, Total = total,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SembrarVentaAsync(Contexto ctx, int idArticulo, decimal cantidad, decimal total)
    {
        await SembrarLineaAsync(ctx, idArticulo, cantidad, total);
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.ComprobantesVenta.OrderByDescending(c => c.Id).Select(c => c.Id).FirstAsync();
    }

    private static string Url(
        int idEmpresa, DateOnly? desde = null, DateOnly? hasta = null, int? idPuntoVenta = null, int? idArea = null,
        int? idGrupo = null, int? idCategoria = null, int? idArticulo = null, string ruta = "")
    {
        var d = desde ?? Dia;
        var h = hasta ?? Dia;
        return $"/api/reportes/ventas/por-articulo{ruta}?idEmpresa={idEmpresa}&desde={d:yyyy-MM-dd}&hasta={h:yyyy-MM-dd}" +
            (idPuntoVenta is { } pv ? $"&idPuntoVenta={pv}" : string.Empty) +
            (idArea is { } a ? $"&idArea={a}" : string.Empty) +
            (idGrupo is { } g ? $"&idGrupo={g}" : string.Empty) +
            (idCategoria is { } c ? $"&idCategoria={c}" : string.Empty) +
            (idArticulo is { } ar ? $"&idArticulo={ar}" : string.Empty);
    }

    private static async Task<VentasPorArticulo> ObtenerAsync(HttpClient cliente, string url)
    {
        var respuesta = await cliente.GetAsync(url);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<VentasPorArticulo>(cuerpo, OpcionesJson)!;
    }

    private async Task DarDeBajaGrupoAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var grupo = await db.Grupos.SingleAsync(g => g.Id == id);
        grupo.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaCategoriaAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var categoria = await db.Categorias.SingleAsync(c => c.Id == id);
        categoria.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaAreaAsync(Contexto ctx, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var area = await db.Areas.SingleAsync(a => a.Id == id);
        area.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    // ---- filtros ------------------------------------------------------------------------------

    [Fact]
    public async Task FiltrarPorGrupoDevuelveSoloLosArticulosDelGrupoConCantidadesNetas()
    {
        var ctx = await PrepararAsync(nameof(FiltrarPorGrupoDevuelveSoloLosArticulosDelGrupoConCantidadesNetas));
        var marlboro = await SembrarArticuloAsync(ctx, "Marlboro", idGrupo: ctx.IdGrupoA);
        var lucky = await SembrarArticuloAsync(ctx, "Lucky", idGrupo: ctx.IdGrupoA);
        var alfajor = await SembrarArticuloAsync(ctx, "Alfajor", idGrupo: ctx.IdGrupoB);
        await SembrarLineaAsync(ctx, marlboro, 10m, 1000m);
        await SembrarLineaAsync(ctx, marlboro, -2m, -200m, esNcx: true);
        await SembrarLineaAsync(ctx, lucky, 3m, 450m);
        await SembrarLineaAsync(ctx, alfajor, 5m, 500m);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idGrupo: ctx.IdGrupoA));

        Assert.Equal(2, reporte.Articulos.Count);
        Assert.Equal(marlboro, reporte.Articulos[0].IdArticulo);
        Assert.Equal(8m, reporte.Articulos[0].Cantidad);
        Assert.Equal(800m, reporte.Articulos[0].Total);
        Assert.Equal("Cigarrillos", reporte.Articulos[0].Grupo);
        Assert.Equal(lucky, reporte.Articulos[1].IdArticulo);
        Assert.Equal(450m, reporte.Articulos[1].Total);
        Assert.Equal(11m, reporte.CantidadTotal);
        Assert.Equal(1250m, reporte.Total);
        Assert.False(string.IsNullOrWhiteSpace(reporte.ZonaHoraria));
    }

    /// <summary>Cláusula bajo prueba: el filtro por grupo del subconjunto de artículos. Mutación
    /// aplicada (quitarlo): este test y <c>FiltrarPorGrupo…</c> fallaron; revertida, pasaron.</summary>
    [Fact]
    public async Task LosFiltrosCombinadosSeIntersectan()
    {
        var ctx = await PrepararAsync(nameof(LosFiltrosCombinadosSeIntersectan));
        var aA = await SembrarArticuloAsync(ctx, "Art area A grupo A", ctx.IdAreaA, ctx.IdGrupoA);
        var bA = await SembrarArticuloAsync(ctx, "Art area B grupo A", ctx.IdAreaB, ctx.IdGrupoA);
        var aB = await SembrarArticuloAsync(ctx, "Art area A grupo B", ctx.IdAreaA, ctx.IdGrupoB);
        foreach (var id in new[] { aA, bA, aB })
        {
            await SembrarLineaAsync(ctx, id, 1m, 100m);
        }

        var soloGrupo = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idGrupo: ctx.IdGrupoA));
        var interseccion = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idArea: ctx.IdAreaA, idGrupo: ctx.IdGrupoA));
        var porArticuloYOtroGrupo = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idGrupo: ctx.IdGrupoB, idArticulo: aA));

        Assert.Equal(2, soloGrupo.Articulos.Count);
        Assert.Equal(aA, Assert.Single(interseccion.Articulos).IdArticulo);
        Assert.Empty(porArticuloYOtroGrupo.Articulos);
    }

    /// <summary>Cláusula bajo prueba: la expansión de descendientes de <c>idCategoria</c> en
    /// <c>ObtenerVentasPorArticuloAsync</c>. Mutación aplicada (reemplazarla por la categoría
    /// pedida sola): este test falló; revertida, pasó.</summary>
    [Fact]
    public async Task LaCategoriaIncluyeLosArticulosDeSusDescendientes()
    {
        var ctx = await PrepararAsync(nameof(LaCategoriaIncluyeLosArticulosDeSusDescendientes));
        var enPadre = await SembrarArticuloAsync(ctx, "Agua", idCategoria: ctx.IdCategoriaPadre);
        var enHija = await SembrarArticuloAsync(ctx, "Cola", idCategoria: ctx.IdCategoriaHija);
        var enOtra = await SembrarArticuloAsync(ctx, "Lavandina", idCategoria: ctx.IdCategoriaOtra);
        var sinCategoria = await SembrarArticuloAsync(ctx, "Sin categoria");
        foreach (var id in new[] { enPadre, enHija, enOtra, sinCategoria })
        {
            await SembrarLineaAsync(ctx, id, 1m, 100m);
        }

        var delPadre = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idCategoria: ctx.IdCategoriaPadre));
        var soloHija = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idCategoria: ctx.IdCategoriaHija));

        Assert.Equal(
            new[] { enHija, enPadre }.Order(),
            delPadre.Articulos.Select(a => a.IdArticulo).Order());
        Assert.Equal(enHija, Assert.Single(soloHija.Articulos).IdArticulo);
    }

    [Fact]
    public async Task FiltrarPorArticuloDevuelveSoloEseArticulo()
    {
        var ctx = await PrepararAsync(nameof(FiltrarPorArticuloDevuelveSoloEseArticulo));
        var uno = await SembrarArticuloAsync(ctx, "Uno");
        var dos = await SembrarArticuloAsync(ctx, "Dos");
        await SembrarLineaAsync(ctx, uno, 1m, 100m);
        await SembrarLineaAsync(ctx, dos, 1m, 200m);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idArticulo: dos));

        var fila = Assert.Single(reporte.Articulos);
        Assert.Equal(dos, fila.IdArticulo);
        Assert.Equal("Dos", fila.Descripcion);
    }

    [Fact]
    public async Task UnIdDeFiltroDeOtroTenantNoDevuelveNingunaFila()
    {
        var ctxA = await PrepararAsync(nameof(UnIdDeFiltroDeOtroTenantNoDevuelveNingunaFila) + "-A");
        var ctxB = await PrepararAsync(nameof(UnIdDeFiltroDeOtroTenantNoDevuelveNingunaFila) + "-B");
        var articulo = await SembrarArticuloAsync(ctxA, "Propio", idGrupo: ctxA.IdGrupoA, idCategoria: ctxA.IdCategoriaPadre);
        await SembrarLineaAsync(ctxA, articulo, 1m, 100m);

        var porGrupoAjeno = await ObtenerAsync(ctxA.Admin, Url(ctxA.IdEmpresa, idGrupo: ctxB.IdGrupoA));
        var porAreaAjena = await ObtenerAsync(ctxA.Admin, Url(ctxA.IdEmpresa, idArea: ctxB.IdAreaA));
        var porCategoriaAjena = await ObtenerAsync(ctxA.Admin, Url(ctxA.IdEmpresa, idCategoria: ctxB.IdCategoriaPadre));

        Assert.Empty(porGrupoAjeno.Articulos);
        Assert.Empty(porAreaAjena.Articulos);
        Assert.Empty(porCategoriaAjena.Articulos);
    }

    // ---- qué cuenta como venta ----------------------------------------------------------------

    [Fact]
    public async Task UnComprobanteAnuladoNuncaApareceEnElReporte()
    {
        var ctx = await PrepararAsync(nameof(UnComprobanteAnuladoNuncaApareceEnElReporte));
        var articulo = await SembrarArticuloAsync(ctx, "Anulado");
        await SembrarLineaAsync(ctx, articulo, 10m, 999_999m, estado: EstadoComprobante.Anulado);
        await SembrarLineaAsync(ctx, articulo, 3m, 250m);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa));

        var fila = Assert.Single(reporte.Articulos);
        Assert.Equal(3m, fila.Cantidad);
        Assert.Equal(250m, fila.Total);
    }

    [Fact]
    public async Task LaVentaYSuEspejoFiscalCuentanUnaSolaVez()
    {
        var ctx = await PrepararAsync(nameof(LaVentaYSuEspejoFiscalCuentanUnaSolaVez));
        var articulo = await SembrarArticuloAsync(ctx, "Con espejo");
        var idVenta = await SembrarVentaAsync(ctx, articulo, 2m, 200m);
        await SembrarLineaAsync(ctx, articulo, 2m, 200m, idVentaOrigenDeEspejo: idVenta);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa));

        var fila = Assert.Single(reporte.Articulos);
        Assert.Equal(2m, fila.Cantidad);
        Assert.Equal(200m, fila.Total);
    }

    [Fact]
    public async Task ElRangoDeFechasSeCortaPorLaZonaHoraria()
    {
        var ctx = await PrepararAsync(nameof(ElRangoDeFechasSeCortaPorLaZonaHoraria));
        var articulo = await SembrarArticuloAsync(ctx, "Noche");
        // 2026-08-05T22:30-03:00 == 2026-08-06T01:30Z.
        await SembrarLineaAsync(ctx, articulo, 1m, 500m, fecha: new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero));

        var configuracionArt = await ctx.Admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + ctx.IdEmpresa,
            new ParametroAlta("zona_horaria", "\"America/Argentina/Buenos_Aires\"", null));
        Assert.Equal(HttpStatusCode.OK, configuracionArt.StatusCode);

        var enArt = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, desde: Dia, hasta: Dia));
        var enArtDiaSiguiente = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, desde: Dia.AddDays(1), hasta: Dia.AddDays(1)));

        var configuracionUtc = await ctx.Admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + ctx.IdEmpresa, new ParametroAlta("zona_horaria", "\"UTC\"", null));
        Assert.Equal(HttpStatusCode.OK, configuracionUtc.StatusCode);

        var enUtc = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, desde: Dia, hasta: Dia));
        var enUtcDiaSiguiente = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, desde: Dia.AddDays(1), hasta: Dia.AddDays(1)));

        Assert.Equal(500m, Assert.Single(enArt.Articulos).Total);
        Assert.Empty(enArtDiaSiguiente.Articulos);
        Assert.Empty(enUtc.Articulos);
        Assert.Equal(500m, Assert.Single(enUtcDiaSiguiente.Articulos).Total);
    }

    [Fact]
    public async Task LasFilasVanOrdenadasPorTotalDescendenteYLuegoPorDescripcion()
    {
        var ctx = await PrepararAsync(nameof(LasFilasVanOrdenadasPorTotalDescendenteYLuegoPorDescripcion));
        var b = await SembrarArticuloAsync(ctx, "B empate");
        var a = await SembrarArticuloAsync(ctx, "A empate");
        var grande = await SembrarArticuloAsync(ctx, "Z grande");
        await SembrarLineaAsync(ctx, b, 1m, 100m);
        await SembrarLineaAsync(ctx, a, 1m, 100m);
        await SembrarLineaAsync(ctx, grande, 1m, 900m);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa));

        Assert.Equal(new[] { grande, a, b }, reporte.Articulos.Select(f => f.IdArticulo));
    }

    // ---- FK colgante: se trata como null ---------------------------------------------------------

    [Fact]
    public async Task UnGrupoDadoDeBajaSeEtiquetaNullYNoMatcheaElFiltro()
    {
        var ctx = await PrepararAsync(nameof(UnGrupoDadoDeBajaSeEtiquetaNullYNoMatcheaElFiltro));
        var articulo = await SembrarArticuloAsync(ctx, "Colgante", idGrupo: ctx.IdGrupoA);
        await SembrarLineaAsync(ctx, articulo, 2m, 200m);
        await DarDeBajaGrupoAsync(ctx, ctx.IdGrupoA);

        var sinFiltro = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa));
        var porGrupoBorrado = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idGrupo: ctx.IdGrupoA));

        var fila = Assert.Single(sinFiltro.Articulos);
        Assert.Null(fila.Grupo);
        Assert.Equal(200m, fila.Total);
        Assert.Empty(porGrupoBorrado.Articulos);
    }

    [Fact]
    public async Task UnaCategoriaYUnAreaDadasDeBajaSeEtiquetanNullYNoMatchean()
    {
        var ctx = await PrepararAsync(nameof(UnaCategoriaYUnAreaDadasDeBajaSeEtiquetanNullYNoMatchean));
        var articulo = await SembrarArticuloAsync(ctx, "Colgante", ctx.IdAreaB, idCategoria: ctx.IdCategoriaOtra);
        await SembrarLineaAsync(ctx, articulo, 1m, 100m);
        await DarDeBajaCategoriaAsync(ctx, ctx.IdCategoriaOtra);
        await DarDeBajaAreaAsync(ctx, ctx.IdAreaB);

        var sinFiltro = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa));
        var porCategoria = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idCategoria: ctx.IdCategoriaOtra));
        var porArea = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idArea: ctx.IdAreaB));

        var fila = Assert.Single(sinFiltro.Articulos);
        Assert.Null(fila.Categoria);
        Assert.Null(fila.Area);
        Assert.Empty(porCategoria.Articulos);
        Assert.Empty(porArea.Articulos);
    }

    [Fact]
    public async Task UnArticuloSinGrupoNiCategoriaConservaSuFilaConEtiquetasNull()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSinGrupoNiCategoriaConservaSuFilaConEtiquetasNull));
        var articulo = await SembrarArticuloAsync(ctx, "Pelado");
        await SembrarLineaAsync(ctx, articulo, 1m, 100m);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa));

        var fila = Assert.Single(reporte.Articulos);
        Assert.Null(fila.Grupo);
        Assert.Null(fila.Categoria);
        Assert.Equal("Area A", fila.Area);
        Assert.StartsWith("Pelado-", fila.Codigo);
    }

    // ---- validaciones y roles -----------------------------------------------------------------

    [Fact]
    public async Task UnaEmpresaDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxA.Admin.GetAsync(Url(ctxB.IdEmpresa));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnPuntoDeVentaQueNoEsDeLaEmpresaDevuelve400()
    {
        var ctxA = await PrepararAsync(nameof(UnPuntoDeVentaQueNoEsDeLaEmpresaDevuelve400) + "-A");
        var ctxB = await PrepararAsync(nameof(UnPuntoDeVentaQueNoEsDeLaEmpresaDevuelve400) + "-B");

        var respuesta = await ctxA.Admin.GetAsync(Url(ctxA.IdEmpresa, idPuntoVenta: ctxB.IdPuntoVenta));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDesdeMayorQueHastaDevuelve400()
    {
        var ctx = await PrepararAsync(nameof(UnDesdeMayorQueHastaDevuelve400));

        var respuesta = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, desde: Dia.AddDays(1), hasta: Dia));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rango_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnVendedorEsRechazadoYUnSupervisorLee()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoYUnSupervisorLee));

        var vendedor = await ctx.Vendedor.GetAsync(Url(ctx.IdEmpresa));
        var vendedorExport = await ctx.Vendedor.GetAsync(Url(ctx.IdEmpresa, ruta: "/export") + "&formato=xlsx");
        var supervisor = await ctx.Supervisor.GetAsync(Url(ctx.IdEmpresa));
        var supervisorExport = await ctx.Supervisor.GetAsync(Url(ctx.IdEmpresa, ruta: "/export") + "&formato=xlsx");

        Assert.Equal(HttpStatusCode.Forbidden, vendedor.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, vendedorExport.StatusCode);
        Assert.Equal(HttpStatusCode.OK, supervisor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, supervisorExport.StatusCode);
    }

    // ---- export --------------------------------------------------------------------------------

    [Fact]
    public async Task ElExportEsIgualAlEndpointJsonYRespetaLosFiltros()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEndpointJsonYRespetaLosFiltros));
        var marlboro = await SembrarArticuloAsync(ctx, "Marlboro", idGrupo: ctx.IdGrupoA);
        var alfajor = await SembrarArticuloAsync(ctx, "Alfajor", idGrupo: ctx.IdGrupoB);
        await SembrarLineaAsync(ctx, marlboro, 4m, 400m);
        await SembrarLineaAsync(ctx, alfajor, 1m, 50m);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, idGrupo: ctx.IdGrupoA));
        var fila = Assert.Single(reporte.Articulos);

        var respuesta = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, idGrupo: ctx.IdGrupoA, ruta: "/export") + "&formato=xlsx");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);
        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        Assert.Equal(
            ["Artículo", "Código", "Descripción", "Área", "Grupo", "Categoría", "Cantidad", "Total"],
            Enumerable.Range(1, 8).Select(c => hoja.Cell(6, c).GetString()));
        Assert.Equal(fila.IdArticulo, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Codigo, hoja.Cell(7, 2).GetString());
        Assert.Equal(fila.Descripcion, hoja.Cell(7, 3).GetString());
        Assert.Equal(fila.Area, hoja.Cell(7, 4).GetString());
        Assert.Equal(fila.Grupo, hoja.Cell(7, 5).GetString());
        Assert.Equal(string.Empty, hoja.Cell(7, 6).GetString());
        Assert.Equal(fila.Cantidad, hoja.Cell(7, 7).GetValue<decimal>());
        Assert.Equal(fila.Total, hoja.Cell(7, 8).GetValue<decimal>());
        Assert.True(hoja.Row(8).IsEmpty());
    }

    [Fact]
    public async Task UnFormatoNoSoportadoRechazaElExport()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaElExport));

        var respuesta = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, ruta: "/export") + "&formato=pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnExportQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 0)));

        var ctx = await PrepararAsync(nameof(UnExportQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var articulo = await SembrarArticuloAsync(ctx, "Tope");
        await SembrarLineaAsync(ctx, articulo, 1m, 100m);

        var respuesta = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, ruta: "/export") + "&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 1 filas", problema.GetProperty("title").GetString());
    }
}
