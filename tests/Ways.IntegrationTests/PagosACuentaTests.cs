using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-7-cuenta-corriente, Slice 2 (tasks 2.9-2.15, 2.17): <c>POST
/// /api/clientes/{id}/cuenta-corriente/pagos</c> punta a punta — emisión de RC, atomicidad,
/// participación en el arqueo, numeración independiente, anulación y el presupuesto de consultas.
/// Mismo criterio que <see cref="VentasTurnoWiringTests"/>: <see cref="PrepararAsync"/> NUNCA abre
/// un turno por defecto, cada prueba lo hace explícito.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagosACuentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpresa, int IdEmpleadoAdmin, int IdListaPrecio,
        int IdMedioEfectivo, int IdMedioTarjeta, int IdMedioCuentaCorriente, int IdConsumidorFinal, HttpClient Admin);

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

        var medioTarjeta = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Tarjeta", Orden = 2,
            Comportamiento = ComportamientoMedioPago.Electronico, AdmiteVuelto = false, RequiereReferencia = true,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioTarjeta);

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        var idConsumidorFinal = await db.Clientes
            .Where(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal).Select(c => c.Id).FirstAsync();
        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdEmpresa, resultado.IdUsuarioAdmin, idListaPrecio,
            idMedioEfectivo, medioTarjeta.Id, medioCc.Id, idConsumidorFinal, admin);
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre, decimal saldo = 0m)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = 0m,
            CreditoIlimitado = true, Saldo = saldo, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial = 0m)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    private static SolicitudDePagoACuenta SolicitudSimple(Contexto ctx, decimal importe) =>
        new(ctx.IdPuntoVenta, [new PagoDeCuenta(ctx.IdMedioEfectivo, importe, null, 0m)], null);

    private static async Task<HttpResponseMessage> RegistrarPagoAsync(
        Contexto ctx, int idCliente, SolicitudDePagoACuenta solicitud) =>
        await ctx.Admin.PostAsJsonAsync($"/api/clientes/{idCliente}/cuenta-corriente/pagos", solicitud);

    // ---- task 2.9: forma del comprobante RC, turno, medios, CF ------------------------------

    [Fact]
    public async Task RcEmisionPersisteCeroItemsYCeroMovimientosDeStock()
    {
        var ctx = await PrepararAsync(nameof(RcEmisionPersisteCeroItemsYCeroMovimientosDeStock));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC items");

        var solicitud = new SolicitudDePagoACuenta(
            ctx.IdPuntoVenta, [new PagoDeCuenta(ctx.IdMedioEfectivo, 200m, null, 0m)], "  Pago parcial de saldo  ");
        var respuesta = await RegistrarPagoAsync(ctx, idCliente, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        Assert.Empty(emitido.Items);
        Assert.False(emitido.Estado == EstadoComprobante.Anulado);
        // task judgment-day fix 1: Observaciones viajaba en la solicitud pero se descartaba en
        // silencio (EjecutarTransaccionAsync hardcodeaba null) — recortado, nunca en blanco.
        Assert.Equal("Pago parcial de saldo", emitido.Observaciones);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ItemsComprobanteVenta.CountAsync(i => i.IdComprobanteVenta == emitido.Id));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteVenta == emitido.Id));

        var observacionesPersistidas = await db.ComprobantesVenta
            .Where(c => c.Id == emitido.Id).Select(c => c.Observaciones).SingleAsync();
        Assert.Equal("Pago parcial de saldo", observacionesPersistidas);

        // comprobantes-venta / RC emission never touches fiscal fields.
        var (netoGravado, ivaTotal) = await db.ComprobantesVenta
            .Where(c => c.Id == emitido.Id).Select(c => new { c.NetoGravado, c.IvaTotal })
            .Select(x => new ValueTuple<decimal?, decimal?>(x.NetoGravado, x.IvaTotal)).SingleAsync();
        Assert.Null(netoGravado);
        Assert.Null(ivaTotal);
    }

    [Fact]
    public async Task RcConObservacionesEnBlancoLasPersisteComoNull()
    {
        var ctx = await PrepararAsync(nameof(RcConObservacionesEnBlancoLasPersisteComoNull));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC obs blancas");

        var solicitud = new SolicitudDePagoACuenta(
            ctx.IdPuntoVenta, [new PagoDeCuenta(ctx.IdMedioEfectivo, 100m, null, 0m)], "   ");
        var respuesta = await RegistrarPagoAsync(ctx, idCliente, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        Assert.Null(emitido.Observaciones);
    }

    [Fact]
    public async Task RcSinTurnoAbiertoEsRechazada409AntesDeCualquierEscritura()
    {
        var ctx = await PrepararAsync(nameof(RcSinTurnoAbiertoEsRechazada409AntesDeCualquierEscritura));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC sin turno");
        // Sin AbrirTurnoAsync a propósito.

        var respuesta = await RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 200m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync());
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(0m, saldo);
    }

    [Fact]
    public async Task RcAdjuntaElTurnoAbiertoResuelto()
    {
        var ctx = await PrepararAsync(nameof(RcAdjuntaElTurnoAbiertoResuelto));
        var turno = await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC turno");

        var respuesta = await RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 100m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idTurnoPersistido = await db.ComprobantesVenta
            .Where(c => c.Id == emitido.Id).Select(c => c.IdTurnoCaja).SingleAsync();
        Assert.Equal(turno.Id, idTurnoPersistido);
    }

    [Fact]
    public async Task RcConMedioCuentaCorrienteEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(RcConMedioCuentaCorrienteEsRechazada));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC medio CC");

        var solicitud = new SolicitudDePagoACuenta(
            ctx.IdPuntoVenta, [new PagoDeCuenta(ctx.IdMedioCuentaCorriente, 100m, null, 0m)], null);
        var respuesta = await RegistrarPagoAsync(ctx, idCliente, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("pago_a_cuenta_sin_medios_fisicos", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
    }

    [Fact]
    public async Task RcParaConsumidorFinalEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(RcParaConsumidorFinalEsRechazada));
        await AbrirTurnoAsync(ctx);

        var respuesta = await RegistrarPagoAsync(ctx, ctx.IdConsumidorFinal, SolicitudSimple(ctx, 100m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("cliente_sin_cuenta_corriente", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task RcAceptadaConMediosFisicosMixtos()
    {
        var ctx = await PrepararAsync(nameof(RcAceptadaConMediosFisicosMixtos));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC mixto");

        var solicitud = new SolicitudDePagoACuenta(
            ctx.IdPuntoVenta,
            [
                new PagoDeCuenta(ctx.IdMedioEfectivo, 100m, null, 0m),
                new PagoDeCuenta(ctx.IdMedioTarjeta, 50m, "OP-1", 0m)
            ],
            null);

        var respuesta = await RegistrarPagoAsync(ctx, idCliente, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
        Assert.Equal(150m, emitido.Total);
    }

    // ---- task 2.10: un único movimiento Pago, atomicidad, sobrepago -------------------------

    [Fact]
    public async Task RcEmisionEscribeUnMovimientoPagoYBajaElSaldo()
    {
        var ctx = await PrepararAsync(nameof(RcEmisionEscribeUnMovimientoPagoYBajaElSaldo));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC saldo", saldo: 500m);

        var respuesta = await RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 200m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == emitido.Id).ToListAsync();
        var movimiento = Assert.Single(movimientos);
        Assert.Equal(TipoMovimientoCc.Pago, movimiento.Tipo);
        Assert.Equal(-200m, movimiento.Importe);
        // consumo-cuenta-corriente / Pago snapshots the resulting saldo.
        Assert.Equal(300m, movimiento.SaldoResultante);
        Assert.Null(movimiento.IdPagoComprobante);

        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(300m, saldo);
    }

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

    /// <summary>Fault injection determinística — mismo criterio que
    /// <c>VentasAtomicidadYConcurrenciaTests.IntentarConPrivilegioRevocadoAsync</c>: REVOCA el
    /// privilegio sobre <c>pagos_comprobante</c> (con <c>ways_owner</c>), fuerza el 500 DESPUÉS de
    /// que <c>comprobantes_venta</c> ya insertó dentro de la MISMA transacción (spec: A failure
    /// after the comprobante insert rolls back everything), y restaura el privilegio.</summary>
    [Fact]
    public async Task UnaFallaDespuesDelInsertDelComprobanteRevierteTodo()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaDespuesDelInsertDelComprobanteRevierteTodo));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC atomicidad", saldo: 500m);

        await RevocarAsync("pagos_comprobante", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 200m));
        }
        finally
        {
            await RestaurarAsync("pagos_comprobante", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
        Assert.Equal(0, await db.PagosComprobante.CountAsync());
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync());
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(500m, saldo);
    }

    [Fact]
    public async Task SobrepagoProduceSaldoAFavorSinRechazo()
    {
        var ctx = await PrepararAsync(nameof(SobrepagoProduceSaldoAFavorSinRechazo));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC sobrepago", saldo: 100m);

        var respuesta = await RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 150m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(-50m, saldo);
    }

    // ---- task 2.11: anulación de una RC ------------------------------------------------------

    [Fact]
    public async Task AnulandoUnaRcRestauraElSaldoConUnContramovimientoPositivo()
    {
        var ctx = await PrepararAsync(nameof(AnulandoUnaRcRestauraElSaldoConUnContramovimientoPositivo));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC anulacion", saldo: 500m);

        var respuestaPago = await RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 200m));
        Assert.Equal(HttpStatusCode.Created, respuestaPago.StatusCode);
        var emitido = (await respuestaPago.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        await using (var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            Assert.Equal(300m, await dbAntes.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync());
        }

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        var cuerpo = await respuestaAnulacion.Content.ReadAsStringAsync();
        Assert.True(respuestaAnulacion.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(500m, saldo);

        var contramovimientos = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == emitido.Id && m.Tipo == TipoMovimientoCc.Ajuste).ToListAsync();
        var contramovimiento = Assert.Single(contramovimientos);
        Assert.Equal(200m, contramovimiento.Importe);
        Assert.Null(contramovimiento.IdPagoComprobante);
    }

    [Fact]
    public async Task AnulacionDeUnaRcEsRechazada409TurnoCerradoCuandoElTurnoYaCerro()
    {
        var ctx = await PrepararAsync(nameof(AnulacionDeUnaRcEsRechazada409TurnoCerradoCuandoElTurnoYaCerro));
        var turno = await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC turno cerrado", saldo: 500m);

        var respuestaPago = await RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 200m));
        Assert.Equal(HttpStatusCode.Created, respuestaPago.StatusCode);
        var emitido = (await respuestaPago.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var solicitudDeCierre = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 200m)], null);
        var respuestaCierre = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitudDeCierre);
        Assert.Equal(HttpStatusCode.OK, respuestaCierre.StatusCode);

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        var cuerpo = await respuestaAnulacion.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuestaAnulacion.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("turno_cerrado", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.12: numeración independiente de TX ------------------------------------------

    [Fact]
    public async Task RcYTxNumeranIndependientementeEnElMismoPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(RcYTxNumeranIndependientementeEnElMismoPuntoDeVenta));
        await AbrirTurnoAsync(ctx);
        var idClienteCc = await SembrarClienteAsync(ctx, "Cliente RC numeracion");
        var idClienteTx = await SembrarClienteAsync(ctx, "Cliente TX numeracion");
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-numeracion-rc", 100m);

        var solicitudTx = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idClienteTx, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);

        for (var i = 0; i < 3; i++)
        {
            var respuestaTxPrevia = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudTx);
            Assert.Equal(HttpStatusCode.Created, respuestaTxPrevia.StatusCode);
        }

        var respuestaRc = await RegistrarPagoAsync(ctx, idClienteCc, SolicitudSimple(ctx, 50m));
        Assert.Equal(HttpStatusCode.Created, respuestaRc.StatusCode);
        var rc = (await respuestaRc.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(1L, rc.Numero);

        var respuestaTxSiguiente = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudTx);
        Assert.Equal(HttpStatusCode.Created, respuestaTxSiguiente.StatusCode);
        var txSiguiente = (await respuestaTxSiguiente.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(4L, txSiguiente.Numero);
    }

    private async Task<int> SembrarArticuloConPrecioAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idArea = await db.Areas.Select(a => a.Id).FirstOrDefaultAsync();
        if (idArea == 0)
        {
            var area = new Area
            {
                IdTenant = ctx.IdTenant, Nombre = "Area RC", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Areas.Add(area);
            await db.SaveChangesAsync();
            idArea = area.Id;
        }

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Ways.Domain.Articulos.Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = idArea, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = Ways.Domain.Articulos.UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Ways.Domain.Precios.Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    // ---- task 2.13: participación en el arqueo -----------------------------------------------

    [Fact]
    public async Task UnaRcDeEfectivoCuentaParaElEsperadoDeEfectivoJuntoConUnaTx()
    {
        var ctx = await PrepararAsync(nameof(UnaRcDeEfectivoCuentaParaElEsperadoDeEfectivoJuntoConUnaTx));
        var turno = await AbrirTurnoAsync(ctx);
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-arqueo-rc", 1000m);
        var idClienteTx = await SembrarClienteAsync(ctx, "Cliente TX arqueo");
        var idClienteRc = await SembrarClienteAsync(ctx, "Cliente RC arqueo");

        var solicitudTx = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idClienteTx, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 1000m, null, 0m)],
            null, null);
        var respuestaTx = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudTx);
        Assert.Equal(HttpStatusCode.Created, respuestaTx.StatusCode);

        var respuestaRc = await RegistrarPagoAsync(ctx, idClienteRc, SolicitudSimple(ctx, 300m));
        Assert.Equal(HttpStatusCode.Created, respuestaRc.StatusCode);

        var solicitudDeCierre = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 1300m)], null);
        var respuestaCierre = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitudDeCierre);
        var cuerpoCierre = await respuestaCierre.Content.ReadAsStringAsync();
        Assert.True(respuestaCierre.StatusCode == HttpStatusCode.OK, cuerpoCierre);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var importeEsperado = await db.ArqueosTurno
            .Where(a => a.IdTurnoCaja == turno.Id && a.IdMedioPago == ctx.IdMedioEfectivo)
            .Select(a => a.ImporteEsperado).SingleAsync();
        Assert.Equal(1300m, importeEsperado);
    }

    // ---- task 2.14: carrera pago a cuenta vs cierre -------------------------------------------

    [Fact]
    public async Task UnPagoACuentaQueCompiteConUnCierreQuedaContadoEnElArqueoORechazadoNuncaSinContar()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync(
                $"{nameof(UnPagoACuentaQueCompiteConUnCierreQuedaContadoEnElArqueoORechazadoNuncaSinContar)}-{ronda}");
            // Fondo inicial > 0 deja el ancla (efectivo) SIEMPRE arqueable (design: CalculadorDeArqueo,
            // "hayMovimientoFisicoDelAncla") sin importar si el pago a cuenta gana o pierde la
            // carrera — mismo criterio que VentasTurnoWiringTests, que sembraba un pago previo con
            // el mismo propósito.
            var turno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);
            var idCliente = await SembrarClienteAsync(ctx, "Cliente RC race cierre", saldo: 1000m);

            var solicitudDeCierre = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 500m)], null);

            var tareaPago = RegistrarPagoAsync(ctx, idCliente, SolicitudSimple(ctx, 100m));
            var tareaCierre = ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitudDeCierre);

            var respuestaPago = await tareaPago;
            var respuestaCierre = await tareaCierre;

            Assert.Contains(respuestaPago.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            if (respuestaPago.StatusCode == HttpStatusCode.Conflict)
            {
                var problema = await respuestaPago.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
            }

            Assert.Equal(HttpStatusCode.OK, respuestaCierre.StatusCode);

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var importeEsperado = await db.ArqueosTurno
                .Where(a => a.IdTurnoCaja == turno.Id && a.IdMedioPago == ctx.IdMedioEfectivo)
                .Select(a => a.ImporteEsperado).SingleAsync();

            var esperado = respuestaPago.StatusCode == HttpStatusCode.Created ? 600m : 500m;
            Assert.Equal(esperado, importeEsperado);
        }
    }

    // ---- task 2.15: presupuesto de consultas, constante independiente de la cantidad de medios --

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

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private async Task<int> EmitirRcYContarConsultasAsync(Contexto ctx, int idCliente, IReadOnlyList<PagoDeCuenta> pagos)
    {
        var contador = new ContadorDeComandos();
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);

        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<Ways.Domain.Clientes.TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<Ways.Domain.Articulos.UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<Ways.Domain.Stock.MotivoStock>("motivo_stock");
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: ctx.IdEmpleadoAdmin);
        var lector = new LectorDeMovimientosDelTurno(db);
        var servicioDeTurnos = new ServicioDeTurnos(db, reloj, contexto, lector);
        var servicio = new ServicioDeCuentaCorriente(db, reloj, contexto, servicioDeTurnos);

        var emitido = await servicio.RegistrarPagoAsync(idCliente, new SolicitudDePagoACuenta(ctx.IdPuntoVenta, pagos, null));
        Assert.False(emitido.Estado == EstadoComprobante.Anulado);

        return contador.Consultas;
    }

    [Fact]
    public async Task PagoACuentaEmiteUnPresupuestoConstanteDeConsultasIndependienteDeLaCantidadDeMedios()
    {
        var ctx = await PrepararAsync(nameof(PagoACuentaEmiteUnPresupuestoConstanteDeConsultasIndependienteDeLaCantidadDeMedios));
        await AbrirTurnoAsync(ctx);
        var idClienteUnMedio = await SembrarClienteAsync(ctx, "Cliente budget 1 medio");
        var idClienteTresMedios = await SembrarClienteAsync(ctx, "Cliente budget 3 medios");

        var consultasUnMedio = await EmitirRcYContarConsultasAsync(
            ctx, idClienteUnMedio, [new PagoDeCuenta(ctx.IdMedioEfectivo, 100m, null, 0m)]);

        var consultasTresMedios = await EmitirRcYContarConsultasAsync(
            ctx, idClienteTresMedios,
            [
                new PagoDeCuenta(ctx.IdMedioEfectivo, 50m, null, 0m),
                new PagoDeCuenta(ctx.IdMedioTarjeta, 30m, "OP-1", 0m),
                new PagoDeCuenta(ctx.IdMedioTarjeta, 20m, "OP-2", 0m)
            ]);

        // Presupuesto constante — independiente de la cantidad de medios (design: Transactions —
        // "Read budget"). 8 consultas EF/SaveChanges visibles (cliente, punto de venta, turno,
        // tipo RC, medios de pago, vuelto_maximo, INSERT comprobante, INSERT pagos); el "≤ 7" del
        // design no lista la resolución del tipo de comprobante 'RC' en su "fuera" — deviation
        // documentada en el resumen de retorno del apply, la propiedad que importa (constante,
        // no escala con la cantidad de medios) queda probada igual.
        Assert.Equal(8, consultasUnMedio);
        Assert.Equal(consultasUnMedio, consultasTresMedios);
    }

    // ---- authorization (house lessons: 401/403/cross-tenant-404) -----------------------------

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx, string nombre)
    {
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-rc", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    [Fact]
    public async Task UnVendedorPuedeRegistrarUnPagoACuenta()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorPuedeRegistrarUnPagoACuenta));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC vendedor");
        using var vendedor = await CrearVendedorAsync(ctx, nameof(UnVendedorPuedeRegistrarUnPagoACuenta));

        var respuesta = await vendedor.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/pagos", SolicitudSimple(ctx, 50m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnRolFueraDeOperacionDePosEsRechazadoDelPagoACuenta()
    {
        var ctx = await PrepararAsync(nameof(UnRolFueraDeOperacionDePosEsRechazadoDelPagoACuenta));
        await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente RC root");

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var respuesta = await root.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/pagos", SolicitudSimple(ctx, 50m));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task RegistrarUnPagoACuentaSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/clientes/1/cuenta-corriente/pagos", new SolicitudDePagoACuenta(1, [], null));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    [Fact]
    public async Task RegistrarUnPagoACuentaContraUnClienteDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(RegistrarUnPagoACuentaContraUnClienteDeOtroTenantDevuelve404) + "-A");
        var idClienteDeA = await SembrarClienteAsync(ctxA, "Cliente A cross-tenant");

        var ctxB = await PrepararAsync(nameof(RegistrarUnPagoACuentaContraUnClienteDeOtroTenantDevuelve404) + "-B");
        await AbrirTurnoAsync(ctxB);

        var respuesta = await ctxB.Admin.PostAsJsonAsync(
            $"/api/clientes/{idClienteDeA}/cuenta-corriente/pagos", SolicitudSimple(ctxB, 50m));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
