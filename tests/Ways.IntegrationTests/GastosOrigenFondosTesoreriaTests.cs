using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-gastos-origen-fondos-pos (PR2): <c>SolicitudDeGasto.OrigenFondos</c> punta a punta —
/// <see cref="OrigenFondosGasto.Tesoreria"/> escribe su propio <see cref="MovimientoTesoreria"/>
/// encadenado (mismo protocolo que el cierre, vía <see cref="EscriturasDeTesoreria"/>), queda
/// EXCLUIDO de todo cálculo de arqueo, y el cierre deja de restar gastos de la tesorería (spec:
/// gastos / origen tesorería no afecta el arqueo del turno).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class GastosOrigenFondosTesoreriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private static async Task<GastoRegistrado> RegistrarGastoAsync(
        Contexto ctx, int idMedioPago, decimal importe, OrigenFondosGasto origenFondos, string concepto = "Gasto de prueba")
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, concepto, null, idMedioPago, null, importe,
                IdComprobanteCompra: null, OrigenFondos: origenFondos));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
    }

    // ---- dto-contract-honesty: OrigenFondos viaja de punta a punta -----------------------------

    [Fact]
    public async Task UnaSolicitudSinOrigenFondosPersisteCajaTurnoPorDefault()
    {
        var ctx = await PrepararAsync(nameof(UnaSolicitudSinOrigenFondosPersisteCajaTurnoPorDefault));
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Sin origen explícito", null,
                ctx.IdMedioEfectivo, null, 100m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(OrigenFondosGasto.CajaTurno, gasto.OrigenFondos);
    }

    [Fact]
    public async Task UnGastoDeOrigenTesoreriaPersisteElOrigenYNaceAtadoAlTurnoAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeOrigenTesoreriaPersisteElOrigenYNaceAtadoAlTurnoAbierto));
        var turno = await AbrirTurnoAsync(ctx);

        var gasto = await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 300m, OrigenFondosGasto.Tesoreria);

        Assert.Equal(OrigenFondosGasto.Tesoreria, gasto.OrigenFondos);
        // Traceability: sigue atado al turno abierto aunque no afecte su arqueo.
        Assert.Equal(turno.Id, gasto.IdTurnoCaja);
    }

    // ---- el movimiento de tesorería que escribe el gasto ----------------------------------------

    [Fact]
    public async Task UnGastoDeOrigenTesoreriaEscribeUnMovimientoDeTesoreriaEncadenadoConIdGasto()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeOrigenTesoreriaEscribeUnMovimientoDeTesoreriaEncadenadoConIdGasto));
        var turno = await AbrirTurnoAsync(ctx);

        var gasto = await RegistrarGastoAsync(
            ctx, ctx.IdMedioEfectivo, 250m, OrigenFondosGasto.Tesoreria, concepto: "Flete de mercadería");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosTesoreria.Where(m => m.IdGasto == gasto.Id).SingleAsync();

        Assert.Equal(TipoMovimientoTesoreria.Gasto, movimiento.Tipo);
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
    public async Task DosGastosDeTesoreriaSeguidosEncadenanElInicioDesdeElFinalDelAnterior()
    {
        var ctx = await PrepararAsync(nameof(DosGastosDeTesoreriaSeguidosEncadenanElInicioDesdeElFinalDelAnterior));
        await AbrirTurnoAsync(ctx);

        var primero = await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 100m, OrigenFondosGasto.Tesoreria);
        var segundo = await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 40m, OrigenFondosGasto.Tesoreria);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filaPrimero = await db.MovimientosTesoreria.Where(m => m.IdGasto == primero.Id).SingleAsync();
        var filaSegundo = await db.MovimientosTesoreria.Where(m => m.IdGasto == segundo.Id).SingleAsync();

        Assert.Equal(0m, filaPrimero.Inicio);
        Assert.Equal(-100m, filaPrimero.Final);
        // La aserción discriminante: el segundo gasto encadena desde el FINAL del primero.
        Assert.Equal(-100m, filaSegundo.Inicio);
        Assert.Equal(-140m, filaSegundo.Final);
    }

    // ---- exclusión del arqueo (mutation-proof-tests: filtro OrigenFondos en LectorDeMovimientosDelTurno) --

    /// <summary>Cláusula bajo prueba: el filtro <c>g.OrigenFondos == OrigenFondosGasto.CajaTurno</c>
    /// en <c>LectorDeMovimientosDelTurno.LeerAsync</c> (gastosPorMedio). Mutación verificada
    /// (mutation-proof-tests): quitando ese filtro, este test falla — el esperado de efectivo pasa
    /// de 1000m a 700m (resta el gasto de tesorería) y el medio tarjeta se vuelve arqueable; con el
    /// filtro restaurado, vuelve a verde.</summary>
    [Fact]
    public async Task UnGastoDeOrigenTesoreriaNoAfectaElEsperadoDeNingunMedioNiLoVuelveArqueable()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeOrigenTesoreriaNoAfectaElEsperadoDeNingunMedioNiLoVuelveArqueable));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);

        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 1000m);
        // Gasto de tesorería sobre TARJETA — si el filtro faltara, tarjeta se volvería arqueable
        // (TuvoFilas) y su esperado pasaría de "sin fila" a -300.
        await RegistrarGastoAsync(ctx, ctx.IdMedioTarjeta, 300m, OrigenFondosGasto.Tesoreria);

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>(
            $"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        var efectivo = resumen!.Medios.Single(m => m.IdMedioPago == ctx.IdMedioEfectivo);
        // 1000 pagos - 0 gastos(caja_turno) + (500 fondo - 0 retiros) = 1500, no 1000 - 300 = 700.
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

        db.PagosComprobante.Add(new Ways.Domain.Ventas.PagoComprobante
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

    // ---- el cierre deja de descontar gastos de la tesorería -------------------------------------

    [Fact]
    public async Task ElCierreEscribeEgresoCeroEIngresoIgualARetirosAunConGastosDeLosDosOrigenes()
    {
        var ctx = await PrepararAsync(nameof(ElCierreEscribeEgresoCeroEIngresoIgualARetirosAunConGastosDeLosDosOrigenes));
        var turno = await AbrirTurnoAsync(ctx);

        var movimiento = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 100m, "retiro de prueba"));
        Assert.Equal(HttpStatusCode.Created, movimiento.StatusCode);

        await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 40m, OrigenFondosGasto.CajaTurno);
        await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 25m, OrigenFondosGasto.Tesoreria);

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

        // El gasto de tesorería sigue teniendo SU PROPIA fila (Tipo = Gasto) — el cierre no la toca.
        Assert.Equal(2, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == turno.Id));
    }

    // ---- el Z-report/historial listan los dos orígenes, sin filtrar -----------------------------

    [Fact]
    public async Task ElHistorialDeGastosExponeElOrigenDeFondosDeCadaFila()
    {
        var ctx = await PrepararAsync(nameof(ElHistorialDeGastosExponeElOrigenDeFondosDeCadaFila));
        await AbrirTurnoAsync(ctx);

        await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 50m, OrigenFondosGasto.CajaTurno);
        await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 70m, OrigenFondosGasto.Tesoreria);

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDeGastos>(
            $"/api/gastos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        Assert.Contains(pagina.Items, i => i.Importe == 50m && i.OrigenFondos == OrigenFondosGasto.CajaTurno);
        Assert.Contains(pagina.Items, i => i.Importe == 70m && i.OrigenFondos == OrigenFondosGasto.Tesoreria);
    }

    // ---- concurrencia: un gasto de tesorería (PV1) y un cierre (PV2), MISMA empresa -------------

    /// <summary>single-read-under-lock: un gasto de origen Tesoreria en PV1 y un cierre en PV2 de
    /// la MISMA empresa comparten la cadena — el lock advisory de <see cref="EscriturasDeTesoreria"/>
    /// tiene que serializarlos. Rendezvous determinístico (mismo patrón que
    /// <c>OrganizacionTests.ElFlipDeModoQuePierdeLaCarreraAuditaYEscribeSobreElEstadoQueVioBajoElLock</c>):
    /// el gasto se pausa justo después de abrir su transacción (antes de CUALQUIER lock, incluido
    /// el del turno), el cierre corre entero sobre un cliente limpio y comitea, y solo entonces el
    /// gasto continúa. Si el gasto leyera el <c>Final</c> de la cadena ANTES del lock (el defecto
    /// que esta skill previene), esta prueba lo mataría: encadenaría desde 0 en vez de desde el
    /// <c>Final</c> del cierre.</summary>
    [Fact]
    public async Task UnGastoDeTesoreriaYUnCierreDeOtroPuntoDeVentaDeLaMismaEmpresaEncadenanSinPerderActualizaciones()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeTesoreriaYUnCierreDeOtroPuntoDeVentaDeLaMismaEmpresaEncadenanSinPerderActualizaciones));

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

        var tareaGasto = clientePausado.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Gasto de tesorería concurrente", null,
                ctx.IdMedioEfectivo, null, 60m, IdComprobanteCompra: null, OrigenFondos: OrigenFondosGasto.Tesoreria));

        await transaccionIniciada.Task;

        // Con el gasto pausado ANTES de cualquier lock, el cierre de PV2 corre entero y comitea.
        var cierrePv2 = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turnoPv2.Id}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 0m)], null));
        var cuerpoCierrePv2 = await cierrePv2.Content.ReadAsStringAsync();
        Assert.True(cierrePv2.StatusCode == HttpStatusCode.OK, cuerpoCierrePv2);

        puedeContinuar.TrySetResult();

        var respuestaGasto = await tareaGasto;
        var cuerpoGasto = await respuestaGasto.Content.ReadAsStringAsync();
        Assert.True(respuestaGasto.StatusCode == HttpStatusCode.Created, cuerpoGasto);
        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpoGasto, OpcionesJson)!;

        await using var verificacion = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filaCierre = await verificacion.MovimientosTesoreria
            .Where(m => m.IdTurnoCaja == turnoPv2.Id && m.Tipo == TipoMovimientoTesoreria.RetiroCaja)
            .SingleAsync();
        var filaGasto = await verificacion.MovimientosTesoreria.Where(m => m.IdGasto == gasto.Id).SingleAsync();

        Assert.Equal(0m, filaCierre.Inicio);
        Assert.Equal(200m, filaCierre.Final);

        // La aserción discriminante (single-read-under-lock): el gasto, que se resolvió DESPUÉS,
        // encadena desde el Final del cierre (200) — nunca desde 0. Si la lectura del `Final`
        // hubiera nacido antes del lock, este valor sería 0 y el CHECK de cadena seguiría
        // cumpliéndose igual (0 - 60 = -60), así que solo esta aserción de valor lo mata.
        Assert.Equal(200m, filaGasto.Inicio);
        Assert.Equal(140m, filaGasto.Final);

        Assert.Equal(2, await verificacion.MovimientosTesoreria.CountAsync(m => m.IdEmpresa == filaCierre.IdEmpresa));
    }
}
