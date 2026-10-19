using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Reportes;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/reportes/ventas/por-hora</c> punta a punta: grilla 7×24 por día de la semana y hora
/// local, con promedios por ocurrencia del día. Siembra directa de comprobantes con
/// <c>Fecha</c> exacta, sin <c>ServicioDeVentas</c> (que exige turno abierto).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesVentasPorHoraTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string ZonaArgentina = "America/Argentina/Buenos_Aires";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    // 2026-08-03 es lunes; Argentina es UTC-3 sin horario de verano.
    private static readonly DateOnly Lunes = new(2026, 8, 3);

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdCliente, int IdEmpleadoAdmin,
        int IdTipoTx, int IdTipoNcx, int IdTipoFa);

    private async Task<Contexto> PrepararAsync(string nombre, bool fijarZonaArgentina = true)
    {
        var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        if (fijarZonaArgentina)
        {
            await FijarZonaAsync(admin, resultado.IdEmpresa, ZonaArgentina);
        }

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();
        var idFa = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, idCliente,
            resultado.IdUsuarioAdmin, idTx, idNcx, idFa);
    }

    private static async Task FijarZonaAsync(HttpClient admin, int idEmpresa, string zona)
    {
        var configuracion = await admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + idEmpresa, new ParametroAlta("zona_horaria", $"\"{zona}\"", null));
        Assert.Equal(HttpStatusCode.OK, configuracion.StatusCode);
    }

    private async Task<int> AgregarPuntoVentaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var puntoVenta = new PuntoVenta
        {
            IdEmpresa = ctx.IdEmpresa, Nombre = nombre, Modo = ModoPuntoVenta.Web, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();
        return puntoVenta.Id;
    }

    /// <summary>Un comprobante sin ítems; el <paramref name="total"/> lleva el signo (negativo en una
    /// NCX). Un espejo fiscal lleva tipo FA, CAE aprobado y <c>IdVentaOrigen</c>.</summary>
    private async Task<int> SembrarComprobanteAsync(
        Contexto ctx, decimal total, DateTimeOffset fecha, bool esNcx = false,
        EstadoComprobante estado = EstadoComprobante.Emitido, int? idVentaOrigenDeEspejo = null, int? idPuntoVenta = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var espejo = idVentaOrigenDeEspejo is not null;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = espejo ? ctx.IdTipoFa : esNcx ? ctx.IdTipoNcx : ctx.IdTipoTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = idPuntoVenta ?? ctx.IdPuntoVenta,
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
        return comprobante.Id;
    }

    /// <summary>Instante UTC de una hora local de Argentina (UTC-3).</summary>
    private static DateTimeOffset Arg(DateOnly dia, int hora, int minuto) =>
        new DateTimeOffset(dia.Year, dia.Month, dia.Day, hora, minuto, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

    private static string Url(int idEmpresa, DateOnly desde, DateOnly hasta, int? idPuntoVenta = null) =>
        $"/api/reportes/ventas/por-hora?idEmpresa={idEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}" +
        (idPuntoVenta is { } pv ? $"&idPuntoVenta={pv}" : string.Empty);

    private static async Task<VentasPorHora> ObtenerAsync(HttpClient cliente, string url)
    {
        var respuesta = await cliente.GetAsync(url);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<VentasPorHora>(cuerpo, OpcionesJson)!;
    }

    private static CeldaDeVentasPorHora Celda(VentasPorHora reporte, int dia, int hora) =>
        reporte.Celdas.Single(c => c.DiaDeLaSemana == dia && c.Hora == hora);

    private static int OcurrenciasDe(VentasPorHora reporte, int dia) =>
        reporte.Dias.Single(d => d.DiaDeLaSemana == dia).Ocurrencias;

    // ---- grilla y contrato ----------------------------------------------------------------------

    [Fact]
    public async Task SinVentasLaGrillaTiene168CeldasEnCero()
    {
        var ctx = await PrepararAsync(nameof(SinVentasLaGrillaTiene168CeldasEnCero));

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes.AddDays(6)));

        Assert.Equal(168, reporte.Celdas.Count);
        Assert.Equal(7, reporte.Dias.Count);
        Assert.All(reporte.Dias, d => Assert.Equal(1, d.Ocurrencias));
        Assert.All(reporte.Celdas, c => Assert.Equal(0m, c.Total + c.Tickets + c.PromedioTotal + c.PromedioTickets));
        Assert.Equal(0m, reporte.Total);
        Assert.Equal(0, reporte.Tickets);
        Assert.Equal(ZonaArgentina, reporte.ZonaHoraria);
    }

    [Fact]
    public async Task LaRespuestaSerializaLosNombresDelContrato()
    {
        var ctx = await PrepararAsync(nameof(LaRespuestaSerializaLosNombresDelContrato));
        await SembrarComprobanteAsync(ctx, 100m, Arg(Lunes, 10, 15));

        var respuesta = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, Lunes, Lunes));
        var json = await respuesta.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            new[] { "desde", "hasta", "zonaHoraria", "total", "tickets", "dias", "celdas" },
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            new[] { "diaDeLaSemana", "ocurrencias" },
            json.GetProperty("dias")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            new[] { "diaDeLaSemana", "hora", "total", "tickets", "promedioTotal", "promedioTickets" },
            json.GetProperty("celdas")[0].EnumerateObject().Select(p => p.Name));
    }

    // ---- zona horaria ---------------------------------------------------------------------------

    /// <summary>Cláusula bajo prueba: <c>TimeZoneInfo.ConvertTime(v.Fecha, zona)</c> en
    /// <c>ObtenerVentasPorHoraAsync</c>. La venta de las 23:30 locales del lunes es martes 02:30Z: sin
    /// la conversión caería en martes/2. El rango incluye ambos días para que la admisión de la fila
    /// no explique el resultado. Mutación aplicada (convertir a UTC): este test falló; revertida, pasó.</summary>
    [Fact]
    public async Task UnaVentaDeLas2330LocalesDelLunesCaeEnLunesHora23()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaDeLas2330LocalesDelLunesCaeEnLunesHora23));
        var instante = new DateTimeOffset(2026, 8, 4, 2, 30, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Tuesday, instante.UtcDateTime.DayOfWeek);
        await SembrarComprobanteAsync(ctx, 750m, instante);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes.AddDays(1)));

        var celda = Celda(reporte, 1, 23);
        Assert.Equal(750m, celda.Total);
        Assert.Equal(1, celda.Tickets);
        Assert.Equal(750m, reporte.Total);
        Assert.Equal(1, reporte.Tickets);
        Assert.Equal(0m, Celda(reporte, 2, 2).Total);
    }

    [Fact]
    public async Task LaZonaConfiguradaDecideLaHoraDeLaCelda()
    {
        var ctx = await PrepararAsync(nameof(LaZonaConfiguradaDecideLaHoraDeLaCelda), fijarZonaArgentina: false);
        await FijarZonaAsync(ctx.Admin, ctx.IdEmpresa, "UTC");
        await SembrarComprobanteAsync(ctx, 300m, new DateTimeOffset(2026, 8, 4, 2, 30, 0, TimeSpan.Zero));

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes.AddDays(1)));

        Assert.Equal("UTC", reporte.ZonaHoraria);
        Assert.Equal(300m, Celda(reporte, 2, 2).Total);
        Assert.Equal(0m, Celda(reporte, 1, 23).Total);
    }

    // ---- promedios ------------------------------------------------------------------------------

    [Fact]
    public async Task DosLunesConVentasDe100Y300PromedianPromedio200()
    {
        var ctx = await PrepararAsync(nameof(DosLunesConVentasDe100Y300PromedianPromedio200));
        await SembrarComprobanteAsync(ctx, 100m, Arg(Lunes, 10, 15));
        await SembrarComprobanteAsync(ctx, 300m, Arg(Lunes.AddDays(7), 10, 15));

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes.AddDays(7)));

        Assert.Equal(2, OcurrenciasDe(reporte, 1));
        var celda = Celda(reporte, 1, 10);
        Assert.Equal(400m, celda.Total);
        Assert.Equal(2, celda.Tickets);
        Assert.Equal(200m, celda.PromedioTotal);
        Assert.Equal(1m, celda.PromedioTickets);
    }

    [Fact]
    public async Task UnLunesSinVentasIgualDivideElPromedio()
    {
        var ctx = await PrepararAsync(nameof(UnLunesSinVentasIgualDivideElPromedio));
        await SembrarComprobanteAsync(ctx, 100m, Arg(Lunes, 10, 15));
        await SembrarComprobanteAsync(ctx, 300m, Arg(Lunes.AddDays(7), 10, 15));

        // El tercer lunes (17/8) no tiene ventas.
        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes.AddDays(14)));

        Assert.Equal(3, OcurrenciasDe(reporte, 1));
        Assert.Equal(2, OcurrenciasDe(reporte, 2));
        var celda = Celda(reporte, 1, 10);
        Assert.Equal(400m, celda.Total);
        Assert.Equal(133.33m, celda.PromedioTotal);
        Assert.Equal(0.67m, celda.PromedioTickets);
    }

    // ---- qué entra en la grilla -----------------------------------------------------------------

    [Fact]
    public async Task LaNotaDeCreditoRestaDelMontoYNoCuentaComoTicket()
    {
        var ctx = await PrepararAsync(nameof(LaNotaDeCreditoRestaDelMontoYNoCuentaComoTicket));
        await SembrarComprobanteAsync(ctx, 1000m, Arg(Lunes, 10, 5));
        await SembrarComprobanteAsync(ctx, 500m, Arg(Lunes, 10, 40));
        await SembrarComprobanteAsync(ctx, -300m, Arg(Lunes, 10, 50), esNcx: true);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes));

        var celda = Celda(reporte, 1, 10);
        Assert.Equal(1200m, celda.Total);
        Assert.Equal(2, celda.Tickets);
        Assert.Equal(1200m, reporte.Total);
        Assert.Equal(2, reporte.Tickets);
    }

    [Fact]
    public async Task LasAnuladasYLosEspejosFiscalesNoEntran()
    {
        var ctx = await PrepararAsync(nameof(LasAnuladasYLosEspejosFiscalesNoEntran));
        var idVenta = await SembrarComprobanteAsync(ctx, 100m, Arg(Lunes, 9, 0));
        await SembrarComprobanteAsync(ctx, 999m, Arg(Lunes, 9, 10), estado: EstadoComprobante.Anulado);
        await SembrarComprobanteAsync(ctx, 100m, Arg(Lunes, 9, 20), idVentaOrigenDeEspejo: idVenta);

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes));

        var celda = Celda(reporte, 1, 9);
        Assert.Equal(100m, celda.Total);
        Assert.Equal(1, celda.Tickets);
        Assert.Equal(100m, reporte.Total);
    }

    [Fact]
    public async Task LasVentasFueraDelRangoLocalNoEntran()
    {
        var ctx = await PrepararAsync(nameof(LasVentasFueraDelRangoLocalNoEntran));
        await SembrarComprobanteAsync(ctx, 10m, Arg(Lunes.AddDays(-1), 23, 59));
        await SembrarComprobanteAsync(ctx, 20m, Arg(Lunes, 0, 0));
        await SembrarComprobanteAsync(ctx, 40m, Arg(Lunes, 23, 59));
        await SembrarComprobanteAsync(ctx, 80m, Arg(Lunes.AddDays(1), 0, 0));

        var reporte = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes));

        Assert.Equal(60m, reporte.Total);
        Assert.Equal(20m, Celda(reporte, 1, 0).Total);
        Assert.Equal(40m, Celda(reporte, 1, 23).Total);
    }

    [Fact]
    public async Task ElFiltroDePuntoDeVentaLimitaLasVentas()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroDePuntoDeVentaLimitaLasVentas));
        var otroPuntoVenta = await AgregarPuntoVentaAsync(ctx, "Sucursal 2");
        await SembrarComprobanteAsync(ctx, 100m, Arg(Lunes, 11, 0));
        await SembrarComprobanteAsync(ctx, 700m, Arg(Lunes, 11, 30), idPuntoVenta: otroPuntoVenta);

        var todos = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes));
        var soloPrimero = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes, ctx.IdPuntoVenta));
        var soloSegundo = await ObtenerAsync(ctx.Admin, Url(ctx.IdEmpresa, Lunes, Lunes, otroPuntoVenta));

        Assert.Equal(800m, Celda(todos, 1, 11).Total);
        Assert.Equal(100m, Celda(soloPrimero, 1, 11).Total);
        Assert.Equal(700m, Celda(soloSegundo, 1, 11).Total);
    }

    // ---- validaciones ---------------------------------------------------------------------------

    [Fact]
    public async Task UnaEmpresaDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxA.Admin.GetAsync(Url(ctxB.IdEmpresa, Lunes, Lunes));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnPuntoDeVentaQueNoEsDeLaEmpresaDevuelve400()
    {
        var ctxA = await PrepararAsync(nameof(UnPuntoDeVentaQueNoEsDeLaEmpresaDevuelve400) + "-A");
        var ctxB = await PrepararAsync(nameof(UnPuntoDeVentaQueNoEsDeLaEmpresaDevuelve400) + "-B");

        var respuesta = await ctxA.Admin.GetAsync(Url(ctxA.IdEmpresa, Lunes, Lunes, ctxB.IdPuntoVenta));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDesdeMayorQueHastaDevuelve400()
    {
        var ctx = await PrepararAsync(nameof(UnDesdeMayorQueHastaDevuelve400));

        var respuesta = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, Lunes.AddDays(1), Lunes));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rango_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnRangoDeMasDe366DiasDevuelve400()
    {
        var ctx = await PrepararAsync(nameof(UnRangoDeMasDe366DiasDevuelve400));

        var respuesta = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, Lunes, Lunes.AddDays(367)));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rango_demasiado_amplio", problema.GetProperty("codigo").GetString());
    }
}
