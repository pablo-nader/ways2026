using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Gastos;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 5a (design: G2/G3 — minimal aggregation, decisión 10; spec
/// historico-de-cajas: G2 Detail Reuses ResumenDeTurno Plus Ticket And Gasto Listings, Role Split
/// — Turno Detail Under OperacionDePos): <c>GET /api/caja/turnos/{id}/detalle</c> — la casa de las
/// 4 pruebas (cruce de tenant, anulados excluidos como el resumen, fixture hand-computed) más el
/// caso de rol que motiva la co-locación: un Vendedor puede leer el Z-report del turno que él
/// mismo cerró.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DetalleDeTurnoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdCliente, int IdTipoComprobanteTx,
        int IdMedioEfectivo, HttpClient Admin, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
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

        var corto = Guid.NewGuid().ToString("N")[..8];
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var altaVendedor = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"vendedor-{corto}", mailVendedor, (int)RolConocido.Vendedor, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        var vendedor = fixture.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipoComprobanteTx,
            idMedioEfectivo, admin, vendedor);
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx, HttpClient cliente, decimal fondoInicial = 0m) =>
        (await (await cliente.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba")))
            .Content.ReadFromJsonAsync<TurnoResumen>(OpcionesJson))!;

    private static async Task CerrarTurnoAsync(HttpClient cliente, int idTurno, int idMedioPago, decimal declarado)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(idMedioPago, declarado)], "Cierre de prueba detalle"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    /// <summary>Mismo criterio que <c>CajaResumenContenidoTests.SembrarVentaAsync</c> — siembra
    /// directo, sin pasar por el checkout completo.</summary>
    private async Task SembrarVentaAsync(
        Contexto ctx, int idTurno, decimal importe, EstadoComprobante estado = EstadoComprobante.Emitido)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var numero = Interlocked.Increment(ref _numeroSecuencial);

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = numero,
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = importe,
            DescuentoTotal = 0m,
            Total = importe,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        // El pago (mismo importe, medio efectivo) es lo que hace al medio "con actividad" para
        // CalculadorDeArqueo — sin esto el turno no tendría ningún medio arqueable.
        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = ctx.IdMedioEfectivo,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static async Task RegistrarGastoAsync(Contexto ctx, HttpClient cliente, int idMedioPago, decimal importe)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Gasto de prueba", null, idMedioPago, null, importe));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    // ---- task 5b.7: 4-test pattern -----------------------------------------------------------

    [Fact]
    public async Task ElDetalleDeUnTurnoDeOtroTenantEs404()
    {
        var ctxA = await PrepararAsync(nameof(ElDetalleDeUnTurnoDeOtroTenantEs404) + "A");
        var ctxB = await PrepararAsync(nameof(ElDetalleDeUnTurnoDeOtroTenantEs404) + "B");

        var turnoB = await AbrirTurnoAsync(ctxB, ctxB.Admin, 100m);
        await CerrarTurnoAsync(ctxB.Admin, turnoB.Id, ctxB.IdMedioEfectivo, 100m);

        var respuesta = await ctxA.Admin.GetAsync($"/api/caja/turnos/{turnoB.Id}/detalle");

        // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF/RLS ya deja
        // invisible un turno ajeno (mismo criterio que ServicioDeResumenDeTurno.ObtenerAsync).
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Mismo filtro que la derivación del resumen (spec: Anulados Are Excluded From The
    /// Derivation) — un comprobante anulado no aparece en <see cref="DetalleDeTurno.Tickets"/>.
    /// </summary>
    [Fact]
    public async Task LosTicketsAnuladosQuedanExcluidosDelDetalle()
    {
        var ctx = await PrepararAsync(nameof(LosTicketsAnuladosQuedanExcluidosDelDetalle));
        var turno = await AbrirTurnoAsync(ctx, ctx.Admin, 0m);

        await SembrarVentaAsync(ctx, turno.Id, 100m);
        await SembrarVentaAsync(ctx, turno.Id, 999m, EstadoComprobante.Anulado);
        await CerrarTurnoAsync(ctx.Admin, turno.Id, ctx.IdMedioEfectivo, 100m);

        var detalle = await ctx.Admin.GetFromJsonAsync<DetalleDeTurno>($"/api/caja/turnos/{turno.Id}/detalle", OpcionesJson);

        Assert.NotNull(detalle);
        var ticket = Assert.Single(detalle!.Tickets);
        Assert.Equal(100m, ticket.Total);
    }

    /// <summary>task 5b.7 (hand-computed fixture equality): <see cref="DetalleDeTurno.Resumen"/>
    /// es EL MISMO payload que <c>/resumen</c> (mismo invariante que
    /// <c>ServicioDeResumenDeTurno</c>), y <see cref="DetalleDeTurno.Tickets"/>/<see
    /// cref="DetalleDeTurno.Gastos"/> tienen exactamente las filas sembradas.</summary>
    [Fact]
    public async Task ElDetalleReponeElMismoResumenMasLosTicketsYGastosSembrados()
    {
        var ctx = await PrepararAsync(nameof(ElDetalleReponeElMismoResumenMasLosTicketsYGastosSembrados));
        var turno = await AbrirTurnoAsync(ctx, ctx.Admin, 500m);

        await SembrarVentaAsync(ctx, turno.Id, 100m);
        await SembrarVentaAsync(ctx, turno.Id, 200m);
        await RegistrarGastoAsync(ctx, ctx.Admin, ctx.IdMedioEfectivo, 30m);

        // esperado efectivo = 500 (fondo) + 100 + 200 - 30 (gasto) = 770.
        await CerrarTurnoAsync(ctx.Admin, turno.Id, ctx.IdMedioEfectivo, 770m);

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        var detalle = await ctx.Admin.GetFromJsonAsync<DetalleDeTurno>($"/api/caja/turnos/{turno.Id}/detalle", OpcionesJson);

        Assert.NotNull(resumen);
        Assert.NotNull(detalle);
        Assert.Equal(resumen!.IdMedioAncla, detalle!.Resumen.IdMedioAncla);
        var esperadoEfectivo = detalle.Resumen.Medios.Single(m => m.IdMedioPago == ctx.IdMedioEfectivo).ImporteEsperado;
        Assert.Equal(770m, esperadoEfectivo);

        Assert.Equal(2, detalle.Tickets.Count);
        Assert.Equal(300m, detalle.Tickets.Sum(t => t.Total));
        var gasto = Assert.Single(detalle.Gastos);
        Assert.Equal(30m, gasto.Importe);
    }

    /// <summary>judgment-day fix (Judge B, MAJOR, mutation-proven): el filtro por
    /// <c>IdTurnoCaja</c> en <see cref="LectorDeLineasDelTurno"/> es lo único que separa el detalle
    /// de DOS turnos del MISMO tenant — RLS y los fixtures de un solo turno no lo cubren. Se abren y
    /// cierran dos turnos consecutivos del mismo PV, cada uno con su propio ticket y gasto (importes
    /// únicos), y se verifica que el detalle del segundo turno solo trae sus propias filas.</summary>
    [Fact]
    public async Task ElDetalleDeUnTurnoExcluyeLasLineasDeOtroTurnoDelMismoTenant()
    {
        var ctx = await PrepararAsync(nameof(ElDetalleDeUnTurnoExcluyeLasLineasDeOtroTurnoDelMismoTenant));

        var turnoA = await AbrirTurnoAsync(ctx, ctx.Admin, 0m);
        await SembrarVentaAsync(ctx, turnoA.Id, 555m);
        await RegistrarGastoAsync(ctx, ctx.Admin, ctx.IdMedioEfectivo, 45m);
        await CerrarTurnoAsync(ctx.Admin, turnoA.Id, ctx.IdMedioEfectivo, 510m);

        var turnoB = await AbrirTurnoAsync(ctx, ctx.Admin, 0m);
        await SembrarVentaAsync(ctx, turnoB.Id, 321m);
        await RegistrarGastoAsync(ctx, ctx.Admin, ctx.IdMedioEfectivo, 17m);
        await CerrarTurnoAsync(ctx.Admin, turnoB.Id, ctx.IdMedioEfectivo, 304m);

        var detalle = await ctx.Admin.GetFromJsonAsync<DetalleDeTurno>($"/api/caja/turnos/{turnoB.Id}/detalle", OpcionesJson);

        Assert.NotNull(detalle);
        var ticket = Assert.Single(detalle!.Tickets);
        Assert.Equal(321m, ticket.Total);
        Assert.DoesNotContain(detalle.Tickets, t => t.Total == 555m);

        var gasto = Assert.Single(detalle.Gastos);
        Assert.Equal(17m, gasto.Importe);
        Assert.DoesNotContain(detalle.Gastos, g => g.Importe == 45m);
    }

    // ---- task 5b.8: el Vendedor lee el Z-report del turno que él mismo cerró ------------------

    [Fact]
    public async Task UnVendedorLeeElDetalleDelTurnoQueElMismoCerro()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorLeeElDetalleDelTurnoQueElMismoCerro));

        // fondoInicial != 0 — mueve físicamente el ancla (CalculadorDeArqueo), así el medio
        // efectivo queda arqueable y el conteo declarado no dispara medio_sin_actividad_en_el_turno.
        var turno = await AbrirTurnoAsync(ctx, ctx.Vendedor, 100m);
        await CerrarTurnoAsync(ctx.Vendedor, turno.Id, ctx.IdMedioEfectivo, 100m);

        var respuesta = await ctx.Vendedor.GetAsync($"/api/caja/turnos/{turno.Id}/detalle");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }
}
