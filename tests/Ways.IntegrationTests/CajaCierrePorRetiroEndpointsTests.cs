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
/// etapa 5 (cierre por retiro, práctica del dueño): <c>POST /api/caja/turnos/{id}/cierre-por-retiro</c>
/// y <c>GET /api/caja/turnos/{id}/resumen-de-cierre</c> punta a punta — spec arqueo-de-cierre,
/// sección "Cierre Por Retiro". Mismo criterio de siembra directa por EF que
/// <see cref="CajaCierreEndpointsTests"/>: la derivación solo lee <c>pagos_comprobante</c> +
/// <c>comprobantes_venta.{id_turno_caja,estado}</c> + <c>gastos</c> + <c>movimientos_caja</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CajaCierrePorRetiroEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, string NombreAdmin, int IdCliente,
        int IdTipoComprobanteTx, int IdMedioEfectivo, int IdMedioTarjeta, HttpClient Admin);

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
        var nombreAdmin = await db.Usuarios.Where(u => u.Id == resultado.IdUsuarioAdmin).Select(u => u.NombreUsuario).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, nombreAdmin, idCliente,
            idTipoComprobanteTx, idMedioEfectivo, idMedioTarjeta, admin);
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

    // ---- feliz camino: cash+tarjeta, vuelto, retiro intra-turno, gasto en efectivo, refuerzo ----

    [Fact]
    public async Task ElCierrePorRetiroDeclaraElFondoEnElAnclaYElEsperadoEnElRestoYPersisteLaTesoreria()
    {
        var ctx = await PrepararAsync(nameof(ElCierrePorRetiroDeclaraElFondoEnElAnclaYElEsperadoEnElRestoYPersisteLaTesoreria));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);

        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 1000m, vuelto: 50m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioTarjeta, importe: 300m);
        await RegistrarMovimientoAsync(ctx, turno.Id, TipoMovimientoCaja.Retiro, 80m, "retiro intra-turno");
        await RegistrarMovimientoAsync(ctx, turno.Id, TipoMovimientoCaja.Refuerzo, 40m, "refuerzo de prueba");
        await RegistrarGastoAsync(ctx, ctx.IdMedioEfectivo, 60m);

        var solicitud = new SolicitudDeCierrePorRetiro(200m, "Cierre por retiro de prueba");
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre-por-retiro", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resumen = JsonSerializer.Deserialize<ResumenDeCierrePorRetiro>(cuerpo, OpcionesJson)!;

        Assert.Equal(turno.Id, resumen.IdTurnoCaja);
        Assert.Equal(ctx.IdPuntoVenta, resumen.PuntoVenta.Id);
        Assert.Equal(ctx.IdPuntoVenta, resumen.PuntoVenta.Numero);
        Assert.Equal(ctx.NombreAdmin, resumen.Vendedor);
        Assert.Equal(ctx.NombreAdmin, resumen.EmpleadoCierre);
        Assert.Equal(500m, resumen.FondoInicial);

        // Ventas por medio: efectivo neto de vuelto (1000 - 50 = 950), tarjeta tal cual (300) —
        // SIN restar el gasto de 60.
        var ventaEfectivo = resumen.VentasPorMedio.Single(v => v.IdMedioPago == ctx.IdMedioEfectivo);
        var ventaTarjeta = resumen.VentasPorMedio.Single(v => v.IdMedioPago == ctx.IdMedioTarjeta);
        Assert.Equal(950m, ventaEfectivo.Importe);
        Assert.Equal(300m, ventaTarjeta.Importe);
        Assert.Equal(1250m, resumen.TotalVentas);
        Assert.Equal(950m, resumen.VentasEnEfectivoNetas);
        Assert.Equal(60m, resumen.GastosEnEfectivo);
        Assert.Equal(40m, resumen.Refuerzos);

        // Retiros: el intra-turno (80) + el de cierre (200) = 280, en ese orden.
        Assert.Equal(2, resumen.Retiros.Count);
        Assert.Equal(80m, resumen.Retiros[0].Importe);
        Assert.Equal("retiro intra-turno", resumen.Retiros[0].Motivo);
        Assert.Equal(ctx.NombreAdmin, resumen.Retiros[0].Empleado);
        Assert.Equal(200m, resumen.Retiros[1].Importe);
        Assert.Equal("Retiro de cierre de turno", resumen.Retiros[1].Motivo);
        Assert.Equal(280m, resumen.TotalRetiros);

        // diferencia = retiros - (ventas_efectivo_netas - gastos_efectivo + refuerzos)
        //            = 280 - (950 - 60 + 40) = 280 - 930 = -650.
        Assert.Equal(-650m, resumen.Diferencia);

        // Persistencia: el ancla se declaró con el FONDO INICIAL (500), tarjeta con su esperado
        // (300 - 0 gastos = 300); la diferencia del ancla es el negativo de resumen.Diferencia.
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var arqueos = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == turno.Id).ToListAsync();
        Assert.Equal(2, arqueos.Count);

        var arqueoEfectivo = arqueos.Single(a => a.IdMedioPago == ctx.IdMedioEfectivo);
        Assert.Equal(500m, arqueoEfectivo.ImporteDeclarado);
        // esperado(ancla) = 1000 - 60 + 500 + 40 - 280 - 50 = 1150.
        Assert.Equal(1150m, arqueoEfectivo.ImporteEsperado);
        Assert.Equal(650m, arqueoEfectivo.Diferencia);
        Assert.Equal(-arqueoEfectivo.Diferencia, resumen.Diferencia);

        var arqueoTarjeta = arqueos.Single(a => a.IdMedioPago == ctx.IdMedioTarjeta);
        Assert.Equal(300m, arqueoTarjeta.ImporteDeclarado);
        Assert.Equal(300m, arqueoTarjeta.ImporteEsperado);
        Assert.Equal(0m, arqueoTarjeta.Diferencia);

        var turnoPersistido = await db.TurnosCaja.SingleAsync(t => t.Id == turno.Id);
        Assert.Equal(EstadoTurno.Cerrado, turnoPersistido.Estado);
        Assert.Equal(ctx.IdEmpleadoAdmin, turnoPersistido.IdEmpleadoCierre);
        Assert.Equal("Apertura de prueba\nCierre por retiro de prueba", turnoPersistido.Observaciones);

        // Tesorería: UN movimiento, ingreso = Σ retiros (280), egreso = Σ gastos (60).
        var tesoreria = await db.MovimientosTesoreria.SingleAsync(m => m.IdTurnoCaja == turno.Id);
        Assert.Equal(0m, tesoreria.Inicio);
        Assert.Equal(280m, tesoreria.Ingreso);
        Assert.Equal(60m, tesoreria.Egreso);
        Assert.Equal(220m, tesoreria.Final);

        // El movimiento de retiro de cierre quedó persistido como cualquier otro retiro.
        var movimientos = await db.MovimientosCaja
            .Where(m => m.IdTurnoCaja == turno.Id && m.Tipo == TipoMovimientoCaja.Retiro)
            .OrderBy(m => m.CreadoEl)
            .ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.Equal("Retiro de cierre de turno", movimientos[1].Motivo);
        Assert.Equal(200m, movimientos[1].Importe);
        Assert.Equal(ctx.IdEmpleadoAdmin, movimientos[1].IdEmpleado);
    }

    // ---- importe 0: ningún movimiento de retiro se inserta -------------------------------------

    [Fact]
    public async Task ConImporteRetiradoEnCeroNoSeInsertaNingunMovimientoDeRetiro()
    {
        var ctx = await PrepararAsync(nameof(ConImporteRetiradoEnCeroNoSeInsertaNingunMovimientoDeRetiro));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 400m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resumen = JsonSerializer.Deserialize<ResumenDeCierrePorRetiro>(cuerpo, OpcionesJson)!;
        Assert.Empty(resumen.Retiros);
        Assert.Equal(0m, resumen.TotalRetiros);
        // diferencia = 0 - (400 - 0 + 0) = -400.
        Assert.Equal(-400m, resumen.Diferencia);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosCaja.CountAsync(m => m.IdTurnoCaja == turno.Id));

        var arqueoEfectivo = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == turno.Id && a.IdMedioPago == ctx.IdMedioEfectivo);
        Assert.Equal(100m, arqueoEfectivo.ImporteDeclarado);
    }

    // ---- validación: importe negativo -----------------------------------------------------------

    [Fact]
    public async Task UnImporteRetiradoNegativoDaImporteRetiradoInvalidoYElTurnoSigueAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnImporteRetiradoNegativoDaImporteRetiradoInvalidoYElTurnoSigueAbierto));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(-1m, null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("importe_retirado_invalido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var estado = await db.TurnosCaja.Where(t => t.Id == turno.Id).Select(t => t.Estado).SingleAsync();
        Assert.Equal(EstadoTurno.Abierto, estado);
    }

    // ---- turno ya cerrado --------------------------------------------------------------------

    [Fact]
    public async Task UnSegundoCierrePorRetiroDelMismoTurnoDaTurnoYaCerrado()
    {
        var ctx = await PrepararAsync(nameof(UnSegundoCierrePorRetiroDelMismoTurnoDaTurnoYaCerrado));
        var turno = await AbrirTurnoAsync(ctx);

        var primero = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        Assert.Equal(HttpStatusCode.OK, primero.StatusCode);

        var segundo = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);
        var problema = await segundo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_ya_cerrado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CerrarPorRetiroUnTurnoInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(CerrarPorRetiroUnTurnoInexistenteDevuelve404));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos/999999/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- GET resumen-de-cierre: antes de cerrar, después de cerrar, tenant ----------------------

    [Fact]
    public async Task ElResumenDeCierreAntesDeCerrarDaTurnoNoCerrado()
    {
        var ctx = await PrepararAsync(nameof(ElResumenDeCierreAntesDeCerrarDaTurnoNoCerrado));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.GetAsync($"/api/caja/turnos/{turno.Id}/resumen-de-cierre");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_no_cerrado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElResumenDeCierreDespuesDeCerrarCoincideByteABiteConLoQueDevolvioElPost()
    {
        var ctx = await PrepararAsync(nameof(ElResumenDeCierreDespuesDeCerrarCoincideByteABiteConLoQueDevolvioElPost));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 300m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 600m);

        var respuestaPost = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(150m, null));
        var cuerpoPost = await respuestaPost.Content.ReadAsStringAsync();
        Assert.True(respuestaPost.StatusCode == HttpStatusCode.OK, cuerpoPost);

        var respuestaGet = await ctx.Admin.GetAsync($"/api/caja/turnos/{turno.Id}/resumen-de-cierre");
        var cuerpoGet = await respuestaGet.Content.ReadAsStringAsync();
        Assert.True(respuestaGet.StatusCode == HttpStatusCode.OK, cuerpoGet);

        var post = JsonSerializer.Deserialize<ResumenDeCierrePorRetiro>(cuerpoPost, OpcionesJson)!;
        var get = JsonSerializer.Deserialize<ResumenDeCierrePorRetiro>(cuerpoGet, OpcionesJson)!;

        // Comparación campo a campo (mutation-proof-tests regla 6): un record con campos
        // IReadOnlyList<T> NO tiene igualdad profunda automática — List<T> no sobrecarga Equals,
        // así que Assert.Equal(post, get) sobre el objeto entero compararía las listas por
        // referencia y pasaría/fallaría por el motivo equivocado. Se listan las dos colecciones
        // elemento a elemento.
        Assert.Equal(post.IdTurnoCaja, get.IdTurnoCaja);
        Assert.Equal(post.PuntoVenta, get.PuntoVenta);
        Assert.Equal(post.FechaApertura, get.FechaApertura);
        Assert.Equal(post.FechaCierre, get.FechaCierre);
        Assert.Equal(post.Vendedor, get.Vendedor);
        Assert.Equal(post.EmpleadoCierre, get.EmpleadoCierre);
        Assert.Equal(post.FondoInicial, get.FondoInicial);
        Assert.Equal(post.TotalVentas, get.TotalVentas);
        Assert.Equal(post.TotalRetiros, get.TotalRetiros);
        Assert.Equal(post.VentasEnEfectivoNetas, get.VentasEnEfectivoNetas);
        Assert.Equal(post.GastosEnEfectivo, get.GastosEnEfectivo);
        Assert.Equal(post.Refuerzos, get.Refuerzos);
        Assert.Equal(post.Diferencia, get.Diferencia);

        Assert.Equal(post.VentasPorMedio.Count, get.VentasPorMedio.Count);
        Assert.NotEmpty(post.VentasPorMedio);
        foreach (var (esperado, obtenido) in post.VentasPorMedio.Zip(get.VentasPorMedio))
        {
            Assert.Equal(esperado, obtenido);
        }

        Assert.Equal(post.Retiros.Count, get.Retiros.Count);
        Assert.NotEmpty(post.Retiros);
        foreach (var (esperado, obtenido) in post.Retiros.Zip(get.Retiros))
        {
            Assert.Equal(esperado, obtenido);
        }
    }

    [Fact]
    public async Task CerrarPorRetiroUnTurnoDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(CerrarPorRetiroUnTurnoDeOtroTenantDevuelve404) + "-A");
        var turnoDeA = await AbrirTurnoAsync(ctxA);

        var ctxB = await PrepararAsync(nameof(CerrarPorRetiroUnTurnoDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxB.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turnoDeA.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElResumenDeCierreDeUnTurnoDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(ElResumenDeCierreDeUnTurnoDeOtroTenantDevuelve404) + "-A");
        var turnoDeA = await AbrirTurnoAsync(ctxA);
        var cierre = await ctxA.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turnoDeA.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        Assert.Equal(HttpStatusCode.OK, cierre.StatusCode);

        var ctxB = await PrepararAsync(nameof(ElResumenDeCierreDeUnTurnoDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxB.Admin.GetAsync($"/api/caja/turnos/{turnoDeA.Id}/resumen-de-cierre");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- autorización -------------------------------------------------------------------------

    [Fact]
    public async Task UnRolFueraDeOperacionDePosEsRechazadoDelCierrePorRetiroYDelResumenDeCierre()
    {
        var ctx = await PrepararAsync(nameof(UnRolFueraDeOperacionDePosEsRechazadoDelCierrePorRetiroYDelResumenDeCierre));
        var turno = await AbrirTurnoAsync(ctx);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var cierre = await root.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        Assert.Equal(HttpStatusCode.Forbidden, cierre.StatusCode);

        var resumen = await root.GetAsync($"/api/caja/turnos/{turno.Id}/resumen-de-cierre");
        Assert.Equal(HttpStatusCode.Forbidden, resumen.StatusCode);
    }

    // ---- ef-retry-safe-writes: falla transitoria en la escritura no idempotente -----------------

    /// <summary>
    /// ef-retry-safe-writes regla 3: fallo transitorio REAL sobre el INSERT de <c>arqueos_turno</c>
    /// — el mismo statement que <c>CajaCierreAtomicidadYConcurrenciaTests</c> ejercita para el
    /// cierre clásico. <c>CerrarPorRetiroAsync</c> usa <c>FabricaDeEstrategiaSinReintento</c>
    /// (forma (b) del skill: manual, raro, sin clave de idempotencia — igual que
    /// <c>CerrarAsync</c>), así que el fallo NO se reintenta: llega tal cual al operador como
    /// <c>503 resultado_incierto</c> (regla 4, el residual central — este sitio no necesita copia
    /// propia) y la transacción entera se deshace: el turno sigue abierto, sin arqueos, sin
    /// movimiento de retiro de cierre, sin tesorería.
    /// </summary>
    [Fact]
    public async Task UnFalloTransitorioEnElInsertDeArqueosNoSeReintentaYLlegaComoResultadoIncierto()
    {
        var ctx = await PrepararAsync(nameof(UnFalloTransitorioEnElInsertDeArqueosNoSeReintentaYLlegaComoResultadoIncierto));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 200m);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("arqueos_turno", "40001");

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await ctx.Admin.PostAsJsonAsync(
                $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(50m, null));
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("resultado_incierto", problema.GetProperty("codigo").GetString());
        Assert.Equal(1, interceptor.Intentos);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var estado = await db.TurnosCaja.Where(t => t.Id == turno.Id).Select(t => t.Estado).SingleAsync();
        Assert.Equal(EstadoTurno.Abierto, estado);
        Assert.Equal(0, await db.ArqueosTurno.CountAsync(a => a.IdTurnoCaja == turno.Id));
        Assert.Equal(0, await db.MovimientosCaja.CountAsync(m => m.IdTurnoCaja == turno.Id && m.Tipo == TipoMovimientoCaja.Retiro));
        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == turno.Id));

        // El turno sigue abierto y utilizable: un segundo intento (sin el interceptor) cierra bien.
        var reintento = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(50m, null));
        Assert.Equal(HttpStatusCode.OK, reintento.StatusCode);
        Assert.Equal(1, await db.MovimientosCaja.CountAsync(m => m.IdTurnoCaja == turno.Id && m.Tipo == TipoMovimientoCaja.Retiro));
    }

    /// <summary>
    /// La mitad DIRECTA (sin HTTP) del mismo net: <see cref="WaysApiFixture.CrearContextoDeAplicacionConReintentos"/>
    /// arma una estrategia REINTENTABLE de verdad (la que <c>EnableRetryOnFailure</c> usaría) y el
    /// interceptor rompe el primer INSERT de <c>movimientos_caja</c> — el valor discriminante es
    /// <see cref="InterceptorQueRompeLaPrimeraEscritura.Intentos"/> == 1: si <c>CerrarPorRetiroAsync</c>
    /// perdiera <c>FabricaDeEstrategiaSinReintento</c> y volviera a <c>CreateExecutionStrategy()</c>,
    /// el reintento re-entraría al lambda y <c>Intentos</c> daría 2 (y el retiro de cierre se
    /// insertaría DOS veces, ver ef-retry-safe-writes regla 1).
    /// </summary>
    [Fact]
    public async Task ElCierrePorRetiroNoSeReintentaAnteUnFalloTransitorioDelMovimientoDeCierre()
    {
        var ctx = await PrepararAsync(nameof(ElCierrePorRetiroNoSeReintentaAnteUnFalloTransitorioDelMovimientoDeCierre));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, importe: 200m);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("movimientos_caja", "40001");
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(tenantActual, interceptor))
        {
            var contexto = new ContextoFijoDePrueba(ctx.IdEmpleadoAdmin, ctx.IdTenant);
            var reloj = new RelojFijoDePrueba(DateTimeOffset.UtcNow);
            var lector = new LectorDeMovimientosDelTurno(db);
            var servicio = new ServicioDeTurnos(db, reloj, contexto, lector, new LectorDeResumenDeCierrePorRetiro(db, lector));

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => servicio.CerrarPorRetiroAsync(turno.Id, new SolicitudDeCierrePorRetiro(75m, null)));
            Assert.Equal(1, interceptor.Intentos);
            Assert.NotNull(error);
        }

        await using var dbVerificacion = fixture.CrearContextoDeAplicacion(tenantActual);
        Assert.Equal(0, await dbVerificacion.MovimientosCaja.CountAsync(m => m.IdTurnoCaja == turno.Id && m.Tipo == TipoMovimientoCaja.Retiro));
        var estado = await dbVerificacion.TurnosCaja.Where(t => t.Id == turno.Id).Select(t => t.Estado).SingleAsync();
        Assert.Equal(EstadoTurno.Abierto, estado);
    }

    private sealed class ContextoFijoDePrueba(int usuarioId, int idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId { get; } = usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public Ways.Domain.Usuarios.RolConocido Rol => Ways.Domain.Usuarios.RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private sealed class RelojFijoDePrueba(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    // ---- task de contrato: el cuerpo nunca nombra un total ni un campo por medio ----------------

    /// <summary>spec arqueo-de-cierre, Cierre Por Retiro Payload Carries Only The Withdrawal
    /// Amount — mismo criterio de prueba reflexiva que
    /// <c>CajaCierreEndpointsTests.NingunCampoDeSolicitudDeCierreOConteoDeclaradoNombraUnTotalOUnEsperado</c>.</summary>
    [Fact]
    public void NingunCampoDeSolicitudDeCierrePorRetiroNombraUnTotalUnEsperadoOUnMedio()
    {
        var prohibidos = new[] { "total", "esperado", "importeesperado", "subtotal", "declarado", "medio" };

        foreach (var propiedad in typeof(SolicitudDeCierrePorRetiro).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var nombre = propiedad.Name.ToLowerInvariant();
            Assert.DoesNotContain(prohibidos, p => nombre.Contains(p, StringComparison.Ordinal));
        }
    }
}
