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
/// stage-10-agregacion-dashboard, Slice 4 (tasks 4.6-4.7): <c>GET /api/reportes/rentabilidad</c>
/// punta a punta — el patrón de 4 pruebas no negociable (spec rentabilidad-y-comisiones: LecturaDe
/// Rentabilidad Policy Admits Admin Only, Margin Excludes Estimated Cost Lines By Default, NULL Cost
/// Is Never Treated As Zero And Coverage Is Mandatory), la matriz de roles (Supervisor 403 es la
/// prueba distintiva de esta slice: a diferencia de <c>/ventas/resumen</c>, acá NO alcanza con
/// <c>LecturaDeReportes</c>) y la cobertura de costo obligatoria. Consolidado en un único archivo
/// (mismo criterio que <c>ReportesVentasResumenTests</c>, slice 2) — la composición de políticas ya
/// quedó probada a nivel unitario en <c>PoliticasTests</c> (slice 1); acá solo se prueba el
/// <em>wiring</em> del endpoint.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RentabilidadTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        HttpClient Root, int IdCliente, int IdEmpleadoAdmin, int IdTipoComprobanteTx, int IdTipoComprobanteNcx,
        int IdArea, int IdAlicuotaIva, int IdListaPrecio);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Rentabilidad-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        dbTenant.Areas.Add(area);
        await dbTenant.SaveChangesAsync();

        var idAlicuotaIva = await dbTenant.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Rentabilidad", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        dbTenant.ListasPrecio.Add(lista);
        await dbTenant.SaveChangesAsync();

        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idTipoComprobanteNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor, root,
            idCliente, resultado.IdUsuarioAdmin, idTipoComprobanteTx, idTipoComprobanteNcx, area.Id, idAlicuotaIva, lista.Id);
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

    /// <summary>Siembra directo, sin pasar por <c>ServicioDeVentas</c> (que exige turno abierto) —
    /// mismo criterio que <c>ReportesVentasResumenTests.SembrarComprobanteAsync</c>. Una sola línea
    /// por comprobante alcanza para el reporte de rentabilidad.</summary>
    private async Task<int> SembrarLineaAsync(
        Contexto ctx, DateTimeOffset fecha, decimal total, decimal cantidad, decimal? costoUnitario,
        bool costoEsEstimado = false, int? idArticulo = null, bool esNcx = false,
        EstadoComprobante estado = EstadoComprobante.Emitido, bool eliminado = false, string descripcion = "linea-rentabilidad")
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
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            Orden = 1,
            IdArticulo = idArticulo,
            Descripcion = descripcion,
            IdArea = ctx.IdArea,
            IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva,
            PorcentajeIva = 0m,
            Cantidad = cantidad,
            PrecioUnitario = cantidad != 0 ? total / cantidad : 0m,
            Descuento = 0m,
            Total = total,
            CostoUnitario = costoUnitario,
            CostoEsEstimado = costoEsEstimado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return comprobante.Id;
    }

    /// <summary>Solo para <see cref="ElMargenCoincideConElCalculoAManoYUnaNcxLoRevierte"/>:
    /// <c>id_articulo</c> tiene FK real contra <c>articulos</c>, así que <see cref="RentabilidadPorArticulo"/>
    /// necesita un artículo genuino para agrupar, no un id inventado.</summary>
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

    private static async Task<HttpResponseMessage> LlamarRentabilidadAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, bool? incluirEstimados = null,
        int? idPuntoVenta = null)
    {
        var query =
            $"/api/reportes/rentabilidad?idEmpresa={idEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}"
            + (incluirEstimados is { } flag ? $"&incluirEstimados={flag}" : string.Empty)
            + (idPuntoVenta is { } id ? $"&idPuntoVenta={id}" : string.Empty);
        return await cliente.GetAsync(query);
    }

    private static async Task<Rentabilidad> ObtenerRentabilidadAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, bool? incluirEstimados = null)
    {
        var respuesta = await LlamarRentabilidadAsync(cliente, idEmpresa, desde, hasta, incluirEstimados);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<Rentabilidad>(cuerpo, OpcionesJson)!;
    }

    // ---- task 4.6: el patrón de 4 pruebas ---------------------------------------------------------

    [Fact]
    public async Task UnaLineaDeOtroTenantNuncaApareceEnLaRentabilidad()
    {
        var ctxA = await PrepararAsync(nameof(UnaLineaDeOtroTenantNuncaApareceEnLaRentabilidad) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaLineaDeOtroTenantNuncaApareceEnLaRentabilidad) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarLineaAsync(ctxB, mediodiaUtc, total: 999_999m, cantidad: 1m, costoUnitario: 1m);

        var rentabilidad = await ObtenerRentabilidadAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Equal(0m, rentabilidad.VentaConsiderada);
        Assert.Equal(0, rentabilidad.Cobertura.LineasTotales);
    }

    [Fact]
    public async Task UnaLineaSoftDeletedNuncaApareceEnLaRentabilidad()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaSoftDeletedNuncaApareceEnLaRentabilidad));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarLineaAsync(ctx, mediodiaUtc, total: 999_999m, cantidad: 1m, costoUnitario: 1m, eliminado: true);
        await SembrarLineaAsync(ctx, mediodiaUtc, total: 100m, cantidad: 1m, costoUnitario: 40m);

        var rentabilidad = await ObtenerRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(60m, rentabilidad.Margen);
        Assert.Equal(1, rentabilidad.Cobertura.LineasTotales);
    }

    [Fact]
    public async Task UnaLineaDeComprobanteAnuladoNuncaApareceEnLaRentabilidad()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaDeComprobanteAnuladoNuncaApareceEnLaRentabilidad));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarLineaAsync(ctx, mediodiaUtc, total: 999_999m, cantidad: 1m, costoUnitario: 1m, estado: EstadoComprobante.Anulado);
        await SembrarLineaAsync(ctx, mediodiaUtc, total: 250m, cantidad: 1m, costoUnitario: 100m);

        var rentabilidad = await ObtenerRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(150m, rentabilidad.Margen);
        Assert.Equal(1, rentabilidad.Cobertura.LineasTotales);
    }

    [Fact]
    public async Task ElMargenCoincideConElCalculoAManoYUnaNcxLoRevierte()
    {
        var ctx = await PrepararAsync(nameof(ElMargenCoincideConElCalculoAManoYUnaNcxLoRevierte));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-rentabilidad");

        // TX real: venta 300, costo unitario 100 × cantidad 1 → margen de línea 200.
        await SembrarLineaAsync(ctx, mediodiaUtc, total: 300m, cantidad: 1m, costoUnitario: 100m, idArticulo: idArticulo);
        // NCX real sobre el mismo artículo: cantidad y total negativos, costo SIN signo (design
        // decisión 4/9) → costo×cantidad también negativo, la línea resta 50 al margen total.
        await SembrarLineaAsync(ctx, mediodiaUtc, total: -150m, cantidad: -1m, costoUnitario: 100m, esNcx: true, idArticulo: idArticulo);

        var rentabilidad = await ObtenerRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(150m, rentabilidad.VentaConsiderada);
        Assert.Equal(0m, rentabilidad.CostoConsiderado);
        Assert.Equal(150m, rentabilidad.Margen);
        Assert.Equal(100m, rentabilidad.MargenPorcentaje);

        var porArticulo = Assert.Single(rentabilidad.PorArticulo);
        Assert.Equal(idArticulo, porArticulo.IdArticulo);
        Assert.Equal(150m, porArticulo.Margen);
    }

    // ---- task 4.6: opt-in explícito de líneas estimadas (spec: Margin Excludes Estimated Cost
    // Lines By Default) -----------------------------------------------------------------------------

    [Fact]
    public async Task UnaLineaEstimadaSeExcluyePorDefectoYSeIncluyeSoloConElOptInExplicito()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaEstimadaSeExcluyePorDefectoYSeIncluyeSoloConElOptInExplicito));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        // total 150, costo_unitario 100, costo_es_estimado true → $50 de margen en juego.
        await SembrarLineaAsync(ctx, mediodiaUtc, total: 150m, cantidad: 1m, costoUnitario: 100m, costoEsEstimado: true);

        var sinOptIn = await ObtenerRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);
        Assert.Equal(0m, sinOptIn.VentaConsiderada);
        Assert.Equal(0m, sinOptIn.Margen);
        Assert.Equal(1, sinOptIn.Cobertura.LineasConCostoEstimado);
        Assert.Equal(150m, sinOptIn.Cobertura.VentaConCostoEstimado);
        Assert.False(sinOptIn.Cobertura.IncluyeEstimados);

        var conOptIn = await ObtenerRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy, incluirEstimados: true);
        Assert.Equal(150m, conOptIn.VentaConsiderada);
        Assert.Equal(50m, conOptIn.Margen);
        Assert.True(conOptIn.Cobertura.IncluyeEstimados);
    }

    // ---- task 4.6: costo desconocido nunca se trata como cero (spec: NULL Cost Is Never Treated As
    // Zero) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaLineaDeCostoDesconocidoSeSalteaDelMargenYSeReportaAparteNuncaComoCero()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaDeCostoDesconocidoSeSalteaDelMargenYSeReportaAparteNuncaComoCero));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarLineaAsync(ctx, mediodiaUtc, total: 200m, cantidad: 1m, costoUnitario: null);

        var rentabilidad = await ObtenerRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        // Si costo_unitario NULL se tratara como 0, margen == venta (200) — nunca debe pasar: la
        // línea queda totalmente afuera del margen (0/0, no 200/0).
        Assert.Equal(0m, rentabilidad.VentaConsiderada);
        Assert.Equal(0m, rentabilidad.CostoConsiderado);
        Assert.Equal(0m, rentabilidad.Margen);
        Assert.Null(rentabilidad.MargenPorcentaje);
        Assert.Equal(1, rentabilidad.Cobertura.LineasSinCosto);
        Assert.Equal(200m, rentabilidad.Cobertura.VentaSinCosto);
        Assert.Empty(rentabilidad.PorArticulo);
    }

    // ---- task 4.6: la cobertura refleja un período mixto (spec: Coverage Reflects A Mixed Period) --

    [Fact]
    public async Task LaCoberturaReflejaUnPeriodoMixtoDeCostoRealEstimadoYDesconocido()
    {
        var ctx = await PrepararAsync(nameof(LaCoberturaReflejaUnPeriodoMixtoDeCostoRealEstimadoYDesconocido));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        // 7 líneas con costo real ($100 c/u, costo $60 c/u).
        for (var i = 0; i < 7; i++)
        {
            await SembrarLineaAsync(ctx, mediodiaUtc, total: 100m, cantidad: 1m, costoUnitario: 60m);
        }

        // 2 líneas estimadas ($50 c/u, costo $30 c/u).
        for (var i = 0; i < 2; i++)
        {
            await SembrarLineaAsync(ctx, mediodiaUtc, total: 50m, cantidad: 1m, costoUnitario: 30m, costoEsEstimado: true);
        }

        // 1 línea de costo desconocido ($40).
        await SembrarLineaAsync(ctx, mediodiaUtc, total: 40m, cantidad: 1m, costoUnitario: null);

        var rentabilidad = await ObtenerRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(10, rentabilidad.Cobertura.LineasTotales);
        Assert.Equal(7, rentabilidad.Cobertura.LineasConCostoReal);
        Assert.Equal(700m, rentabilidad.Cobertura.VentaConCostoReal);
        Assert.Equal(2, rentabilidad.Cobertura.LineasConCostoEstimado);
        Assert.Equal(100m, rentabilidad.Cobertura.VentaConCostoEstimado);
        Assert.Equal(1, rentabilidad.Cobertura.LineasSinCosto);
        Assert.Equal(40m, rentabilidad.Cobertura.VentaSinCosto);

        // Por defecto solo las 7 reales entran al margen: venta 700, costo 420, margen 280.
        Assert.Equal(700m, rentabilidad.VentaConsiderada);
        Assert.Equal(420m, rentabilidad.CostoConsiderado);
        Assert.Equal(280m, rentabilidad.Margen);
    }

    // ---- matriz de roles: Supervisor rechazado ES la prueba distintiva de esta slice (a diferencia
    // de /ventas/resumen, LecturaDeReportes solo no alcanza acá) -------------------------------------

    [Fact]
    public async Task UnSupervisorEsRechazadoDeLaRentabilidad()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoDeLaRentabilidad));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarRentabilidadAsync(ctx.Supervisor, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoDeLaRentabilidad()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDeLaRentabilidad));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarRentabilidadAsync(ctx.Vendedor, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnRootEsRechazadoDeLaRentabilidad()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoDeLaRentabilidad));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarRentabilidadAsync(ctx.Root, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAdminLeeLaRentabilidad()
    {
        var ctx = await PrepararAsync(nameof(UnAdminLeeLaRentabilidad));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarRentabilidadAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaEmpresaDeOtroTenantDevuelve404EnRentabilidad()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnRentabilidad) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnRentabilidad) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarRentabilidadAsync(ctxA.Admin, ctxB.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
