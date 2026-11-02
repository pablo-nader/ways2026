using System.Net;
using Ways.Application.MercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// Judgment-day E7 ronda 2 contra Postgres real: reabrir una propuesta descartada (la descartada ya no es irreversible, y es el único camino de vuelta de
/// un retiro que la importación no vuelve a proponer) y las cancelaciones de retiro (<c>WITHDRAWAL_CANCEL</c>): descartan el retiro pendiente, proponen la
/// reversión del aplicado y solo se cuentan si el retiro estaba descartado, no existe o viene en el mismo archivo. Comparte el escenario de
/// <see cref="MercadoPagoVentaTests"/>.
/// </summary>
public partial class MercadoPagoVentaTests
{
    private static Task<HttpResponseMessage> PropReabrirAsync(HttpClient cliente, int idConciliacion, int idPropuesta) =>
        cliente.PostAsync($"{RutaPropuestas(idConciliacion)}/{idPropuesta}/reabrir", null);

    // Clausula: reabrir devuelve una descartada a pendiente sin tocar el libro, borra quien y cuando la resolvio, queda auditado, es solo del Admin y
    // la propuesta vuelve a poder aplicarse; reabrir una pendiente, una aplicada o una que no existe no cambia nada (409 propuesta_no_descartada / 404).
    [Fact]
    public async Task ReabrirUnaPropuestaDescartadaLaDevuelveAPendienteYVuelveAPoderAplicarse()
    {
        var e = await PrepararAsync("mp-prop-reabrir");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        var filasAntes = await PropCantidadDeFilasAsync(e.Ctx.IdCuenta);

        await AssertCodigoAsync(
            await PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "propuesta_no_descartada");
        await LeerAsync<PropuestaDeTesoreria>(await PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal(HttpStatusCode.Forbidden, (await PropReabrirAsync(e.Ctx.Supervisor, importada.Conciliacion.Id, propuesta.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PropReabrirAsync(e.Ctx.Vendedor, importada.Conciliacion.Id, propuesta.Id)).StatusCode);
        Assert.Equal("descartada", Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Estado);

        var reabierta = await LeerAsync<PropuestaDeTesoreria>(await PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal("pendiente", reabierta.Estado);
        Assert.Null(reabierta.ResueltaEl);
        Assert.Null(reabierta.ResueltaPor);
        Assert.Null(reabierta.Motivo);
        Assert.Equal(filasAntes, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestareabierta", propuesta.Id));
        await AssertCodigoAsync(
            await PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "propuesta_no_descartada");

        var aplicada = await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));
        Assert.Equal("aplicada", aplicada.Estado);
        Assert.Equal(93.50m, await PropSaldoAsync(e.Ctx.IdCuenta));
        await AssertCodigoAsync(
            await PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id), HttpStatusCode.Conflict, "propuesta_no_descartada");
        Assert.Equal(404, (int)(await PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id + 9999)).StatusCode);
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestareabierta", propuesta.Id));
    }

    // Clausula: un retiro descartado no se vuelve a proponer en otro reporte, asi que reabrirlo es la unica forma de aplicarlo; reabierto mueve el dinero
    // una sola vez.
    [Fact]
    public async Task UnRetiroDescartadoSeReabreYSeAplicaUnaSolaVez()
    {
        var e = await PrepararAsync("mp-prop-reabrir-retiro");
        await PropMedioConCuentaAsync(e);
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");
        var primera = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9301", "WITHDRAWAL", "-40.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id));
        await LeerAsync<PropuestaDeTesoreria>(await PropDescartarAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiro.Id));
        var segunda = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9301", "WITHDRAWAL", "-40.00"), PropFila("9302", "WITHDRAWAL", "-5.00")));
        Assert.Equal(1, segunda.RetirosYaPropuestos);
        Assert.DoesNotContain(await PropListarAsync(e.Ctx.Admin, segunda.Conciliacion.Id), p => p.SourceId == "9301");

        await LeerAsync<PropuestaDeTesoreria>(await PropReabrirAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiro.Id));
        var aplicada = await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiro.Id, idBanco));

        Assert.Equal("aplicada", aplicada.Estado);
        Assert.Equal(-40m, await PropSaldoAsync(e.Ctx.IdCuenta));
        Assert.Equal(40m, await PropSaldoAsync(idBanco));
    }

    // Clausula: dos reaperturas simultaneas de la misma descartada se serializan en el lock de su fila: una gana y la otra ve 409.
    [Fact]
    public async Task DosReaperturasSimultaneasDeLaMismaPropuestaSeQuedanConUna()
    {
        var e = await PrepararAsync("mp-prop-reabrir-carrera");
        var (_, datos) = await PropVentaAsync(e, e.Desde, porcentaje: 5m);
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFilaDelCobro(datos, "-6.50")));
        var propuesta = Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id));
        await LeerAsync<PropuestaDeTesoreria>(await PropDescartarAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        var respuestas = await Task.WhenAll(
            PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id),
            PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id),
            PropReabrirAsync(e.Ctx.Admin, importada.Conciliacion.Id, propuesta.Id));

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.Conflict },
            respuestas.Select(r => r.StatusCode).OrderBy(s => (int)s));
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestareabierta", propuesta.Id));
    }

    // Clausula (rama pendiente): un WITHDRAWAL_CANCEL de un retiro pendiente lo descarta con el motivo "cancelado por Mercado Pago", queda auditado, no
    // mueve dinero, la cancelacion no propone nada y se cuenta; el retiro descartado no se aplica y el Admin lo puede reabrir.
    [Fact]
    public async Task LaCancelacionDeUnRetiroPendienteLoDescartaSinMoverDinero()
    {
        var e = await PrepararAsync("mp-prop-cancel-pendiente");
        await PropMedioConCuentaAsync(e);
        var primera = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9311", "WITHDRAWAL", "-40.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id));
        Assert.Equal("pendiente", retiro.Estado);

        var segunda = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9311", "WITHDRAWAL_CANCEL", "40.00")));

        Assert.Equal(1, segunda.RetirosCancelados);
        Assert.Empty(await PropListarAsync(e.Ctx.Admin, segunda.Conciliacion.Id));
        var descartado = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id));
        Assert.Equal("descartada", descartado.Estado);
        Assert.Equal("cancelado por Mercado Pago", descartado.Motivo);
        Assert.NotNull(descartado.ResueltaEl);
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestadescartada", retiro.Id));
        Assert.Equal(0L, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiro.Id, e.Ctx.IdCuenta), HttpStatusCode.Conflict, "propuesta_descartada");

        var reabierto = await LeerAsync<PropuestaDeTesoreria>(await PropReabrirAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiro.Id));
        Assert.Equal("pendiente", reabierto.Estado);
        Assert.Null(reabierto.Motivo);
    }

    // Clausula (rama aplicada): un WITHDRAWAL_CANCEL de un retiro aplicado propone una reversion (retiro_cancelado) entre las MISMAS dos cuentas, con el
    // mismo monto, que no lleva cuentas a elegir; aplicarla devuelve los dos saldos en una transaccion, una sola vez, y un tercer reporte con la misma
    // cancelacion no propone otra.
    [Fact]
    public async Task LaCancelacionDeUnRetiroAplicadoProponeLaReversionYAplicarlaDevuelveLosSaldos()
    {
        var e = await PrepararAsync("mp-prop-cancel-aplicado");
        await PropMedioConCuentaAsync(e);
        var idBanco = await ApoyoDeMercadoPago.CrearCuentaAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "Banco");
        var primera = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9321", "WITHDRAWAL", "-40.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id));
        await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiro.Id, idBanco));
        Assert.Equal(-40m, await PropSaldoAsync(e.Ctx.IdCuenta));
        Assert.Equal(40m, await PropSaldoAsync(idBanco));

        var segunda = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9321", "WITHDRAWAL_CANCEL", "40.00")));

        Assert.Equal(1, segunda.RetirosCancelados);
        var reversion = Assert.Single(await PropListarAsync(e.Ctx.Admin, segunda.Conciliacion.Id));
        Assert.Equal("retiro_cancelado", reversion.Tipo);
        Assert.Equal("pendiente", reversion.Estado);
        Assert.Equal(40m, reversion.Importe);
        Assert.Equal((e.Ctx.IdCuenta, idBanco, "9321"), (reversion.IdCuentaOrigen!.Value, reversion.IdCuentaDestino!.Value, reversion.SourceId));
        Assert.Equal("aplicada", Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id)).Estado);
        Assert.Equal(-40m, await PropSaldoAsync(e.Ctx.IdCuenta));

        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, segunda.Conciliacion.Id, reversion.Id, idBanco), HttpStatusCode.BadRequest, "cuenta_destino_no_aplica");
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, segunda.Conciliacion.Id, reversion.Id, null, e.Ctx.IdCuenta), HttpStatusCode.BadRequest, "cuenta_origen_no_aplica");
        Assert.Equal(HttpStatusCode.Forbidden, (await PropAplicarAsync(e.Ctx.Supervisor, segunda.Conciliacion.Id, reversion.Id)).StatusCode);
        Assert.Equal(-40m, await PropSaldoAsync(e.Ctx.IdCuenta));

        var aplicada = await LeerAsync<PropuestaDeTesoreria>(await PropAplicarAsync(e.Ctx.Admin, segunda.Conciliacion.Id, reversion.Id));

        Assert.Equal("aplicada", aplicada.Estado);
        Assert.Equal(0m, await PropSaldoAsync(e.Ctx.IdCuenta));
        Assert.Equal(0m, await PropSaldoAsync(idBanco));
        var entrada = (await ConsultarAsync(
            "SELECT id_cuenta_tesoreria, ingreso, concepto FROM movimientos_cuenta WHERE id_movimiento_cuenta = $1", aplicada.IdMovimientoCuenta!.Value)).Single();
        Assert.Equal((e.Ctx.IdCuenta, 40m), ((int)entrada["id_cuenta_tesoreria"]!, (decimal)entrada["ingreso"]!));
        Assert.Contains("Reversión del retiro cancelado", (string)entrada["concepto"]!);
        var salida = (await ConsultarAsync(
            "SELECT id_cuenta_tesoreria, egreso FROM movimientos_cuenta WHERE id_movimiento_cuenta = $1", aplicada.IdMovimientoCuentaDestino!.Value)).Single();
        Assert.Equal((idBanco, 40m), ((int)salida["id_cuenta_tesoreria"]!, (decimal)salida["egreso"]!));
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestaaplicada", reversion.Id));
        await AssertCodigoAsync(
            await PropAplicarAsync(e.Ctx.Admin, segunda.Conciliacion.Id, reversion.Id), HttpStatusCode.Conflict, "propuesta_ya_aplicada");

        var tercera = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9321", "WITHDRAWAL_CANCEL", "40.00"), PropFila("9322", "WITHDRAWAL", "-1.00")));
        Assert.Equal(1, tercera.RetirosCancelados);
        Assert.DoesNotContain(await PropListarAsync(e.Ctx.Admin, tercera.Conciliacion.Id), p => p.Tipo == "retiro_cancelado");
        Assert.Equal(1L, (long)(await ConsultarAsync(
            "SELECT count(*) AS n FROM propuestas_tesoreria_mercadopago WHERE id_empresa = $1 AND tipo = 'retiro_cancelado' AND clave_origen = '9321'", e.Ctx.IdEmpresa)).Single()["n"]!);
        Assert.Equal(0m, await PropSaldoAsync(e.Ctx.IdCuenta));
    }

    // Clausula (rama descartada o inexistente): la cancelacion de un retiro descartado a mano, o que nunca se propuso, no propone nada ni toca el retiro;
    // solo se cuenta.
    [Fact]
    public async Task LaCancelacionDeUnRetiroDescartadoOInexistenteSoloSeCuenta()
    {
        var e = await PrepararAsync("mp-prop-cancel-descartado");
        await PropMedioConCuentaAsync(e);
        var primera = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9331", "WITHDRAWAL", "-40.00")));
        var retiro = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id));
        await LeerAsync<PropuestaDeTesoreria>(await PropDescartarAsync(e.Ctx.Admin, primera.Conciliacion.Id, retiro.Id));

        var segunda = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(PropFila("9331", "WITHDRAWAL_CANCEL", "40.00"), PropFila("9332", "WITHDRAWAL_CANCEL", "10.00")));

        Assert.Equal(2, segunda.RetirosCancelados);
        Assert.Empty(await PropListarAsync(e.Ctx.Admin, segunda.Conciliacion.Id));
        var intacto = Assert.Single(await PropListarAsync(e.Ctx.Admin, primera.Conciliacion.Id));
        Assert.Equal("descartada", intacto.Estado);
        Assert.Null(intacto.Motivo);
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.propuestadescartada", retiro.Id));
        Assert.Equal(0L, await PropCantidadDeFilasAsync(e.Ctx.IdCuenta));
    }

    // Clausula (mismo archivo): si el reporte trae el retiro y su cancelacion no se propone el retiro; se cuenta como cancelado y no como "ya propuesto".
    [Fact]
    public async Task UnRetiroYSuCancelacionEnElMismoArchivoNoProponenNada()
    {
        var e = await PrepararAsync("mp-prop-cancel-mismo-archivo");
        await PropMedioConCuentaAsync(e);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa,
            Reporte(PropFila("9341", "WITHDRAWAL", "-40.00"), PropFila("9341", "WITHDRAWAL_CANCEL", "40.00"), PropFila("9342", "WITHDRAWAL", "-7.00")));

        Assert.Equal(1, importada.RetirosCancelados);
        Assert.Equal(0, importada.RetirosYaPropuestos);
        Assert.Equal("9342", Assert.Single(await PropListarAsync(e.Ctx.Admin, importada.Conciliacion.Id)).SourceId);
    }
}
