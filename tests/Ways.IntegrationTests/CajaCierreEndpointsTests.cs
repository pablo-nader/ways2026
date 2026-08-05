using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-6-turnos-caja, Slice 4 (tasks 4.7, 4.8, 4.13, 4.15, 4.16): <c>GET
/// /api/caja/turnos/{id}/resumen</c> y <c>POST /api/caja/turnos/{id}/cierre</c> punta a punta —
/// la derivación, los tres rechazos de <c>ValidadorDeConteos</c>, el ancla no-única, una fila de
/// arqueo por medio con actividad, la cadena de tesorería, y autorización/ADR-8 (spec:
/// arqueo-de-cierre, tesoreria).
///
/// Los pagos/gastos/movimientos de cada turno se siembran DIRECTO por EF (sin pasar por el
/// checkout completo, que no es parte de esta slice — Slice 5 lo conecta) — mismo criterio que
/// <c>VentasStockYCuentaCorrienteRlsTests</c>: la derivación solo lee <c>pagos_comprobante</c> +
/// <c>comprobantes_venta.{id_turno_caja,estado}</c> + <c>gastos</c> + <c>movimientos_caja</c>,
/// nunca <c>items_comprobante_venta</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CajaCierreEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdMedioEfectivo, int IdMedioTarjeta, HttpClient Admin);

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

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idMedioTarjeta = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico)
            .Select(m => m.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipoComprobanteTx,
            idMedioEfectivo, idMedioTarjeta, admin);
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

    /// <summary>Siembra directo un comprobante emitido (o anulado) con UN pago — la derivación
    /// nunca toca <c>items_comprobante_venta</c>, así que esta prueba no los siembra.</summary>
    private async Task SembrarPagoAsync(
        Contexto ctx, int idTurno, int idMedioPago, decimal importe, decimal vuelto = 0m,
        EstadoComprobante estado = EstadoComprobante.Emitido)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
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

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = vuelto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SembrarMedioAsync(Contexto ctx, string nombre, ComportamientoMedioPago comportamiento)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 9, Comportamiento = comportamiento,
            AdmiteVuelto = false, RequiereReferencia = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();

        return medio.Id;
    }

    private static async Task RegistrarMovimientoAsync(Contexto ctx, int idTurno, TipoMovimientoCaja tipo, decimal importe, string motivo)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/movimientos", new SolicitudDeMovimiento(tipo, importe, motivo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    private static async Task RegistrarGastoAsync(Contexto ctx, int idMedioPago, decimal importe)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Gasto de prueba", null, idMedioPago, null, importe));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
    }

    // ---- task 4.13/derivación: resumen y feliz camino de cierre ---------------------------------

    [Fact]
    public async Task ElResumenParcialDerivaFondoPagosVueltoGastosRetiroYRefuerzo()
    {
        var ctx = await PrepararAsync(nameof(ElResumenParcialDerivaFondoPagosVueltoGastosRetiroYRefuerzo));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);

        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 1000m, vuelto: 50m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioTarjeta, importe: 300m);
        await RegistrarMovimientoAsync(ctx, turno.Id, TipoMovimientoCaja.Retiro, 100m, "retiro de prueba");
        await RegistrarMovimientoAsync(ctx, turno.Id, TipoMovimientoCaja.Refuerzo, 50m, "refuerzo de prueba");
        await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 80m);
        await RegistrarGastoAsync(ctx, ctx.IdMedioTarjeta, 20m);

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);
        Assert.Equal(ctx.IdMedioEfectivo, resumen!.IdMedioAncla);

        var efectivo = resumen.Medios.Single(m => m.IdMedioPago == ctx.IdMedioEfectivo);
        var tarjeta = resumen.Medios.Single(m => m.IdMedioPago == ctx.IdMedioTarjeta);

        // efectivo = 1000 - 80 + (500 + 50 - 100 - 50) = 1320 ; tarjeta = 300 - 20 = 280.
        Assert.Equal(1320m, efectivo.ImporteEsperado);
        Assert.Equal(280m, tarjeta.ImporteEsperado);
    }

    [Fact]
    public async Task ElCierreConLosConteosCorrectosEsAceptadoYPersisteLosArqueos()
    {
        var ctx = await PrepararAsync(nameof(ElCierreConLosConteosCorrectosEsAceptadoYPersisteLosArqueos));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);

        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 1000m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioTarjeta, importe: 300m);

        var solicitud = new SolicitudDeCierre(
            [new ConteoDeclarado(ctx.IdMedioEfectivo, 1500m), new ConteoDeclarado(ctx.IdMedioTarjeta, 300m)], null);

        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<TurnoConArqueos>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoTurno.Cerrado, resultado.Estado);
        Assert.Equal(2, resultado.Arqueos.Count);

        var efectivo = resultado.Arqueos.Single(a => a.IdMedioPago == ctx.IdMedioEfectivo);
        Assert.Equal(1500m, efectivo.ImporteEsperado);
        Assert.Equal(1500m, efectivo.ImporteDeclarado);
        Assert.Equal(0m, efectivo.Diferencia);

        // La respuesta de GET .../{id} también expone los mismos arqueos (design: API Surface —
        // "Turno + its arqueos_turno, the Z-report payload").
        var porId = await ctx.Admin.GetFromJsonAsync<TurnoConArqueos>($"/api/caja/turnos/{turno.Id}", OpcionesJson);
        Assert.Equal(2, porId!.Arqueos.Count);
    }

    // ---- task 4.16: una fila por medio con actividad, ninguna para CC ni sin actividad ----------

    [Fact]
    public async Task ElCierreEscribeUnaFilaPorMedioConActividadNingunaParaCcNiParaMedioSinActividad()
    {
        var ctx = await PrepararAsync(nameof(ElCierreEscribeUnaFilaPorMedioConActividadNingunaParaCcNiParaMedioSinActividad));
        var idMedioSinActividad = await SembrarMedioAsync(ctx, "QR", ComportamientoMedioPago.Electronico);
        var idMedioCc = await SembrarMedioAsync(ctx, "Cuenta corriente", ComportamientoMedioPago.CuentaCorriente);

        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 200m);
        await SembrarPagoAsync(ctx, turno.Id, idMedioCc, importe: 400m);

        var solicitud = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 200m)], null);
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<TurnoConArqueos>(cuerpo, OpcionesJson)!;
        Assert.Single(resultado.Arqueos);
        Assert.Equal(ctx.IdMedioEfectivo, resultado.Arqueos[0].IdMedioPago);
        Assert.DoesNotContain(resultado.Arqueos, a => a.IdMedioPago == idMedioSinActividad);
        Assert.DoesNotContain(resultado.Arqueos, a => a.IdMedioPago == idMedioCc);
    }

    // ---- ValidadorDeConteos: los tres rechazos ---------------------------------------------------

    [Fact]
    public async Task FaltarUnMedioArqueableEnLosConteosDaArqueoIncompleto()
    {
        var ctx = await PrepararAsync(nameof(FaltarUnMedioArqueableEnLosConteosDaArqueoIncompleto));
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 100m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioTarjeta, importe: 50m);

        var solicitud = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null);
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("arqueo_incompleto", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task DeclararUnMedioSinActividadDaMedioSinActividadEnElTurno()
    {
        var ctx = await PrepararAsync(nameof(DeclararUnMedioSinActividadDaMedioSinActividadEnElTurno));
        var idMedioSinActividad = await SembrarMedioAsync(ctx, "QR", ComportamientoMedioPago.Electronico);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 100m);

        var solicitud = new SolicitudDeCierre(
            [new ConteoDeclarado(ctx.IdMedioEfectivo, 100m), new ConteoDeclarado(idMedioSinActividad, 0m)], null);
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("medio_sin_actividad_en_el_turno", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task DeclararUnaCuentaCorrienteDaMedioNoArqueable()
    {
        var ctx = await PrepararAsync(nameof(DeclararUnaCuentaCorrienteDaMedioNoArqueable));
        var idMedioCc = await SembrarMedioAsync(ctx, "Cuenta corriente", ComportamientoMedioPago.CuentaCorriente);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 100m);
        await SembrarPagoAsync(ctx, turno.Id, idMedioCc, importe: 500m);

        var solicitud = new SolicitudDeCierre(
            [new ConteoDeclarado(ctx.IdMedioEfectivo, 100m), new ConteoDeclarado(idMedioCc, 500m)], null);
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("medio_no_arqueable", problema.GetProperty("codigo").GetString());
    }

    // ---- 404 / 409 turno_ya_cerrado / ancla no única ---------------------------------------------

    [Fact]
    public async Task CerrarUnTurnoInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(CerrarUnTurnoInexistenteDevuelve404));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos/999999/cierre", new SolicitudDeCierre([], null));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task CerrarUnTurnoDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(CerrarUnTurnoDeOtroTenantDevuelve404) + "-A");
        var turnoDeA = await AbrirTurnoAsync(ctxA);

        var ctxB = await PrepararAsync(nameof(CerrarUnTurnoDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxB.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turnoDeA.Id}/cierre", new SolicitudDeCierre([], null));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSegundoCierreDelMismoTurnoDaTurnoYaCerrado()
    {
        var ctx = await PrepararAsync(nameof(UnSegundoCierreDelMismoTurnoDaTurnoYaCerrado));
        var turno = await AbrirTurnoAsync(ctx);

        var primero = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", new SolicitudDeCierre([], null));
        Assert.Equal(HttpStatusCode.OK, primero.StatusCode);

        var segundo = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", new SolicitudDeCierre([], null));
        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);

        var problema = await segundo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_ya_cerrado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task SinNingunaActividadElCierreConConteosVaciosEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(SinNingunaActividadElCierreConConteosVaciosEsAceptado));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", new SolicitudDeCierre([], null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<TurnoConArqueos>(cuerpo, OpcionesJson)!;
        Assert.Empty(resultado.Arqueos);
    }

    [Fact]
    public async Task DosMediosEfectivoHacenQueElResumenYElCierreDevuelvan409()
    {
        var ctx = await PrepararAsync(nameof(DosMediosEfectivoHacenQueElResumenYElCierreDevuelvan409));
        await SembrarMedioAsync(ctx, "Efectivo caja chica", ComportamientoMedioPago.Efectivo);
        var turno = await AbrirTurnoAsync(ctx);

        var resumen = await ctx.Admin.GetAsync($"/api/caja/turnos/{turno.Id}/resumen");
        Assert.Equal(HttpStatusCode.Conflict, resumen.StatusCode);
        var problemaResumen = await resumen.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("caja_sin_medio_efectivo_unico", problemaResumen.GetProperty("codigo").GetString());

        var cierre = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", new SolicitudDeCierre([], null));
        Assert.Equal(HttpStatusCode.Conflict, cierre.StatusCode);
        var problemaCierre = await cierre.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("caja_sin_medio_efectivo_unico", problemaCierre.GetProperty("codigo").GetString());

        // El UPDATE guardado (statement 1) ya corrió antes de que el ancla no-única tirara —
        // el rollback de la transacción tiene que deshacer TAMBIÉN esa transición de estado
        // (design: The Cierre Transaction, "any failure between 1 and 6 rolls everything back").
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var estado = await db.TurnosCaja.Where(t => t.Id == turno.Id).Select(t => t.Estado).SingleAsync();
        Assert.Equal(EstadoTurno.Abierto, estado);
        Assert.Equal(0, await db.ArqueosTurno.CountAsync(a => a.IdTurnoCaja == turno.Id));
    }

    // ---- task 4.13: identidad byte-a-byte entre resumen y cierre ---------------------------------

    [Fact]
    public async Task ElResumenInmediatamenteAntesDelCierreCoincideByteABiteConLoQuePersisteElCierre()
    {
        var ctx = await PrepararAsync(nameof(ElResumenInmediatamenteAntesDelCierreCoincideByteABiteConLoQuePersisteElCierre));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 300m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 750.33m, vuelto: 12.11m);
        await RegistrarMovimientoAsync(ctx, turno.Id, TipoMovimientoCaja.Refuerzo, 44.50m, "refuerzo de prueba");

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        var conteos = resumen!.Medios.Select(m => new ConteoDeclarado(m.IdMedioPago, m.ImporteEsperado)).ToList();
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", new SolicitudDeCierre(conteos, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<TurnoConArqueos>(cuerpo, OpcionesJson)!;
        foreach (var linea in resumen.Medios)
        {
            var arqueo = resultado.Arqueos.Single(a => a.IdMedioPago == linea.IdMedioPago);
            Assert.Equal(linea.ImporteEsperado, arqueo.ImporteEsperado);
            Assert.Equal(0m, arqueo.Diferencia);
        }
    }

    // ---- tesorería: primera y segunda cadena ------------------------------------------------------

    [Fact]
    public async Task LaTesoreriaEncadenaDesdeElFinalDeLaUltimaFilaDelMismoPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(LaTesoreriaEncadenaDesdeElFinalDeLaUltimaFilaDelMismoPuntoDeVenta));

        var primerTurno = await AbrirTurnoAsync(ctx);
        await RegistrarMovimientoAsync(ctx, primerTurno.Id, TipoMovimientoCaja.Retiro, 100m, "retiro de prueba");
        await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 40m);
        var primerCierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{primerTurno.Id}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, -140m)], null));
        Assert.Equal(HttpStatusCode.OK, primerCierre.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var fila = await db.MovimientosTesoreria.Where(m => m.IdTurnoCaja == primerTurno.Id).SingleAsync();
            Assert.Equal(0m, fila.Inicio);
            Assert.Equal(100m, fila.Ingreso);
            Assert.Equal(40m, fila.Egreso);
            Assert.Equal(60m, fila.Final);
        }

        var segundoTurno = await AbrirTurnoAsync(ctx);
        await RegistrarMovimientoAsync(ctx, segundoTurno.Id, TipoMovimientoCaja.Retiro, 50m, "segundo retiro de prueba");
        var segundoCierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{segundoTurno.Id}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, -50m)], null));
        Assert.Equal(HttpStatusCode.OK, segundoCierre.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var fila = await db.MovimientosTesoreria.Where(m => m.IdTurnoCaja == segundoTurno.Id).SingleAsync();
            Assert.Equal(60m, fila.Inicio);
            Assert.Equal(50m, fila.Ingreso);
            Assert.Equal(0m, fila.Egreso);
            Assert.Equal(110m, fila.Final);

            Assert.Equal(2, await db.MovimientosTesoreria.CountAsync(m => m.IdPuntoVenta == ctx.IdPuntoVenta));
        }
    }

    // ---- autorización -------------------------------------------------------------------------

    [Fact]
    public async Task UnRolFueraDeOperacionDePosEsRechazadoDelCierreYDelResumen()
    {
        var ctx = await PrepararAsync(nameof(UnRolFueraDeOperacionDePosEsRechazadoDelCierreYDelResumen));
        var turno = await AbrirTurnoAsync(ctx);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var resumen = await root.GetAsync($"/api/caja/turnos/{turno.Id}/resumen");
        Assert.Equal(HttpStatusCode.Forbidden, resumen.StatusCode);

        var cierre = await root.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", new SolicitudDeCierre([], null));
        Assert.Equal(HttpStatusCode.Forbidden, cierre.StatusCode);
    }

    // ---- task 4.15: el contrato de cierre nunca acepta un total -----------------------------------

    [Fact]
    public void NingunCampoDeSolicitudDeCierreOConteoDeclaradoNombraUnTotalOUnEsperado()
    {
        var prohibidos = new[] { "total", "esperado", "importeesperado", "subtotal" };

        foreach (var tipo in new[] { typeof(SolicitudDeCierre), typeof(ConteoDeclarado) })
        {
            foreach (var propiedad in tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var nombre = propiedad.Name.ToLowerInvariant();
                Assert.DoesNotContain(prohibidos, p => nombre.Contains(p, StringComparison.Ordinal));
            }
        }
    }
}
