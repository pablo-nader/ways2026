using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-6-turnos-caja, Slice 4 (tasks 4.11, 4.12, 4.14, 4.17, 4.18): las tres superficies
/// racy/atómicas del cierre — fallas forzadas en cada punto de escritura real de la transacción
/// (statements 1/5/6; los pasos 2-4 son puros/de solo lectura, sin punto de falla propio),
/// dos cierres concurrentes del mismo turno, el presupuesto de consultas del resumen, y las dos
/// carreras <c>FOR SHARE</c> (movimiento/gasto vs. cierre) que el hallazgo de judgment-day de la
/// task 4.17 cierra.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CajaCierreAtomicidadYConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdCliente, int IdTipoComprobanteTx,
        int IdMedioEfectivo, string MailAdmin, string PasswordAdmin, HttpClient Admin);

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
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipoComprobanteTx,
            idMedioEfectivo, mailAdmin, resultado.PasswordTemporal, admin);
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

    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, int idMedioPago, decimal importe)
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
            Estado = EstadoComprobante.Emitido,
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
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // ---- task 4.11: un punto de falla por prueba, en cada statement de escritura real ------------

    private async Task RevocarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"REVOKE {privilegios} ON {tabla} FROM {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task RestaurarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"GRANT {privilegios} ON {tabla} TO {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<HttpResponseMessage> IntentarCierreConPrivilegioRevocadoAsync(
        Contexto ctx, int idTurno, SolicitudDeCierre solicitud, string tabla, string privilegios)
    {
        await RevocarAsync(tabla, privilegios);
        try
        {
            return await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{idTurno}/cierre", solicitud);
        }
        finally
        {
            await RestaurarAsync(tabla, privilegios);
        }
    }

    private async Task VerificarTurnoSigueAbiertoSinArqueosNiTesoreriaAsync(Contexto ctx, int idTurno)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var estado = await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.Estado).SingleAsync();
        Assert.Equal(EstadoTurno.Abierto, estado);
        Assert.Equal(0, await db.ArqueosTurno.CountAsync(a => a.IdTurnoCaja == idTurno));
        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == idTurno));
    }

    [Fact]
    public async Task UnaFallaEnElUpdateDeTurnosCajaNoPersisteNadaYElTurnoSigueAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnElUpdateDeTurnosCajaNoPersisteNadaYElTurnoSigueAbierto));
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 100m);

        var respuesta = await IntentarCierreConPrivilegioRevocadoAsync(
            ctx, turno.Id, new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null),
            "turnos_caja", "UPDATE");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        await VerificarTurnoSigueAbiertoSinArqueosNiTesoreriaAsync(ctx, turno.Id);
    }

    [Fact]
    public async Task UnaFallaEnElInsertDeArqueosTurnoNoPersisteNadaYElTurnoSigueAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnElInsertDeArqueosTurnoNoPersisteNadaYElTurnoSigueAbierto));
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 100m);

        var respuesta = await IntentarCierreConPrivilegioRevocadoAsync(
            ctx, turno.Id, new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null),
            "arqueos_turno", "INSERT");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        // El UPDATE guardado (statement 1) YA corrió cuando el INSERT de arqueos (statement 5)
        // falla — prueba que el rollback deshace TAMBIÉN un statement anterior de la MISMA
        // transacción, no solo el que tiró la excepción.
        await VerificarTurnoSigueAbiertoSinArqueosNiTesoreriaAsync(ctx, turno.Id);
    }

    [Fact]
    public async Task UnaFallaEnElInsertDeMovimientosTesoreriaNoPersisteNadaYElTurnoSigueAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnElInsertDeMovimientosTesoreriaNoPersisteNadaYElTurnoSigueAbierto));
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 100m);

        var respuesta = await IntentarCierreConPrivilegioRevocadoAsync(
            ctx, turno.Id, new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null),
            "movimientos_tesoreria", "INSERT");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        // El INSERT de arqueos_turno (statement 5) YA corrió — prueba que el rollback también lo
        // deshace, no solo el statement 6 que tiró la excepción (spec: A failed cierre leaves the
        // turno open with no side effects; tesoreria / A failed cierre leaves no tesorería row).
        await VerificarTurnoSigueAbiertoSinArqueosNiTesoreriaAsync(ctx, turno.Id);
    }

    // ---- task 4.12: dos cierres concurrentes del mismo turno --------------------------------------

    [Fact]
    public async Task DosCierresConcurrentesDelMismoTurnoProducenExactamenteUnGanador()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync($"{nameof(DosCierresConcurrentesDelMismoTurnoProducenExactamenteUnGanador)}-{ronda}");
            var turno = await AbrirTurnoAsync(ctx);
            await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 100m);

            var solicitud = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null);

            var tareaA = ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);
            var tareaB = ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);

            var respuestas = await Task.WhenAll(tareaA, tareaB);
            var estados = respuestas.Select(r => r.StatusCode).ToList();

            Assert.Contains(HttpStatusCode.OK, estados);
            Assert.Contains(HttpStatusCode.Conflict, estados);

            var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            var problema = await perdedora.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("turno_ya_cerrado", problema.GetProperty("codigo").GetString());

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            Assert.Equal(1, await db.ArqueosTurno.CountAsync(a => a.IdTurnoCaja == turno.Id));
            Assert.Equal(1, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == turno.Id));
        }
    }

    // ---- task 4.17/4.18: las carreras FOR SHARE ----------------------------------------------------

    /// <summary>Invariante robusto a la interleaving real (no depende de forzar el rendezvous):
    /// si el movimiento ganó (201), su importe queda contado en <c>Ingreso</c> de la tesorería;
    /// si perdió (409 turno_no_abierto), nunca se insertó — <c>Σ movimientos_caja(retiro) =
    /// Ingreso</c> es verdad en los dos casos, nunca "201 pero no contado".</summary>
    [Fact]
    public async Task UnMovimientoQueCompiteConUnCierreQuedaContadoORechazadoNuncaSinContar()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync($"{nameof(UnMovimientoQueCompiteConUnCierreQuedaContadoORechazadoNuncaSinContar)}-{ronda}");
            var turno = await AbrirTurnoAsync(ctx);
            // El pago ya deja el ancla arqueable independientemente de si el retiro gana o
            // pierde la carrera — el set de conteos declarados no cambia según el resultado.
            await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 500m);

            var solicitudDeCierre = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 500m)], null);

            var tareaMovimiento = ctx.Admin.PostAsJsonAsync(
                $"/api/caja/turnos/{turno.Id}/movimientos",
                new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 50m, "retiro en carrera con cierre"));
            var tareaCierre = ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitudDeCierre);

            var respuestaMovimiento = await tareaMovimiento;
            var respuestaCierre = await tareaCierre;

            Assert.Contains(respuestaMovimiento.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            if (respuestaMovimiento.StatusCode == HttpStatusCode.Conflict)
            {
                var problema = await respuestaMovimiento.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
            }

            // El cierre SIEMPRE tiene que ganar en algún momento (es el único que compite por
            // cerrar) — o ya se cerró antes del movimiento, o se cierra después de que el
            // movimiento comitea. Nunca puede fallar por la carrera en sí.
            Assert.Equal(HttpStatusCode.OK, respuestaCierre.StatusCode);

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var sumaDeRetiros = await db.MovimientosCaja
                .Where(m => m.IdTurnoCaja == turno.Id && m.Tipo == TipoMovimientoCaja.Retiro)
                .SumAsync(m => m.Importe);
            var ingresoDeTesoreria = await db.MovimientosTesoreria
                .Where(m => m.IdTurnoCaja == turno.Id).Select(m => m.Ingreso).SingleAsync();

            Assert.Equal(sumaDeRetiros, ingresoDeTesoreria);
        }
    }

    /// <summary>Mismo invariante que el movimiento, para gastos: <c>Σ gastos = Egreso</c> sin
    /// importar si el gasto ganó o perdió la carrera contra el cierre.</summary>
    [Fact]
    public async Task UnGastoQueCompiteConUnCierreQuedaContadoORechazadoNuncaSinContar()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync($"{nameof(UnGastoQueCompiteConUnCierreQuedaContadoORechazadoNuncaSinContar)}-{ronda}");
            var turno = await AbrirTurnoAsync(ctx);
            await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 500m);

            var solicitudDeCierre = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 500m)], null);

            var tareaGasto = ctx.Admin.PostAsJsonAsync(
                "/api/gastos",
                new SolicitudDeGasto(
                    ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "gasto en carrera con cierre", null,
                    ctx.IdMedioEfectivo, null, 30m));
            var tareaCierre = ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitudDeCierre);

            var respuestaGasto = await tareaGasto;
            var respuestaCierre = await tareaCierre;

            Assert.Contains(respuestaGasto.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            if (respuestaGasto.StatusCode == HttpStatusCode.Conflict)
            {
                var problema = await respuestaGasto.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
            }

            Assert.Equal(HttpStatusCode.OK, respuestaCierre.StatusCode);

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var sumaDeGastos = await db.Gastos.Where(g => g.IdTurnoCaja == turno.Id).SumAsync(g => g.Importe);
            var egresoDeTesoreria = await db.MovimientosTesoreria
                .Where(m => m.IdTurnoCaja == turno.Id).Select(m => m.Egreso).SingleAsync();

            Assert.Equal(sumaDeGastos, egresoDeTesoreria);
        }
    }

    // ---- task 4.14: presupuesto de consultas del resumen -------------------------------------------

    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Consultas++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ElResumenEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeTickets()
    {
        var ctx = await PrepararAsync(nameof(ElResumenEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeTickets));
        var turno = await AbrirTurnoAsync(ctx);

        var interceptor = new ContadorDeComandos();
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var consultasCon2 = await SembrarYMedirAsync(ctx, cliente, turno.Id, interceptor, cantidadNueva: 2);
        var consultasCon50 = await SembrarYMedirAsync(ctx, cliente, turno.Id, interceptor, cantidadNueva: 48);
        var consultasCon200 = await SembrarYMedirAsync(ctx, cliente, turno.Id, interceptor, cantidadNueva: 150);

        Assert.Equal(consultasCon2, consultasCon50);
        Assert.Equal(consultasCon2, consultasCon200);
    }

    private async Task<int> SembrarYMedirAsync(
        Contexto ctx, HttpClient cliente, int idTurno, ContadorDeComandos interceptor, int cantidadNueva)
    {
        for (var i = 0; i < cantidadNueva; i++)
        {
            await SembrarPagoAsync(ctx, idTurno, ctx.IdMedioEfectivo, 10m);
        }

        var antes = interceptor.Consultas;
        var respuesta = await cliente.GetAsync($"/api/caja/turnos/{idTurno}/resumen");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return interceptor.Consultas - antes;
    }
}
