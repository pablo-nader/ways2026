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
using Ways.Domain.Pagos;
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

    private static async Task ConciliarHastaAsync(Contexto ctx, DateTimeOffset hasta)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/tesoreria/conciliaciones", new SolicitudDeConciliacion(ctx.IdEmpresa, hasta, null));
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());
    }

    /// <summary>Evidencia de mutación (mutation-proof-tests), registrada al escribir las pruebas: (1) hacer que el planeo trate todo turno como no congelado (<c>congelado = false</c>) hace fallar las dos pruebas con marca; (2) cambiar <c>&lt;=</c> por <c>&lt;</c>
    /// en <c>ReglasDeConciliacion.EstaCongelado</c> hace fallar la del borde (turno con fecha de cierre igual a la marca); (3) quitar la lectura
    /// de la marca de la vista previa hace fallar la del plan informado.</summary>
    [Fact]
    public async Task UnTurnoConFechaDeCierreIgualALaMarcaNoSeReconstruyeYElPosteriorSiYLaVistaPreviaLoInforma()
    {
        var (ctx, cuenta, _, turno1, turno2, _) = await SembrarHistorialAsync(
            nameof(UnTurnoConFechaDeCierreIgualALaMarcaNoSeReconstruyeYElPosteriorSiYLaVistaPreviaLoInforma));
        await ConciliarHastaAsync(ctx, Dia10);

        var vista = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));
        Assert.Equal((1, 2, Dia10), (vista.Turnos, vista.Filas, vista.ConciliadoHasta));
        Assert.Equal([turno2], vista.Acreditaciones.Select(a => a.IdTurnoCaja).ToArray());
        Assert.Equal([turno1], vista.Congeladas.Select(a => a.IdTurnoCaja).ToArray());

        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));
        Assert.Equal((1, 2, Dia10), (plan.Turnos, plan.Filas, plan.ConciliadoHasta));
        Assert.Equal([turno1], plan.Congeladas.Select(a => a.IdTurnoCaja).ToArray());

        var libro = await LibroAsync(ctx, cuenta);
        Assert.DoesNotContain(libro, f => f.IdTurnoCaja == turno1);
        Assert.Equal(2, libro.Count(f => f.IdTurnoCaja == turno2));
        Assert.Equal(1000m + 1800m + 450m - 20m, await SaldoDeLaCuentaAsync(ctx, cuenta));

        var otra = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));
        Assert.Equal((0, 0), (otra.Turnos, otra.Filas));
        Assert.Equal([turno1], otra.Congeladas.Select(a => a.IdTurnoCaja).ToArray());
    }

    [Fact]
    public async Task UnaMarcaUnSegundoAnteriorAlCierreNoCongelaElTurnoYUnaPosteriorALosDosNoEscribeNada()
    {
        var (ctx, cuenta, _, turno1, turno2, _) = await SembrarHistorialAsync(
            nameof(UnaMarcaUnSegundoAnteriorAlCierreNoCongelaElTurnoYUnaPosteriorALosDosNoEscribeNada));
        await ConciliarHastaAsync(ctx, Dia10.AddSeconds(-1));

        var vista = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));
        Assert.Equal((2, 4, 0), (vista.Turnos, vista.Filas, vista.Congeladas.Count));
        Assert.Equal([turno1, turno2], vista.Acreditaciones.Select(a => a.IdTurnoCaja).ToArray());

        await ConciliarHastaAsync(ctx, Dia11);
        var antes = await LibroAsync(ctx, cuenta);
        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));

        Assert.Equal((0, 0, 0), (plan.Turnos, plan.Filas, plan.Cuentas.Count));
        Assert.Equal([turno1, turno2], plan.Congeladas.Select(a => a.IdTurnoCaja).ToArray());
        var despues = await LibroAsync(ctx, cuenta);
        Assert.Equal(antes.Select(f => (f.Id, f.Inicio, f.Final)), despues.Select(f => (f.Id, f.Inicio, f.Final)));
    }

    [Fact]
    public async Task SinMarcaDeConciliacionNingunTurnoSeOmiteYElPlanNoInformaMarca()
    {
        var (ctx, cuenta, _, turno1, turno2, _) = await SembrarHistorialAsync(
            nameof(SinMarcaDeConciliacionNingunTurnoSeOmiteYElPlanNoInformaMarca));
        var sinMarca = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));

        Assert.Null(sinMarca.ConciliadoHasta);
        Assert.Empty(sinMarca.Congeladas);
        Assert.Equal([turno1, turno2], sinMarca.Acreditaciones.Select(a => a.IdTurnoCaja).ToArray());
        Assert.Equal(1430m, await SaldoDeLaCuentaAsync(ctx, cuenta));
    }

    /// <summary>Evidencia de mutación: quitar <c>GuardaDeConciliacion.TomarCompartidoAsync</c> de <c>AplicarAsync</c> hace fallar la
    /// espera (la aplicación termina con el lock exclusivo retenido).</summary>
    [Fact]
    public async Task AplicarEsperaAlEscritorDeLaMarcaYLeeLaMarcaRecienDespuesDeQueTermina()
    {
        var (ctx, cuenta, _, turno1, turno2, _) = await SembrarHistorialAsync(
            nameof(AplicarEsperaAlEscritorDeLaMarcaYLeeLaMarcaRecienDespuesDeQueTermina));
        Task<HttpResponseMessage> aplicacion;
        await using (var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant))
        await using (var transaccion = await conexion.BeginTransactionAsync())
        {
            await using (var lockExclusivo = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conexion, transaccion))
            {
                lockExclusivo.Parameters.Add(new NpgsqlParameter { Value = GuardaDeConciliacion.ClaveDe(ctx.IdTenant, ctx.IdEmpresa) });
                await lockExclusivo.ExecuteNonQueryAsync();
            }

            aplicacion = ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11"));
            var primera = await Task.WhenAny(aplicacion, Task.Delay(1500));
            Assert.False(ReferenceEquals(primera, aplicacion), "La aplicación no esperó al escritor de la marca.");

            await using (var dbMarca = Db(ctx))
            {
                dbMarca.ConciliacionesTesoreria.Add(new ConciliacionTesoreria
                {
                    IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Hasta = Dia11, IdUsuario = ctx.IdEmpleado,
                    CreadoEl = DateTimeOffset.UtcNow
                });
                await dbMarca.SaveChangesAsync();
            }

            await transaccion.CommitAsync();
        }

        var plan = await LeerPlanAsync(await aplicacion);
        Assert.Equal((0, 0, Dia11), (plan.Turnos, plan.Filas, plan.ConciliadoHasta));
        Assert.Equal([turno1, turno2], plan.Congeladas.Select(a => a.IdTurnoCaja).ToArray());
        Assert.DoesNotContain(await LibroAsync(ctx, cuenta), f => f.IdTurnoCaja == turno1 || f.IdTurnoCaja == turno2);
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

    // ---- débitos de pagos ------------------------------------------------------------------------------

    /// <summary>Alta administrativa de un pago de una sola línea, con la fecha llevada por SQL a <paramref name="fecha"/>: el alta
    /// siempre fecha el pago en el día del negocio.</summary>
    private async Task<int> PagoAsync(Contexto ctx, int idMedio, decimal importe, DateTimeOffset fecha)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", new
        {
            fecha = FechaDelNegocio.Hoy().ToString("yyyy-MM-dd"), idEmpresa = ctx.IdEmpresa, categoria = "Servicios",
            concepto = "Luz", lineas = new[] { new { idMedioPago = idMedio, importe, referencia = (string?)null } }
        });
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var idPago = JsonSerializer.Deserialize<JsonElement>(cuerpo).GetProperty("id").GetInt32();

        var utc = fecha.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss+00");
        await EjecutarCrudoAsync(ctx, $"UPDATE pagos SET fecha = '{utc}' WHERE id_pago = {idPago}");
        return idPago;
    }

    /// <summary>Sobre el historial de <see cref="SembrarHistorialAsync"/>, un pago de 300 con otro medio sin cuenta, fechado entre
    /// los dos turnos viejos; después el medio pasa a la misma cuenta. Saldo esperado: 1430 + 2700 − 300 = 3830.</summary>
    [Fact]
    public async Task UnPagoViejoSinDebitoSeDebitaConSuFechaEnLaMismaPasadaQueLosCierresYLaCadenaQuedaConsistente()
    {
        var (ctx, cuenta, _, turno1, turno2, turnoNuevo) = await SembrarHistorialAsync(
            nameof(UnPagoViejoSinDebitoSeDebitaConSuFechaEnLaMismaPasadaQueLosCierresYLaCadenaQuedaConsistente));
        var transferencia = await CrearMedioAsync(ctx, "Transferencia B", idCuenta: null);
        var fechaDelPago = Dia10.AddHours(2);
        var idPago = await PagoAsync(ctx, transferencia, 300m, fechaDelPago);
        await AsignarCuentaAsync(ctx, transferencia, cuenta);
        Assert.DoesNotContain(await LibroAsync(ctx, cuenta), f => f.IdPago == idPago);

        var vista = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));
        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));

        foreach (var p in new[] { vista, plan })
        {
            Assert.Equal((2, 1, 5), (p.Turnos, p.Pagos, p.Filas));
            var debito = Assert.Single(p.Debitos);
            Assert.Equal(
                new DebitoDeReconstruccion(
                    idPago, fechaDelPago, CategoriaPago.Servicios, "Luz", transferencia, "Transferencia B", cuenta, "Mercado Pago", 300m),
                debito);
            var total = Assert.Single(p.Cuentas);
            Assert.Equal((cuenta, 5, 2400m, 1430m, 3830m), (total.IdCuentaTesoreria, total.Filas, total.Neto, total.SaldoAntes, total.SaldoDespues));
            Assert.Empty(p.DebitosCongelados);
        }

        Assert.Equal(vista.Debitos, plan.Debitos);
        Assert.Equal(vista.Cuentas, plan.Cuentas);

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(8, libro.Count);
        AfirmarFila(libro[0], TipoMovimientoCuenta.Cobro, 1000m, 1000m, 0m, 2000m);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 2000m, 0m, 100m, 1900m);
        AfirmarFila(libro[2], TipoMovimientoCuenta.Pago, 1900m, 0m, 300m, 1600m);
        AfirmarFila(libro[3], TipoMovimientoCuenta.Cobro, 1600m, 2000m, 0m, 3600m);
        AfirmarFila(libro[4], TipoMovimientoCuenta.Comision, 3600m, 0m, 200m, 3400m);
        AfirmarFila(libro[5], TipoMovimientoCuenta.Cobro, 3400m, 500m, 0m, 3900m);
        AfirmarFila(libro[6], TipoMovimientoCuenta.Comision, 3900m, 0m, 50m, 3850m);
        AfirmarFila(libro[7], TipoMovimientoCuenta.Ajuste, 3850m, 0m, 20m, 3830m);
        Assert.Equal([turno1, turno1, null, turno2, turno2, turnoNuevo, turnoNuevo, null], libro.Select(f => f.IdTurnoCaja).ToArray());
        Assert.Equal(
            ((int?)idPago, (int?)transferencia, fechaDelPago, $"Pago {idPago}", ctx.IdEmpleado, (decimal?)null),
            (libro[2].IdPago, libro[2].IdMedioPago, libro[2].Fecha, libro[2].Concepto, libro[2].IdEmpleado, libro[2].BaseBruta));
        Assert.Equal(libro.OrderBy(f => f.Fecha).ThenBy(f => f.Id).Select(f => f.Id), libro.Select(f => f.Id));
        Assert.Equal(3830m, await SaldoDeLaCuentaAsync(ctx, cuenta));

        var segunda = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));
        var otraVista = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));
        foreach (var p in new[] { segunda, otraVista })
        {
            Assert.Equal((0, 0, 0), (p.Turnos, p.Pagos, p.Filas));
            Assert.Empty(p.Debitos);
            Assert.Empty(p.Cuentas);
        }

        Assert.Equal(
            libro.Select(f => (f.Id, f.Inicio, f.Final)),
            (await LibroAsync(ctx, cuenta)).Select(f => (f.Id, f.Inicio, f.Final)));
    }

    /// <summary>Evidencia de mutación (mutation-proof-tests), registrada al escribir la prueba: quitar
    /// <c>!db.MovimientosCuenta.Any(m =&gt; m.IdPago == p.Id)</c> del planeo de débitos hace fallar esta prueba (la vista previa
    /// vuelve a debitar el pago ya debitado por el reconciliador).</summary>
    [Fact]
    public async Task UnPagoYaDebitadoUnoSinCuentaUnoDadoDeBajaYUnoConLaCuentaDeOtraEmpresaNoSeDebitan()
    {
        var ctx = await PrepararAsync(nameof(UnPagoYaDebitadoUnoSinCuentaUnoDadoDeBajaYUnoConLaCuentaDeOtraEmpresaNoSeDebitan));
        var cuenta = await CrearCuentaAsync(ctx, "Banco", saldoInicial: 1000m);
        var transferencia = await CrearMedioAsync(ctx, "Transferencia B", idCuenta: null);
        var sinCuenta = await CrearMedioAsync(ctx, "Billetera sin cuenta", idCuenta: null);

        int idAjena, medioAjeno;
        await using (var db = Db(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var otra = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(otra);
            await db.SaveChangesAsync();
            var ajena = new CuentaTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = otra.Id, Nombre = "Ajena", SaldoInicial = 50m, Activo = true,
                CreatedAt = ahora, UpdatedAt = ahora
            };
            db.CuentasTesoreria.Add(ajena);
            await db.SaveChangesAsync();
            idAjena = ajena.Id;
        }

        medioAjeno = await CrearMedioAsync(ctx, "Billetera ajena", idAjena);

        var viejo = await PagoAsync(ctx, transferencia, 30m, Dia10.AddHours(1));
        var borrado = await PagoAsync(ctx, transferencia, 100m, Dia10);
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{borrado}")).StatusCode);
        var deMedioSinCuenta = await PagoAsync(ctx, sinCuenta, 50m, Dia10);
        var conCuentaAjena = await PagoAsync(ctx, medioAjeno, 70m, Dia10);
        await AsignarCuentaAsync(ctx, transferencia, cuenta);
        var yaDebitado = await PagoAsync(ctx, transferencia, 200m, Dia10.AddHours(2));
        Assert.Single(await LibroAsync(ctx, cuenta), f => f.IdPago == yaDebitado);

        var vista = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-10")));
        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-10")));

        foreach (var p in new[] { vista, plan })
        {
            Assert.Equal([viejo], p.Debitos.Select(d => d.IdPago).ToArray());
            Assert.Equal((0, 1, 1), (p.Turnos, p.Pagos, p.Filas));
        }

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(1, libro.Count(f => f.IdPago == yaDebitado));
        Assert.Equal(1, libro.Count(f => f.IdPago == viejo));
        Assert.DoesNotContain(libro, f => f.IdPago == borrado || f.IdPago == deMedioSinCuenta || f.IdPago == conCuentaAjena);
        Assert.Equal(1000m - 200m - 30m, await SaldoDeLaCuentaAsync(ctx, cuenta));
        Assert.Empty(await LibroAsync(ctx, idAjena));
    }

    /// <summary>Evidencia de mutación: tratar todo pago como no congelado en el planeo de débitos hace fallar esta prueba.</summary>
    [Fact]
    public async Task UnPagoConFechaIgualALaMarcaNoSeDebitaYSeInformaComoCongeladoYElPosteriorSi()
    {
        var ctx = await PrepararAsync(nameof(UnPagoConFechaIgualALaMarcaNoSeDebitaYSeInformaComoCongeladoYElPosteriorSi));
        var cuenta = await CrearCuentaAsync(ctx, "Banco", saldoInicial: 1000m);
        var transferencia = await CrearMedioAsync(ctx, "Transferencia B", idCuenta: null);
        var congelado = await PagoAsync(ctx, transferencia, 100m, Dia10);
        var posterior = await PagoAsync(ctx, transferencia, 200m, Dia11);
        await AsignarCuentaAsync(ctx, transferencia, cuenta);
        await ConciliarHastaAsync(ctx, Dia10);

        var vista = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaVistaPrevia, Pedido(ctx, "2026-03-10", "2026-03-11")));
        var plan = await LeerPlanAsync(await ctx.Admin.PostAsJsonAsync(RutaAplicar, Pedido(ctx, "2026-03-10", "2026-03-11")));

        foreach (var p in new[] { vista, plan })
        {
            Assert.Equal((1, 1, Dia10), (p.Pagos, p.Filas, p.ConciliadoHasta));
            Assert.Equal([posterior], p.Debitos.Select(d => d.IdPago).ToArray());
            Assert.Equal([(congelado, 100m)], p.DebitosCongelados.Select(d => (d.IdPago, d.Importe)).ToArray());
        }

        var fila = Assert.Single(await LibroAsync(ctx, cuenta));
        Assert.Equal(((int?)posterior, Dia11, 1000m, 200m, 800m), (fila.IdPago, fila.Fecha, fila.Inicio, fila.Egreso, fila.Final));
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
