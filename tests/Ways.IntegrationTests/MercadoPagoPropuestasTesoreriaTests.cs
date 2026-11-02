using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Ways.Application.Caja;
using Ways.Application.MercadoPago;
using Ways.Domain.MercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// Las propuestas de tesorería de una conciliación de Mercado Pago (E7) contra Postgres real: la diferencia de comisión contra lo que el cierre
/// de turno descontó (cierre real por la API), un retiro por fila WITHDRAWAL/PAYOUT, y aplicar o descartar (Admin) con su efecto en el libro de
/// las cuentas, su idempotencia y su carrera, las validaciones de la cuenta de destino, el período conciliado, los roles y el alcance por tenant.
/// Comparte el escenario de <see cref="MercadoPagoVentaTests"/>.
/// </summary>
public partial class MercadoPagoVentaTests
{
    private static string RutaPropuestas(int idConciliacion) => $"{RutaConciliaciones}/{idConciliacion}/propuestas";

    private static Task<HttpResponseMessage> PropAplicarAsync(
        HttpClient cliente, int idConciliacion, int idPropuesta, int? idCuentaDestino = null, int? idCuentaOrigen = null) =>
        cliente.PostAsJsonAsync($"{RutaPropuestas(idConciliacion)}/{idPropuesta}/aplicar", new SolicitudDeAplicacionDePropuesta(idCuentaDestino, idCuentaOrigen));

    /// <summary>Le pone al medio con proveedor la cuenta de Mercado Pago del escenario: el único origen posible de un retiro.</summary>
    private Task PropMedioConCuentaAsync(Escenario e) =>
        EjecutarAsync("UPDATE medios_pago SET id_cuenta_tesoreria = $1 WHERE id_medio_pago = $2", e.Ctx.IdCuenta, e.IdMedio);

    private static Task<HttpResponseMessage> PropDescartarAsync(HttpClient cliente, int idConciliacion, int idPropuesta) =>
        cliente.PostAsync($"{RutaPropuestas(idConciliacion)}/{idPropuesta}/descartar", null);

    private static async Task<List<PropuestaDeTesoreria>> PropListarAsync(HttpClient cliente, int idConciliacion) =>
        await LeerAsync<List<PropuestaDeTesoreria>>(await cliente.GetAsync(RutaPropuestas(idConciliacion)));

    /// <summary>Le pone al medio la cuenta de Mercado Pago del escenario y el porcentaje de comisión, hace una venta pagada con un cobro de
    /// Mercado Pago de 100 y, si se pide, cierra el turno por la API: el cierre acredita el cobro neto de la comisión estimada.</summary>
    private async Task<(CobroDeMercadoPago Cobro, DatosDelCobro Datos)> PropVentaAsync(
        Escenario e, long numero, decimal porcentaje, bool cerrar = true)
    {
        await EjecutarAsync(
            "UPDATE medios_pago SET id_cuenta_tesoreria = $1, comision_porcentaje = $2 WHERE id_medio_pago = $3",
            e.Ctx.IdCuenta, porcentaje, e.IdMedio);
        var (cobro, _) = await ConcVentaConMercadoPagoAsync(e, numero);
        if (cerrar)
        {
            await PropCerrarTurnoAsync(e);
        }

        return (cobro, await ConcDatosDelCobroAsync(cobro.IdCobro));
    }

    private async Task PropCerrarTurnoAsync(Escenario e)
    {
        var idTurno = (int)(await ConsultarAsync(
            "SELECT id_turno_caja FROM turnos_caja WHERE id_punto_venta = $1 ORDER BY id_turno_caja DESC LIMIT 1", e.IdPuntoVenta)).Single()["id_turno_caja"]!;
        var cierre = await e.Ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{idTurno}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null, ForzarSinRendicion: true, MotivoSinRendicion: "La prueba cierra el turno sin cola de dispositivo."));
        Assert.True(cierre.StatusCode == HttpStatusCode.OK, await cierre.Content.ReadAsStringAsync());
    }

    private async Task<decimal> PropSaldoAsync(int idCuenta)
    {
        var filas = await ConsultarAsync(
            "SELECT final FROM movimientos_cuenta WHERE id_cuenta_tesoreria = $1 ORDER BY fecha DESC, id_movimiento_cuenta DESC LIMIT 1", idCuenta);
        return filas.Count == 0 ? 0m : (decimal)filas.Single()["final"]!;
    }

    private async Task<long> PropCantidadDeFilasAsync(int idCuenta) =>
        (long)(await ConsultarAsync("SELECT count(*) AS n FROM movimientos_cuenta WHERE id_cuenta_tesoreria = $1", idCuenta)).Single()["n"]!;

    private static string PropFila(string sourceId, string tipo, string importe, string? comision = null, string? neto = null) =>
        FilaDelReporte(sourceId, null, tipo, importe, comision, neto, FechaDelReporte);

    private static string PropFilaDelCobro(DatosDelCobro datos, string comision) =>
        FilaDelReporte(
            datos.ReferenciaDelPago.ToString(CultureInfo.InvariantCulture), null, "SETTLEMENT", "100.00", comision, null, FechaDelReporte);

    // Clausula: la comision estimada es la que el CIERRE descontó (porcentaje_aplicado de su fila de comision), no la del medio de hoy:
    // con el medio al 5 % al cerrar y al 10 % después, la diferencia es contra el 5 %. Cobrar 6,50 contra 5,00 propone debitar 1,50 de la
    // cuenta del medio, con el detalle del cálculo.
    [Fact]
    public async Task LaImportacionProponeLaDiferenciaDeComisionContraLoQueElCierreDescontoYNoContraElPorcentajeDeHoy()
    {
        var e = await PrepararAsync("mp-prop-diferencia");
        var (cobro, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        await EjecutarAsync("UPDATE medios_pago SET comision_porcentaje = 10 WHERE id_medio_pago = $1", e.IdMedio);

        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));

        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        Assert.Equal("diferencia_comision", propuesta.Tipo);
        Assert.Equal("pendiente", propuesta.Estado);
        Assert.Equal(-1.50m, propuesta.Importe);
        Assert.Equal(e.Ctx.IdCuenta, propuesta.IdCuentaOrigen);
        Assert.Equal("Mercado Pago", propuesta.NombreCuentaOrigen);
        Assert.Null(propuesta.IdCuentaDestino);
        var detalle = propuesta.Detalle!;
        Assert.Equal(1, detalle.CantidadDeCobros);
        Assert.Equal(6.50m, detalle.ComisionRealTotal);
        Assert.Equal(5.00m, detalle.ComisionEstimadaTotal);
        Assert.Equal(0, detalle.CobrosSinCierre);
        var linea = Assert.Single(detalle.Lineas);
        Assert.Equal(cobro.IdCobro, linea.IdCobroMercadoPago);
        Assert.Equal(5m, linea.PorcentajeEstimado);
        Assert.Equal(1.50m, linea.Diferencia);

        foreach (var cliente in new[] { e.Ctx.Admin, e.Ctx.Supervisor })
        {
            Assert.Equal(propuesta.Id, Assert.Single(await PropListarAsync(cliente, importada.Conciliacion.Id)).Id);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await e.Ctx.Vendedor.GetAsync(RutaPropuestas(importada.Conciliacion.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Ctx.Admin.GetAsync(RutaPropuestas(importada.Conciliacion.Id + 999))).StatusCode);
    }

    // Clausula: la diferencia tiene signo: si Mercado Pago cobro MENOS que lo estimado, la propuesta acredita la cuenta.
    [Fact]
    public async Task UnaComisionRealMenorQueLaEstimadaProponeAcreditarLaCuenta()
    {
        var e = await PrepararAsync("mp-prop-signo");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);

        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-3.00")));

        Assert.Equal(2.00m, Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Importe);
    }

    // Clausula: una comision real igual a la estimada no propone nada (la diferencia es cero).
    [Fact]
    public async Task UnaComisionRealIgualALaEstimadaNoProponeNada()
    {
        var e = await PrepararAsync("mp-prop-igual");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);

        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-5.00")));

        Assert.Empty(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
    }

    // Clausula: un cobro de un turno que todavia no acredito el medio no tiene comision estimada que corregir: no entra, no genera
    // propuesta y queda contado en la auditoria de la importacion.
    [Fact]
    public async Task UnCobroDeUnTurnoSinCerrarNoGeneraDiferenciaYQuedaContadoEnLaAuditoria()
    {
        var e = await PrepararAsync("mp-prop-sin-cierre");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m, cerrar: false);

        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));

        Assert.Empty(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var valor = (string)(await ConsultarAsync(
            "SELECT valor_nuevo::text AS v FROM auditoria WHERE accion = 'conciliacion.importacion' AND id_entidad = $1", importada.Conciliacion.Id)).Single()["v"]!;
        Assert.Contains("\"cobros_sin_cierre\": 1", valor);
        Assert.Contains("\"propuestas_diferencia_comision\": 0", valor);
    }

    // Clausula: un cobro conciliado sin comision en el reporte no entra a la diferencia (no hay comision real que comparar).
    [Fact]
    public async Task UnCobroConciliadoSinComisionRealNoGeneraDiferencia()
    {
        var e = await PrepararAsync("mp-prop-sin-comision");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa,
            Reporte(FilaDelReporte(datos.ReferenciaDelPago.ToString(CultureInfo.InvariantCulture), null, "SETTLEMENT", "100.00", null, "95.00", FechaDelReporte)));

        Assert.Empty(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
    }

    // Clausula: un retiro por cada fila WITHDRAWAL o PAYOUT (importe positivo aunque el reporte lo informe negativo); WITHDRAWAL_CANCEL de un retiro que
    // no existe no propone nada (solo se cuenta); el origen es la cuenta de la conexion con Mercado Pago; un monto que no entra en el libro no genera propuesta.
    [Fact]
    public async Task CadaRetiroOPayoutProponeUnMovimientoYLaCancelacionNo()
    {
        var e = await PrepararAsync("mp-prop-retiros");
        await PropMedioConCuentaAsync(e);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa,
            Reporte(
                PropFila("9001", "WITHDRAWAL", "-500.00"),
                PropFila("9002", "PAYOUT", "250.50"),
                PropFila("9003", "WITHDRAWAL_CANCEL", "500.00"),
                PropFila("9004", "WITHDRAWAL", "-1000000000000.00")));

        var propuestas = await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id);
        Assert.Equal(1, importada.RetirosCancelados);
        Assert.Equal(2, propuestas.Count);
        Assert.All(propuestas, p => Assert.Equal("retiro", p.Tipo));
        Assert.Equal(new[] { 500m, 250.50m }, propuestas.Select(p => p.Importe));
        Assert.All(propuestas, p => Assert.Equal(e.Ctx.IdCuenta, p.IdCuentaOrigen));
        Assert.Equal(new[] { "9001", "9002" }, propuestas.Select(p => p.SourceId));
        Assert.Equal(new int?[] { 2, 3 }, propuestas.Select(p => p.NumeroLinea));
    }

    // Clausula: aplicar una diferencia escribe UN ajuste en la cuenta del medio con el signo del calculo (saldo 95 al cierre -> 93,50), deja la
    // propuesta aplicada con la fila del libro, queda auditado, y una segunda aplicacion es 409 sin escribir nada. Solo el Admin aplica.
    [Fact]
    public async Task AplicarLaDiferenciaAjustaLaCuentaDelMedioUnaSolaVezYQuedaAuditado()
    {
        var e = await PrepararAsync("mp-prop-aplicar-diferencia");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        Assert.Equal(95m, await PropSaldoAsync(e.Ctx.IdCuenta));
        var filasAntes = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta);

        Assert.Equal(HttpStatusCode.Forbidden, (await PropAplicarAsync(e.Ctx.Supervisor, importada.Conciliacion.Id, propuesta.Id)).StatusCode);
        Assert.Equal(filasAntes, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));

        var aplicada = await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal("aplicada", aplicada.Estado);
        Assert.NotNull(aplicada.IdMovimientoCuenta);
        Assert.Null(aplicada.IdMovimientoCuentaDestino);
        Assert.Equal(93.50m, await PropSaldoAsync(e.Ctx.IdCuenta));
        Assert.Equal(filasAntes + 1, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        var fila = (await ConsultarAsync(
            "SELECT tipo::text AS tipo, ingreso, egreso, inicio, final, concepto, id_turno_caja FROM movimientos_cuenta WHERE id_movimiento_cuenta = $1",
            aplicada.IdMovimientoCuenta!.Value)).Single();
        Assert.Equal("ajuste", fila["tipo"]);
        Assert.Equal((0m, 1.50m, 95m, 93.50m), ((decimal)fila["ingreso"]!, (decimal)fila["egreso"]!, (decimal)fila["inicio"]!, (decimal)fila["final"]!));
        Assert.Null(fila["id_turno_caja"]);
        Assert.Contains($"conciliación {importada.Conciliacion.Id}", (string)fila["concepto"]!);
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestaaplicada", propuesta.Id));

        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "propuesta_ya_aplicada");
        await AssertCodigoAsync(
            await PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "propuesta_ya_aplicada");
        Assert.Equal(93.50m, await PropSaldoAsync(e.Ctx.IdCuenta));
        Assert.Equal(filasAntes + 1, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestaaplicada", propuesta.Id));
    }

    // Clausula: una diferencia con signo positivo (Mercado Pago cobro menos) acredita la cuenta.
    [Fact]
    public async Task AplicarUnaDiferenciaPositivaAcreditaLaCuenta()
    {
        var e = await PrepararAsync("mp-prop-aplicar-credito");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-3.00")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));

        await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal(97m, await PropSaldoAsync(e.Ctx.IdCuenta));
    }

    // Clausula: un retiro sale de la cuenta de Mercado Pago y entra a la cuenta elegida en UNA transaccion: dos filas de ajuste, cada saldo se
    // mueve por el monto, la propuesta recuerda las dos filas y la cuenta de destino, y queda auditado.
    [Fact]
    public async Task AplicarUnRetiroMueveElDineroDeLaCuentaDeMercadoPagoALaElegida()
    {
        var e = await PrepararAsync("mp-prop-aplicar-retiro");
        await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9101", "WITHDRAWAL", "-40.00")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        Assert.Equal(95m, await PropSaldoAsync(e.Ctx.IdCuenta));

        var aplicada = await LeerAsync<PropuestaDeTesoreria>(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id, idBanco));

        Assert.Equal("aplicada", aplicada.Estado);
        Assert.Equal(idBanco, aplicada.IdCuentaDestino);
        Assert.Equal("Banco", aplicada.NombreCuentaDestino);
        Assert.NotNull(aplicada.IdMovimientoCuenta);
        Assert.NotNull(aplicada.IdMovimientoCuentaDestino);
        Assert.Equal(55m, await PropSaldoAsync(e.Ctx.IdCuenta));
        Assert.Equal(40m, await PropSaldoAsync(idBanco));
        var salida = (await ConsultarAsync(
            "SELECT id_cuenta_tesoreria, tipo::text AS tipo, egreso FROM movimientos_cuenta WHERE id_movimiento_cuenta = $1", aplicada.IdMovimientoCuenta!.Value)).Single();
        Assert.Equal((e.Ctx.IdCuenta, "ajuste", 40m), ((int)salida["id_cuenta_tesoreria"]!, (string)salida["tipo"]!, (decimal)salida["egreso"]!));
        var entrada = (await ConsultarAsync(
            "SELECT id_cuenta_tesoreria, tipo::text AS tipo, ingreso FROM movimientos_cuenta WHERE id_movimiento_cuenta = $1", aplicada.IdMovimientoCuentaDestino!.Value)).Single();
        Assert.Equal((idBanco, "ajuste", 40m), ((int)entrada["id_cuenta_tesoreria"]!, (string)entrada["tipo"]!, (decimal)entrada["ingreso"]!));
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestaaplicada", propuesta.Id));
    }

    // Clausula: el retiro exige una cuenta de destino activa de la misma empresa y distinta del origen; una diferencia no lleva destino; nada
    // se escribe y la propuesta sigue pendiente. El 404 va antes que cualquier validacion del cuerpo.
    [Fact]
    public async Task LaCuentaDeDestinoDeUnRetiroSeValidaYNadaSeEscribeSiFalla()
    {
        var e = await PrepararAsync("mp-prop-destino");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50"), PropFila("9201", "WITHDRAWAL", "-10.00")));
        var propuestas = await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id);
        var diferencia = propuestas.Single(p => p.Tipo == "diferencia_comision");
        var retiro = propuestas.Single(p => p.Tipo == "retiro");
        var (_, idCuentaAjena) = await _apoyo.CrearOtraEmpresaConCuentaAsync(e.Ctx, "Otra empresa del destino");
        var idInactiva = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco inactivo", activo: false);
        var filasAntes = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta);

        await AssertCodigoAsync(await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id), HttpStatusCode.BadRequest, "cuenta_destino_requerida");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, e.Ctx.IdCuenta), HttpStatusCode.BadRequest, "cuentas_iguales");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idCuentaAjena), HttpStatusCode.BadRequest, "cuenta_destino_invalida");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idInactiva), HttpStatusCode.BadRequest, "cuenta_destino_invalida");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, 987654), HttpStatusCode.BadRequest, "cuenta_destino_invalida");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, diferencia.Id, idInactiva), HttpStatusCode.BadRequest, "cuenta_destino_no_aplica");

        Assert.Equal(HttpStatusCode.NotFound, (await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id + 999, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id + 999, retiro.Id, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id + 999)).StatusCode);

        var otra = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9202", "WITHDRAWAL", "-5.00")));
        Assert.Equal(HttpStatusCode.NotFound, (await PropAplicarAsync(e.Ctx.Admin, otra.Conciliacion.Id, retiro.Id, 0)).StatusCode);

        Assert.Equal(filasAntes, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        Assert.All(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id), p => Assert.Equal("pendiente", p.Estado));
    }

    // Clausula: sin una cuenta en los medios de Mercado Pago de la empresa un retiro no tiene origen fijo ni opciones: aplicar exige idCuentaOrigen
    // (400 cuenta_origen_requerida), una cuenta que no es de esos medios se rechaza (400 cuenta_origen_invalida) y solo queda descartarlo.
    [Fact]
    public async Task UnRetiroSinCuentaEnLosMediosDeMercadoPagoNoTieneOrigenYSoloSePuedeDescartar()
    {
        var e = await PrepararAsync("mp-prop-sin-origen");
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9301", "WITHDRAWAL", "-10.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        Assert.Null(retiro.IdCuentaOrigen);
        Assert.Empty(retiro.OrigenesPosibles);

        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idBanco), HttpStatusCode.BadRequest, "cuenta_origen_requerida");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idBanco, e.Ctx.IdCuenta), HttpStatusCode.BadRequest, "cuenta_origen_invalida");

        var descartada = await LeerAsync<PropuestaDeTesoreria>(await PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id));
        Assert.Equal("descartada", descartada.Estado);
    }

    // Clausula (R2): el origen de un retiro es la cuenta de los medios de Mercado Pago, NO la de la conexion: con un solo medio con cuenta propia
    // (distinta de la de la conexion) el origen fijo es esa cuenta y sale de ella lo retirado; elegir otra origen es 400 cuenta_origen_no_aplica.
    [Fact]
    public async Task ElOrigenDeUnRetiroEsLaCuentaDelMedioYNoLaDeLaConexionConMercadoPago()
    {
        var e = await PrepararAsync("mp-prop-origen-del-medio");
        var idCuentaDelMedio = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Cuenta del medio");
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");
        await EjecutarAsync("UPDATE medios_pago SET id_cuenta_tesoreria = $1 WHERE id_medio_pago = $2", idCuentaDelMedio, e.IdMedio);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9311", "WITHDRAWAL", "-40.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));

        Assert.Equal(idCuentaDelMedio, retiro.IdCuentaOrigen);
        Assert.NotEqual(e.Ctx.IdCuenta, retiro.IdCuentaOrigen);
        Assert.Empty(retiro.OrigenesPosibles);
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idBanco, idBanco), HttpStatusCode.BadRequest, "cuenta_origen_no_aplica");

        await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idBanco));

        Assert.Equal(-40m, await PropSaldoAsync(idCuentaDelMedio));
        Assert.Equal(40m, await PropSaldoAsync(idBanco));
        Assert.Equal(0L, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
    }

    // Clausula (R2): con dos medios de Mercado Pago que acreditan en cuentas distintas no hay origen fijo: la propuesta ofrece las dos y aplicar exige
    // elegir una; una cuenta que no es de esos medios (banco, otra empresa, inactiva, inexistente) es 400 cuenta_origen_invalida; igual al destino es
    // 400 cuentas_iguales; la elegida queda escrita en la propuesta y es de donde sale el dinero. Una diferencia no admite idCuentaOrigen.
    [Fact]
    public async Task ConVariasCuentasDeMediosDeMercadoPagoElAdminEligeElOrigenYSoloEntreEsas()
    {
        var e = await PrepararAsync("mp-prop-origen-elegido");
        var idCuentaB = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Cuenta del segundo medio");
        var idMedioB = await CrearMedioConProveedorAsync(e.Ctx, "QR Mercado Pago B");
        await EjecutarAsync("UPDATE medios_pago SET id_cuenta_tesoreria = $1 WHERE id_medio_pago = $2", idCuentaB, idMedioB);
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");
        var idInactiva = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Inactiva", activo: false);
        var (_, idCuentaAjena) = await _apoyo.CrearOtraEmpresaConCuentaAsync(e.Ctx, "Otra empresa del origen");
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50"), PropFila("9321", "WITHDRAWAL", "-30.00")));
        var propuestas = await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id);
        var retiro = propuestas.Single(p => p.Tipo == "retiro");
        var diferencia = propuestas.Single(p => p.Tipo == "diferencia_comision");

        Assert.Null(retiro.IdCuentaOrigen);
        Assert.Equal(new[] { e.Ctx.IdCuenta, idCuentaB }, retiro.OrigenesPosibles.Select(c => c.Id));
        Assert.Empty(diferencia.OrigenesPosibles);
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idBanco), HttpStatusCode.BadRequest, "cuenta_origen_requerida");
        foreach (var invalida in new[] { idBanco, idInactiva, idCuentaAjena, 987654 })
        {
            await AssertCodigoAsync(
                await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, e.Ctx.IdCuenta, invalida), HttpStatusCode.BadRequest, "cuenta_origen_invalida");
        }

        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idCuentaB, idCuentaB), HttpStatusCode.BadRequest, "cuentas_iguales");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, diferencia.Id, null, e.Ctx.IdCuenta), HttpStatusCode.BadRequest, "cuenta_origen_no_aplica");
        Assert.Equal(0L, await PropCantidadDeFilasAsync(idCuentaB));
        Assert.Equal(0L, await PropCantidadDeFilasAsync(idBanco));

        var aplicada = await LeerAsync<PropuestaDeTesoreria>(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idBanco, idCuentaB));

        Assert.Equal(idCuentaB, aplicada.IdCuentaOrigen);
        Assert.Empty(aplicada.OrigenesPosibles);
        Assert.Equal(-30m, await PropSaldoAsync(idCuentaB));
        Assert.Equal(30m, await PropSaldoAsync(idBanco));
        Assert.Equal(95m, await PropSaldoAsync(e.Ctx.IdCuenta));
    }

    // Clausula (R1): un retiro se propone UNA vez por empresa aunque dos reportes de rangos que se pisan lo traigan: el segundo reporte no vuelve a
    // proponer el retiro que el primero ya propuso, ni cuando esta aplicado ni cuando esta descartado; el resumen de la importacion y la auditoria lo
    // cuentan; aplicarlo una vez mueve el dinero una sola vez y una tercera importacion con la misma operacion tampoco genera otra.
    [Fact]
    public async Task UnRetiroQueAparecenEnDosReportesQueSePisanSeProponeYSeAplicaUnaSolaVez()
    {
        var e = await PrepararAsync("mp-prop-reportes-que-se-pisan");
        await PropMedioConCuentaAsync(e);
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");

        var primera = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9801", "WITHDRAWAL", "-40.00"), PropFila("9802", "PAYOUT", "-25.00")));
        Assert.Equal(0, primera.RetirosYaPropuestos);
        var retiroUno = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id), p => p.SourceId == "9801");
        await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiroUno.Id, idBanco));
        var retiroDos = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id), p => p.SourceId == "9802");
        await LeerAsync<PropuestaDeTesoreria>(await PropDescartarAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiroDos.Id));

        var segunda = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa,
            Reporte(PropFila("9801", "WITHDRAWAL", "-40.00"), PropFila("9802", "PAYOUT", "-25.00"), PropFila("9803", "WITHDRAWAL", "-10.00")));

        Assert.Equal(2, segunda.RetirosYaPropuestos);
        var propuestasDeLaSegunda = await PropListarAsync(e.Ctx.Admin, segunda.Conciliacion.Id);
        var nueva = Assert.Single(propuestasDeLaSegunda);
        Assert.Equal("9803", nueva.SourceId);
        var valor = (string)(await ConsultarAsync(
            "SELECT valor_nuevo::text AS v FROM auditoria WHERE accion = 'conciliacion.importacion' AND id_entidad = $1", segunda.Conciliacion.Id)).Single()["v"]!;
        Assert.Contains("\"retiros_ya_propuestos\": 2", valor);
        Assert.Contains("\"propuestas_retiro\": 1", valor);

        var tercera = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9801", "WITHDRAWAL", "-40.00"), PropFila("9804", "WITHDRAWAL", "-5.00")));
        Assert.Equal(1, tercera.RetirosYaPropuestos);
        Assert.Equal("9804", Assert.Single(await PropListarAsync(e.Ctx.Admin, tercera.Conciliacion.Id)).SourceId);

        Assert.Equal(1L, (long)(await ConsultarAsync(
            "SELECT count(*) AS n FROM propuestas_tesoreria_mercadopago WHERE id_empresa = $1 AND tipo = 'retiro' AND clave_origen = '9801'", e.Ctx.IdEmpresa)).Single()["n"]!);
        Assert.Equal(40m, await PropSaldoAsync(idBanco));
        Assert.Equal(-40m, await PropSaldoAsync(e.Ctx.IdCuenta));
    }

    // Clausula (R1): un SOURCE_ID repetido dentro del mismo archivo (otra fila, otro importe) tampoco se propone dos veces.
    [Fact]
    public async Task UnRetiroRepetidoEnElMismoArchivoSeProponeUnaSolaVez()
    {
        var e = await PrepararAsync("mp-prop-retiro-repetido");

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9811", "WITHDRAWAL", "-40.00"), PropFila("9811", "WITHDRAWAL", "-41.00")));

        Assert.Equal(1, importada.RetirosYaPropuestos);
        Assert.Equal(40m, Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Importe);
    }

    // Clausula: descartar no escribe nada en la tesoreria, queda auditado, es solo del Admin, y una propuesta descartada no se aplica ni se
    // vuelve a descartar.
    [Fact]
    public async Task DescartarNoEscribeNadaYUnaPropuestaDescartadaNoSeAplica()
    {
        var e = await PrepararAsync("mp-prop-descartar");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var filasAntes = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta);

        Assert.Equal(HttpStatusCode.Forbidden, (await PropDescartarAsync(e.Ctx.Supervisor, importada.Conciliacion.Id, propuesta.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PropDescartarAsync(e.Ctx.Vendedor, importada.Conciliacion.Id, propuesta.Id)).StatusCode);

        var descartada = await LeerAsync<PropuestaDeTesoreria>(await PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal("descartada", descartada.Estado);
        Assert.NotNull(descartada.ResueltaEl);
        Assert.Equal(filasAntes, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestadescartada", propuesta.Id));
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "propuesta_descartada");
        await AssertCodigoAsync(
            await PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "propuesta_ya_descartada");
        Assert.Equal(filasAntes, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
    }

    // Clausula: dos aplicaciones simultaneas de la misma propuesta se serializan en el lock de su fila: una gana y la otra ve 409; el libro
    // recibe UNA sola fila.
    [Fact]
    public async Task DosAplicacionesSimultaneasDeLaMismaPropuestaEscribenUnaSolaVez()
    {
        var e = await PrepararAsync("mp-prop-carrera");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var filasAntes = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta);

        var respuestas = await Task.WhenAll(
            PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id),
            PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id),
            PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.Conflict },
            respuestas.Select(r => r.StatusCode).OrderBy(s => (int)s));
        Assert.Equal(filasAntes + 1, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        Assert.Equal(93.50m, await PropSaldoAsync(e.Ctx.IdCuenta));
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestaaplicada", propuesta.Id));
    }

    // Clausula: aplicar descartando y aplicando a la vez: una sola de las dos resoluciones se queda con la propuesta.
    [Fact]
    public async Task AplicarYDescartarAlMismoTiempoSeQuedanConUnaSolaResolucion()
    {
        var e = await PrepararAsync("mp-prop-carrera-mixta");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var filasAntes = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta);

        var respuestas = await Task.WhenAll(
            PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id),
            PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, respuestas.Select(r => r.StatusCode).OrderBy(s => (int)s));
        var final = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var escribio = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta) - filasAntes;
        Assert.Equal(final.Estado == "aplicada" ? 1 : 0, escribio);
    }

    // Clausula: un ajuste fechado en o antes de la marca de conciliacion de tesoreria de la empresa responde 409 periodo_conciliado: nada se
    // escribe y la propuesta sigue pendiente (se puede aplicar cuando la marca ya no la cubra o descartar).
    [Fact]
    public async Task AplicarEnUnPeriodoConciliadoDeTesoreriaEs409YNoEscribeNada()
    {
        var e = await PrepararAsync("mp-prop-periodo");
        await PropMedioConCuentaAsync(e);
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9401", "WITHDRAWAL", "-10.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        await EjecutarAsync(
            "INSERT INTO conciliaciones_tesoreria (id_tenant, id_empresa, hasta, id_usuario, creado_el) VALUES ($1, $2, $3, $4, now())",
            e.Ctx.IdTenant, e.Ctx.IdEmpresa, DateTimeOffset.UtcNow.AddDays(1), e.Ctx.IdUsuarioAdmin);

        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, retiro.Id, idBanco), HttpStatusCode.Conflict, "periodo_conciliado");

        Assert.Equal(0L, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        Assert.Equal(0L, await PropCantidadDeFilasAsync(idBanco));
        Assert.Equal("pendiente", Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Estado);
    }

    // Clausula: la diferencia (un solo ajuste, en la cuenta del medio) tambien respeta el periodo conciliado: 409 y el libro no cambia.
    [Fact]
    public async Task AplicarUnaDiferenciaEnUnPeriodoConciliadoDeTesoreriaEs409YNoEscribeNada()
    {
        var e = await PrepararAsync("mp-prop-periodo-diferencia");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var filasAntes = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta);
        await EjecutarAsync(
            "INSERT INTO conciliaciones_tesoreria (id_tenant, id_empresa, hasta, id_usuario, creado_el) VALUES ($1, $2, $3, $4, now())",
            e.Ctx.IdTenant, e.Ctx.IdEmpresa, DateTimeOffset.UtcNow.AddDays(1), e.Ctx.IdUsuarioAdmin);

        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "periodo_conciliado");

        Assert.Equal(filasAntes, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        Assert.Equal("pendiente", Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Estado);
    }

    // Clausula: las propuestas son del tenant: otro tenant no las lista, no las aplica ni las descarta (RLS + filtro de tenant).
    [Fact]
    public async Task LasPropuestasDeOtroTenantNoSeVenNiSeResuelven()
    {
        var e = await PrepararAsync("mp-prop-tenant");
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9501", "WITHDRAWAL", "-10.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var ajeno = await _apoyo.PrepararAsync("mp-prop-ajeno");

        Assert.Equal(HttpStatusCode.NotFound, (await ajeno.Admin.GetAsync(RutaPropuestas(importada.Conciliacion.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PropAplicarAsync(ajeno.Admin, importada.Conciliacion.Id, retiro.Id, ajeno.IdCuenta)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PropDescartarAsync(ajeno.Admin, importada.Conciliacion.Id, retiro.Id)).StatusCode);
        Assert.Equal("pendiente", Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Estado);
    }

    // Clausula: ux_propuestas_tesoreria_mercadopago_origen: la base rechaza dos diferencias de la misma cuenta en una conciliacion (23505), asi que
    // recalcular nunca duplica. Solo la escribe la importacion, dentro de la transaccion que crea la conciliacion, por eso no hay traduccion a 409:
    // ningun cliente la alcanza.
    [Fact]
    public async Task LaBaseRechazaDosDiferenciasDeLaMismaCuentaEnUnaConciliacion()
    {
        var e = await PrepararAsync("mp-prop-unico");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var existente = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() => PropInsertarDiferenciaCrudaAsync(
            e, importada.Conciliacion.Id, $"cuenta:{existente.IdCuentaOrigen}", e.Ctx.IdCuenta));

        Assert.Equal("23505", rechazo.SqlState);
        Assert.Equal("ux_propuestas_tesoreria_mercadopago_origen", rechazo.ConstraintName);
    }

    // Clausula (R1): ux_propuestas_tesoreria_mercadopago_retiro: la base rechaza un retiro con el mismo SOURCE_ID en otra conciliacion de la empresa
    // (23505), aunque el generador ya lo salte: es el respaldo si dos importaciones de la misma empresa llegaran a correr a la vez.
    [Fact]
    public async Task LaBaseRechazaElMismoRetiroEnOtraConciliacionDeLaEmpresa()
    {
        var e = await PrepararAsync("mp-prop-unico-retiro");
        await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9601", "WITHDRAWAL", "-10.00")));
        var segunda = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9602", "WITHDRAWAL", "-20.00")));
        var propuestaDeLaSegunda = Assert.Single(await PropListarAsync(e.Ctx.Admin, segunda.Conciliacion.Id));
        var idFila = (int)(await ConsultarAsync(
            "SELECT id_fila_conciliacion_mercadopago AS id FROM propuestas_tesoreria_mercadopago WHERE id_propuesta_tesoreria_mercadopago = $1", propuestaDeLaSegunda.Id)).Single()["id"]!;

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() => PropInsertarCrudaAsync(
            e, segunda.Conciliacion.Id, "retiro", "9601", 10m, idFila, e.Ctx.IdCuenta, "pendiente"));

        Assert.Equal("23505", rechazo.SqlState);
        Assert.Equal("ux_propuestas_tesoreria_mercadopago_retiro", rechazo.ConstraintName);
    }

    // Clausula (R4): solo cuentan los pagos de comprobantes emitidos, como el bruto del cierre: una venta anulada antes del cierre no entra a la
    // diferencia (queda contada como cobro fuera del calculo) aunque su cobro se haya conciliado con la comision del reporte.
    [Fact]
    public async Task ElPagoDeUnaVentaAnuladaNoEntraALaDiferenciaDeComision()
    {
        var e = await PrepararAsync("mp-prop-venta-anulada");
        var (_, datosVigente) = await PropVentaAsync(e, e.Desde, porcentaje: 5m, cerrar: false);
        var (cobroAnulado, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde + 1);
        var datosAnulado = await ConcDatosDelCobroAsync(cobroAnulado.IdCobro);
        await EjecutarAsync(
            "UPDATE comprobantes_venta SET estado = 'anulado'::estado_comprobante WHERE id_comprobante_venta IN " +
            "(SELECT id_comprobante_venta FROM cobros WHERE id_cobro_mercadopago = $1)", cobroAnulado.IdCobro);
        await PropCerrarTurnoAsync(e);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(
                FilaDelReporte("77001", datosVigente.Referencia, "SETTLEMENT", "100.00", "-6.50", null, FechaDelReporte),
                FilaDelReporte("77002", datosAnulado.Referencia, "SETTLEMENT", "100.00", "-6.50", null, FechaDelReporte)));

        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        Assert.Equal(-1.50m, propuesta.Importe);
        Assert.Equal(1, propuesta.Detalle!.CantidadDeCobros);
        Assert.Equal(1, propuesta.Detalle.CobrosSinCierre);
    }

    // Clausula: los CHECK de la forma de la propuesta: un retiro con importe no positivo (23514), una aplicada sin fila del libro (23514) y una
    // cuenta de otra empresa (FK compuesta con la empresa, 23503).
    [Fact]
    public async Task LaBaseRechazaPropuestasDeFormaInvalidaYCuentasDeOtraEmpresa()
    {
        var e = await PrepararAsync("mp-prop-checks");
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9701", "WITHDRAWAL", "-10.00")));
        var idFila = (int)(await ConsultarAsync(
            "SELECT id_fila_conciliacion_mercadopago AS id FROM filas_conciliacion_mercadopago WHERE id_conciliacion_mercadopago = $1", importada.Conciliacion.Id)).Single()["id"]!;
        var (_, idCuentaAjena) = await _apoyo.CrearOtraEmpresaConCuentaAsync(e.Ctx, "Otra empresa de la FK");

        var negativo = await Assert.ThrowsAsync<PostgresException>(() => PropInsertarCrudaAsync(
            e, importada.Conciliacion.Id, "retiro", "90", -10m, idFila, e.Ctx.IdCuenta, "pendiente"));
        Assert.Equal("23514", negativo.SqlState);
        Assert.Equal("ck_propuestas_tesoreria_mercadopago_forma", negativo.ConstraintName);

        var aplicadaSinFila = await Assert.ThrowsAsync<PostgresException>(() => PropInsertarCrudaAsync(
            e, importada.Conciliacion.Id, "retiro", "91", 10m, idFila, e.Ctx.IdCuenta, "aplicada"));
        Assert.Equal("23514", aplicadaSinFila.SqlState);

        var ajena = await Assert.ThrowsAsync<PostgresException>(() => PropInsertarCrudaAsync(
            e, importada.Conciliacion.Id, "retiro", "92", 10m, idFila, idCuentaAjena, "pendiente"));
        Assert.Equal("23503", ajena.SqlState);
        Assert.Equal("fk_propuestas_tesoreria_mercadopago_cuenta_origen", ajena.ConstraintName);
    }

    private Task PropInsertarDiferenciaCrudaAsync(Escenario e, int idConciliacion, string clave, int idCuentaOrigen) =>
        EjecutarAsync(
            "INSERT INTO propuestas_tesoreria_mercadopago (id_tenant, id_empresa, id_conciliacion_mercadopago, tipo, clave_origen, importe, " +
            "id_cuenta_origen, estado, detalle, created_at, updated_at) VALUES ($1, $2, $3, 'diferencia_comision', $4, 1, $5, 'pendiente', '{}'::jsonb, now(), now())",
            e.Ctx.IdTenant, e.Ctx.IdEmpresa, idConciliacion, clave, idCuentaOrigen);

    private Task PropInsertarCrudaAsync(
        Escenario e, int idConciliacion, string tipo, string clave, decimal importe, int idFila, int idCuentaOrigen, string estado) =>
        EjecutarAsync(
            "INSERT INTO propuestas_tesoreria_mercadopago (id_tenant, id_empresa, id_conciliacion_mercadopago, tipo, clave_origen, importe, " +
            "id_fila_conciliacion_mercadopago, id_cuenta_origen, estado, created_at, updated_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, now(), now())",
            e.Ctx.IdTenant, e.Ctx.IdEmpresa, idConciliacion, tipo, clave, importe, idFila, idCuentaOrigen, estado);
}
