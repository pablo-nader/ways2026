using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Reportes;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 3 (task 3.4): <c>GET /api/reportes/ventas/por-punto-venta</c>,
/// <c>/por-vendedor</c> y <c>/por-medio-pago</c> punta a punta — el patrón de 4 pruebas por ruta
/// (cross-tenant, soft-delete, anulado, cálculo a mano) más el chequeo de signo NCX por ruta (spec
/// reportes-de-gestion: Ventas Breakdown Endpoints By Punto De Venta, Vendedor, Medio De Pago),
/// consolidado en un único archivo — mismo criterio que <see cref="ReportesVentasResumenTests"/>.
///
/// El chequeo cross-tenant de estas tres rutas es cobertura ORDINARIA, no mutation-proof
/// (mutation-proof-tests skill, Decision Gate: "cannot name the clause under test"): a diferencia
/// de <see cref="LectorDeSerieTemporal"/> (que tiene un <c>id_tenant = $2</c> propio y separado del
/// alcance por punto de venta), las tres rutas LINQ de esta slice NO agregan ningún predicado de
/// tenant propio — reusan exactamente <c>ServicioDeReportesDeVentas.ResolverPuntosDeVentaAsync</c>,
/// ya probado por <c>ReportesVentasResumenTests</c>. No hay una segunda cláusula que aislar del
/// confound de <c>idEmpresa</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesVentasPorDimensionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        HttpClient Root, int IdCliente, int IdEmpleadoAdmin, int IdTipoComprobanteTx, int IdTipoComprobanteNcx,
        int IdMedioPagoEfectivo, int IdMedioPagoTransferencia);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        // Sin "using": ctx.Root viaja en el Contexto devuelto y se usa después de que este
        // método retorna — mismo criterio que ReportesVentasResumenTests.PrepararAsync.
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
        var idsMedioPago = await dbTenant.MediosPago.OrderBy(m => m.Orden).Select(m => m.Id).ToListAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idTipoComprobanteNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor, root,
            idCliente, resultado.IdUsuarioAdmin, idTipoComprobanteTx, idTipoComprobanteNcx,
            idsMedioPago[0], idsMedioPago[1]);
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

    /// <summary>Siembra directo, sin pasar por <c>ServicioDeVentas</c> — mismo criterio que
    /// <c>ReportesVentasResumenTests.SembrarComprobanteAsync</c>. Devuelve el id del comprobante
    /// para que los tests de <c>por-medio-pago</c> puedan encadenar <see cref="SembrarPagoAsync"/>.</summary>
    private async Task<int> SembrarComprobanteAsync(
        Contexto ctx, DateTimeOffset fecha, decimal total, int? idPuntoVenta = null, int? idEmpleado = null,
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
            IdPuntoVenta = idPuntoVenta ?? ctx.IdPuntoVenta,
            IdEmpleado = idEmpleado ?? ctx.IdEmpleadoAdmin,
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
        return comprobante.Id;
    }

    /// <summary><c>pagos_comprobante.importe</c> siempre no-negativo (CHECK
    /// <c>ck_pagos_comprobante_importe_no_negativo</c>) — el signo de una NCX lo aporta el
    /// <c>Signo</c> del tipo de comprobante del encabezado, nunca este importe.</summary>
    private async Task SembrarPagoAsync(Contexto ctx, int idComprobanteVenta, int idMedioPago, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = idComprobanteVenta,
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Sin endpoint de alta de puntos de venta (<c>OrganizacionEndpoints</c> solo lista/
    /// edita) — sembrado directo, mismo criterio que el resto de este archivo.</summary>
    private async Task<int> SembrarPuntoVentaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta
        {
            IdTenant = ctx.IdTenant,
            IdEmpresa = ctx.IdEmpresa,
            Nombre = nombre,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();
        return puntoVenta.Id;
    }

    private static string Rango(DateOnly desde, DateOnly hasta) =>
        $"desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}";

    // ------------------------------------------------------------------------------------------
    // por-punto-venta
    // ------------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> LlamarPorPuntoVentaAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/reportes/ventas/por-punto-venta?idEmpresa={idEmpresa}&{Rango(desde, hasta)}");

    private static async Task<VentasPorPuntoVenta> ObtenerPorPuntoVentaAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta)
    {
        var respuesta = await LlamarPorPuntoVentaAsync(cliente, idEmpresa, desde, hasta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<VentasPorPuntoVenta>(cuerpo, OpcionesJson)!;
    }

    [Fact]
    public async Task PorPuntoVentaNuncaMuestraVentasDeOtroTenant()
    {
        var ctxA = await PrepararAsync(nameof(PorPuntoVentaNuncaMuestraVentasDeOtroTenant) + "-A");
        var ctxB = await PrepararAsync(nameof(PorPuntoVentaNuncaMuestraVentasDeOtroTenant) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctxB, DateTimeOffset.UtcNow, 999_999m);

        var reporte = await ObtenerPorPuntoVentaAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Empty(reporte.Filas);
    }

    [Fact]
    public async Task PorPuntoVentaExcluyeUnaFilaSoftDeleted()
    {
        var ctx = await PrepararAsync(nameof(PorPuntoVentaExcluyeUnaFilaSoftDeleted));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, eliminado: true);
        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 100m);

        var reporte = await ObtenerPorPuntoVentaAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(100m, fila.Neto);
    }

    [Fact]
    public async Task PorPuntoVentaExcluyeUnComprobanteAnulado()
    {
        var ctx = await PrepararAsync(nameof(PorPuntoVentaExcluyeUnComprobanteAnulado));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, estado: EstadoComprobante.Anulado);
        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 250m);

        var reporte = await ObtenerPorPuntoVentaAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(250m, fila.Neto);
    }

    [Fact]
    public async Task PorPuntoVentaAgrupaCadaPuntoDeVentaConSuPropioSubtotal()
    {
        var ctx = await PrepararAsync(nameof(PorPuntoVentaAgrupaCadaPuntoDeVentaConSuPropioSubtotal));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var idPuntoVenta2 = await SembrarPuntoVentaAsync(ctx, "Sucursal 2");

        await SembrarComprobanteAsync(ctx, mediodiaUtc, 100m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, 200m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, 300m, idPuntoVenta: idPuntoVenta2);

        var reporte = await ObtenerPorPuntoVentaAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(2, reporte.Filas.Count);
        var filaPv1 = Assert.Single(reporte.Filas, f => f.IdPuntoVenta == ctx.IdPuntoVenta);
        Assert.Equal(300m, filaPv1.Neto);
        Assert.Equal(2, filaPv1.CantidadTx);
        Assert.Equal(150m, filaPv1.TicketPromedio);

        var filaPv2 = Assert.Single(reporte.Filas, f => f.IdPuntoVenta == idPuntoVenta2);
        Assert.Equal(300m, filaPv2.Neto);
        Assert.Equal(1, filaPv2.CantidadTx);
        Assert.Equal(300m, filaPv2.TicketPromedio);
    }

    [Fact]
    public async Task PorPuntoVentaUnaNcxReduceElSubtotalDelPuntoDeVentaSinRamaEspecial()
    {
        var ctx = await PrepararAsync(nameof(PorPuntoVentaUnaNcxReduceElSubtotalDelPuntoDeVentaSinRamaEspecial));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarComprobanteAsync(ctx, mediodiaUtc, 300m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, -50m, esNcx: true);

        var reporte = await ObtenerPorPuntoVentaAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(250m, fila.Neto);
        Assert.Equal(1, fila.CantidadTx);
        Assert.Equal(300m, fila.TicketPromedio);
    }

    // ------------------------------------------------------------------------------------------
    // por-vendedor
    // ------------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> LlamarPorVendedorAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/reportes/ventas/por-vendedor?idEmpresa={idEmpresa}&{Rango(desde, hasta)}");

    private static async Task<VentasPorVendedor> ObtenerPorVendedorAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta)
    {
        var respuesta = await LlamarPorVendedorAsync(cliente, idEmpresa, desde, hasta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<VentasPorVendedor>(cuerpo, OpcionesJson)!;
    }

    [Fact]
    public async Task PorVendedorNuncaMuestraVentasDeOtroTenant()
    {
        var ctxA = await PrepararAsync(nameof(PorVendedorNuncaMuestraVentasDeOtroTenant) + "-A");
        var ctxB = await PrepararAsync(nameof(PorVendedorNuncaMuestraVentasDeOtroTenant) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctxB, DateTimeOffset.UtcNow, 999_999m);

        var reporte = await ObtenerPorVendedorAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Empty(reporte.Filas);
    }

    [Fact]
    public async Task PorVendedorExcluyeUnaFilaSoftDeleted()
    {
        var ctx = await PrepararAsync(nameof(PorVendedorExcluyeUnaFilaSoftDeleted));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, eliminado: true);
        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 100m);

        var reporte = await ObtenerPorVendedorAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(100m, fila.Neto);
    }

    [Fact]
    public async Task PorVendedorExcluyeUnComprobanteAnulado()
    {
        var ctx = await PrepararAsync(nameof(PorVendedorExcluyeUnComprobanteAnulado));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, estado: EstadoComprobante.Anulado);
        await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 250m);

        var reporte = await ObtenerPorVendedorAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(250m, fila.Neto);
    }

    /// <summary>spec reportes-de-gestion, Scenario "Grouping by vendedor sums each empleado's TX
    /// independently": vendedor A emite $500, vendedor B emite $700, dos filas, sin fila de total
    /// cruzado (satisfecho estructuralmente — <see cref="VentasPorVendedor"/> no tiene un campo de
    /// total agregado).</summary>
    [Fact]
    public async Task PorVendedorSumaCadaEmpleadoDeFormaIndependiente()
    {
        var ctx = await PrepararAsync(nameof(PorVendedorSumaCadaEmpleadoDeFormaIndependiente));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var meSupervisor = await ctx.Supervisor.GetAsync("/api/auth/me");
        var idEmpleadoSupervisor = (await meSupervisor.Content.ReadFromJsonAsync<UsuarioAutenticado>())!.Id;

        await SembrarComprobanteAsync(ctx, mediodiaUtc, 500m, idEmpleado: ctx.IdEmpleadoAdmin);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, 700m, idEmpleado: idEmpleadoSupervisor);

        var reporte = await ObtenerPorVendedorAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(2, reporte.Filas.Count);
        Assert.Equal(500m, Assert.Single(reporte.Filas, f => f.IdEmpleado == ctx.IdEmpleadoAdmin).Neto);
        Assert.Equal(700m, Assert.Single(reporte.Filas, f => f.IdEmpleado == idEmpleadoSupervisor).Neto);
    }

    [Fact]
    public async Task PorVendedorUnaNcxReduceElSubtotalDelVendedorSinRamaEspecial()
    {
        var ctx = await PrepararAsync(nameof(PorVendedorUnaNcxReduceElSubtotalDelVendedorSinRamaEspecial));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarComprobanteAsync(ctx, mediodiaUtc, 300m);
        await SembrarComprobanteAsync(ctx, mediodiaUtc, -50m, esNcx: true);

        var reporte = await ObtenerPorVendedorAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(250m, fila.Neto);
        Assert.Equal(1, fila.CantidadTx);
        Assert.Equal(300m, fila.TicketPromedio);
    }

    // ------------------------------------------------------------------------------------------
    // por-medio-pago
    // ------------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> LlamarPorMedioPagoAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/reportes/ventas/por-medio-pago?idEmpresa={idEmpresa}&{Rango(desde, hasta)}");

    private static async Task<VentasPorMedioPago> ObtenerPorMedioPagoAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta)
    {
        var respuesta = await LlamarPorMedioPagoAsync(cliente, idEmpresa, desde, hasta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<VentasPorMedioPago>(cuerpo, OpcionesJson)!;
    }

    [Fact]
    public async Task PorMedioPagoNuncaMuestraVentasDeOtroTenant()
    {
        var ctxA = await PrepararAsync(nameof(PorMedioPagoNuncaMuestraVentasDeOtroTenant) + "-A");
        var ctxB = await PrepararAsync(nameof(PorMedioPagoNuncaMuestraVentasDeOtroTenant) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var idComprobanteB = await SembrarComprobanteAsync(ctxB, DateTimeOffset.UtcNow, 999_999m);
        await SembrarPagoAsync(ctxB, idComprobanteB, ctxB.IdMedioPagoEfectivo, 999_999m);

        var reporte = await ObtenerPorMedioPagoAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Empty(reporte.Filas);
    }

    [Fact]
    public async Task PorMedioPagoExcluyeUnPagoDeUnEncabezadoSoftDeleted()
    {
        var ctx = await PrepararAsync(nameof(PorMedioPagoExcluyeUnPagoDeUnEncabezadoSoftDeleted));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var idEliminado = await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, eliminado: true);
        await SembrarPagoAsync(ctx, idEliminado, ctx.IdMedioPagoEfectivo, 999_999m);

        var idVisible = await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 100m);
        await SembrarPagoAsync(ctx, idVisible, ctx.IdMedioPagoEfectivo, 100m);

        var reporte = await ObtenerPorMedioPagoAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(100m, fila.Neto);
    }

    [Fact]
    public async Task PorMedioPagoExcluyeUnPagoDeUnComprobanteAnulado()
    {
        var ctx = await PrepararAsync(nameof(PorMedioPagoExcluyeUnPagoDeUnComprobanteAnulado));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var idAnulado = await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 999_999m, estado: EstadoComprobante.Anulado);
        await SembrarPagoAsync(ctx, idAnulado, ctx.IdMedioPagoEfectivo, 999_999m);

        var idVisible = await SembrarComprobanteAsync(ctx, DateTimeOffset.UtcNow, 250m);
        await SembrarPagoAsync(ctx, idVisible, ctx.IdMedioPagoEfectivo, 250m);

        var reporte = await ObtenerPorMedioPagoAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(250m, fila.Neto);
    }

    [Fact]
    public async Task PorMedioPagoAgrupaCadaMedioConSuPropioSubtotal()
    {
        var ctx = await PrepararAsync(nameof(PorMedioPagoAgrupaCadaMedioConSuPropioSubtotal));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        var idEfectivo = await SembrarComprobanteAsync(ctx, mediodiaUtc, 400m);
        await SembrarPagoAsync(ctx, idEfectivo, ctx.IdMedioPagoEfectivo, 400m);

        var idTransferencia = await SembrarComprobanteAsync(ctx, mediodiaUtc, 600m);
        await SembrarPagoAsync(ctx, idTransferencia, ctx.IdMedioPagoTransferencia, 600m);

        var reporte = await ObtenerPorMedioPagoAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(2, reporte.Filas.Count);
        var filaEfectivo = Assert.Single(reporte.Filas, f => f.IdMedioPago == ctx.IdMedioPagoEfectivo);
        Assert.Equal(400m, filaEfectivo.Neto);
        Assert.Equal(1, filaEfectivo.CantidadPagos);

        var filaTransferencia = Assert.Single(reporte.Filas, f => f.IdMedioPago == ctx.IdMedioPagoTransferencia);
        Assert.Equal(600m, filaTransferencia.Neto);
        Assert.Equal(1, filaTransferencia.CantidadPagos);
    }

    /// <summary>Mutation-proof (skill mutation-proof-tests): la cláusula bajo prueba es
    /// <c>x.Importe * x.Signo</c> en <c>ServicioDeReportesDeVentas.ConsultarPorMedioPagoAsync</c> —
    /// el único lugar de esta etapa donde una NCX resta un importe que, por esquema, SIEMPRE llega
    /// no-negativo (<c>ck_pagos_comprobante_importe_no_negativo</c>). Mutación aplicada: reemplazar
    /// <c>x.Importe * x.Signo</c> por <c>x.Importe</c> (quitar el signo) → este test pasó de
    /// <c>250m</c> a fallar con <c>350m</c> — confirmado, revertido.</summary>
    [Fact]
    public async Task PorMedioPagoUnaNcxReduceElSubtotalDelMedioSinRamaEspecial()
    {
        var ctx = await PrepararAsync(nameof(PorMedioPagoUnaNcxReduceElSubtotalDelMedioSinRamaEspecial));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        var idTx = await SembrarComprobanteAsync(ctx, mediodiaUtc, 300m);
        await SembrarPagoAsync(ctx, idTx, ctx.IdMedioPagoEfectivo, 300m);

        var idNcx = await SembrarComprobanteAsync(ctx, mediodiaUtc, -50m, esNcx: true);
        await SembrarPagoAsync(ctx, idNcx, ctx.IdMedioPagoEfectivo, 50m);

        var reporte = await ObtenerPorMedioPagoAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(250m, fila.Neto);
        Assert.Equal(2, fila.CantidadPagos);
    }
}
