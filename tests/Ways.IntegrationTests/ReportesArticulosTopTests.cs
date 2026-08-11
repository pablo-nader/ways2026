using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 5: <c>GET /api/reportes/articulos/top</c> punta a punta —
/// el patrón de 4 pruebas no negociable (spec reportes-de-gestion: Top Artículos Ranks By Net
/// Quantity And Revenue; Raw SQL/LINQ MUST Spell Out Soft-Delete And Estado Filters Explicitly;
/// Tenant Isolation Holds), más el caso NCX explícito del spec. Sin costo ni margen en ningún
/// assert — eso vive en <c>/rentabilidad</c> (slice 4), fuera del alcance de este archivo. Sigue
/// el mismo criterio de seeding directo (sin <c>ServicioDeVentas</c>, que exige turno abierto) que
/// <c>ReportesVentasResumenTests.SembrarComprobanteAsync</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesArticulosTopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        HttpClient Root, int IdCliente, int IdEmpleadoAdmin, int IdTipoComprobanteTx, int IdTipoComprobanteNcx,
        int IdArea, int IdListaPrecio, int IdAlicuotaIva);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        // Sin "using": ctx.Root/Supervisor/Vendedor viajan en el Contexto devuelto y se usan
        // después de que este método retorna — mismo criterio que ReportesVentasResumenTests.
        var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();
        var idListaPrecio = await dbTenant.ListasPrecio.Select(l => l.Id).FirstAsync();
        var idAlicuotaIva = await dbTenant.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area top", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        dbTenant.Areas.Add(area);
        await dbTenant.SaveChangesAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idTipoComprobanteNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor, root,
            idCliente, resultado.IdUsuarioAdmin, idTipoComprobanteTx, idTipoComprobanteNcx,
            area.Id, idListaPrecio, idAlicuotaIva);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
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

    /// <summary>Siembra un comprobante con un único ítem — directo, sin <c>ServicioDeVentas</c>
    /// (que exige turno abierto), mismo criterio que <c>ReportesVentasResumenTests.
    /// SembrarComprobanteAsync</c>. <paramref name="cantidad"/>/<paramref name="total"/> llevan el
    /// signo (negativos en una NCX, spec: An NCX Line Reduces Its Article's Ranking Figures) —
    /// nunca los calcula esta función.</summary>
    private async Task SembrarComprobanteConItemAsync(
        Contexto ctx, DateTimeOffset fecha, int idArticulo, string descripcion, decimal cantidad, decimal total,
        bool esNcx = false, EstadoComprobante estado = EstadoComprobante.Emitido, bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = esNcx ? ctx.IdTipoComprobanteNcx : ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminado ? ahora : null
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = 1, IdArticulo = idArticulo,
            Descripcion = descripcion, IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = cantidad,
            PrecioUnitario = total / cantidad, Descuento = 0m, Total = total,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> LlamarTopAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, int? limite = null)
    {
        var query = $"/api/reportes/articulos/top?idEmpresa={idEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}" +
            (limite is { } n ? $"&limite={n}" : string.Empty);
        return await cliente.GetAsync(query);
    }

    private static async Task<TopArticulos> ObtenerTopAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, int? limite = null)
    {
        var respuesta = await LlamarTopAsync(cliente, idEmpresa, desde, hasta, limite);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<TopArticulos>(cuerpo, OpcionesJson)!;
    }

    // ---- el patrón de 4 pruebas ------------------------------------------------------------------

    [Fact]
    public async Task UnaFilaDeOtroTenantNuncaApareceEnElTop()
    {
        var ctxA = await PrepararAsync(nameof(UnaFilaDeOtroTenantNuncaApareceEnElTop) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaFilaDeOtroTenantNuncaApareceEnElTop) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        var idArticuloB = await SembrarArticuloAsync(ctxB, "articulo-otro-tenant");
        await SembrarComprobanteConItemAsync(ctxB, mediodiaUtc, idArticuloB, "articulo-otro-tenant", 10m, 999_999m);

        var top = await ObtenerTopAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Empty(top.Articulos);
    }

    [Fact]
    public async Task UnaFilaSoftDeletedNuncaApareceEnElTop()
    {
        var ctx = await PrepararAsync(nameof(UnaFilaSoftDeletedNuncaApareceEnElTop));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-soft-delete");
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticulo, "articulo-soft-delete", 10m, 999_999m, eliminado: true);
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticulo, "articulo-soft-delete", 2m, 100m);

        var top = await ObtenerTopAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(top.Articulos);
        Assert.Equal(100m, fila.Total);
        Assert.Equal(2m, fila.Cantidad);
    }

    /// <summary>Nombra la cláusula bajo prueba: <c>x.Comprobante.Estado != EstadoComprobante.
    /// Anulado</c> en <c>ServicioDeReportesDeArticulos.ObtenerTopArticulosAsync</c>
    /// (mutation-proof-tests). Evidencia de mutación registrada en el resumen de apply: la
    /// cláusula se borró, esta prueba pasó a fallar (el $999.999 anulado aparecía en el top), se
    /// revirtió y volvió a pasar.</summary>
    [Fact]
    public async Task UnComprobanteAnuladoNuncaApareceEnElTop()
    {
        var ctx = await PrepararAsync(nameof(UnComprobanteAnuladoNuncaApareceEnElTop));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-anulado");
        await SembrarComprobanteConItemAsync(
            ctx, mediodiaUtc, idArticulo, "articulo-anulado", 10m, 999_999m, estado: EstadoComprobante.Anulado);
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticulo, "articulo-anulado", 3m, 250m);

        var top = await ObtenerTopAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(top.Articulos);
        Assert.Equal(250m, fila.Total);
        Assert.Equal(3m, fila.Cantidad);
    }

    [Fact]
    public async Task ElTopCoincideConElCalculoAManoYUnaNcxReduceLaFiguraDelArticulo()
    {
        var ctx = await PrepararAsync(nameof(ElTopCoincideConElCalculoAManoYUnaNcxReduceLaFiguraDelArticulo));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        // spec: "articulo 42 sold 10 units for $1000, then 2 units returned via NCX for -$200" →
        // cantidad = 8, total = 800.
        var idArticulo42 = await SembrarArticuloAsync(ctx, "articulo-42");
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticulo42, "articulo-42", 10m, 1000m);
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticulo42, "articulo-42", -2m, -200m, esNcx: true);

        // Un segundo artículo con menos monto neto, para probar el orden descendente por Total.
        var idArticuloMenor = await SembrarArticuloAsync(ctx, "articulo-menor");
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticuloMenor, "articulo-menor", 1m, 50m);

        var top = await ObtenerTopAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(2, top.Articulos.Count);
        Assert.Equal(idArticulo42, top.Articulos[0].IdArticulo);
        Assert.Equal(8m, top.Articulos[0].Cantidad);
        Assert.Equal(800m, top.Articulos[0].Total);
        Assert.Equal(idArticuloMenor, top.Articulos[1].IdArticulo);
        Assert.False(string.IsNullOrWhiteSpace(top.ZonaHoraria));
    }

    [Fact]
    public async Task UnLimiteRecortaElTopALasPrimerasFilasPorMontoDescendente()
    {
        var ctx = await PrepararAsync(nameof(UnLimiteRecortaElTopALasPrimerasFilasPorMontoDescendente));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        var idArticuloAlto = await SembrarArticuloAsync(ctx, "articulo-alto");
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticuloAlto, "articulo-alto", 1m, 500m);
        var idArticuloBajo = await SembrarArticuloAsync(ctx, "articulo-bajo");
        await SembrarComprobanteConItemAsync(ctx, mediodiaUtc, idArticuloBajo, "articulo-bajo", 1m, 50m);

        var top = await ObtenerTopAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy, limite: 1);

        var fila = Assert.Single(top.Articulos);
        Assert.Equal(idArticuloAlto, fila.IdArticulo);
    }

    // ---- matriz de roles: Vendedor rechazado, Supervisor aceptado; empresa de otro tenant → 404 --

    [Fact]
    public async Task UnVendedorEsRechazadoDelTopDeArticulos()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelTopDeArticulos));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarTopAsync(ctx.Vendedor, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorLeeElTopDeArticulos()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeElTopDeArticulos));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarTopAsync(ctx.Supervisor, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaEmpresaDeOtroTenantDevuelve404EnElTop()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnElTop) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnElTop) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarTopAsync(ctxA.Admin, ctxB.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
