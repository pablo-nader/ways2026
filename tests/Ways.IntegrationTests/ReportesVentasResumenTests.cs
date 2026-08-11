using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Reportes;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 2 (tasks 2.11-2.13): <c>GET /api/reportes/ventas/resumen</c>
/// punta a punta — el patrón de 4 pruebas no negociable por endpoint (spec reportes-de-gestion: Net
/// Sales Has No Sign Branch, Raw SQL MUST Spell Out Soft-Delete And Estado Filters Explicitly,
/// Tenant Isolation Holds On Raw SQL Via Connection-Level RLS), el corte de zona horaria (spec:
/// Business-Day Bucketing) y la semántica NCX de ticket promedio (spec: Ticket Promedio Excludes
/// NCX From Both Sides) — consolidado en un único archivo (en vez de tres) para no triplicar el
/// boilerplate de <see cref="PrepararAsync"/> bajo el presupuesto de revisión de esta slice.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesVentasResumenTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        HttpClient Root, int IdCliente, int IdEmpleadoAdmin, int IdTipoComprobanteTx, int IdTipoComprobanteNcx);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        // Sin "using": ctx.Root viaja en el Contexto devuelto y se usa después de que este
        // método retorna — mismo criterio que ctx.Admin, nunca se dispone acá.
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
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idTipoComprobanteNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor, root,
            idCliente, resultado.IdUsuarioAdmin, idTipoComprobanteTx, idTipoComprobanteNcx);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        // "usuario" tiene un máximo de 40 caracteres (ServicioDeUsuarios.CrearAsync) — los
        // nombres de test son largos, así que el login usa un sufijo corto y único en vez del
        // nombre completo del caso.
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
    /// mismo criterio que <c>SaldoDeProveedorTests.SembrarPagoAsync</c>: la derivación del reporte
    /// nunca toca <c>items_comprobante_venta</c>/<c>pagos_comprobante</c>.</summary>
    private async Task SembrarComprobanteAsync(
        Contexto ctx, DateTimeOffset fecha, decimal total, bool esNcx = false,
        EstadoComprobante estado = EstadoComprobante.Emitido, bool eliminado = false)
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
    }

    private static async Task<HttpResponseMessage> LlamarResumenAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, Granularidad granularidad = Granularidad.Dia,
        int? idPuntoVenta = null)
    {
        var query =
            $"/api/reportes/ventas/resumen?idEmpresa={idEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}" +
            $"&granularidad={granularidad}" + (idPuntoVenta is { } id ? $"&idPuntoVenta={id}" : string.Empty);
        return await cliente.GetAsync(query);
    }

    private static async Task<ResumenDeVentas> ObtenerResumenAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, Granularidad granularidad = Granularidad.Dia)
    {
        var respuesta = await LlamarResumenAsync(cliente, idEmpresa, desde, hasta, granularidad);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResumenDeVentas>(cuerpo, OpcionesJson)!;
    }

    // ---- task 2.11: el patrón de 4 pruebas ------------------------------------------------------

    [Fact]
    public async Task UnaFilaDeOtroTenantNuncaApareceEnElResumen()
    {
        var ctxA = await PrepararAsync(nameof(UnaFilaDeOtroTenantNuncaApareceEnElResumen) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaFilaDeOtroTenantNuncaApareceEnElResumen) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctxB, DateTimeOffset.UtcNow, 999_999m);

        var resumen = await ObtenerResumenAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Equal(0m, resumen.NetoVendido);
    }

    /// <summary>El test de arriba pasa igual aunque se borre <c>AND cv.id_tenant = $2</c> del SQL:
    /// tanto RLS (GUC de conexión) como la lista de puntos de venta resuelta por
    /// <c>ServicioDeReportesDeVentas.ResolverPuntosDeVentaAsync</c> (siempre acotada a la empresa,
    /// por lo tanto al tenant) ya lo enmascaran. Este test ejercita <see cref="LectorDeSerieTemporal"/>
    /// directo, con un <see cref="WaysDbContext"/> en modo <c>Plataforma</c> (RLS bypasseada por
    /// <c>app_es_plataforma()</c>) y una <c>idsPuntoVenta</c> armada a mano que incluye el punto de
    /// venta del tenant B — así el único filtro de tenant que puede aplicar es el predicado
    /// <c>id_tenant = $2</c> del propio SQL.</summary>
    [Fact]
    public async Task ElPredicadoDeIdTenantDelSqlExcluyeFilasDeOtroTenant()
    {
        var ctxA = await PrepararAsync(nameof(ElPredicadoDeIdTenantDelSqlExcluyeFilasDeOtroTenant) + "-A");
        var ctxB = await PrepararAsync(nameof(ElPredicadoDeIdTenantDelSqlExcluyeFilasDeOtroTenant) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var desdeUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 0, 0, 0, TimeSpan.Zero);

        await SembrarComprobanteAsync(ctxB, mediodiaUtc, 999_999m);

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var lector = new LectorDeSerieTemporal(dbPlataforma);

        var filas = await lector.EjecutarVentasAsync(
            Granularidad.Dia, "UTC", ctxA.IdTenant, [ctxA.IdPuntoVenta, ctxB.IdPuntoVenta], desdeUtc, desdeUtc.AddDays(1));

        Assert.Empty(filas);
    }

    [Fact]
    public async Task UnaFilaSoftDeletedNuncaApareceEnElResumen()
    {
        var ctx = await PrepararAsync(nameof(UnaFilaSoftDeletedNuncaApareceEnElResumen));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, eliminado: true);
        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 100m);

        var resumen = await ObtenerResumenAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(100m, resumen.NetoVendido);
    }

    [Fact]
    public async Task UnComprobanteAnuladoNuncaApareceEnElResumen()
    {
        var ctx = await PrepararAsync(nameof(UnComprobanteAnuladoNuncaApareceEnElResumen));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, estado: EstadoComprobante.Anulado);
        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 250m);

        var resumen = await ObtenerResumenAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(250m, resumen.NetoVendido);
    }

    [Fact]
    public async Task ElResumenCoincideConElCalculoAMano()
    {
        var ctx = await PrepararAsync(nameof(ElResumenCoincideConElCalculoAMano));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarComprobanteAsync(ctx, mediodiaUtc, 100m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, 200m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, 300m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, -50m, esNcx: true);

        var resumen = await ObtenerResumenAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(550m, resumen.NetoVendido);
        Assert.Equal(3, resumen.CantidadTx);
        Assert.Equal(200m, resumen.TicketPromedio);
        Assert.Equal(1, resumen.CantidadNcx);
        Assert.Equal(-50m, resumen.NetoNcx);
    }

    // ---- task 2.12: el corte de día vive en la zona del punto de venta ---------------------------

    [Fact]
    public async Task UnaVentaALas2230ArtBucketeaEnDiasDistintosSegunLaZonaConfigurada()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaALas2230ArtBucketeaEnDiasDistintosSegunLaZonaConfigurada));
        // 2026-08-05T22:30:00-03:00 == 2026-08-06T01:30:00Z.
        var instante = new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero);
        await SembrarComprobanteAsync(ctx, instante, 500m);

        // Rango de 3 días: con un solo día en el rango, hay un único bucket posible y el test no
        // distingue si el bucketing por zona en verdad corrió en el SQL (timezone($1, cv.fecha))
        // o si el date_trunc truncó en la zona de sesión de Postgres sin más. Con 3 días, la venta
        // tiene que caer en el bucket correcto SEGÚN LA ZONA, no en cualquiera del rango.
        var desde = new DateOnly(2026, 8, 4);
        var hasta = new DateOnly(2026, 8, 6);

        var configuracionArt = await ctx.Admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + ctx.IdEmpresa,
            new ParametroAlta("zona_horaria", "\"America/Argentina/Buenos_Aires\"", null));
        Assert.Equal(HttpStatusCode.OK, configuracionArt.StatusCode);

        var resumenArt = await ObtenerResumenAsync(ctx.Admin, ctx.IdEmpresa, desde, hasta);
        Assert.Equal(500m, resumenArt.NetoVendido);
        Assert.Equal(500m, BucketPor(resumenArt, "2026-08-05").Neto);
        Assert.Equal(0m, BucketPor(resumenArt, "2026-08-06").Neto);

        var configuracionUtc = await ctx.Admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + ctx.IdEmpresa, new ParametroAlta("zona_horaria", "\"UTC\"", null));
        Assert.Equal(HttpStatusCode.OK, configuracionUtc.StatusCode);

        var resumenUtc = await ObtenerResumenAsync(ctx.Admin, ctx.IdEmpresa, desde, hasta);
        Assert.Equal(500m, resumenUtc.NetoVendido);
        Assert.Equal(0m, BucketPor(resumenUtc, "2026-08-05").Neto);
        Assert.Equal(500m, BucketPor(resumenUtc, "2026-08-06").Neto);
    }

    private static BucketDeVentas BucketPor(ResumenDeVentas resumen, string etiqueta) =>
        resumen.Serie.Single(b => b.Etiqueta == etiqueta);

    // ---- task 2.13: NCX se excluye de numerador Y denominador del ticket promedio ----------------

    [Fact]
    public async Task UnaNcxReduceElNetoSinAlterarElTicketPromedioNiLaCantidadDeTx()
    {
        var ctx = await PrepararAsync(nameof(UnaNcxReduceElNetoSinAlterarElTicketPromedioNiLaCantidadDeTx));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarComprobanteAsync(ctx, mediodiaUtc, 100m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, 200m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, 300m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, -50m, esNcx: true);

        var resumen = await ObtenerResumenAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        // 600/3 = 200, nunca 550/4.
        Assert.Equal(200m, resumen.TicketPromedio);
        Assert.Equal(3, resumen.CantidadTx);
        Assert.Equal(550m, resumen.NetoVendido);
    }

    // ---- matriz de roles: Vendedor y Root rechazados, Supervisor y Admin aceptados ----------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelReporteDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelReporteDeVentas));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarResumenAsync(ctx.Vendedor, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnRootEsRechazadoDelReporteDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoDelReporteDeVentas));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarResumenAsync(ctx.Root, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorLeeElReporteDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeElReporteDeVentas));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarResumenAsync(ctx.Supervisor, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnEmpresaDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnEmpresaDeOtroTenantDevuelve404) + "-A");
        var ctxB = await PrepararAsync(nameof(UnEmpresaDeOtroTenantDevuelve404) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarResumenAsync(ctxA.Admin, ctxB.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
