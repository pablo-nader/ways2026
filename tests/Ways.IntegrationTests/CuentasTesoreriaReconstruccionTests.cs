using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Reconstrucción histórica de las cuentas de tesorería contra Postgres real: turnos cerrados cuando el medio no tenía cuenta se
/// acreditan con su fecha de cierre, la cadena se recalcula en orden <c>(fecha, id)</c>, la segunda corrida no escribe nada, un turno
/// ya acreditado no se toca y solo el Admin puede pedirla. Los turnos se cierran por la API y después se les fija una fecha de cierre
/// vieja por SQL, porque el cierre siempre usa la hora actual.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentasTesoreriaReconstruccionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string RutaVistaPrevia = "/api/cuentas-tesoreria/reconstruccion/vista-previa";
    private const string RutaAplicar = "/api/cuentas-tesoreria/reconstruccion";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpleado, int IdCliente, int IdTipoComprobante,
        HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

    private long _numero = 1;

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipo = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipo,
            admin, supervisor, vendedor);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<int> CrearCuentaAsync(Contexto ctx, string nombre, decimal saldoInicial = 0m)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var cuenta = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, SaldoInicial = saldoInicial,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.CuentasTesoreria.Add(cuenta);
        await db.SaveChangesAsync();
        return cuenta.Id;
    }

    private async Task<int> CrearMedioAsync(Contexto ctx, string nombre, int? idCuenta, decimal comision = 0m)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 60, Comportamiento = ComportamientoMedioPago.Electronico,
            AdmiteVuelto = false, RequiereReferencia = false, IdCuentaTesoreria = idCuenta,
            ComisionPorcentaje = comision, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    private async Task AsignarCuentaAsync(Contexto ctx, int idMedio, int idCuenta) =>
        await EjecutarCrudoAsync(ctx, $"UPDATE medios_pago SET id_cuenta_tesoreria = {idCuenta} WHERE id_medio_pago = {idMedio}");

    private async Task EjecutarCrudoAsync(Contexto ctx, string sql)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<int> TurnoCerradoAsync(Contexto ctx, int idMedio, decimal importe, DateTimeOffset? fechaDeCierre)
    {
        var apertura = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de prueba"));
        var cuerpo = await apertura.Content.ReadAsStringAsync();
        Assert.True(apertura.StatusCode == HttpStatusCode.Created, cuerpo);
        var idTurno = JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;

        await SembrarPagoAsync(ctx, idTurno, idMedio, importe);

        var cierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        Assert.True(cierre.StatusCode == HttpStatusCode.OK, await cierre.Content.ReadAsStringAsync());

        if (fechaDeCierre is { } fecha)
        {
            var utc = fecha.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss+00");
            var antes = fecha.UtcDateTime.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss+00");
            await EjecutarCrudoAsync(
                ctx,
                $"UPDATE turnos_caja SET fecha_apertura = '{antes}', fecha_cierre = '{utc}' WHERE id_turno_caja = {idTurno}");
        }

        return idTurno;
    }

    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, int idMedioPago, decimal importe)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = ctx.IdTipoComprobante, Numero = Interlocked.Increment(ref _numero),
            Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta, IdTurnoCaja = idTurno, IdEmpleado = ctx.IdEmpleado,
            IdCliente = ctx.IdCliente, Subtotal = importe, DescuentoTotal = 0m, Total = importe,
            Estado = EstadoComprobante.Emitido, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.Cobros.Add(new Cobro
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = idMedioPago, Importe = importe,
            Vuelto = 0m, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<MovimientoCuenta>> LibroAsync(Contexto ctx, int idCuenta)
    {
        await using var db = Db(ctx);
        return await db.MovimientosCuenta
            .Where(m => m.IdCuentaTesoreria == idCuenta).OrderBy(m => m.Fecha).ThenBy(m => m.Id).AsNoTracking().ToListAsync();
    }

    private static SolicitudDeReconstruccionDeCuentas Pedido(Contexto ctx, string desde, string hasta) =>
        new(ctx.IdEmpresa, DateOnly.Parse(desde), DateOnly.Parse(hasta));

    private static async Task<PlanDeReconstruccion> LeerPlanAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PlanDeReconstruccion>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta)
    {
        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        return documento.RootElement.GetProperty("codigo").GetString()!;
    }

    private static async Task<decimal> SaldoDeLaCuentaAsync(Contexto ctx, int idCuenta)
    {
        var cuenta = await ctx.Admin.GetFromJsonAsync<CuentaTesoreriaListado>($"/api/cuentas-tesoreria/{idCuenta}", OpcionesJson);
        return cuenta!.SaldoActual;
    }

    private static void AfirmarFila(
        MovimientoCuenta fila, TipoMovimientoCuenta tipo, decimal inicio, decimal ingreso, decimal egreso, decimal final)
    {
        Assert.Equal(tipo, fila.Tipo);
        Assert.Equal((inicio, ingreso, egreso, final), (fila.Inicio, fila.Ingreso, fila.Egreso, fila.Final));
    }

    private static readonly DateTimeOffset Dia10 = new(2026, 3, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Dia11 = new(2026, 3, 11, 15, 0, 0, TimeSpan.Zero);

    /// <summary>Dos turnos viejos sin cuenta, la cuenta asignada, un turno nuevo acreditado al cierre y un ajuste manual
    /// después. Saldo inicial 1000, comisión 10 %: el turno nuevo (500) deja 1000 + 500 − 50 = 1450 y el ajuste −20 deja 1430.</summary>
    private async Task<(Contexto Ctx, int Cuenta, int Medio, int Turno1, int Turno2, int TurnoNuevo)> SembrarHistorialAsync(string nombre)
    {
        var ctx = await PrepararAsync(nombre);
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago", saldoInicial: 1000m);
        var medio = await CrearMedioAsync(ctx, "QR MP", idCuenta: null, comision: 10m);

        var turno1 = await TurnoCerradoAsync(ctx, medio, 1000m, Dia10);
        var turno2 = await TurnoCerradoAsync(ctx, medio, 2000m, Dia11);
        Assert.Empty(await LibroAsync(ctx, cuenta));

        await AsignarCuentaAsync(ctx, medio, cuenta);
        var turnoNuevo = await TurnoCerradoAsync(ctx, medio, 500m, fechaDeCierre: null);
        var ajuste = await ctx.Admin.PostAsJsonAsync(
            $"/api/cuentas-tesoreria/{cuenta}/ajustes", new SolicitudDeAjusteDeCuenta("Diferencia", -20m, null));
        Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
        Assert.Equal(1430m, await SaldoDeLaCuentaAsync(ctx, cuenta));

        return (ctx, cuenta, medio, turno1, turno2, turnoNuevo);
    }

    // ---- el caso completo ---------------------------------------------------------------------------

    [Fact]
    public async Task LaVistaPreviaListaLoQueSeAcreditariaYNoEscribeNada()
    {
        var (ctx, cuenta, medio, turno1, turno2, _) = await SembrarHistorialAsync(nameof(LaVistaPreviaListaLoQueSeAcreditariaYNoEscribeNada));
        var antes = await LibroAsync(ctx, cuenta);

        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));

        Assert.Equal((ctx.IdEmpresa, 2, 4), (plan.IdEmpresa, plan.Turnos, plan.Filas));
        Assert.Equal([turno1, turno2], plan.Acreditaciones.Select(a => a.IdTurnoCaja).ToArray());
        Assert.Equal([Dia10, Dia11], plan.Acreditaciones.Select(a => a.FechaCierre).ToArray());
        Assert.Equal([1000m, 2000m], plan.Acreditaciones.Select(a => a.BaseBruta).ToArray());
        Assert.Equal([900m, 1800m], plan.Acreditaciones.Select(a => a.Neto).ToArray());
        Assert.All(plan.Acreditaciones, a => Assert.Equal((medio, cuenta), (a.IdMedioPago, a.IdCuentaTesoreria)));
        Assert.Equal(
            [TipoMovimientoCuenta.Cobro, TipoMovimientoCuenta.Comision],
            plan.Acreditaciones[0].Componentes.Select(c => c.Tipo).ToArray());
        Assert.Equal([null, 10m], plan.Acreditaciones[0].Componentes.Select(c => c.PorcentajeAplicado).ToArray());
        var total = Assert.Single(plan.Cuentas);
        Assert.Equal((cuenta, 4, 2700m, 1430m, 4130m), (total.IdCuentaTesoreria, total.Filas, total.Neto, total.SaldoAntes, total.SaldoDespues));

        var despues = await LibroAsync(ctx, cuenta);
        Assert.Equal(antes.Select(f => (f.Id, f.Inicio, f.Final)), despues.Select(f => (f.Id, f.Inicio, f.Final)));
        Assert.Equal(1430m, await SaldoDeLaCuentaAsync(ctx, cuenta));
    }

    [Fact]
    public async Task AplicarAcreditaConLaFechaDeCierreYRecalculaLasFilasPosterioresIncluidoElAjuste()
    {
        var (ctx, cuenta, medio, turno1, turno2, turnoNuevo) =
            await SembrarHistorialAsync(nameof(AplicarAcreditaConLaFechaDeCierreYRecalculaLasFilasPosterioresIncluidoElAjuste));
        var antes = await LibroAsync(ctx, cuenta);
        Assert.Equal(3, antes.Count);
        AfirmarFila(antes[2], TipoMovimientoCuenta.Ajuste, 1450m, 0m, 20m, 1430m);

        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));

        Assert.Equal((2, 4), (plan.Turnos, plan.Filas));
        var total = Assert.Single(plan.Cuentas);
        Assert.Equal((cuenta, 4, 2700m, 1430m, 4130m), (total.IdCuentaTesoreria, total.Filas, total.Neto, total.SaldoAntes, total.SaldoDespues));

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(7, libro.Count);
        AfirmarFila(libro[0], TipoMovimientoCuenta.Cobro, 1000m, 1000m, 0m, 2000m);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 2000m, 0m, 100m, 1900m);
        AfirmarFila(libro[2], TipoMovimientoCuenta.Cobro, 1900m, 2000m, 0m, 3900m);
        AfirmarFila(libro[3], TipoMovimientoCuenta.Comision, 3900m, 0m, 200m, 3700m);
        AfirmarFila(libro[4], TipoMovimientoCuenta.Cobro, 3700m, 500m, 0m, 4200m);
        AfirmarFila(libro[5], TipoMovimientoCuenta.Comision, 4200m, 0m, 50m, 4150m);
        AfirmarFila(libro[6], TipoMovimientoCuenta.Ajuste, 4150m, 0m, 20m, 4130m);

        Assert.Equal([turno1, turno1, turno2, turno2, turnoNuevo, turnoNuevo, null], libro.Select(f => f.IdTurnoCaja).ToArray());
        Assert.Equal([Dia10, Dia10, Dia11, Dia11], libro.Take(4).Select(f => f.Fecha).ToArray());
        Assert.All(libro.Take(4), fila =>
        {
            Assert.Equal((ctx.IdTenant, cuenta, medio, ctx.IdEmpleado), (fila.IdTenant, fila.IdCuentaTesoreria, fila.IdMedioPago, fila.IdEmpleado));
            Assert.Contains("QR MP", fila.Concepto);
        });
        Assert.Equal(
            ["Cobros con QR MP - turno " + turno1, "Comisión de QR MP - turno " + turno1,
             "Cobros con QR MP - turno " + turno2, "Comisión de QR MP - turno " + turno2],
            libro.Take(4).Select(f => f.Concepto).ToArray());
        Assert.Equal([1000m, 1000m, 2000m, 2000m], libro.Take(4).Select(f => f.BaseBruta!.Value).ToArray());
        Assert.Equal([null, 10m, null, 10m], libro.Take(4).Select(f => f.PorcentajeAplicado).ToArray());

        // Las filas que ya existían conservan id, diferencia y fecha: solo cambian inicio y final.
        Assert.Equal(antes.Select(f => (f.Id, f.Fecha, f.Ingreso, f.Egreso)), libro.Skip(4).Select(f => (f.Id, f.Fecha, f.Ingreso, f.Egreso)));
        Assert.Equal(4130m, await SaldoDeLaCuentaAsync(ctx, cuenta));
    }

    [Fact]
    public async Task UnaSegundaCorridaSobreElMismoRangoNoEscribeNadaYNoEsUnError()
    {
        var (ctx, cuenta, _, _, _, _) = await SembrarHistorialAsync(nameof(UnaSegundaCorridaSobreElMismoRangoNoEscribeNadaYNoEsUnError));
        var primera = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));
        Assert.Equal(4, primera.Filas);
        var tras1 = await LibroAsync(ctx, cuenta);

        var segunda = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));
        var vistaPrevia = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));

        foreach (var plan in new[] { segunda, vistaPrevia })
        {
            Assert.Equal((0, 0), (plan.Turnos, plan.Filas));
            Assert.Empty(plan.Acreditaciones);
            Assert.Empty(plan.Cuentas);
        }

        var tras2 = await LibroAsync(ctx, cuenta);
        Assert.Equal(
            tras1.Select(f => (f.Id, f.Fecha, f.Inicio, f.Ingreso, f.Egreso, f.Final)),
            tras2.Select(f => (f.Id, f.Fecha, f.Inicio, f.Ingreso, f.Egreso, f.Final)));
    }

    [Fact]
    public async Task UnTurnoYaAcreditadoAlCierreNoSeTocaAunqueElRangoLoIncluyaYElMedioTengaOtrosPorcentajes()
    {
        var (ctx, cuenta, medio, _, _, turnoNuevo) =
            await SembrarHistorialAsync(nameof(UnTurnoYaAcreditadoAlCierreNoSeTocaAunqueElRangoLoIncluyaYElMedioTengaOtrosPorcentajes));
        await EjecutarCrudoAsync(ctx, $"UPDATE medios_pago SET comision_porcentaje = 25 WHERE id_medio_pago = {medio}");
        var antes = await LibroAsync(ctx, cuenta);
        var hasta = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd");

        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", hasta)));

        // Los dos turnos viejos entran con el 25 % vigente; el nuevo, acreditado al cierre con 10 %, queda como estaba.
        Assert.Equal(2, plan.Turnos);
        Assert.DoesNotContain(plan.Acreditaciones, a => a.IdTurnoCaja == turnoNuevo);
        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(7, libro.Count);
        Assert.Equal([25m, 25m], libro.Where(f => f.Tipo == TipoMovimientoCuenta.Comision && f.IdTurnoCaja != turnoNuevo).Select(f => f.PorcentajeAplicado!.Value).ToArray());
        var delTurnoNuevo = libro.Where(f => f.IdTurnoCaja == turnoNuevo).ToList();
        Assert.Equal(antes.Where(f => f.IdTurnoCaja == turnoNuevo).Select(f => (f.Id, f.Ingreso, f.Egreso, f.PorcentajeAplicado)),
            delTurnoNuevo.Select(f => (f.Id, f.Ingreso, f.Egreso, f.PorcentajeAplicado)));
        Assert.Equal(10m, delTurnoNuevo.Single(f => f.Tipo == TipoMovimientoCuenta.Comision).PorcentajeAplicado);
    }

    [Fact]
    public async Task ElRangoSeResuelveEnLaZonaDeLaEmpresaYLosTurnosSinCuentaOSinCobrosNoEntran()
    {
        var ctx = await PrepararAsync(nameof(ElRangoSeResuelveEnLaZonaDeLaEmpresaYLosTurnosSinCuentaOSinCobrosNoEntran));
        var cuenta = await CrearCuentaAsync(ctx, "Banco");
        var medio = await CrearMedioAsync(ctx, "Billetera A", idCuenta: null);
        var sinCuenta = await CrearMedioAsync(ctx, "Billetera B", idCuenta: null);

        // 02:30 UTC del 11 es 23:30 del 10 en Buenos Aires; 03:30 UTC del 11 ya es el 11 (00:30) en Buenos Aires.
        var tarde = await TurnoCerradoAsync(ctx, medio, 100m, new DateTimeOffset(2026, 3, 11, 2, 30, 0, TimeSpan.Zero));
        var dia11 = await TurnoCerradoAsync(ctx, medio, 100m, new DateTimeOffset(2026, 3, 11, 3, 30, 0, TimeSpan.Zero));
        var otroMedio = await TurnoCerradoAsync(ctx, sinCuenta, 100m, new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        await AsignarCuentaAsync(ctx, medio, cuenta);

        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-10")));

        Assert.Equal([tarde], plan.Acreditaciones.Select(a => a.IdTurnoCaja).ToArray());
        Assert.DoesNotContain(plan.Acreditaciones, a => a.IdTurnoCaja == otroMedio || a.IdTurnoCaja == dia11);
        var libro = await LibroAsync(ctx, cuenta);
        var fila = Assert.Single(libro);
        Assert.Equal((TipoMovimientoCuenta.Cobro, 0m, 100m, 100m), (fila.Tipo, fila.Inicio, fila.Ingreso, fila.Final));
    }

    // ---- libro ----------------------------------------------------------------------------------------

    [Fact]
    public async Task LasFilasReconstruidasQuedanEnOrdenDeFechaYUnRangoPosteriorAbreConElSaldoCorrecto()
    {
        var (ctx, cuenta, _, _, _, _) = await SembrarHistorialAsync(nameof(LasFilasReconstruidasQuedanEnOrdenDeFechaYUnRangoPosteriorAbreConElSaldoCorrecto));
        await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));

        var completo = await LibroAsync(ctx, cuenta);
        Assert.Equal(7, completo.Count);
        Assert.Equal(completo.OrderBy(i => i.Fecha).ThenBy(i => i.Id).Select(i => i.Id), completo.Select(i => i.Id));
        Assert.Equal([2000m, 1900m, 3900m, 3700m, 4200m, 4150m, 4130m], completo.Select(i => i.Final).ToArray());
        Assert.Equal((1000m, 4130m), (completo[0].Inicio, completo[^1].Final));

        // Un rango que arranca después de las filas históricas abre con el final de la última anterior en (fecha, id).
        var desde = DateTimeOffset.Parse("2026-03-12T00:00:00-03:00");
        var posterior = completo.Where(f => f.Fecha >= desde).ToList();
        Assert.Equal(3700m, completo.Last(f => f.Fecha < desde).Final);
        Assert.Equal(3, posterior.Count);
        Assert.Equal(4130m, posterior[^1].Final);

        var segundoDia = DateTimeOffset.Parse("2026-03-11T00:00:00-03:00");
        var hastaDia11 = DateTimeOffset.Parse("2026-03-11T23:59:59-03:00");
        var medio = completo.Where(f => f.Fecha >= segundoDia && f.Fecha <= hastaDia11).ToList();
        Assert.Equal(1900m, completo.Last(f => f.Fecha < segundoDia).Final);
        Assert.Equal([3900m, 3700m], medio.Select(i => i.Final).ToArray());
        Assert.Equal(3700m, medio[^1].Final);
    }

    // ---- autorización y validación -----------------------------------------------------------------

    [Fact]
    public async Task SoloElAdminReconstruyeYElSupervisorYElVendedorReciben403SinEscribir()
    {
        var (ctx, cuenta, _, _, _, _) = await SembrarHistorialAsync(nameof(SoloElAdminReconstruyeYElSupervisorYElVendedorReciben403SinEscribir));
        var antes = await LibroAsync(ctx, cuenta);

        foreach (var cliente in new[] { ctx.Supervisor, ctx.Vendedor })
        {
            foreach (var ruta in new[] { RutaVistaPrevia, RutaAplicar })
            {
                var respuesta = await cliente.PostAsJsonAsync(ruta, Pedido(ctx, "2026-03-10", "2026-03-11"));
                Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
            }
        }

        Assert.Equal(antes.Count, (await LibroAsync(ctx, cuenta)).Count);
    }

    [Theory]
    [InlineData("2026-03-12", "2026-03-10", "rango_invalido")]
    [InlineData("2025-03-08", "2026-03-10", "rango_demasiado_amplio")]
    public async Task UnRangoInvertidoOMayorA366DiasDa400ConSuCodigo(string desde, string hasta, string codigo)
    {
        var ctx = await PrepararAsync($"{nameof(UnRangoInvertidoOMayorA366DiasDa400ConSuCodigo)}{codigo}");

        foreach (var ruta in new[] { RutaVistaPrevia, RutaAplicar })
        {
            var respuesta = await ctx.Admin.PostAsJsonAsync(ruta, Pedido(ctx, desde, hasta));
            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal(codigo, await CodigoAsync(respuesta));
        }
    }

    [Fact]
    public async Task ElRangoDe366DiasDeDiferenciaSeAceptaYLosCamposFaltantesSeRechazan()
    {
        var ctx = await PrepararAsync(nameof(ElRangoDe366DiasDeDiferenciaSeAceptaYLosCamposFaltantesSeRechazan));

        var limite = await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2025-03-09", "2026-03-10"));
        Assert.Equal(HttpStatusCode.OK, limite.StatusCode);

        foreach (var cuerpo in new[]
                 {
                     """{"desde":"2026-03-10","hasta":"2026-03-11"}""",
                     $$"""{"idEmpresa":{{ctx.IdEmpresa}},"hasta":"2026-03-11"}""",
                     $$"""{"idEmpresa":{{ctx.IdEmpresa}},"desde":"2026-03-10"}"""
                 })
        {
            var respuesta = await ctx.Admin.PostAsync(
                RutaAplicar, new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal("solicitud_invalida", await CodigoAsync(respuesta));
        }
    }

    [Fact]
    public async Task UnaEmpresaInexistenteOAjenaDa404()
    {
        var a = await PrepararAsync(nameof(UnaEmpresaInexistenteOAjenaDa404) + "A");
        var b = await PrepararAsync(nameof(UnaEmpresaInexistenteOAjenaDa404) + "B");

        foreach (var idEmpresa in new[] { 999999, b.IdEmpresa })
        {
            var respuesta = await a.Admin.PostAsJsonAsync(
                RutaAplicar, new SolicitudDeReconstruccionDeCuentas(idEmpresa, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 11)));
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    // ---- carrera con otro escritor ---------------------------------------------------------------------

    /// <summary>Antes del primer INSERT del libro, comitea por otra conexión la fila de cobro del mismo turno y medio que la
    /// reconstrucción está por escribir: lo que haría un cierre o una reconstrucción que se saltara los locks.</summary>
    private sealed class InterceptorQueAcreditaPrimero(
        Func<Task> acreditar) : DbCommandInterceptor
    {
        private int _disparado;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO movimientos_cuenta", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _disparado, 1) == 0)
            {
                await acreditar();
            }

            return result;
        }
    }

    [Fact]
    public async Task UnaAcreditacionConcurrenteDelMismoTurnoYMedioDa409ConSuCodigoSinEscribirNadaDeLaReconstruccion()
    {
        var (ctx, cuenta, medio, turno1, _, _) = await SembrarHistorialAsync(
            nameof(UnaAcreditacionConcurrenteDelMismoTurnoYMedioDa409ConSuCodigoSinEscribirNadaDeLaReconstruccion));
        var antes = await LibroAsync(ctx, cuenta);

        var interceptor = new InterceptorQueAcreditaPrimero(() => EjecutarCrudoAsync(
            ctx,
            "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, concepto, inicio, ingreso, egreso, final, " +
            $"id_empleado, id_turno_caja, id_medio_pago) VALUES ({ctx.IdTenant}, {cuenta}, '2026-03-10 15:00:00+00', 'cobro', " +
            $"'Acreditación concurrente', 0, 1, 0, 1, {ctx.IdEmpleado}, {turno1}, {medio})"));

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11"));
        }

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("reconstruccion_concurrente", await CodigoAsync(respuesta));
        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(antes.Count + 1, libro.Count);
        Assert.Equal(1, libro.Count(f => f.Concepto == "Acreditación concurrente"));
        Assert.DoesNotContain(libro, f => f.IdTurnoCaja == turno1 && f.Tipo == TipoMovimientoCuenta.Comision);
    }
}
