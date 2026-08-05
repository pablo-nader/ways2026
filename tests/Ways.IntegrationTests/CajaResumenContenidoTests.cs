using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Follow-up de stage-6-turnos-caja, "Resumen parcial D6-content enrichment" (archive-report.md,
/// Deferred/Follow-Ups (a); design.md Data Flow: "GET …/resumen (D6: áreas, medios, tickets,
/// egresos)"; docs/01 D6 "Ver Parcial") — <see cref="LectorDeContenidoDeResumen"/>: cantidad de
/// tickets, primer/último ticket, ingresos por área y egresos por categoría + retiros.
///
/// Deliberadamente en un archivo SEPARADO de <see cref="CajaCierreEndpointsTests"/>: ese archivo
/// prueba la derivación del arqueo (<c>Medios</c>) — invariante intacto, no tocado acá. Este
/// archivo prueba SOLO el contenido nuevo, aditivo, de <see cref="ResumenDeTurno"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CajaResumenContenidoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdCliente, int IdTipoComprobanteTx,
        int IdMedioEfectivo, int IdMedioTarjeta, int IdListaPrecio, int IdAlicuotaIva, HttpClient Admin);

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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idMedioTarjeta = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico)
            .Select(m => m.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista resumen-contenido", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipoComprobanteTx,
            idMedioEfectivo, idMedioTarjeta, lista.Id, idAlicuotaIva, admin);
    }

    private async Task<int> SembrarAreaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return area.Id;
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial = 0m)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    private long _numeroSecuencial = 1;

    /// <summary>Siembra directo un comprobante emitido (o anulado) con UN ítem (área) y UN pago —
    /// mismo criterio que <c>VentasStockYCuentaCorrienteRlsTests</c>: la venta no pasa por el
    /// checkout completo, que no es parte de esta prueba.</summary>
    private async Task<(long Numero, DateTimeOffset Fecha)> SembrarVentaAsync(
        Contexto ctx, int idTurno, int idArea, int idMedioPago, decimal importe, DateTimeOffset fecha,
        EstadoComprobante estado = EstadoComprobante.Emitido)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var numero = Interlocked.Increment(ref _numeroSecuencial);

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = numero,
            Fecha = fecha,
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

        db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            Orden = 1,
            Descripcion = "Ítem de prueba",
            IdArea = idArea,
            IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva,
            PorcentajeIva = 21m,
            Cantidad = 1m,
            PrecioUnitario = importe,
            Descuento = 0m,
            Total = importe,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (numero, fecha);
    }

    private static async Task RegistrarMovimientoAsync(Contexto ctx, int idTurno, TipoMovimientoCaja tipo, decimal importe, string motivo)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/movimientos", new SolicitudDeMovimiento(tipo, importe, motivo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    private static async Task RegistrarGastoAsync(Contexto ctx, CategoriaGasto categoria, int idMedioPago, decimal importe)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(ctx.IdPuntoVenta, categoria, null, null, "Gasto de prueba", null, idMedioPago, null, importe));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
    }

    [Fact]
    public async Task ElResumenExponeTicketsIngresosPorAreaYEgresosDeUnTurnoConActividad()
    {
        var ctx = await PrepararAsync(nameof(ElResumenExponeTicketsIngresosPorAreaYEgresosDeUnTurnoConActividad));
        var idAreaAlmacen = await SembrarAreaAsync(ctx, "Almacén");
        var idAreaVerduleria = await SembrarAreaAsync(ctx, "Verdulería");
        var turno = await AbrirTurnoAsync(ctx);

        var baseFecha = DateTimeOffset.UtcNow;
        var primero = await SembrarVentaAsync(ctx, turno.Id, idAreaAlmacen, ctx.IdMedioEfectivo, 100m, baseFecha);
        await SembrarVentaAsync(ctx, turno.Id, idAreaVerduleria, ctx.IdMedioTarjeta, 200m, baseFecha.AddMinutes(1));
        var ultimo = await SembrarVentaAsync(ctx, turno.Id, idAreaAlmacen, ctx.IdMedioEfectivo, 50m, baseFecha.AddMinutes(2));

        // anulado: no debe aportar ni a la cantidad de tickets ni a los ingresos por área
        // (mismo criterio que la derivación del arqueo, spec: Anulados Are Excluded).
        await SembrarVentaAsync(
            ctx, turno.Id, idAreaAlmacen, ctx.IdMedioEfectivo, 999m, baseFecha.AddMinutes(3), EstadoComprobante.Anulado);

        await RegistrarGastoAsync(ctx, CategoriaGasto.Proveedor, ctx.IdMedioEfectivo, 30m);
        await RegistrarGastoAsync(ctx, CategoriaGasto.Sueldos, ctx.IdMedioEfectivo, 70m);
        await RegistrarMovimientoAsync(ctx, turno.Id, TipoMovimientoCaja.Retiro, 40m, "retiro de prueba");
        // el refuerzo NUNCA es un egreso — no debe aparecer en Egresos.
        await RegistrarMovimientoAsync(ctx, turno.Id, TipoMovimientoCaja.Refuerzo, 10m, "refuerzo de prueba");

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        Assert.Equal(3, resumen!.CantidadTickets);
        Assert.NotNull(resumen.PrimerTicket);
        Assert.NotNull(resumen.UltimoTicket);
        // Se compara por Numero (identidad exacta, sin ambigüedad de precisión) — Fecha viaja
        // por Postgres (resolución de microsegundos) y no se compara byte a byte contra el
        // DateTimeOffset en memoria sembrado, para no flakear por redondeo de precisión.
        Assert.Equal(primero.Numero, resumen.PrimerTicket!.Numero);
        Assert.Equal(ultimo.Numero, resumen.UltimoTicket!.Numero);
        Assert.True(resumen.PrimerTicket.Fecha <= resumen.UltimoTicket.Fecha);

        Assert.Equal(2, resumen.IngresosPorArea.Count);
        var almacen = resumen.IngresosPorArea.Single(a => a.IdArea == idAreaAlmacen);
        Assert.Equal("Almacén", almacen.NombreArea);
        Assert.Equal(150m, almacen.Total); // 100 + 50 — el anulado (999) no suma.
        var verduleria = resumen.IngresosPorArea.Single(a => a.IdArea == idAreaVerduleria);
        Assert.Equal("Verdulería", verduleria.NombreArea);
        Assert.Equal(200m, verduleria.Total);

        Assert.Equal(2, resumen.Egresos.PorCategoria.Count);
        var proveedor = resumen.Egresos.PorCategoria.Single(e => e.Categoria == CategoriaGasto.Proveedor);
        Assert.Equal(30m, proveedor.Total);
        var sueldos = resumen.Egresos.PorCategoria.Single(e => e.Categoria == CategoriaGasto.Sueldos);
        Assert.Equal(70m, sueldos.Total);
        Assert.Equal(40m, resumen.Egresos.Retiros);
    }

    [Fact]
    public async Task ElResumenDeUnTurnoSinActividadExponeCerosYTicketsNulos()
    {
        var ctx = await PrepararAsync(nameof(ElResumenDeUnTurnoSinActividadExponeCerosYTicketsNulos));
        var turno = await AbrirTurnoAsync(ctx);

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        Assert.Equal(0, resumen!.CantidadTickets);
        Assert.Null(resumen.PrimerTicket);
        Assert.Null(resumen.UltimoTicket);
        Assert.Empty(resumen.IngresosPorArea);
        Assert.Empty(resumen.Egresos.PorCategoria);
        Assert.Equal(0m, resumen.Egresos.Retiros);
    }

    [Fact]
    public async Task UnTurnoConUnSoloTicketTieneElMismoTicketComoPrimeroYUltimo()
    {
        var ctx = await PrepararAsync(nameof(UnTurnoConUnSoloTicketTieneElMismoTicketComoPrimeroYUltimo));
        var idArea = await SembrarAreaAsync(ctx, "Almacén");
        var turno = await AbrirTurnoAsync(ctx);

        var unico = await SembrarVentaAsync(ctx, turno.Id, idArea, ctx.IdMedioEfectivo, 500m, DateTimeOffset.UtcNow);

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        Assert.Equal(1, resumen!.CantidadTickets);
        Assert.Equal(unico.Numero, resumen.PrimerTicket!.Numero);
        Assert.Equal(unico.Numero, resumen.UltimoTicket!.Numero);
    }
}
