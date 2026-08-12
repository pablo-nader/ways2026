using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;
using Ways.Domain.Proveedores;
using Ways.Domain.Reportes;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 5 (tasks 5.4-5.5): <c>GET /api/reportes/compras/por-proveedor</c>
/// y <c>GET /api/reportes/gastos/resumen</c> punta a punta — el patrón de 4 pruebas por endpoint
/// (spec reportes-de-gestion: Compras Bucketed By Fecha De Recepción Confirmada Only, Gastos
/// Resumen, Raw SQL MUST Spell Out Soft-Delete And Estado Filters Explicitly, Tenant Isolation
/// Holds On Raw SQL Via Connection-Level RLS) más la matriz de roles de las dos rutas —
/// <c>ReportesAutorizacionTests</c> (task 4.7, slice 4) no existe todavía en esta rama aislada
/// (slices 3/4/5 corren en paralelo esta noche, cada una desde <c>main</c>): mismo criterio de
/// consolidación que <c>ReportesVentasResumenTests</c> (task 2.13) — el orquestador reconcilia
/// las tres matrices cuando fusiona.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesEgresosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static long _numeroExternoSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        HttpClient Root, int IdProveedor, int IdProveedor2, int IdTipoComprobanteCompra, int IdMedioEfectivo);

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

        // CondicionFiscal es catálogo global (sin id_tenant) — insertarla en modo Tenant viola RLS.
        // Mismo criterio que SaldoDeProveedorTests.PrepararAsync: CondicionFiscal y Proveedores se
        // siembran bajo TenantActualFijo.Plataforma (bypassea RLS vía app_es_plataforma()); el
        // medio de pago efectivo, tenant-scoped, se lee bajo un contexto de tenant aparte.
        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var idTipoComprobanteCompra = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        dbPlataforma.CondicionesFiscales.Add(condicionFiscal);
        await dbPlataforma.SaveChangesAsync();

        var proveedor1 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-Prov1", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedor2 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-Prov2", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        dbPlataforma.Proveedores.AddRange(proveedor1, proveedor2);
        await dbPlataforma.SaveChangesAsync();

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor, root,
            proveedor1.Id, proveedor2.Id, idTipoComprobanteCompra, idMedioEfectivo);
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

    /// <summary>Siembra directo, sin pasar por <c>ServicioDeCompras</c> — mismo criterio que
    /// <c>ReportesVentasResumenTests.SembrarComprobanteAsync</c>. <paramref name="fechaComprobante"/>
    /// por defecto igual a la fecha de recepción; se separa explícitamente cuando el test necesita
    /// probar que el reporte bucketea por <c>fecha_recepcion</c>, no por <c>fecha_comprobante</c>.
    /// El CHECK <c>ck_comprobantes_compra_confirmada_completa</c> exige <c>numero_externo</c>/
    /// <c>fecha_comprobante</c> no nulos en <c>Confirmada</c> — se setean siempre, incluso para el
    /// caso deliberadamente anómalo de un borrador con fecha de recepción (task 5.4, aísla el
    /// filtro de estado de la ausencia normal de esa fecha en un borrador real).</summary>
    private async Task SembrarCompraAsync(
        Contexto ctx, DateTimeOffset fechaRecepcion, decimal total, EstadoCompra estado,
        int? idProveedor = null, bool eliminado = false, DateOnly? fechaComprobante = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var numeroExterno = $"reportes-egresos-{Interlocked.Increment(ref _numeroExternoSecuencial)}";

        var compra = new ComprobanteCompra
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = idProveedor ?? ctx.IdProveedor,
            IdTipoComprobante = ctx.IdTipoComprobanteCompra,
            NumeroExterno = numeroExterno,
            FechaComprobante = fechaComprobante ?? DateOnly.FromDateTime(fechaRecepcion.UtcDateTime),
            FechaRecepcion = fechaRecepcion,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = 1,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminado ? ahora : null
        };
        db.ComprobantesCompra.Add(compra);
        await db.SaveChangesAsync();
    }

    private static async Task<int> AbrirTurnoAsync(HttpClient cliente, int idPuntoVenta)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idPuntoVenta, 0m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    /// <summary>Siembra directo, sin pasar por <c>ServicioDeGastos</c> (que resuelve el turno server
    /// side desde el request HTTP) — <paramref name="idTurno"/> viene de un turno real abierto por
    /// API (FK no bypaseable), pero <see cref="Gasto.Fecha"/>/<see cref="Gasto.DeletedAt"/> se
    /// controlan a mano para las aserciones de bucketing y soft-delete.</summary>
    private async Task SembrarGastoAsync(
        Contexto ctx, int idTurno, DateTimeOffset fecha, decimal importe, CategoriaGasto categoria,
        bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var gasto = new Gasto
        {
            IdTenant = ctx.IdTenant,
            Fecha = fecha,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = 1,
            Categoria = categoria,
            Concepto = "Gasto de reporte",
            IdMedioPago = ctx.IdMedioEfectivo,
            Importe = importe,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminado ? ahora : null
        };
        db.Gastos.Add(gasto);
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> LlamarComprasAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta) =>
        await cliente.GetAsync($"/api/reportes/compras/por-proveedor?idEmpresa={idEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}");

    private static async Task<ComprasPorProveedor> ObtenerComprasAsync(HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta)
    {
        var respuesta = await LlamarComprasAsync(cliente, idEmpresa, desde, hasta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ComprasPorProveedor>(cuerpo, OpcionesJson)!;
    }

    private static async Task<HttpResponseMessage> LlamarGastosAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, Granularidad granularidad = Granularidad.Dia) =>
        await cliente.GetAsync(
            $"/api/reportes/gastos/resumen?idEmpresa={idEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}&granularidad={granularidad}");

    private static async Task<ResumenDeGastos> ObtenerGastosAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta, Granularidad granularidad = Granularidad.Dia)
    {
        var respuesta = await LlamarGastosAsync(cliente, idEmpresa, desde, hasta, granularidad);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResumenDeGastos>(cuerpo, OpcionesJson)!;
    }

    // ==== compras/por-proveedor — el patrón de 4 pruebas ==========================================

    [Fact]
    public async Task UnaCompraDeOtroTenantNuncaApareceEnElReporteDeCompras()
    {
        var ctxA = await PrepararAsync(nameof(UnaCompraDeOtroTenantNuncaApareceEnElReporteDeCompras) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaCompraDeOtroTenantNuncaApareceEnElReporteDeCompras) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarCompraAsync(ctxB, DateTimeOffset.UtcNow, 999_999m, EstadoCompra.Confirmada);

        var reporte = await ObtenerComprasAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Equal(0m, reporte.TotalGeneral);
        Assert.Empty(reporte.PorProveedor);
    }

    [Fact]
    public async Task UnaCompraSoftDeletedNuncaApareceEnElReporteDeCompras()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraSoftDeletedNuncaApareceEnElReporteDeCompras));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodia = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarCompraAsync(ctx, mediodia, 999_999m, EstadoCompra.Confirmada, eliminado: true);
        await SembrarCompraAsync(ctx, mediodia, 100m, EstadoCompra.Confirmada);

        var reporte = await ObtenerComprasAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(100m, reporte.TotalGeneral);
    }

    /// <summary>Clause-proving (mutation-proof-tests): la fila de <c>Borrador</c> lleva
    /// <c>FechaRecepcion</c> seteada a mano (una compra borrador real la tiene <c>NULL</c>) para
    /// que el único filtro capaz de excluirla sea <c>Estado == Confirmada</c>, no la ausencia de
    /// fecha. Mutación registrada: comentar el <c>.Where(c => c.Estado == EstadoCompra.Confirmada)</c>
    /// en <c>ServicioDeReportesDeEgresos.ObtenerComprasPorProveedorAsync</c> hace que este test
    /// FALLE (el borrador de 999999 se suma), revertido vuelve a pasar.</summary>
    [Fact]
    public async Task UnaCompraBorradorConFechaDeRecepcionNuncaApareceEnElReporteDeCompras()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraBorradorConFechaDeRecepcionNuncaApareceEnElReporteDeCompras));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodia = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarCompraAsync(ctx, mediodia, 999_999m, EstadoCompra.Borrador);
        await SembrarCompraAsync(ctx, mediodia, 300m, EstadoCompra.Confirmada);

        var reporte = await ObtenerComprasAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(300m, reporte.TotalGeneral);
    }

    [Fact]
    public async Task UnaCompraAnuladaNuncaApareceEnElReporteDeCompras()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraAnuladaNuncaApareceEnElReporteDeCompras));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodia = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarCompraAsync(ctx, mediodia, 999_999m, EstadoCompra.Anulada);
        await SembrarCompraAsync(ctx, mediodia, 400m, EstadoCompra.Confirmada);

        var reporte = await ObtenerComprasAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(400m, reporte.TotalGeneral);
    }

    /// <summary>Clause-proving (mutation-proof-tests): dos filas confirmadas con
    /// <c>FechaComprobante</c> DISTINTA de <c>FechaRecepcion</c> — una cae dentro del rango solo
    /// por <c>FechaRecepcion</c>, la otra cae dentro del rango solo por <c>FechaComprobante</c>.
    /// Mutación registrada: cambiar el <c>.Where(... c.FechaRecepcion ...)</c> por
    /// <c>c.FechaComprobante</c> en <c>ServicioDeReportesDeEgresos</c> invierte cuál de las dos
    /// filas aparece — este test lo detecta (FALLA con la mutación, pasa revertido).</summary>
    [Fact]
    public async Task ElReporteDeComprasBucketeaPorFechaDeRecepcionNoPorFechaDeComprobante()
    {
        var ctx = await PrepararAsync(nameof(ElReporteDeComprasBucketeaPorFechaDeRecepcionNoPorFechaDeComprobante));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var haceUnMes = hoy.AddMonths(-1);
        var mediodiaHoy = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        // Recibida hoy, facturada hace un mes -> tiene que aparecer (fecha_recepcion en rango).
        await SembrarCompraAsync(ctx, mediodiaHoy, 500m, EstadoCompra.Confirmada, fechaComprobante: haceUnMes);

        var reporte = await ObtenerComprasAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(500m, reporte.TotalGeneral);
    }

    [Fact]
    public async Task ElReporteDeComprasCoincideConElCalculoAMano()
    {
        var ctx = await PrepararAsync(nameof(ElReporteDeComprasCoincideConElCalculoAMano));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodia = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarCompraAsync(ctx, mediodia, 1000m, EstadoCompra.Confirmada, idProveedor: ctx.IdProveedor);
        await SembrarCompraAsync(ctx, mediodia, 1500m, EstadoCompra.Confirmada, idProveedor: ctx.IdProveedor);
        await SembrarCompraAsync(ctx, mediodia, 500m, EstadoCompra.Confirmada, idProveedor: ctx.IdProveedor2);

        var reporte = await ObtenerComprasAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(3000m, reporte.TotalGeneral);
        var lineaProveedor1 = Assert.Single(reporte.PorProveedor, p => p.IdProveedor == ctx.IdProveedor);
        Assert.Equal(2500m, lineaProveedor1.Total);
        Assert.Equal(2, lineaProveedor1.CantidadCompras);
        var lineaProveedor2 = Assert.Single(reporte.PorProveedor, p => p.IdProveedor == ctx.IdProveedor2);
        Assert.Equal(500m, lineaProveedor2.Total);
        Assert.Equal(1, lineaProveedor2.CantidadCompras);
    }

    // ==== gastos/resumen — el patrón de 4 pruebas ==================================================

    [Fact]
    public async Task UnGastoDeOtroTenantNuncaApareceEnElResumenDeGastos()
    {
        var ctxA = await PrepararAsync(nameof(UnGastoDeOtroTenantNuncaApareceEnElResumenDeGastos) + "-A");
        var ctxB = await PrepararAsync(nameof(UnGastoDeOtroTenantNuncaApareceEnElResumenDeGastos) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var turnoB = await AbrirTurnoAsync(ctxB.Admin, ctxB.IdPuntoVenta);

        await SembrarGastoAsync(ctxB, turnoB, DateTimeOffset.UtcNow, 999_999m, CategoriaGasto.Otros);

        var resumen = await ObtenerGastosAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Equal(0m, resumen.ImporteTotal);
    }

    [Fact]
    public async Task UnGastoSoftDeletedNuncaApareceEnElResumenDeGastos()
    {
        var ctx = await PrepararAsync(nameof(UnGastoSoftDeletedNuncaApareceEnElResumenDeGastos));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodia = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        await SembrarGastoAsync(ctx, turno, mediodia, 5000m, CategoriaGasto.Otros, eliminado: true);
        await SembrarGastoAsync(ctx, turno, mediodia, 100m, CategoriaGasto.Otros);

        var resumen = await ObtenerGastosAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(100m, resumen.ImporteTotal);
    }

    /// <summary>Sustituye la pata de "estado" del patrón de 4 pruebas: <c>gastos</c> no tiene
    /// columna de estado (design: Raw-SQL Invariant Checklist), así que la cuarta prueba
    /// no-negociable de este endpoint es su clave de agrupación propia — el desglose por
    /// categoría (spec: Gastos Resumen, "optionally grouped by categoria").</summary>
    [Fact]
    public async Task ElDesglosePorCategoriaSumaCadaCategoriaPorSeparado()
    {
        var ctx = await PrepararAsync(nameof(ElDesglosePorCategoriaSumaCadaCategoriaPorSeparado));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodia = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        await SembrarGastoAsync(ctx, turno, mediodia, 200m, CategoriaGasto.Sueldos);
        await SembrarGastoAsync(ctx, turno, mediodia, 300m, CategoriaGasto.Sueldos);
        await SembrarGastoAsync(ctx, turno, mediodia, 150m, CategoriaGasto.Servicios);

        var resumen = await ObtenerGastosAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(650m, resumen.ImporteTotal);
        var sueldos = Assert.Single(resumen.PorCategoria, c => c.Categoria == CategoriaGasto.Sueldos);
        Assert.Equal(500m, sueldos.Importe);
        Assert.Equal(2, sueldos.CantidadGastos);
        var servicios = Assert.Single(resumen.PorCategoria, c => c.Categoria == CategoriaGasto.Servicios);
        Assert.Equal(150m, servicios.Importe);
        Assert.Equal(1, servicios.CantidadGastos);
    }

    [Fact]
    public async Task ElResumenDeGastosCoincideConElCalculoAManoYRellenaLosBucketsSinGastos()
    {
        var ctx = await PrepararAsync(nameof(ElResumenDeGastosCoincideConElCalculoAManoYRellenaLosBucketsSinGastos));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var ayer = hoy.AddDays(-1);
        var mediodiaHoy = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        await SembrarGastoAsync(ctx, turno, mediodiaHoy, 700m, CategoriaGasto.Otros);

        var resumen = await ObtenerGastosAsync(ctx.Admin, ctx.IdEmpresa, ayer, hoy);

        Assert.Equal(700m, resumen.ImporteTotal);
        Assert.Equal(2, resumen.Serie.Count);
        Assert.Equal(0m, resumen.Serie.Single(b => b.Inicio == ayer).Importe);
        Assert.Equal(700m, resumen.Serie.Single(b => b.Inicio == hoy).Importe);
    }

    // ==== matriz de roles — ambas rutas (ReportesAutorizacionTests todavía no existe en esta rama) ==

    [Theory]
    [InlineData("compras/por-proveedor")]
    [InlineData("gastos/resumen")]
    public async Task UnVendedorEsRechazadoDeLosReportesDeEgresos(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDeLosReportesDeEgresos) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Vendedor.GetAsync(RutaConGranularidad(ruta, ctx.IdEmpresa, hoy));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Theory]
    [InlineData("compras/por-proveedor")]
    [InlineData("gastos/resumen")]
    public async Task UnRootEsRechazadoDeLosReportesDeEgresos(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoDeLosReportesDeEgresos) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Root.GetAsync(RutaConGranularidad(ruta, ctx.IdEmpresa, hoy));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Theory]
    [InlineData("compras/por-proveedor")]
    [InlineData("gastos/resumen")]
    public async Task UnSupervisorLeeLosReportesDeEgresos(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeLosReportesDeEgresos) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Supervisor.GetAsync(RutaConGranularidad(ruta, ctx.IdEmpresa, hoy));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaEmpresaDeOtroTenantDevuelve404EnCompras()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnCompras) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnCompras) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarComprasAsync(ctxA.Admin, ctxB.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaEmpresaDeOtroTenantDevuelve404EnGastos()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnGastos) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404EnGastos) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarGastosAsync(ctxA.Admin, ctxB.IdEmpresa, hoy, hoy);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    private static string RutaConGranularidad(string ruta, int idEmpresa, DateOnly hoy) =>
        ruta == "gastos/resumen"
            ? $"/api/reportes/{ruta}?idEmpresa={idEmpresa}&desde={hoy:yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}&granularidad=Dia"
            : $"/api/reportes/{ruta}?idEmpresa={idEmpresa}&desde={hoy:yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}";
}
