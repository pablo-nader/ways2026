using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Fiscal;
using Ways.Application.Ventas;
using Ways.Domain.Fiscal;
using Ways.Domain.Ventas;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T8c: anular una venta con la factura aprobada deja decidida su nota de crédito (NC) en la misma
/// transacción, la intenta emitir en línea y la informa en <c>fiscal.notaDeCredito</c>; con la factura rechazada liquida el cupo en
/// esa misma transacción; con la factura en trámite responde 409. Postgres real, espías de WSAA/WSFE y, para las ventas reales, el
/// endpoint del cajero de dispositivo con su ticket de acceso cacheado.
///
/// Mutation-proof-tests: las cláusulas bajo prueba son cada rama de la regla de tres estados (sin espejo, rechazado, aprobado y en
/// trámite), el <c>FOR UPDATE</c> sobre la decisión de la factura, la llamada que liquida la factura rechazada, que la anulación
/// nunca toque <c>numeraciones_fiscales</c>, la condición de ticket cacheado del intento en línea y los estados de la nota en las
/// dos lecturas (por id y por turno).
/// </summary>
public partial class FacturacionDeVentasEmisionTests
{
    private static async Task<(HttpResponseMessage Respuesta, string Cuerpo)> AnularAsync(HttpClient cliente, int idVenta)
    {
        var respuesta = await cliente.PostAsync($"/api/ventas/{idVenta}/anulacion", null);
        return (respuesta, await respuesta.Content.ReadAsStringAsync());
    }

    private static async Task<ComprobanteEmitido> AnularConExitoAsync(HttpClient cliente, int idVenta)
    {
        var (respuesta, cuerpo) = await AnularAsync(cliente, idVenta);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Descarta el ticket de acceso cacheado: uno ya vencido ocupa su lugar, así que el intento en línea no corre.</summary>
    private static Task QuitarElTicketAsync(Ctx c) =>
        c.Fabrica.Services.GetRequiredService<IRepositorioDeTicketDeAcceso>().GuardarAsync(
            new ClaveDeTicket(c.IdEmpresa, AmbienteFiscal.Homologacion, "wsfe"),
            new TicketDeAcceso("VENCIDO", "VENCIDO", c.Reloj.Ahora.AddHours(-1)), CancellationToken.None);

    private sealed record FilaDeDecisionDeNota(
        int Id, string Estado, int IdEmpresa, string Motivo, string Aportes, DateOnly Periodo, int Intentos, string? UltimoError,
        DateTimeOffset? ProximoIntento, int? IdComprobanteFiscal, int IdOrigen);

    private Task<List<FilaDeDecisionDeNota>> LeerDecisionesDeNotaAsync(Ctx c, int idVenta) => ConsultarAsync(
        c,
        "SELECT id_facturacion_venta, estado::text, id_empresa, motivo::text, aportes::text, periodo, intentos, ultimo_error, " +
        "proximo_intento_en, id_comprobante_fiscal, id_facturacion_origen FROM facturaciones_de_venta " +
        "WHERE id_comprobante_venta = $1 AND id_facturacion_origen IS NOT NULL",
        l => new FilaDeDecisionDeNota(
            l.GetInt32(0), l.GetString(1), l.GetInt32(2), l.GetString(3), l.GetString(4), l.GetFieldValue<DateOnly>(5),
            l.GetInt32(6), l.IsDBNull(7) ? null : l.GetString(7), l.IsDBNull(8) ? null : l.GetFieldValue<DateTimeOffset>(8),
            l.IsDBNull(9) ? null : l.GetInt32(9), l.GetInt32(10)),
        idVenta);

    private static async Task<JsonElement> LeerVentaCrudaAsync(HttpClient cliente, int idVenta)
    {
        var respuesta = await cliente.GetAsync($"/api/ventas/{idVenta}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonDocument.Parse(cuerpo).RootElement.Clone();
    }

    private static async Task<string?> LeerEstadoDeNotaPorTurnoAsync(HttpClient cliente, int idTurno, int idVenta)
    {
        var respuesta = await cliente.GetAsync($"/api/ventas/por-turno/{idTurno}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var fila = JsonDocument.Parse(cuerpo).RootElement.EnumerateArray().Single(v => v.GetProperty("id").GetInt32() == idVenta);
        return fila.TryGetProperty("estadoNotaDeCredito", out var estado) && estado.ValueKind == JsonValueKind.String
            ? estado.GetString()
            : null;
    }

    /// <summary>Una venta real del cajero con la factura aprobada en línea (ticket cacheado) y el espía listo para la nota.</summary>
    private async Task<(Cobro T, ComprobanteEmitido Vendida)> VenderFacturadaAsync(string nombre)
    {
        var t = await PrepararCobroAsync(nombre);
        var vendida = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        Assert.True(vendida.Fiscal!.Estado == EstadosFiscalesDeVenta.Aprobado, $"{vendida.Fiscal.Estado}: {vendida.Fiscal.Error}");
        t.C.Wsfe.Reiniciar();
        return (t, vendida);
    }

    // ===================================================================================================
    // Factura aprobada: la anulación decide la nota y la emite en línea
    // ===================================================================================================

    [Fact]
    public async Task AnularUnaVentaConLaFacturaAprobadaEmiteEnLineaLaNotaConLaFacturaAsociadaYLaInformaEnLaRespuesta()
    {
        var (t, vendida) = await VenderFacturadaAsync("anulncok");
        var factura = vendida.Fiscal!.Comprobante!;
        var decisionDeLaFactura = await LeerDecisionAsync(t.C, vendida.Fiscal.IdFacturacion!.Value);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado("70222222222222"));

        var anulado = await AnularConExitoAsync(t.Cajero, vendida.Id);

        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, anulado.Fiscal!.Estado);
        Assert.Equal(factura.IdComprobante, anulado.Fiscal.Comprobante!.IdComprobante);
        var nota = anulado.Fiscal.NotaDeCredito!;
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, nota.Estado);
        Assert.Null(nota.Error);
        Assert.Equal($"NC{factura.Letra}", nota.Comprobante!.CodigoTipoComprobante);
        Assert.Equal(factura.Letra, nota.Comprobante.Letra);
        Assert.Equal(1, nota.Comprobante.Numero);
        Assert.Equal("70222222222222", nota.Comprobante.Cae);

        // Lo que viajó a ARCA: la nota de la letra de la factura, con la factura en CbtesAsoc.
        Assert.Equal(["FECAESolicitar"], t.C.Wsfe.Operaciones);
        var cuerpo = Assert.Single(t.C.Wsfe.CuerposDeSolicitud);
        Assert.Equal(factura.Letra == 'A' ? "3" : "8", CampoDelCuerpo(cuerpo, "CbteTipo"));
        Assert.Equal(factura.Numero.ToString(), ComprobanteAsociadoDelPedido(cuerpo)["Nro"]);

        // La decisión de la nota copia empresa, motivo y período de la de la factura, sin aportes, y quedó emitida.
        var decision = Assert.Single(await LeerDecisionesDeNotaAsync(t.C, vendida.Id));
        Assert.Equal(nota.IdFacturacion, decision.Id);
        Assert.Equal(("emitida", t.C.IdEmpresa, "[]"), (decision.Estado, decision.IdEmpresa, decision.Aportes));
        Assert.Equal(vendida.Fiscal.IdFacturacion, decision.IdOrigen);
        Assert.Equal(nota.Comprobante.IdComprobante, decision.IdComprobanteFiscal);
        var periodoDeLaFactura = await ScalarOwnerAsync<DateOnly>(
            "SELECT periodo FROM facturaciones_de_venta WHERE id_facturacion_venta = $1", vendida.Fiscal.IdFacturacion);
        Assert.Equal(periodoDeLaFactura, decision.Periodo);
        Assert.Equal("emitida", (await LeerDecisionAsync(t.C, vendida.Fiscal.IdFacturacion!.Value)).Estado);
        Assert.Equal(decisionDeLaFactura.IdComprobanteFiscal, factura.IdComprobante);
    }

    [Fact]
    public async Task SinTicketCacheadoLaAnulacionDejaLaNotaPendienteSinPedirNadaNiTomarNumeroYElLoteLaEmiteDespues()
    {
        var (t, vendida) = await VenderFacturadaAsync("anulncsinticket");
        var series = await LeerSeriesAsync(t.C);
        await QuitarElTicketAsync(t.C);

        var anulado = await AnularConExitoAsync(t.Cajero, vendida.Id);

        var nota = anulado.Fiscal!.NotaDeCredito!;
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, nota.Estado);
        Assert.Null(nota.Error);
        Assert.Null(nota.Comprobante);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, anulado.Fiscal.Estado);
        Assert.Empty(t.C.Wsfe.Operaciones);

        // La decisión nace pendiente, sin aportes, sin intentos y sin espera; no hay espejo ni número tomado.
        var decision = Assert.Single(await LeerDecisionesDeNotaAsync(t.C, vendida.Id));
        Assert.Equal(("pendiente", "[]", 0, null, null, null), (decision.Estado, decision.Aportes, decision.Intentos,
            decision.UltimoError, decision.ProximoIntento, decision.IdComprobanteFiscal));
        Assert.Equal(vendida.Fiscal!.IdFacturacion, decision.IdOrigen);
        Assert.Equal(series, await LeerSeriesAsync(t.C));
        Assert.Single(await LeerEspejosAsync(t.C));

        // Las dos lecturas la informan pendiente, con la forma JSON del contrato.
        var cruda = await LeerVentaCrudaAsync(t.Cajero, vendida.Id);
        var jsonNota = cruda.GetProperty("fiscal").GetProperty("notaDeCredito");
        Assert.Equal("pendiente", jsonNota.GetProperty("estado").GetString());
        Assert.Equal(nota.IdFacturacion, jsonNota.GetProperty("idFacturacion").GetInt32());
        Assert.Equal(JsonValueKind.Null, jsonNota.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, jsonNota.GetProperty("comprobante").ValueKind);
        Assert.Equal("aprobado", cruda.GetProperty("fiscal").GetProperty("estado").GetString());
        Assert.Equal("pendiente", await LeerEstadoDeNotaPorTurnoAsync(t.Cajero, t.C.IdTurno, vendida.Id));

        await GuardarTicketVigenteAsync(t.C);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado("70333333333333"));
        var lote = await ProcesarLoteAsync(t.C.Admin, t.C.IdPuntoVenta);

        Assert.Equal(1, lote.Emitidas);
        var aprobada = (await LeerVentaCrudaAsync(t.Cajero, vendida.Id)).GetProperty("fiscal").GetProperty("notaDeCredito");
        Assert.Equal("aprobado", aprobada.GetProperty("estado").GetString());
        Assert.Equal($"NC{vendida.Fiscal!.Comprobante!.Letra}", aprobada.GetProperty("comprobante").GetProperty("codigoTipoComprobante").GetString());
        Assert.Equal("aprobado", await LeerEstadoDeNotaPorTurnoAsync(t.Cajero, t.C.IdTurno, vendida.Id));
    }

    [Fact]
    public async Task UnaNotaRechazadaPorArcaSeInformaRechazadaConSuCodigoEnLasDosLecturas()
    {
        var (t, vendida) = await VenderFacturadaAsync("anulncrech");
        t.C.Wsfe.Solicitar = Responde(FecaeRechazado());

        var anulado = await AnularConExitoAsync(t.Cajero, vendida.Id);

        var nota = anulado.Fiscal!.NotaDeCredito!;
        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, nota.Estado);
        Assert.Equal("10015", nota.Error);
        Assert.Null(nota.Comprobante);
        var leida = (await LeerVentaCrudaAsync(t.Cajero, vendida.Id)).GetProperty("fiscal").GetProperty("notaDeCredito");
        Assert.Equal("rechazado", leida.GetProperty("estado").GetString());
        Assert.Equal("10015", leida.GetProperty("error").GetString());
        Assert.Equal("rechazado", await LeerEstadoDeNotaPorTurnoAsync(t.Cajero, t.C.IdTurno, vendida.Id));
    }

    [Fact]
    public async Task UnaVentaNoAnuladaNoInformaNotaDeCreditoNiEnLaLecturaNiEnElListadoDelTurno()
    {
        var (t, vendida) = await VenderFacturadaAsync("anulncnula");

        var cruda = await LeerVentaCrudaAsync(t.Cajero, vendida.Id);

        Assert.Equal(JsonValueKind.Null, cruda.GetProperty("fiscal").GetProperty("notaDeCredito").ValueKind);
        Assert.Null(await LeerEstadoDeNotaPorTurnoAsync(t.Cajero, t.C.IdTurno, vendida.Id));
    }

    // ===================================================================================================
    // Factura en trámite, rechazada, sin decisión, y carreras
    // ===================================================================================================

    [Fact]
    public async Task AnularConLaFacturaRechazadaLiquidaElCupoEnLaMismaTransaccionSinCrearNota()
    {
        var c = await PrepararAsync("anulrech");
        var venta = await SembrarFacturaRechazadaAsync(c);

        var anulado = await AnularConExitoAsync(c.Admin, venta.IdVenta);

        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, anulado.Fiscal!.Estado);
        Assert.Null(anulado.Fiscal.NotaDeCredito);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal("cancelada", decision.Estado);
        Assert.Contains("10015", decision.UltimoError);
        Assert.Equal(AcumuladosAntesDeDecidir(c, venta.Periodo), await LeerAcumuladosAsync(c));
        Assert.Empty(await LeerDecisionesDeNotaAsync(c, venta.IdVenta));

        // Idempotente: procesar la decisión de nuevo no revierte dos veces.
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, (await ProcesarAsync(c.Admin, venta.IdFacturacion)).Estado);
        Assert.Equal(AcumuladosAntesDeDecidir(c, venta.Periodo), await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task UnaVentaSinDecisionSeAnulaSinEfectoFiscal()
    {
        var t = await PrepararCobroAsync("anulsindec");
        var vendida = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, forzar: false));
        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, vendida.Fiscal!.Estado);

        var anulado = await AnularConExitoAsync(t.Cajero, vendida.Id);

        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);
        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, anulado.Fiscal!.Estado);
        Assert.Null(anulado.Fiscal.NotaDeCredito);
        Assert.Empty(await LeerDecisionesDeNotaAsync(t.C, vendida.Id));
        Assert.Empty(t.C.Wsfe.Operaciones);
    }

    [Fact]
    public async Task UnaDecisionPendienteSinEspejoSeAnulaSinCrearNotaYSigueLaCancelacionDiferida()
    {
        var c = await PrepararAsync("anulpend");
        var venta = await SembrarVentaDecididaAsync(c);

        var anulado = await AnularConExitoAsync(c.Admin, venta.IdVenta);

        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, anulado.Fiscal!.Estado);
        Assert.Null(anulado.Fiscal.NotaDeCredito);
        Assert.Equal("pendiente", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Empty(await LeerDecisionesDeNotaAsync(c, venta.IdVenta));
        Assert.Empty(await LeerEspejosAsync(c));
    }

    [Fact]
    public async Task DosAnulacionesConcurrentesDeUnaVentaFacturadaDejanUnaSolaNotaYUnSoloExito()
    {
        var c = await PrepararAsync("anulcarrera");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        await QuitarElTicketAsync(c);

        var respuestas = await Task.WhenAll(AnularAsync(c.Admin, venta.IdVenta), AnularAsync(c.Admin, venta.IdVenta));

        var estados = respuestas.Select(r => r.Respuesta.StatusCode).Order().ToList();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], estados);
        var perdedora = respuestas.Single(r => r.Respuesta.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("comprobante_ya_anulado", JsonDocument.Parse(perdedora.Cuerpo).RootElement.GetProperty("codigo").GetString());
        Assert.Single(await LeerDecisionesDeNotaAsync(c, venta.IdVenta));
    }

    /// <summary>Cláusula bajo prueba: el <c>FOR UPDATE</c> sobre la decisión de la factura. Con la fila de la decisión bloqueada por
    /// otra sesión, la anulación espera ese lock; al soltarlo termina y crea la nota.</summary>
    [Fact]
    public async Task LaAnulacionBloqueaLaDecisionDeLaFacturaAntesDeInsertarLaNota()
    {
        var c = await PrepararAsync("anullockdec");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        await QuitarElTicketAsync(c);

        await using var sesion = await AbrirOwnerAsync();
        await using var transaccion = await sesion.BeginTransactionAsync();
        await using (var bloqueo = sesion.CreateCommand())
        {
            bloqueo.Transaction = transaccion;
            bloqueo.CommandText = "SELECT 1 FROM facturaciones_de_venta WHERE id_facturacion_venta = $1 FOR UPDATE";
            bloqueo.Parameters.Add(new NpgsqlParameter { Value = venta.IdFacturacion });
            await bloqueo.ExecuteScalarAsync();
        }

        var anulacion = AnularAsync(c.Admin, venta.IdVenta);
        await EsperarAsync(
            () => HayEsperaDeLockAsync("SELECT id_facturacion_venta, id_empresa"), "la anulación esperando la decisión de la factura");
        Assert.False(anulacion.IsCompleted);

        await transaccion.CommitAsync();
        var (respuesta, cuerpo) = await ConTopeAsync(anulacion);

        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        Assert.Single(await LeerDecisionesDeNotaAsync(c, venta.IdVenta));
    }

    /// <summary>Cláusula bajo prueba: la anulación nunca toca <c>numeraciones_fiscales</c>. El paso A del proceso toma esa serie antes
    /// que la venta; si la anulación la pidiera después de bloquear la venta, se cruzaría con él. Con todas las series bloqueadas
    /// por otra sesión la anulación igual termina.</summary>
    [Fact]
    public async Task LaAnulacionTerminaConTodasLasSeriesFiscalesBloqueadasPorqueNuncaLasToca()
    {
        var c = await PrepararAsync("anulseries");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        await ProcesarAsync(c.Admin, venta.IdFacturacion);
        await QuitarElTicketAsync(c);
        Assert.NotEmpty(await LeerSeriesAsync(c));

        await using var sesion = await AbrirOwnerAsync();
        await using var transaccion = await sesion.BeginTransactionAsync();
        await using (var bloqueo = sesion.CreateCommand())
        {
            bloqueo.Transaction = transaccion;
            bloqueo.CommandText = "SELECT count(*) FROM (SELECT 1 FROM numeraciones_fiscales FOR UPDATE) s";
            await bloqueo.ExecuteScalarAsync();
        }

        var (respuesta, cuerpo) = await ConTopeAsync(AnularAsync(c.Admin, venta.IdVenta));

        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        Assert.Single(await LeerDecisionesDeNotaAsync(c, venta.IdVenta));
        await transaccion.RollbackAsync();
    }

    [Fact]
    public async Task OtroTenantNoLeeLaVentaAnuladaNiLaNotaDeLaOtra()
    {
        var (t, vendida) = await VenderFacturadaAsync("anulaislaa");
        await AnularConExitoAsync(t.Cajero, vendida.Id);
        var otro = await PrepararAsync("anulaislab");

        Assert.Equal(HttpStatusCode.NotFound, (await otro.Admin.GetAsync($"/api/ventas/{vendida.Id}")).StatusCode);
        var porTurno = await otro.Admin.GetAsync($"/api/ventas/por-turno/{t.C.IdTurno}");
        Assert.Equal(HttpStatusCode.OK, porTurno.StatusCode);
        Assert.Empty(JsonDocument.Parse(await porTurno.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await otro.Admin.PostAsync($"/api/ventas/{vendida.Id}/anulacion", null)).StatusCode);
    }
}
