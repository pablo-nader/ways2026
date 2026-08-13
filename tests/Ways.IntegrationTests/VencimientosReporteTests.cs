using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 13: <c>GET /api/reportes/stock/vencimientos</c> y su
/// <c>/resumen</c> — clasificación de cuatro estados (spec lotes-y-vencimientos: "Vencimientos
/// Report Resolves 'Hoy' In The Punto De Venta's Own Zona Horaria, With An Export Sibling"),
/// exclusión de saldo cero, y el 403 del gate. El export sibling (equality fila-por-fila, cap +
/// 403) vive en <see cref="VencimientosExportTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VencimientosReporteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Vendedor);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? factory = null)
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

        var vendedor = await CrearYLoguearAsync(admin, host, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area vencimientos", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, admin, vendedor);
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

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    /// <summary>Siembra directa de <c>lotes</c> + <c>stock_lotes</c> (bypass de los tres
    /// escritores reales — esta slice no toca la recepción/venta) — mismo criterio que
    /// <c>ExistenciasExportTests.SembrarStockAsync</c> sembrando <c>stock</c> directo.</summary>
    private async Task<int> SembrarLoteAsync(
        Contexto ctx, int idArticulo, DateOnly? fechaVencimiento, decimal cantidad,
        bool esSinIdentificar = false, string? codigo = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var lote = new Lote
        {
            IdTenant = ctx.IdTenant,
            IdArticulo = idArticulo,
            Codigo = codigo ?? (esSinIdentificar ? ReglaDeLotes.CodigoSinIdentificar : fechaVencimiento!.Value.ToString("yyyy-MM-dd")),
            FechaVencimiento = fechaVencimiento,
            EsSinIdentificar = esSinIdentificar,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();

        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = lote.Id, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();

        return lote.Id;
    }

    private static Task<HttpResponseMessage> LlamarReporteAsync(HttpClient cliente, int idPuntoVenta, int? dias = null) =>
        cliente.GetAsync(
            $"/api/reportes/stock/vencimientos?idPuntoVenta={idPuntoVenta}"
            + (dias is { } valorDias ? $"&dias={valorDias}" : string.Empty));

    // ---- task 13.5: MUTATION TARGET — TimeZoneInfo.ConvertTime(reloj.Ahora, zona) reemplazado
    // por reloj.Ahora.UtcDateTime tiene que hacer fallar este test --------------------------------

    /// <summary>Nombra el objetivo de mutación (mutation-proof-tests regla 1):
    /// <c>ServicioDeReportesDeStock.ResolverContextoAsync</c>, la conversión
    /// <c>TimeZoneInfo.ConvertTime(reloj.Ahora, zona)</c> antes de tomar el <c>DateOnly</c>. Reloj
    /// fijado a las 22:30 ART del 12/8 (01:30 UTC del 13/8, mismo instante que el escenario del
    /// spec) y un lote que vence el 12/8: si el servicio leyera <c>reloj.Ahora.UtcDateTime</c>
    /// directo, "hoy" caería en el 13/8 y el lote (vencimiento 12/8 &lt; hoy 13/8) clasificaría
    /// <c>vencido</c>; con la conversión correcta "hoy" es 12/8 y el lote — vencimiento inclusive,
    /// decisión de la spec — clasifica <c>por_vencer</c>. El resultado es el mismo sin importar la
    /// hora real de corrida del test, a diferencia de comparar contra <c>DateTime.UtcNow</c>.
    /// Mutación aplicada (reemplazado <c>TimeZoneInfo.ConvertTime(reloj.Ahora, zona).DateTime</c>
    /// por <c>reloj.Ahora.UtcDateTime</c> en <c>ResolverContextoAsync</c>): este test pasó de
    /// esperar <c>PorVencer</c> y obtener <c>Vencido</c> — FALLÓ — a pasar al revertir. Evidencia
    /// registrada en el resumen de apply.</summary>
    [Fact]
    public async Task LaClasificacionSeResuelveEnLaZonaHorariaDelPuntoDeVentaNoEnUtc()
    {
        using var factoryConRelojFijo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(
                    new RelojFijo(new DateTimeOffset(2026, 8, 13, 1, 30, 0, TimeSpan.Zero)))));

        var ctx = await PrepararAsync(
            nameof(LaClasificacionSeResuelveEnLaZonaHorariaDelPuntoDeVentaNoEnUtc), factoryConRelojFijo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Yogur bebible 200ml");
        await SembrarLoteAsync(ctx, idArticulo, new DateOnly(2026, 8, 12), cantidad: 6m);

        var respuesta = await LlamarReporteAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        var vencimientos = JsonSerializer.Deserialize<Vencimientos>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!;

        Assert.Equal(new DateOnly(2026, 8, 12), vencimientos.Hoy);
        var fila = Assert.Single(vencimientos.Filas);
        Assert.Equal(EstadoDeVencimiento.PorVencer, fila.Estado);
    }

    // ---- task 13.6: los cuatro estados, sin_fecha cuenta en los totales --------------------------

    [Fact]
    public async Task ClasificaLosCuatroEstadosYElSinFechaCuentaEnLosTotales()
    {
        using var factoryConRelojFijo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(
                    new RelojFijo(new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero)))));

        var ctx = await PrepararAsync(nameof(ClasificaLosCuatroEstadosYElSinFechaCuentaEnLosTotales), factoryConRelojFijo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Queso crema 300g");

        // hoy resuelto = 2026-08-12 (mediodía UTC, sin riesgo de borde de zona). dias_alerta
        // default = 30 (mismos bordes que ReglaDeLotesTests.ClasificarEnLosCuatroBordesDelHorizonteDeAlerta).
        var idVencido = await SembrarLoteAsync(ctx, idArticulo, new DateOnly(2026, 8, 11), 4m, codigo: "L-VENCIDO");
        var idPorVencer = await SembrarLoteAsync(ctx, idArticulo, new DateOnly(2026, 9, 11), 7m, codigo: "L-PORVENCER");
        var idVigente = await SembrarLoteAsync(ctx, idArticulo, new DateOnly(2026, 9, 12), 9m, codigo: "L-VIGENTE");
        var idSinFecha = await SembrarLoteAsync(ctx, idArticulo, fechaVencimiento: null, cantidad: 12m, esSinIdentificar: true);

        var respuesta = await LlamarReporteAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        var vencimientos = JsonSerializer.Deserialize<Vencimientos>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!;

        Assert.Equal(4, vencimientos.Filas.Count);
        Assert.Equal(EstadoDeVencimiento.Vencido, vencimientos.Filas.Single(f => f.IdLote == idVencido).Estado);
        Assert.Equal(EstadoDeVencimiento.PorVencer, vencimientos.Filas.Single(f => f.IdLote == idPorVencer).Estado);
        Assert.Equal(EstadoDeVencimiento.Vigente, vencimientos.Filas.Single(f => f.IdLote == idVigente).Estado);
        Assert.Equal(EstadoDeVencimiento.SinFecha, vencimientos.Filas.Single(f => f.IdLote == idSinFecha).Estado);

        // Tile de Tablero: mismos tres conteos (vencido/por_vencer/sin_fecha), nunca una segunda
        // agregación que pudiera divergir (task 13.2).
        var resumenRespuesta = await ctx.Admin.GetAsync($"/api/reportes/stock/vencimientos/resumen?idPuntoVenta={ctx.IdPuntoVenta}");
        Assert.Equal(HttpStatusCode.OK, resumenRespuesta.StatusCode);
        var resumen = JsonSerializer.Deserialize<ResumenDeVencimientos>(await resumenRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;

        Assert.Equal(1, resumen.Vencidos);
        Assert.Equal(1, resumen.PorVencer);
        Assert.Equal(1, resumen.SinFecha);
    }

    // ---- task 13.7: saldo cero nunca aparece -------------------------------------------------------

    [Fact]
    public async Task UnLoteConSaldoCeroNuncaApareceEnElReporte()
    {
        var ctx = await PrepararAsync(nameof(UnLoteConSaldoCeroNuncaApareceEnElReporte));
        var idArticulo = await SembrarArticuloAsync(ctx, "Manteca 200g");
        await SembrarLoteAsync(ctx, idArticulo, new DateOnly(2027, 1, 1), cantidad: 0m);

        var respuesta = await LlamarReporteAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        var vencimientos = JsonSerializer.Deserialize<Vencimientos>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!;

        Assert.Empty(vencimientos.Filas);
    }

    // ---- task 13.10: 403 ---------------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelReporteDeVencimientos()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelReporteDeVencimientos));

        var respuesta = await LlamarReporteAsync(ctx.Vendedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- JD-FIX (judgment-day slice 13, juez B MAJOR): cobertura del override dias= --------------

    /// <summary>JD-FIX NOTE (judgment-day slice 13, juez B MAJOR): la rama
    /// <c>dias ?? await ResolverDiasAlertaAsync(...)</c> en <c>ObtenerVencimientosAsync</c> no
    /// tenía NINGÚN test — todos los tests existentes ejercitaban solo el default resuelto
    /// (<c>dias</c> ausente). Un mismo lote (vencimiento a 40 días de "hoy") clasifica distinto
    /// según el horizonte aplicado: con el default (<c>dias_alerta_vencimiento</c> = 30) es
    /// <c>vigente</c> (40 &gt; 30); con <c>dias=45</c> explícito por query string es
    /// <c>por_vencer</c> (40 &lt;= 45). Mutation target: el operador <c>??</c> del override en
    /// <c>ObtenerVencimientosAsync</c>. Mutación aplicada (reemplazado
    /// <c>dias ?? await ResolverDiasAlertaAsync(idEmpresa, idPuntoVenta, ct)</c> por
    /// <c>await ResolverDiasAlertaAsync(idEmpresa, idPuntoVenta, ct)</c>, ignorando el parámetro):
    /// este test pasó de esperar <c>PorVencer</c>/<c>DiasDeAlerta == 45</c> con el override y
    /// obtener <c>Vigente</c>/<c>DiasDeAlerta == 30</c> — FALLÓ — a pasar al revertir.</summary>
    [Fact]
    public async Task ElOverrideDeDiasExplicitoCambiaLaClasificacionRespectoDelDefault()
    {
        using var factoryConRelojFijo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(
                    new RelojFijo(new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero)))));

        var ctx = await PrepararAsync(
            nameof(ElOverrideDeDiasExplicitoCambiaLaClasificacionRespectoDelDefault), factoryConRelojFijo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Yogur descremado 500g");

        // hoy resuelto = 2026-08-12. Vencimiento a 40 días: default (dias_alerta=30) -> vigente
        // (40 > 30); dias=45 explícito -> por_vencer (40 <= 45).
        await SembrarLoteAsync(ctx, idArticulo, new DateOnly(2026, 9, 21), cantidad: 5m, codigo: "L-OVERRIDE");

        var respuestaDefault = await LlamarReporteAsync(ctx.Admin, ctx.IdPuntoVenta);
        Assert.Equal(HttpStatusCode.OK, respuestaDefault.StatusCode);
        var vencimientosDefault = JsonSerializer.Deserialize<Vencimientos>(
            await respuestaDefault.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(30, vencimientosDefault.DiasDeAlerta);
        Assert.Equal(EstadoDeVencimiento.Vigente, Assert.Single(vencimientosDefault.Filas).Estado);

        var respuestaOverride = await LlamarReporteAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 45);
        Assert.Equal(HttpStatusCode.OK, respuestaOverride.StatusCode);
        var vencimientosOverride = JsonSerializer.Deserialize<Vencimientos>(
            await respuestaOverride.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(45, vencimientosOverride.DiasDeAlerta);
        Assert.Equal(EstadoDeVencimiento.PorVencer, Assert.Single(vencimientosOverride.Filas).Estado);
    }
}
