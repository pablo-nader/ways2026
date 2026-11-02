using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Ways.Application.Ventas;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using Ways.Application.MercadoPago;
using Ways.Application.Organizacion;

namespace Ways.IntegrationTests;

/// <summary>
/// La conciliación con el reporte de transacciones de Mercado Pago (E6-T1) contra Postgres real: subir el CSV, el cotejo de cada fila con
/// los cobros de la empresa, la comisión y el neto reales en el cobro, los candidatos de una aprobación manual (que nunca se aplican
/// solos), la confirmación del Admin, el rechazo del mismo archivo dos veces (con su carrera y su SQLSTATE), el alcance por empresa y por
/// tenant, y los roles. Comparte el escenario de venta con cobro de <see cref="MercadoPagoVentaTests"/>.
/// </summary>
public partial class MercadoPagoVentaTests
{
    private const string RutaConciliaciones = "/api/mercadopago/conciliaciones";

    private const string EncabezadoDelReporte =
        "SOURCE_ID,EXTERNAL_REFERENCE,TRANSACTION_TYPE,TRANSACTION_AMOUNT,FEE_AMOUNT,SETTLEMENT_NET_AMOUNT,TRANSACTION_DATE,MONEY_RELEASE_DATE,EXTERNAL_POS_ID";

    private static readonly DateTimeOffset FechaDelReporte = new(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);

    private static string Reporte(params string[] filas) => EncabezadoDelReporte + "\n" + string.Join("\n", filas) + "\n";

    private static string FilaDelReporte(
        string sourceId, string? referencia, string tipo, string importe, string? comision, string? neto, DateTimeOffset fecha,
        DateTimeOffset? liberacion = null, string? pos = null) =>
        string.Join(
            ",", sourceId, referencia ?? string.Empty, tipo, importe, comision ?? string.Empty, neto ?? string.Empty, Iso(fecha),
            liberacion is null ? string.Empty : Iso(liberacion.Value), pos ?? string.Empty);

    private static string Iso(DateTimeOffset fecha) => fecha.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private static async Task<HttpResponseMessage> ConcSubirAsync(HttpClient cliente, int idEmpresa, string csv, string nombre = "reporte.csv")
    {
        using var contenido = new MultipartFormDataContent();
        contenido.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "archivo", nombre);
        return await cliente.PostAsync($"{RutaConciliaciones}?idEmpresa={idEmpresa}", contenido);
    }

    private static async Task<ConciliacionImportada> ConcSubirOkAsync(HttpClient cliente, int idEmpresa, string csv) =>
        await LeerAsync<ConciliacionImportada>(await ConcSubirAsync(cliente, idEmpresa, csv));

    private static async Task<DetalleDeConciliacion> ConcDetalleAsync(HttpClient cliente, int idConciliacion, string consulta = "") =>
        await LeerAsync<DetalleDeConciliacion>(await cliente.GetAsync($"{RutaConciliaciones}/{idConciliacion}{consulta}"));

    private static Task<HttpResponseMessage> ConcConfirmarAsync(HttpClient cliente, int idConciliacion, int idFila, int idCobro) =>
        cliente.PostAsJsonAsync($"{RutaConciliaciones}/{idConciliacion}/filas/{idFila}/confirmar", new SolicitudDeConfirmacionDeFila(idCobro));

    private static async Task<(CobroDeMercadoPago Cobro, int IdVenta)> ConcVentaConMercadoPagoAsync(Escenario e, long numero)
    {
        var cobro = await CobroAprobadoAsync(e, numero);
        var venta = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, numero, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);
        return (cobro, venta.Id);
    }

    private sealed record DatosDelCobro(string Referencia, string IdPago, long ReferenciaDelPago);

    private async Task<DatosDelCobro> ConcDatosDelCobroAsync(int idCobro)
    {
        var fila = (await ConsultarAsync(
            "SELECT external_reference, id_pago_mercadopago, referencia_pago_mercadopago FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1",
            idCobro)).Single();
        return new DatosDelCobro((string)fila["external_reference"]!, (string)fila["id_pago_mercadopago"]!, (long)fila["referencia_pago_mercadopago"]!);
    }

    /// <summary>Una venta de 100 cobrada con una aprobación manual sin verificar, fechada en <paramref name="fecha"/>; devuelve el id del
    /// pago (<c>cobros.id_cobro</c>).</summary>
    private async Task<(int IdVenta, int IdPago)> ConcVentaManualAsync(Escenario e, long numero, DateTimeOffset fecha)
    {
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, numero, PagoManual(e, $"OP-{numero}"))), HttpStatusCode.Created);
        await EjecutarAsync("UPDATE comprobantes_venta SET fecha = $1 WHERE id_comprobante_venta = $2", fecha, emitido.Id);
        var idPago = (int)(await ConsultarAsync(
            "SELECT id_cobro FROM cobros WHERE id_comprobante_venta = $1 AND deleted_at IS NULL", emitido.Id)).Single()["id_cobro"]!;
        return (emitido.Id, idPago);
    }

    private async Task<Dictionary<string, object?>> ConcCobroMercadoPagoAsync(int idCobro) =>
        (await ConsultarAsync(
            "SELECT comision_real, neto_real, fecha_liberacion, id_fila_conciliacion FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1",
            idCobro)).Single();

    private async Task<string?> ConcVerificacionDelPagoAsync(int idPago) =>
        (await ConsultarAsync("SELECT verificacion_mercadopago FROM cobros WHERE id_cobro = $1", idPago)).Single()["verificacion_mercadopago"] as string;

    private async Task<long> ConcAuditoriasAsync(string accion, int idEntidad) =>
        (long)(await ConsultarAsync("SELECT count(*) AS n FROM auditoria WHERE accion = $1 AND id_entidad = $2", accion, idEntidad)).Single()["n"]!;

    // ---- El cotejo --------------------------------------------------------------------------------------------------------------

    // Clausula: un cobro aprobado y ligado a una venta, por el mismo importe, queda conciliado por SOURCE_ID (la referencia numerica del
    // pago, sin EXTERNAL_REFERENCE): se le guardan la comision, el neto y la liberacion reales y la fila que lo concilio; la importacion
    // queda auditada, con su resumen, y el listado y el detalle la devuelven a Supervisor y Admin pero no al Vendedor.
    [Fact]
    public async Task UnReporteConciliaElCobroLigadoPorSourceIdYLeGuardaSuComisionRealYQuedaAuditado()
    {
        var e = await PrepararAsync("mp-conc-conciliado");
        var (cobro, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde);
        var datos = await ConcDatosDelCobroAsync(cobro.IdCobro);
        var libera = FechaDelReporte.AddDays(14);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin,
            e.Ctx.IdEmpresa,
            Reporte(FilaDelReporte(datos.ReferenciaDelPago.ToString(CultureInfo.InvariantCulture), null, "SETTLEMENT", "100.00", "-6.50", "93.50", FechaDelReporte, libera)));

        Assert.Equal(1, importada.Conciliacion.CantidadFilas);
        Assert.Equal(0, importada.Conciliacion.CantidadInvalidas);
        Assert.Empty(importada.Invalidas);
        Assert.Equal(FechaDelReporte, importada.Conciliacion.FechaDesde);
        Assert.Equal(FechaDelReporte, importada.Conciliacion.FechaHasta);
        var resumen = Assert.Single(importada.Conciliacion.Resumen);
        Assert.Equal(new ResumenDeResultadoDeConciliacion("conciliado", 1, 100m, -6.50m, 93.50m), resumen);

        var detalle = await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id);
        var fila = Assert.Single(detalle.Filas);
        Assert.Equal("conciliado", fila.Resultado);
        Assert.Equal(cobro.IdCobro, fila.IdCobroMercadoPago);
        Assert.Equal(2, fila.NumeroLinea);
        Assert.Equal(-6.50m, fila.Comision);

        var guardado = await ConcCobroMercadoPagoAsync(cobro.IdCobro);
        Assert.Equal(-6.50m, guardado["comision_real"]);
        Assert.Equal(93.50m, guardado["neto_real"]);
        Assert.Equal(libera, guardado["fecha_liberacion"] is DateTimeOffset conZona ? conZona : new DateTimeOffset(DateTime.SpecifyKind((DateTime)guardado["fecha_liberacion"]!, DateTimeKind.Utc)));
        Assert.Equal(fila.Id, guardado["id_fila_conciliacion"]);

        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.importacion", importada.Conciliacion.Id));

        foreach (var cliente in new[] { e.Ctx.Admin, e.Ctx.Supervisor })
        {
            var lista = await LeerAsync<List<ConciliacionListado>>(await cliente.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}"));
            Assert.Equal(importada.Conciliacion.Id, Assert.Single(lista).Id);
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync($"{RutaConciliaciones}/{importada.Conciliacion.Id}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await e.Ctx.Vendedor.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Ctx.Vendedor.GetAsync($"{RutaConciliaciones}/{importada.Conciliacion.Id}")).StatusCode);
    }

    // Clausula: EXTERNAL_REFERENCE se busca ANTES que SOURCE_ID: una fila que nombra un cobro por referencia y a otro por id de pago
    // concilia el de la referencia; sin referencia, el id del pago (texto) encuentra al segundo.
    [Fact]
    public async Task LaReferenciaExternaSeBuscaAntesQueElSourceId()
    {
        var e = await PrepararAsync("mp-conc-orden");
        var (uno, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde);
        var (otro, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde + 1);
        var datosUno = await ConcDatosDelCobroAsync(uno.IdCobro);
        var datosOtro = await ConcDatosDelCobroAsync(otro.IdCobro);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin,
            e.Ctx.IdEmpresa,
            Reporte(
                FilaDelReporte(datosOtro.IdPago, datosUno.Referencia, "SETTLEMENT", "100.00", "-1.00", "99.00", FechaDelReporte),
                FilaDelReporte(datosOtro.IdPago, null, "SETTLEMENT", "100.00", "-2.00", "98.00", FechaDelReporte.AddMinutes(1))));

        var filas = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas;
        Assert.Equal(uno.IdCobro, filas[0].IdCobroMercadoPago);
        Assert.Equal(otro.IdCobro, filas[1].IdCobroMercadoPago);
        Assert.Equal(-1.00m, (await ConcCobroMercadoPagoAsync(uno.IdCobro))["comision_real"]);
        Assert.Equal(-2.00m, (await ConcCobroMercadoPagoAsync(otro.IdCobro))["comision_real"]);
    }

    // Clausula: cada resultado se clasifica (conciliado, importe_distinto, sin_venta, sin_cobro_en_ways, devolucion, contracargo, retiro),
    // una fila ilegible queda invalida con su linea y su motivo sin rechazar el archivo, solo la fila conciliada toca el cobro (la primera
    // que lo concilia gana) y una devolucion se liga al cobro sin modificarlo.
    [Fact]
    public async Task CadaResultadoSeClasificaYSoloLaFilaConciliadaTocaElCobro()
    {
        var e = await PrepararAsync("mp-conc-resultados");
        var (conciliado, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde);
        var (distinto, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde + 1);
        var apartado = await CobroAprobadoAsync(e, e.Desde + 2);
        Assert.Equal(HttpStatusCode.OK, (await e.Cajero.PostAsync($"{Ruta}/{apartado.IdCobro}/apartar", null)).StatusCode);
        var datosConciliado = await ConcDatosDelCobroAsync(conciliado.IdCobro);
        var datosDistinto = await ConcDatosDelCobroAsync(distinto.IdCobro);
        var datosApartado = await ConcDatosDelCobroAsync(apartado.IdCobro);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin,
            e.Ctx.IdEmpresa,
            Reporte(
                FilaDelReporte("1001", datosConciliado.Referencia, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte),
                FilaDelReporte("1002", datosDistinto.Referencia, "SETTLEMENT", "99.00", "-5.00", "94.00", FechaDelReporte),
                FilaDelReporte("1003", datosApartado.Referencia, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte),
                FilaDelReporte("1004", "W0P0N0I0", "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte),
                FilaDelReporte("1005", datosConciliado.Referencia, "REFUND", "-100.00", null, null, FechaDelReporte.AddDays(1)),
                FilaDelReporte("1006", null, "CHARGEBACK", "-100.00", null, null, FechaDelReporte.AddDays(1)),
                FilaDelReporte("1007", datosConciliado.Referencia, "WITHDRAWAL", "-500.00", null, null, FechaDelReporte.AddDays(2)),
                "1008,,SETTLEMENT,cien,,,2026-10-31T15:00:00.000+00:00,,",
                FilaDelReporte("1009", datosConciliado.Referencia, "SETTLEMENT", "100.00", "-9.00", "91.00", FechaDelReporte.AddHours(1))));

        Assert.Equal(9, importada.Conciliacion.CantidadFilas);
        Assert.Equal(1, importada.Conciliacion.CantidadInvalidas);
        var invalida = Assert.Single(importada.Invalidas);
        Assert.Equal(9, invalida.NumeroLinea);
        Assert.Contains("TRANSACTION_AMOUNT", invalida.Motivo);

        var filas = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas;
        Assert.Equal(
            new[] { "conciliado", "importe_distinto", "sin_venta", "sin_cobro_en_ways", "devolucion", "contracargo", "retiro", "invalida", "conciliado" },
            filas.Select(f => f.Resultado));
        Assert.Equal(conciliado.IdCobro, filas[0].IdCobroMercadoPago);
        Assert.Equal(distinto.IdCobro, filas[1].IdCobroMercadoPago);
        Assert.Equal(apartado.IdCobro, filas[2].IdCobroMercadoPago);
        Assert.Null(filas[3].IdCobroMercadoPago);
        Assert.Equal(conciliado.IdCobro, filas[4].IdCobroMercadoPago);
        Assert.Null(filas[5].IdCobroMercadoPago);
        Assert.Null(filas[6].IdCobroMercadoPago);
        Assert.Null(filas[7].SourceId);
        Assert.Contains("TRANSACTION_AMOUNT", filas[7].Motivo);

        var primero = await ConcCobroMercadoPagoAsync(conciliado.IdCobro);
        Assert.Equal(-5.00m, primero["comision_real"]);
        Assert.Equal(filas[0].Id, primero["id_fila_conciliacion"]);
        Assert.Null((await ConcCobroMercadoPagoAsync(distinto.IdCobro))["id_fila_conciliacion"]);
        Assert.Null((await ConcCobroMercadoPagoAsync(apartado.IdCobro))["id_fila_conciliacion"]);

        var porResultado = importada.Conciliacion.Resumen.ToDictionary(r => r.Resultado);
        Assert.Equal(2, porResultado["conciliado"].Cantidad);
        Assert.Equal(1, porResultado["invalida"].Cantidad);

        var soloConciliadas = await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id, "?resultado=conciliado");
        Assert.Equal(2, soloConciliadas.Total);
        var segunda = await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id, "?pagina=2&tamano=4");
        Assert.Equal(new[] { 6, 7, 8, 9 }, segunda.Filas.Select(f => f.NumeroLinea));
        Assert.Equal(9, segunda.Total);
    }

    // Clausula: el cotejo es por empresa: un cobro de otra empresa del mismo tenant no se concilia (sin_cobro_en_ways) y el mismo archivo
    // en la otra empresa si lo concilia; cada empresa lista solo sus conciliaciones; otro tenant no ve nada (404).
    [Fact]
    public async Task ElCotejoYLosListadosSonDeLaEmpresaYUnTenantAjenoNoVeNada()
    {
        var e = await PrepararAsync("mp-conc-empresa");
        var (cobro, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde);
        var datos = await ConcDatosDelCobroAsync(cobro.IdCobro);
        var (idOtraEmpresa, _) = await _apoyo.CrearOtraEmpresaConCuentaAsync(e.Ctx, "Otra empresa conciliada");
        await EjecutarAsync("UPDATE cobros_mercadopago SET id_empresa = $1 WHERE id_cobro_mercadopago = $2", idOtraEmpresa, cobro.IdCobro);
        var csv = Reporte(FilaDelReporte("1", datos.Referencia, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte));

        var enLaPropia = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv);
        var enLaOtra = await ConcSubirOkAsync(e.Ctx.Admin, idOtraEmpresa, csv);

        Assert.Equal("sin_cobro_en_ways", Assert.Single(enLaPropia.Conciliacion.Resumen).Resultado);
        Assert.Equal("conciliado", Assert.Single(enLaOtra.Conciliacion.Resumen).Resultado);
        Assert.Equal(enLaOtra.Conciliacion.Id, Assert.Single(await LeerAsync<List<ConciliacionListado>>(
            await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}?idEmpresa={idOtraEmpresa}"))).Id);
        Assert.Equal(enLaPropia.Conciliacion.Id, Assert.Single(await LeerAsync<List<ConciliacionListado>>(
            await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}"))).Id);

        var ajeno = await _apoyo.PrepararAsync("mp-conc-ajeno");
        Assert.Equal(HttpStatusCode.NotFound, (await ajeno.Admin.GetAsync($"{RutaConciliaciones}/{enLaPropia.Conciliacion.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ajeno.Admin.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ConcSubirAsync(ajeno.Admin, e.Ctx.IdEmpresa, csv + " ")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ConcConfirmarAsync(ajeno.Admin, enLaPropia.Conciliacion.Id, 1, 1)).StatusCode);
    }

    // ---- Candidatos de una aprobacion manual -----------------------------------------------------------------------------------------

    // Clausula: una fila sin cobro en Ways sugiere los pagos sin verificar del mismo importe, a no mas de un dia de la venta (24 horas,
    // extremos incluidos) y, si trae EXTERNAL_POS_ID, de la misma caja; nunca se aplican solos: el pago sigue sin verificar.
    [Fact]
    public async Task UnaFilaSinCobroSugiereLosPagosSinVerificarCercanosYNuncaLosAplicaSola()
    {
        var e = await PrepararAsync("mp-conc-candidatos");
        var (idVenta, idPago) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        var caja = (string)(await ConsultarAsync("SELECT external_id FROM cajas_mercadopago WHERE id_punto_venta = $1", e.IdPuntoVenta)).Single()["external_id"]!;

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin,
            e.Ctx.IdEmpresa,
            Reporte(
                FilaDelReporte("2001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte.AddHours(23)),
                FilaDelReporte("2002", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte.AddHours(25)),
                FilaDelReporte("2003", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte, pos: "OTRA-CAJA"),
                FilaDelReporte("2004", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte, pos: caja),
                FilaDelReporte("2005", null, "SETTLEMENT", "100.01", "-5.00", "95.01", FechaDelReporte),
                FilaDelReporte("2006", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte.AddHours(-24)),
                FilaDelReporte("2007", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte.AddHours(-24).AddSeconds(-1))));

        var filas = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas;
        Assert.All(filas, f => Assert.Equal("sin_cobro_en_ways", f.Resultado));
        Assert.Equal(new[] { true, false, false, true, false, true, false }, filas.Select(f => f.Candidatos.Count == 1));
        var candidato = filas[0].Candidatos.Single();
        Assert.Equal(idPago, candidato.IdCobro);
        Assert.Equal(idVenta, candidato.IdComprobanteVenta);
        Assert.Equal(100m, candidato.Importe);
        Assert.Equal($"OP-{e.Desde}", candidato.Referencia);
        Assert.Equal("sin_verificar", await ConcVerificacionDelPagoAsync(idPago));
        Assert.All(filas, f => Assert.Null(f.IdCobro));
    }

    // Clausula: una venta anulada no es candidata.
    [Fact]
    public async Task UnaVentaAnuladaNoEsCandidata()
    {
        var e = await PrepararAsync("mp-conc-candidata-anulada");
        var (idVenta, _) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        await EjecutarAsync("UPDATE comprobantes_venta SET estado = 'anulado'::estado_comprobante WHERE id_comprobante_venta = $1", idVenta);

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(FilaDelReporte("3001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));

        Assert.Empty(Assert.Single((await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas).Candidatos);
    }

    // ---- Confirmar --------------------------------------------------------------------------------------------------------------------

    // Clausula: el Admin confirma una fila sin cobro contra un pago sin verificar: el pago pasa a verificado_manualmente (y deja de figurar
    // en pagos-sin-verificar), la fila lo recuerda, la accion queda auditada y la fila ya no ofrece candidatos.
    [Fact]
    public async Task ConfirmarVerificaElPagoUneLaFilaYQuedaAuditado()
    {
        var e = await PrepararAsync("mp-conc-confirmar");
        var (idVenta, idPago) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(FilaDelReporte("4001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));
        var idFila = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas.Single().Id;

        var respuesta = await ConcConfirmarAsync(e.Ctx.Admin, importada.Conciliacion.Id, idFila, idPago);

        var confirmada = await LeerAsync<FilaDeConciliacion>(respuesta);
        Assert.Equal(idPago, confirmada.IdCobro);
        Assert.Equal("verificado_manualmente", await ConcVerificacionDelPagoAsync(idPago));
        var despues = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas.Single();
        Assert.Equal(idPago, despues.IdCobro);
        Assert.Empty(despues.Candidatos);
        Assert.Equal(1, await ConcAuditoriasAsync("conciliacion.confirmacion", importada.Conciliacion.Id));
        Assert.Empty((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa)).Items);
        Assert.Equal(idVenta, (int)(await ConsultarAsync("SELECT id_comprobante_venta FROM cobros WHERE id_cobro = $1", idPago)).Single()["id_comprobante_venta"]!);
    }

    // Clausula: confirmar es solo de Admin: Supervisor y Vendedor reciben 403 y nada cambia.
    [Fact]
    public async Task ConfirmarYSubirSonSoloDelAdmin()
    {
        var e = await PrepararAsync("mp-conc-roles");
        var (_, idPago) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        var csv = Reporte(FilaDelReporte("5001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte));
        var importada = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv);
        var idFila = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas.Single().Id;

        foreach (var cliente in new[] { e.Ctx.Supervisor, e.Ctx.Vendedor, e.Cajero })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await ConcConfirmarAsync(cliente, importada.Conciliacion.Id, idFila, idPago)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ConcSubirAsync(cliente, e.Ctx.IdEmpresa, csv + "\n")).StatusCode);
        }

        Assert.Equal("sin_verificar", await ConcVerificacionDelPagoAsync(idPago));
        Assert.Single(await LeerAsync<List<ConciliacionListado>>(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}")));
    }

    // Clausula: 404 antes que cualquier otra validacion (conciliacion, fila de esa conciliacion, pago de la empresa) y despues los 409:
    // fila ya confirmada, fila que no es sin_cobro_en_ways, pago ya verificado, pago que no esta sin verificar.
    [Fact]
    public async Task ConfirmarResponde404PrimeroYLuego409PorCadaLado()
    {
        var e = await PrepararAsync("mp-conc-confirmar-guardas");
        var (_, pagoUno) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        var (_, pagoDos) = await ConcVentaManualAsync(e, e.Desde + 1, FechaDelReporte);
        var idVentaEfectivo = await VentaSembradaAsync(e, e.Desde + 2);
        var pagoEfectivo = (int)(await ConsultarAsync("SELECT id_cobro FROM cobros WHERE id_comprobante_venta = $1", idVentaEfectivo)).Single()["id_cobro"]!;
        var (cobro, _) = await ConcVentaConMercadoPagoAsync(e, e.Desde + 3);
        var datos = await ConcDatosDelCobroAsync(cobro.IdCobro);
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin,
            e.Ctx.IdEmpresa,
            Reporte(
                FilaDelReporte("6001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte),
                FilaDelReporte("6002", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte),
                FilaDelReporte("6003", datos.Referencia, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));
        var id = importada.Conciliacion.Id;
        var filas = (await ConcDetalleAsync(e.Ctx.Admin, id)).Filas;

        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id + 1_000_000, filas[0].Id, pagoUno), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id, filas[0].Id + 1_000_000, 0), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id, filas[0].Id, pagoUno + 1_000_000), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id, filas[0].Id, 0), HttpStatusCode.BadRequest, "conciliacion_pago_invalido");

        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id, filas[2].Id, pagoUno), HttpStatusCode.Conflict, "conciliacion_fila_no_confirmable");
        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id, filas[0].Id, pagoEfectivo), HttpStatusCode.Conflict, "conciliacion_pago_no_esta_sin_verificar");

        Assert.Equal(HttpStatusCode.OK, (await ConcConfirmarAsync(e.Ctx.Admin, id, filas[0].Id, pagoUno)).StatusCode);
        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id, filas[0].Id, pagoDos), HttpStatusCode.Conflict, "conciliacion_fila_ya_confirmada");
        await AssertCodigoAsync(await ConcConfirmarAsync(e.Ctx.Admin, id, filas[1].Id, pagoUno), HttpStatusCode.Conflict, "conciliacion_pago_ya_verificado");

        Assert.Equal("sin_verificar", await ConcVerificacionDelPagoAsync(pagoDos));
        Assert.Null((await ConsultarAsync("SELECT id_cobro FROM filas_conciliacion_mercadopago WHERE id_fila_conciliacion_mercadopago = $1", filas[1].Id)).Single()["id_cobro"]);
    }

    // Clausula: un pago de otra empresa del mismo tenant no se confirma contra una fila de esta (404).
    [Fact]
    public async Task ElPagoDeOtraEmpresaNoSeConfirmaContraLaFila()
    {
        var e = await PrepararAsync("mp-conc-confirmar-empresa");
        var (idVenta, idPago) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        var (idOtraEmpresa, _) = await _apoyo.CrearOtraEmpresaConCuentaAsync(e.Ctx, "Otra empresa del pago");
        var otroPv = await e.Ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(idOtraEmpresa, "Caja de la otra empresa", ModoPuntoVenta.Escritorio));
        var idOtroPv = (await LeerAsync<PuntoVentaListado>(otroPv, HttpStatusCode.Created)).Id;
        await EjecutarAsync("UPDATE comprobantes_venta SET id_punto_venta = $1 WHERE id_comprobante_venta = $2", idOtroPv, idVenta);
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(FilaDelReporte("7001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));
        var idFila = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas.Single().Id;

        var respuesta = await ConcConfirmarAsync(e.Ctx.Admin, importada.Conciliacion.Id, idFila, idPago);

        await AssertCodigoAsync(respuesta, HttpStatusCode.NotFound, "no_encontrado");
        Assert.Equal("sin_verificar", await ConcVerificacionDelPagoAsync(idPago));
    }

    // Clausula: dos confirmaciones de la misma fila contra pagos distintos a la vez: gana una (200) y la otra recibe 409; la fila queda
    // con el pago del ganador y el perdedor sigue sin verificar (el lock de la fila serializa; sin el, las dos escribirian y la ultima pisaria).
    [Fact]
    public async Task DosConfirmacionesDeLaMismaFilaSoloUnaGana()
    {
        var e = await PrepararAsync("mp-conc-carrera-fila");
        var (_, pagoUno) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        var (_, pagoDos) = await ConcVentaManualAsync(e, e.Desde + 1, FechaDelReporte);
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(FilaDelReporte("8001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));
        var idFila = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas.Single().Id;

        var respuestas = await Task.WhenAll(
            ConcConfirmarAsync(e.Ctx.Admin, importada.Conciliacion.Id, idFila, pagoUno),
            ConcConfirmarAsync(e.Ctx.Admin, importada.Conciliacion.Id, idFila, pagoDos));

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, respuestas.Select(r => r.StatusCode).OrderBy(s => (int)s));
        var ganador = (int)(await ConsultarAsync(
            "SELECT id_cobro FROM filas_conciliacion_mercadopago WHERE id_fila_conciliacion_mercadopago = $1", idFila)).Single()["id_cobro"]!;
        var perdedor = ganador == pagoUno ? pagoDos : pagoUno;
        Assert.Equal("verificado_manualmente", await ConcVerificacionDelPagoAsync(ganador));
        Assert.Equal("sin_verificar", await ConcVerificacionDelPagoAsync(perdedor));
    }

    // Clausula: ux_filas_conciliacion_mercadopago_cobro: un pago no se confirma contra dos filas (23505 -> 409 pago_ya_verificado); una
    // escritura cruda que se saltara los locks lo respeta igual.
    [Fact]
    public async Task LaBaseRechazaUnPagoConfirmadoContraDosFilas()
    {
        var e = await PrepararAsync("mp-conc-unico-pago");
        var (_, idPago) = await ConcVentaManualAsync(e, e.Desde, FechaDelReporte);
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin,
            e.Ctx.IdEmpresa,
            Reporte(
                FilaDelReporte("9001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte),
                FilaDelReporte("9002", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));
        var filas = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas;
        await EjecutarAsync("UPDATE filas_conciliacion_mercadopago SET id_cobro = $1 WHERE id_fila_conciliacion_mercadopago = $2", idPago, filas[0].Id);

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE filas_conciliacion_mercadopago SET id_cobro = $1 WHERE id_fila_conciliacion_mercadopago = $2", idPago, filas[1].Id));

        Assert.Equal("23505", rechazo.SqlState);
        Assert.Equal("ux_filas_conciliacion_mercadopago_cobro", rechazo.ConstraintName);
        var (estado, codigo) = await ManejarAsync(rechazo);
        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("conciliacion_pago_ya_verificado", codigo);
    }

    // ---- El mismo archivo dos veces ---------------------------------------------------------------------------------------------------

    // Clausula: el mismo archivo (mismos bytes) dos veces en la misma empresa es 409 conciliacion_ya_importada y no escribe nada; en otra
    // empresa se acepta; un archivo con un byte distinto tambien.
    [Fact]
    public async Task ElMismoArchivoEnLaMismaEmpresaEs409YEnOtraSeAcepta()
    {
        var e = await PrepararAsync("mp-conc-duplicado");
        var (idOtraEmpresa, _) = await _apoyo.CrearOtraEmpresaConCuentaAsync(e.Ctx, "Otra empresa duplicada");
        var csv = Reporte(FilaDelReporte("10001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte));
        var primera = await ConcSubirOkAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv);

        await AssertCodigoAsync(await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv, "otro-nombre.csv"), HttpStatusCode.Conflict, "conciliacion_ya_importada");
        Assert.Equal(HttpStatusCode.OK, (await ConcSubirAsync(e.Ctx.Admin, idOtraEmpresa, csv)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv + " ")).StatusCode);

        var lista = await LeerAsync<List<ConciliacionListado>>(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}"));
        Assert.Equal(2, lista.Count);
        Assert.Contains(lista, c => c.Id == primera.Conciliacion.Id && c.NombreArchivo == "reporte.csv");
    }

    // Clausula: dos subidas simultaneas del mismo archivo: una gana (200) y la otra es 409; queda una sola conciliacion.
    [Fact]
    public async Task DosSubidasSimultaneasDelMismoArchivoSoloUnaGana()
    {
        var e = await PrepararAsync("mp-conc-carrera-archivo");
        var csv = Reporte(FilaDelReporte("11001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte));

        var respuestas = await Task.WhenAll(
            ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv),
            ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv),
            ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, csv));

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.Conflict },
            respuestas.Select(r => r.StatusCode).OrderBy(s => (int)s));
        Assert.Equal(
            1L,
            (await ConsultarAsync("SELECT count(*) AS n FROM conciliaciones_mercadopago WHERE id_empresa = $1", e.Ctx.IdEmpresa)).Single()["n"]);
    }

    // Clausula: ux_conciliaciones_mercadopago_archivo: la base rechaza la misma huella en la misma empresa (23505) y la traduccion es 409
    // conciliacion_ya_importada, aun si una escritura se saltara el lock y la pre-lectura.
    [Fact]
    public async Task LaBaseRechazaLaMismaHuellaEnLaMismaEmpresa()
    {
        var e = await PrepararAsync("mp-conc-unico-archivo");
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(FilaDelReporte("12001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));
        var huella = (string)(await ConsultarAsync(
            "SELECT hash_archivo FROM conciliaciones_mercadopago WHERE id_conciliacion_mercadopago = $1", importada.Conciliacion.Id)).Single()["hash_archivo"]!;

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync(
                "INSERT INTO conciliaciones_mercadopago (id_tenant, id_empresa, hash_archivo, id_usuario, cantidad_filas, cantidad_invalidas, created_at, updated_at) " +
                "VALUES ($1, $2, $3, $4, 0, 0, now(), now())",
                e.IdTenant, e.Ctx.IdEmpresa, huella, e.Ctx.IdUsuarioAdmin));

        Assert.Equal("23505", rechazo.SqlState);
        Assert.Equal("ux_conciliaciones_mercadopago_archivo", rechazo.ConstraintName);
        var (estado, codigo) = await ManejarAsync(rechazo);
        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("conciliacion_ya_importada", codigo);
    }

    // Clausula: ck_cobros_mercadopago_conciliacion: la comision, el neto o la liberacion reales no existen sin la fila que los trajo (23514).
    [Fact]
    public async Task LaBaseRechazaUnaComisionRealSinSuFila()
    {
        var e = await PrepararAsync("mp-conc-ck-comision");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE cobros_mercadopago SET comision_real = 5 WHERE id_cobro_mercadopago = $1", cobro.IdCobro));

        Assert.Equal("23514", rechazo.SqlState);
        Assert.Equal("ck_cobros_mercadopago_conciliacion", rechazo.ConstraintName);
    }

    // ---- El archivo -------------------------------------------------------------------------------------------------------------------

    // Clausula: un reporte ilegible se rechaza entero (400 conciliacion_reporte_invalido) sin escribir nada: encabezado sin una columna
    // obligatoria, sin ninguna fila valida, vacio; sin el campo archivo o sin multipart es 400 conciliacion_archivo_requerido; y la
    // empresa inexistente es 404 antes que todo eso.
    [Fact]
    public async Task UnArchivoIlegibleSeRechazaEnteroSinEscribirNada()
    {
        var e = await PrepararAsync("mp-conc-archivo-invalido");

        await AssertCodigoAsync(
            await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, "SOURCE_ID,TRANSACTION_TYPE,TRANSACTION_AMOUNT\n1,SETTLEMENT,1.00\n"),
            HttpStatusCode.BadRequest,
            "conciliacion_reporte_invalido");
        await AssertCodigoAsync(
            await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte("1,,SETTLEMENT,uno,,,2026-10-31T15:00:00.000+00:00,,")),
            HttpStatusCode.BadRequest,
            "conciliacion_reporte_invalido");
        await AssertCodigoAsync(await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, string.Empty), HttpStatusCode.BadRequest, "conciliacion_archivo_requerido");

        using (var sinCampo = new MultipartFormDataContent { { new StringContent("x"), "otro" } })
        {
            await AssertCodigoAsync(
                await e.Ctx.Admin.PostAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}", sinCampo),
                HttpStatusCode.BadRequest,
                "conciliacion_archivo_requerido");
        }

        await AssertCodigoAsync(
            await e.Ctx.Admin.PostAsJsonAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}", new { archivo = "x" }),
            HttpStatusCode.BadRequest,
            "conciliacion_archivo_requerido");

        await AssertCodigoAsync(
            await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa + 1_000_000, Reporte(FilaDelReporte("1", null, "SETTLEMENT", "1.00", null, null, FechaDelReporte))),
            HttpStatusCode.NotFound,
            "no_encontrado");
        Assert.Empty(await LeerAsync<List<ConciliacionListado>>(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa}")));
    }

    // Clausula: el archivo no pasa de 10 MB (413); uno de 10 MB justos llega al lector (y se rechaza por ilegible, no por tamaño).
    [Fact]
    public async Task UnArchivoDeMasDeDiezMegabytesEs413()
    {
        var e = await PrepararAsync("mp-conc-tamano");
        var tope = ServicioDeConciliacionMercadoPago.TamanoMaximoDelArchivo;

        var grande = new string('a', tope + 1);
        await AssertCodigoAsync(
            await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, grande), HttpStatusCode.RequestEntityTooLarge, "conciliacion_archivo_demasiado_grande");

        var justo = new string('a', tope);
        await AssertCodigoAsync(
            await ConcSubirAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, justo), HttpStatusCode.BadRequest, "conciliacion_reporte_invalido");
    }

    // Clausula: una fila ilegible entre filas validas no rechaza el archivo: se informa con su linea y su motivo y queda guardada como invalida.
    [Fact]
    public async Task UnaFilaIlegibleSeInformaYSeGuardaConSuMotivo()
    {
        var e = await PrepararAsync("mp-conc-fila-invalida");

        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin,
            e.Ctx.IdEmpresa,
            Reporte(
                FilaDelReporte("13001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte),
                "13002,,SETTLEMENT,100,00,,,2026-10-31T15:00:00.000+00:00,,",
                "13003,,COMPRA,100.00,,,2026-10-31T15:00:00.000+00:00,,"));

        Assert.Equal(new[] { 3, 4 }, importada.Invalidas.Select(i => i.NumeroLinea));
        Assert.False(importada.InvalidasTruncado);
        var filas = (await ConcDetalleAsync(e.Ctx.Admin, importada.Conciliacion.Id)).Filas;
        Assert.Equal(new[] { "sin_cobro_en_ways", "invalida", "invalida" }, filas.Select(f => f.Resultado));
        Assert.All(filas.Skip(1), f => Assert.False(string.IsNullOrEmpty(f.Motivo)));
    }

    // Clausula: filtros y paginacion del detalle: resultado fuera de catalogo, pagina 0 o tamano 0 o mayor a 500 son 400; la conciliacion
    // inexistente es 404 antes de validar.
    [Fact]
    public async Task ElDetalleValidaElFiltroYLaPaginacionDespuesDelNoEncontrado()
    {
        var e = await PrepararAsync("mp-conc-detalle");
        var importada = await ConcSubirOkAsync(
            e.Ctx.Admin, e.Ctx.IdEmpresa, Reporte(FilaDelReporte("14001", null, "SETTLEMENT", "100.00", "-5.00", "95.00", FechaDelReporte)));
        var id = importada.Conciliacion.Id;

        await AssertCodigoAsync(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}/{id}?resultado=nada"), HttpStatusCode.BadRequest, "conciliacion_resultado_invalido");
        await AssertCodigoAsync(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}/{id}?pagina=0"), HttpStatusCode.BadRequest, "conciliacion_paginacion_invalida");
        await AssertCodigoAsync(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}/{id}?tamano=0"), HttpStatusCode.BadRequest, "conciliacion_paginacion_invalida");
        await AssertCodigoAsync(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}/{id}?tamano=501"), HttpStatusCode.BadRequest, "conciliacion_paginacion_invalida");
        await AssertCodigoAsync(await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}/{id + 1_000_000}?resultado=nada"), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Equal(HttpStatusCode.OK, (await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}/{id}?tamano=500")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Ctx.Admin.GetAsync($"{RutaConciliaciones}?idEmpresa={e.Ctx.IdEmpresa + 1_000_000}")).StatusCode);
    }
}
