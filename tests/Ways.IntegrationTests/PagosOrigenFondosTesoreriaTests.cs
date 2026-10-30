using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-gastos-origen-fondos-pos (PR2): <c>SolicitudDePago.OrigenFondos</c> punta a punta —
/// <see cref="OrigenFondosPago.Tesoreria"/> escribe su propio <see cref="MovimientoTesoreria"/>
/// encadenado (mismo protocolo que el cierre, vía <see cref="EscriturasDeTesoreria"/>), queda
/// EXCLUIDO de todo cálculo de arqueo, y el cierre deja de restar pagos de la tesorería (spec:
/// pagos / origen tesorería no afecta el arqueo del turno).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagosOrigenFondosTesoreriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdMedioEfectivo, int IdMedioTarjeta, HttpClient Admin,
        string MailAdmin, string PasswordAdmin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idMedioTarjeta = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico)
            .Select(m => m.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, idMedioEfectivo, idMedioTarjeta, admin, mailAdmin,
            resultado.PasswordTemporal);
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial = 0m)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    private static async Task<PagoRegistrado> RegistrarPagoAsync(
        Contexto ctx, int idMedioPago, decimal importe, OrigenFondosPago origenFondos, string concepto = "Gasto de prueba")
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, concepto, null, idMedioPago, null, importe,
                IdComprobanteCompra: null, OrigenFondos: origenFondos));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
    }

    // ---- dto-contract-honesty: OrigenFondos viaja de punta a punta -----------------------------

    [Fact]
    public async Task UnaSolicitudSinOrigenFondosPersisteCajaTurnoPorDefault()
    {
        var ctx = await PrepararAsync(nameof(UnaSolicitudSinOrigenFondosPersisteCajaTurnoPorDefault));
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Sin origen explícito", null,
                ctx.IdMedioEfectivo, null, 100m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(OrigenFondosPago.CajaTurno, pago.OrigenFondos);
    }

    [Fact]
    public async Task UnPagoDeOrigenTesoreriaPersisteElOrigenYNaceAtadoAlTurnoAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeOrigenTesoreriaPersisteElOrigenYNaceAtadoAlTurnoAbierto));
        var turno = await AbrirTurnoAsync(ctx);

        var pago = await RegistrarPagoAsync(ctx, ctx.IdMedioEfectivo, 300m, OrigenFondosPago.Tesoreria);

        Assert.Equal(OrigenFondosPago.Tesoreria, pago.OrigenFondos);
        // Traceability: sigue atado al turno abierto aunque no afecte su arqueo.
        Assert.Equal(turno.Id, pago.IdTurnoCaja);
    }

    // ---- el movimiento de tesorería que escribe el pago ----------------------------------------

    [Fact]
    public async Task UnPagoDeOrigenTesoreriaEscribeUnMovimientoDeTesoreriaEncadenadoConIdPago()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeOrigenTesoreriaEscribeUnMovimientoDeTesoreriaEncadenadoConIdPago));
        var turno = await AbrirTurnoAsync(ctx);

        var pago = await RegistrarPagoAsync(
            ctx, ctx.IdMedioEfectivo, 250m, OrigenFondosPago.Tesoreria, concepto: "Flete de mercadería");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosTesoreria.Where(m => m.IdPago == pago.Id).SingleAsync();

        Assert.Equal(TipoMovimientoTesoreria.Pago, movimiento.Tipo);
        Assert.Equal(turno.Id, movimiento.IdTurnoCaja);
        Assert.Equal(ctx.IdPuntoVenta, movimiento.IdPuntoVenta);
        Assert.Equal("Flete de mercadería", movimiento.Concepto);
        Assert.Equal(0m, movimiento.Inicio);
        Assert.Equal(0m, movimiento.Ingreso);
        Assert.Equal(250m, movimiento.Egreso);
        Assert.Equal(-250m, movimiento.Final);

        var idEmpresa = await db.PuntosVenta.Where(p => p.Id == ctx.IdPuntoVenta).Select(p => p.IdEmpresa).FirstAsync();
        Assert.Equal(idEmpresa, movimiento.IdEmpresa);
    }

    [Fact]
    public async Task DosPagosDeTesoreriaSeguidosEncadenanElInicioDesdeElFinalDelAnterior()
    {
        var ctx = await PrepararAsync(nameof(DosPagosDeTesoreriaSeguidosEncadenanElInicioDesdeElFinalDelAnterior));
        await AbrirTurnoAsync(ctx);

        var primero = await RegistrarPagoAsync(ctx, ctx.IdMedioEfectivo, 100m, OrigenFondosPago.Tesoreria);
        var segundo = await RegistrarPagoAsync(ctx, ctx.IdMedioEfectivo, 40m, OrigenFondosPago.Tesoreria);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filaPrimero = await db.MovimientosTesoreria.Where(m => m.IdPago == primero.Id).SingleAsync();
        var filaSegundo = await db.MovimientosTesoreria.Where(m => m.IdPago == segundo.Id).SingleAsync();

        Assert.Equal(0m, filaPrimero.Inicio);
        Assert.Equal(-100m, filaPrimero.Final);
        // La aserción discriminante: el segundo pago encadena desde el FINAL del primero.
        Assert.Equal(-100m, filaSegundo.Inicio);
        Assert.Equal(-140m, filaSegundo.Final);
    }

    // ---- exclusión del arqueo (mutation-proof-tests: filtro OrigenFondos en LectorDeMovimientosDelTurno) --

    /// <summary>Cláusula bajo prueba: el filtro <c>g.OrigenFondos == OrigenFondosPago.CajaTurno</c>
    /// en <c>LectorDeMovimientosDelTurno.LeerAsync</c> (pagosPorMedio). Mutación verificada
    /// (mutation-proof-tests): quitando ese filtro, este test falla — el esperado de efectivo pasa
    /// de 1000m a 700m (resta el pago de tesorería) y el medio tarjeta se vuelve arqueable; con el
    /// filtro restaurado, vuelve a verde.</summary>
    [Fact]
    public async Task UnPagoDeOrigenTesoreriaNoAfectaElEsperadoDeNingunMedioNiLoVuelveArqueable()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeOrigenTesoreriaNoAfectaElEsperadoDeNingunMedioNiLoVuelveArqueable));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);

        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 1000m);
        // Pago de tesorería sobre TARJETA — si el filtro faltara, tarjeta se volvería arqueable
        // (TuvoFilas) y su esperado pasaría de "sin fila" a -300.
        await RegistrarPagoAsync(ctx, ctx.IdMedioTarjeta, 300m, OrigenFondosPago.Tesoreria);

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>(
            $"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        var efectivo = resumen!.Medios.Single(m => m.IdMedioPago == ctx.IdMedioEfectivo);
        // 1000 pagos - 0 pagos(caja_turno) + (500 fondo - 0 retiros) = 1500, no 1000 - 300 = 700.
        Assert.Equal(1500m, efectivo.ImporteEsperado);

        Assert.DoesNotContain(resumen.Medios, m => m.IdMedioPago == ctx.IdMedioTarjeta);
    }

    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, int idMedioPago, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();

        var comprobante = new Ways.Domain.Ventas.ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = idTipoComprobanteTx,
            Numero = DateTimeOffset.UtcNow.Ticks % 1_000_000,
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = 1,
            IdCliente = idCliente,
            Subtotal = importe,
            DescuentoTotal = 0m,
            Total = importe,
            Estado = Ways.Domain.Ventas.EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.Cobros.Add(new Ways.Domain.Ventas.Cobro
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
    }

    // ---- el cierre deja de descontar pagos de la tesorería -------------------------------------

    [Fact]
    public async Task ElCierreEscribeEgresoCeroEIngresoIgualARetirosAunConPagosDeLosDosOrigenes()
    {
        var ctx = await PrepararAsync(nameof(ElCierreEscribeEgresoCeroEIngresoIgualARetirosAunConPagosDeLosDosOrigenes));
        var turno = await AbrirTurnoAsync(ctx);

        var movimiento = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 100m, "retiro de prueba"));
        Assert.Equal(HttpStatusCode.Created, movimiento.StatusCode);

        await RegistrarPagoAsync(ctx, ctx.IdMedioEfectivo, 40m, OrigenFondosPago.CajaTurno);
        await RegistrarPagoAsync(ctx, ctx.IdMedioEfectivo, 25m, OrigenFondosPago.Tesoreria);

        var cierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 0m)], null));
        var cuerpo = await cierre.Content.ReadAsStringAsync();
        Assert.True(cierre.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filaDeCierre = await db.MovimientosTesoreria
            .Where(m => m.IdTurnoCaja == turno.Id && m.Tipo == TipoMovimientoTesoreria.RetiroCaja)
            .SingleAsync();

        Assert.Equal(100m, filaDeCierre.Ingreso);
        Assert.Equal(0m, filaDeCierre.Egreso);

        // El pago de tesorería sigue teniendo SU PROPIA fila (Tipo = Pago) — el cierre no la toca.
        Assert.Equal(2, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == turno.Id));
    }

    // ---- el Z-report/historial listan los dos orígenes, sin filtrar -----------------------------

    [Fact]
    public async Task ElHistorialDePagosExponeElOrigenDeFondosDeCadaFila()
    {
        var ctx = await PrepararAsync(nameof(ElHistorialDePagosExponeElOrigenDeFondosDeCadaFila));
        await AbrirTurnoAsync(ctx);

        await RegistrarPagoAsync(ctx, ctx.IdMedioEfectivo, 50m, OrigenFondosPago.CajaTurno);
        await RegistrarPagoAsync(ctx, ctx.IdMedioEfectivo, 70m, OrigenFondosPago.Tesoreria);

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDePagos>(
            $"/api/gastos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        Assert.Contains(pagina.Items, i => i.Importe == 50m && i.OrigenFondos == OrigenFondosPago.CajaTurno);
        Assert.Contains(pagina.Items, i => i.Importe == 70m && i.OrigenFondos == OrigenFondosPago.Tesoreria);
    }

    // ---- concurrencia: un pago de tesorería (PV1) y un cierre (PV2), MISMA empresa -------------

    /// <summary>single-read-under-lock: un pago de origen Tesoreria en PV1 y un cierre en PV2 de
    /// la MISMA empresa comparten la cadena — el lock advisory de <see cref="EscriturasDeTesoreria"/>
    /// tiene que serializarlos. Rendezvous determinístico (mismo patrón que
    /// <c>OrganizacionTests.ElFlipDeModoQuePierdeLaCarreraAuditaYEscribeSobreElEstadoQueVioBajoElLock</c>):
    /// el pago se pausa justo después de abrir su transacción (antes de CUALQUIER lock, incluido
    /// el del turno), el cierre corre entero sobre un cliente limpio y comitea, y solo entonces el
    /// pago continúa. Si el pago leyera el <c>Final</c> de la cadena ANTES del lock (el defecto
    /// que esta skill previene), esta prueba lo mataría: encadenaría desde 0 en vez de desde el
    /// <c>Final</c> del cierre.</summary>
    [Fact]
    public async Task UnPagoDeTesoreriaYUnCierreDeOtroPuntoDeVentaDeLaMismaEmpresaEncadenanSinPerderActualizaciones()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeTesoreriaYUnCierreDeOtroPuntoDeVentaDeLaMismaEmpresaEncadenanSinPerderActualizaciones));

        int idPuntoVenta2;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var idEmpresa = await db.PuntosVenta.Where(p => p.Id == ctx.IdPuntoVenta).Select(p => p.IdEmpresa).FirstAsync();
            var ahora = DateTimeOffset.UtcNow;
            var otro = new PuntoVenta
            {
                IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, Nombre = "Local 2", CreatedAt = ahora, UpdatedAt = ahora
            };
            db.PuntosVenta.Add(otro);
            await db.SaveChangesAsync();
            idPuntoVenta2 = otro.Id;
        }

        var turnoPv1 = await AbrirTurnoAsync(ctx);

        var aperturaPv2 = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idPuntoVenta2, 0m, "Apertura PV2"));
        var cuerpoAperturaPv2 = await aperturaPv2.Content.ReadAsStringAsync();
        Assert.True(aperturaPv2.StatusCode == HttpStatusCode.Created, cuerpoAperturaPv2);
        var turnoPv2 = JsonSerializer.Deserialize<TurnoResumen>(cuerpoAperturaPv2, OpcionesJson)!;

        var movimientoRetiroPv2 = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turnoPv2.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 200m, "retiro PV2"));
        Assert.Equal(HttpStatusCode.Created, movimientoRetiroPv2.StatusCode);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        // ctx.Admin ya está logueado, pero su cookie de sesión vive en el HttpClient de fixture —
        // el cliente pausado necesita su PROPIO login contra el factory con el interceptor.
        using var clientePausado = factory.CreateClient();
        var loginPausado = await clientePausado.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, loginPausado.StatusCode);

        var tareaPago = clientePausado.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Gasto de tesorería concurrente", null,
                ctx.IdMedioEfectivo, null, 60m, IdComprobanteCompra: null, OrigenFondos: OrigenFondosPago.Tesoreria));

        await transaccionIniciada.Task;

        // Con el pago pausado ANTES de cualquier lock, el cierre de PV2 corre entero y comitea.
        var cierrePv2 = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turnoPv2.Id}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 0m)], null));
        var cuerpoCierrePv2 = await cierrePv2.Content.ReadAsStringAsync();
        Assert.True(cierrePv2.StatusCode == HttpStatusCode.OK, cuerpoCierrePv2);

        puedeContinuar.TrySetResult();

        var respuestaPago = await tareaPago;
        var cuerpoPago = await respuestaPago.Content.ReadAsStringAsync();
        Assert.True(respuestaPago.StatusCode == HttpStatusCode.Created, cuerpoPago);
        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpoPago, OpcionesJson)!;

        await using var verificacion = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filaCierre = await verificacion.MovimientosTesoreria
            .Where(m => m.IdTurnoCaja == turnoPv2.Id && m.Tipo == TipoMovimientoTesoreria.RetiroCaja)
            .SingleAsync();
        var filaPago = await verificacion.MovimientosTesoreria.Where(m => m.IdPago == pago.Id).SingleAsync();

        Assert.Equal(0m, filaCierre.Inicio);
        Assert.Equal(200m, filaCierre.Final);

        // La aserción discriminante (single-read-under-lock): el pago, que se resolvió DESPUÉS,
        // encadena desde el Final del cierre (200) — nunca desde 0. Si la lectura del `Final`
        // hubiera nacido antes del lock, este valor sería 0 y el CHECK de cadena seguiría
        // cumpliéndose igual (0 - 60 = -60), así que solo esta aserción de valor lo mata.
        Assert.Equal(200m, filaPago.Inicio);
        Assert.Equal(140m, filaPago.Final);

        Assert.Equal(2, await verificacion.MovimientosTesoreria.CountAsync(m => m.IdEmpresa == filaCierre.IdEmpresa));
    }
}
