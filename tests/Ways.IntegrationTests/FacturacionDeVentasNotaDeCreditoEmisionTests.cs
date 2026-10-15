using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ways.Application.Fiscal;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T8b: <c>ServicioDeFacturacionDeVentas</c> emite la nota de crédito (NC) fiscal de una venta
/// anulada cuya factura está aprobada, por el mismo circuito del espejo (WSAA/WSFE con espías y Postgres real). Ningún camino de
/// la aplicación crea todavía la decisión de la nota (la anulación llega en T8c): se siembra por SQL tal como la insertará la
/// anulación (pendiente, sin aportes, con la empresa, el motivo y el período de la decisión de la factura).
///
/// Mutation-proof-tests: las cláusulas bajo prueba son la rama por clase de decisión de <c>ProcesarAsync</c> (el espejo de la
/// clase de la decisión, la venta anulada y la factura aprobada), los datos legales congelados de la factura, los comprobantes
/// asociados del pedido, el signo de la reversión del cupo de la factura y su condición de decisión emitida, el rechazo sin
/// reversión, las notas de crédito en el lote (cabeza de serie y candidatas por clase de espejo), la liquidación de una factura
/// rechazada de una venta anulada por la decisión suelta (el lote no la toma) y los filtros de clase de factura de T8a: la búsqueda del espejo, <c>ExisteEspejoDeLaVenta</c>,
/// la decisión de <c>ServicioDeDecisionDeFacturacion</c> y <c>TieneFacturaVigenteAsync</c>.
/// </summary>
public partial class FacturacionDeVentasEmisionTests
{
    private const string CodigoNotaB = "NCB";

    private const short CodigoAfipNotaB = 8;

    private const short CodigoAfipNotaA = 3;

    private sealed record FacturaAnulada(Venta Venta, int IdDecisionNota, int IdFactura, long NumeroDeLaFactura, DateOnly FechaDeLaFactura);

    /// <summary>Una venta con su factura aprobada (la emite el servicio real), anulada después, y la decisión pendiente de su
    /// nota de crédito. Deja el espía de WSFE reiniciado.</summary>
    private async Task<FacturaAnulada> SembrarFacturaAprobadaYAnuladaAsync(
        Ctx c, int? idCliente = null, bool sembrarAcumulados = true)
    {
        var venta = await SembrarVentaDecididaAsync(c, idCliente, sembrarAcumulados: sembrarAcumulados);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        var emitida = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.True(emitida.Estado == EstadosFiscalesDeVenta.Aprobado, $"{emitida.Estado}: {emitida.Error}");

        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET estado = 'anulado' WHERE id_comprobante_venta = $1", venta.IdVenta);
        var nota = await SembrarNotaDeCreditoAsync(c, venta.IdVenta, venta.IdFacturacion, idEspejoFactura: null);
        c.Wsfe.Reiniciar();
        return new FacturaAnulada(
            venta, nota.IdDecision, emitida.Comprobante!.IdComprobante, emitida.Comprobante.Numero, emitida.Comprobante.Fecha);
    }

    /// <summary>Lo que el acumulado tenía antes de que se decidiera la venta de <see cref="SembrarVentaDecididaAsync"/>.</summary>
    private static List<FilaDeAcumulado> AcumuladosAntesDeDecidir(Ctx c, DateOnly periodo) =>
    [
        .. new[]
        {
            new FilaDeAcumulado(c.IdEfectivo, periodo, 17.25m, 300m, 200m),
            new FilaDeAcumulado(c.IdTransferencia, periodo, -3.5m, 40m, 10m)
        }.OrderBy(f => f.IdMedio)
    ];

    private sealed record FilaDeNota(
        int Id, int IdAsociado, int IdVentaOrigen, long Numero, DateTimeOffset Fecha, string CodigoTipo, string Resultado,
        string? Cae, decimal Total, int? IdTurno, string? DatosLegales);

    private Task<List<FilaDeNota>> LeerNotasAsync(Ctx c) => ConsultarAsync(
        c,
        "SELECT cv.id_comprobante_venta, cv.id_comprobante_asociado, cv.id_venta_origen, cv.numero, cv.fecha, t.codigo, " +
        "cv.resultado_fiscal::text, cv.cae, cv.total, cv.id_turno_caja, cv.datos_legales_fiscales::text " +
        "FROM comprobantes_venta cv JOIN tipos_comprobante t ON t.id_tipo_comprobante = cv.id_tipo_comprobante " +
        "WHERE cv.id_venta_origen IS NOT NULL AND cv.id_comprobante_asociado IS NOT NULL AND cv.id_punto_venta = $1 " +
        "ORDER BY cv.id_comprobante_venta",
        l => new FilaDeNota(
            l.GetInt32(0), l.GetInt32(1), l.GetInt32(2), l.GetInt64(3), l.GetFieldValue<DateTimeOffset>(4), l.GetString(5),
            l.GetString(6), l.IsDBNull(7) ? null : l.GetString(7), l.GetDecimal(8), l.IsDBNull(9) ? null : l.GetInt32(9),
            l.IsDBNull(10) ? null : l.GetString(10)),
        c.IdPuntoVenta);

    private async Task<string?> LeerDatosLegalesAsync(Ctx c, int idComprobante) => Assert.Single(await ConsultarAsync(
        c,
        "SELECT datos_legales_fiscales::text FROM comprobantes_venta WHERE id_comprobante_venta = $1",
        l => l.IsDBNull(0) ? null : l.GetString(0),
        idComprobante));

    private static Dictionary<string, string> ComprobanteAsociadoDelPedido(string cuerpo)
    {
        var asociado = XDocument.Parse(cuerpo).Descendants().Single(e => e.Name.LocalName == "CbteAsoc");
        return asociado.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
    }

    private static short TipoDeComprobanteDelQr(string payloadQr)
    {
        var codificado = payloadQr["https://www.arca.gob.ar/fe/qr/?p=".Length..];
        using var documento = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(codificado))));
        return documento.RootElement.GetProperty("tipoCmp").GetInt16();
    }

    // ===================================================================================================
    // Emisión aprobada
    // ===================================================================================================

    [Theory]
    [InlineData(false, "NCB", 'B', 8, "6", "5")]
    [InlineData(true, "NCA", 'A', 3, "1", "1")]
    public async Task UnaNotaDeCreditoAprobadaCorrigeLaFacturaConSuComprobanteAsociadoYDevuelveElCupo(
        bool receptorIdentificado, string codigoEsperado, char letraEsperada, int codigoAfipEsperado, string facturaAfip,
        string condicionIvaEsperada)
    {
        var c = await PrepararAsync($"ncok{receptorIdentificado}");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c, receptorIdentificado ? c.IdClienteRi : c.IdClienteCf);
        var espejoDeLaFactura = Assert.Single(await LeerEspejosAsync(c));
        var datosDeLaFactura = await LeerDatosLegalesAsync(c, factura.IdFactura);
        Assert.NotNull(datosDeLaFactura);
        c.Reloj.Ahora = c.Reloj.Ahora.AddDays(1);
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70222222222222"));

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.True(estado.Estado == EstadosFiscalesDeVenta.Aprobado, $"{estado.Estado}: {estado.Error}");
        Assert.Equal(factura.IdDecisionNota, estado.IdFacturacion);
        var nota = estado.Comprobante!;
        Assert.Equal(codigoEsperado, nota.CodigoTipoComprobante);
        Assert.Equal(letraEsperada, nota.Letra);
        Assert.Equal(1, nota.Numero);
        Assert.Equal(1, nota.PuntoDeVentaFiscal);
        Assert.Equal(FechaFiscalDe(c.Reloj.Ahora), nota.Fecha);
        Assert.NotEqual(factura.FechaDeLaFactura, nota.Fecha);
        Assert.Equal("70222222222222", nota.Cae);
        Assert.Equal((short)codigoAfipEsperado, TipoDeComprobanteDelQr(nota.PayloadQr!));

        // Lo que viajó a ARCA: el tipo de la nota, los importes de la factura y la factura que corrige.
        var cuerpo = Assert.Single(c.Wsfe.CuerposDeSolicitud);
        Assert.Equal(codigoAfipEsperado.ToString(), CampoDelCuerpo(cuerpo, "CbteTipo"));
        Assert.Equal("1", CampoDelCuerpo(cuerpo, "CbteDesde"));
        Assert.Equal(Yyyymmdd(FechaFiscalDe(c.Reloj.Ahora)), CampoDelCuerpo(cuerpo, "CbteFch"));
        Assert.Equal(receptorIdentificado ? "80" : "99", CampoDelCuerpo(cuerpo, "DocTipo"));
        Assert.Equal(receptorIdentificado ? "20222222223" : "0", CampoDelCuerpo(cuerpo, "DocNro"));
        Assert.Equal(condicionIvaEsperada, CampoDelCuerpo(cuerpo, "CondicionIVAReceptorId"));
        Assert.Equal("231.00", CampoDelCuerpo(cuerpo, "ImpTotal"));
        Assert.Equal("199.55", CampoDelCuerpo(cuerpo, "ImpNeto"));
        Assert.Equal("31.45", CampoDelCuerpo(cuerpo, "ImpIVA"));
        var asociado = ComprobanteAsociadoDelPedido(cuerpo);
        Assert.Equal(facturaAfip, asociado["Tipo"]);
        Assert.Equal("1", asociado["PtoVta"]);
        Assert.Equal(factura.NumeroDeLaFactura.ToString(), asociado["Nro"]);
        Assert.Equal("20111111112", asociado["Cuit"]);
        Assert.Equal(Yyyymmdd(factura.FechaDeLaFactura), asociado["CbteFch"]);

        // El espejo de la nota: enlazado a la venta y a la factura, con sus datos legales y sus ítems, sin turno ni ledgers.
        var espejo = Assert.Single(await LeerNotasAsync(c));
        Assert.Equal(nota.IdComprobante, espejo.Id);
        Assert.Equal(espejoDeLaFactura.Id, espejo.IdAsociado);
        Assert.Equal(factura.Venta.IdVenta, espejo.IdVentaOrigen);
        Assert.Equal(codigoEsperado, espejo.CodigoTipo);
        Assert.Equal(("aprobado", "70222222222222", 1L), (espejo.Resultado, espejo.Cae, espejo.Numero));
        Assert.Equal(c.Reloj.Ahora, espejo.Fecha);
        Assert.Equal(231m, espejo.Total);
        Assert.Null(espejo.IdTurno);
        Assert.Equal(datosDeLaFactura, espejo.DatosLegales);
        Assert.Equal(await LeerItemsAsync(c, espejoDeLaFactura.Id), await LeerItemsAsync(c, espejo.Id));
        Assert.Equal((0, 0, 0), await LedgersDelEspejoAsync(c, espejo.Id));

        // La decisión de la nota queda emitida; la de la factura no se toca.
        var decision = await LeerDecisionAsync(c, factura.IdDecisionNota);
        Assert.Equal("emitida", decision.Estado);
        Assert.Equal(espejo.Id, decision.IdComprobanteFiscal);
        Assert.Null(decision.UltimoError);
        Assert.Equal("emitida", (await LeerDecisionAsync(c, factura.Venta.IdFacturacion)).Estado);

        // Cada serie avanza por su lado y el cupo de la factura vuelve por completo al estado anterior a la decisión.
        var series = (await LeerSeriesAsync(c)).ToDictionary(s => s.CodigoAfip);
        Assert.Equal(2, series.Count);
        Assert.Equal((2L, (long?)1L), (series[short.Parse(facturaAfip)].Proximo, series[short.Parse(facturaAfip)].UltimoAutorizado));
        Assert.Equal((2L, (long?)1L), (series[(short)codigoAfipEsperado].Proximo, series[(short)codigoAfipEsperado].UltimoAutorizado));
        Assert.Equal(AcumuladosAntesDeDecidir(c, factura.Venta.Periodo), await LeerAcumuladosAsync(c));

        // Idempotente: otra llamada devuelve lo mismo, sin ARCA y sin devolver el cupo dos veces.
        c.Wsfe.Reiniciar();
        var otra = await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        Assert.Equivalent(estado, otra, strict: true);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Single(await LeerNotasAsync(c));
        Assert.Equal(AcumuladosAntesDeDecidir(c, factura.Venta.Periodo), await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task UnaNotaDeCreditoPideConLosDatosCongeladosDeLaFacturaYNoConLasFilasActuales()
    {
        var c = await PrepararAsync("ncfrozen");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c, c.IdClienteRi);
        await EjecutarComoOwnerAsync(
            "UPDATE clientes SET numero_documento = '27333333334', " +
            "id_condicion_fiscal = (SELECT id_condicion_fiscal FROM condiciones_fiscales WHERE codigo = 'NO_RESP') " +
            "WHERE id_cliente = $1", c.IdClienteRi);
        await EjecutarComoOwnerAsync("UPDATE empresas SET cuit = '20999999995' WHERE id_empresa = $1", c.IdEmpresa);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.True(estado.Estado == EstadosFiscalesDeVenta.Aprobado, $"{estado.Estado}: {estado.Error}");
        var cuerpo = Assert.Single(c.Wsfe.CuerposDeSolicitud);
        Assert.Equal("3", CampoDelCuerpo(cuerpo, "CbteTipo"));
        Assert.Equal("80", CampoDelCuerpo(cuerpo, "DocTipo"));
        Assert.Equal("20222222223", CampoDelCuerpo(cuerpo, "DocNro"));
        Assert.Equal("1", CampoDelCuerpo(cuerpo, "CondicionIVAReceptorId"));
        Assert.Equal("20111111112", CampoDelCuerpo(cuerpo, "Cuit"));
        Assert.Equal("20111111112", ComprobanteAsociadoDelPedido(cuerpo)["Cuit"]);
    }

    /// <summary>Cláusula bajo prueba: <c>incluirBajasDeCatalogo</c> al componer las líneas de una nota de crédito. Las alícuotas
    /// de la factura se dan de baja después de aprobarla: la nota se emite igual, con los mismos totales.</summary>
    [Fact]
    public async Task UnaNotaDeCreditoSeEmiteAunqueSeDieranDeBajaLasAlicuotasDeLaFactura()
    {
        var c = await PrepararAsync("ncalibaja");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70222222222223"));

        // El catálogo de alícuotas es compartido por toda la suite: la baja se deshace siempre.
        await EjecutarComoOwnerAsync("UPDATE alicuotas_iva SET deleted_at = now() WHERE codigo_afip IN (4, 5)");
        try
        {
            var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

            Assert.True(estado.Estado == EstadosFiscalesDeVenta.Aprobado, $"{estado.Estado}: {estado.Error}");
            var cuerpo = Assert.Single(c.Wsfe.CuerposDeSolicitud);
            Assert.Equal("231.00", CampoDelCuerpo(cuerpo, "ImpTotal"));
            Assert.Equal("199.55", CampoDelCuerpo(cuerpo, "ImpNeto"));
            Assert.Equal("31.45", CampoDelCuerpo(cuerpo, "ImpIVA"));
        }
        finally
        {
            await EjecutarComoOwnerAsync("UPDATE alicuotas_iva SET deleted_at = NULL WHERE codigo_afip IN (4, 5)");
        }
    }

    // ===================================================================================================
    // Rechazo y falta de respuesta
    // ===================================================================================================

    [Fact]
    public async Task UnaNotaDeCreditoRechazadaDevuelveElNumeroNoTocaElCupoYNoSeReintentaSola()
    {
        var c = await PrepararAsync("ncrech");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var acumuladosConLaFactura = await LeerAcumuladosAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeRechazado());

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Contains("10015", estado.Error);
        Assert.Empty(await LeerNotasAsync(c));
        Assert.Equal(1L, await LeerProximoAsync(c, CodigoAfipNotaB));
        var decision = await LeerDecisionAsync(c, factura.IdDecisionNota);
        Assert.Equal("rechazada", decision.Estado);
        Assert.Contains("10015", decision.UltimoError);
        Assert.Null(decision.IdComprobanteFiscal);
        Assert.Equal("emitida", (await LeerDecisionAsync(c, factura.Venta.IdFacturacion)).Estado);
        Assert.Equal(acumuladosConLaFactura, await LeerAcumuladosAsync(c));

        // Una decisión rechazada no vuelve sola: ni el lote ni otra llamada piden de nuevo.
        c.Wsfe.Reiniciar();
        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);
        Assert.Equal((0, 0, 0, 0), (lote.Emitidas, lote.Rechazadas, lote.Canceladas, lote.PendientesRestantes));
        Assert.Empty(lote.Resultados);
        var otra = await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, otra.Estado);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal("rechazada", (await LeerDecisionAsync(c, factura.IdDecisionNota)).Estado);
        Assert.Equal(acumuladosConLaFactura, await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task SinRespuestaDefinitivaLaNotaQuedaPendienteYLaSiguienteLlamadaConsultaAdoptaYDevuelveElCupo()
    {
        var c = await PrepararAsync("ncnodef");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var acumuladosConLaFactura = await LeerAcumuladosAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();

        var primera = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, primera.Estado);
        Assert.Contains("arca_no_definitivo", primera.Error);
        var pendiente = Assert.Single(await LeerNotasAsync(c));
        Assert.Equal(("pendiente", 1L), (pendiente.Resultado, pendiente.Numero));
        Assert.Equal(2L, await LeerProximoAsync(c, CodigoAfipNotaB));
        Assert.Equal(1, (await LeerDecisionAsync(c, factura.IdDecisionNota)).Intentos);
        Assert.Equal(acumuladosConLaFactura, await LeerAcumuladosAsync(c));

        // ARCA ya tenía el número: se consulta, se adopta y recién ahí vuelve el cupo.
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70555555555555", Yyyymmdd(FechaFiscalDe(pendiente.Fecha))));
        c.Wsfe.Solicitar = RespondeHttp500();
        var segunda = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, segunda.Estado);
        Assert.Equal("70555555555555", segunda.Comprobante!.Cae);
        Assert.Equal(CodigoNotaB, segunda.Comprobante.CodigoTipoComprobante);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
        var adoptada = Assert.Single(await LeerNotasAsync(c));
        Assert.Equal((pendiente.Id, "aprobado"), (adoptada.Id, adoptada.Resultado));
        Assert.Equal("emitida", (await LeerDecisionAsync(c, factura.IdDecisionNota)).Estado);
        Assert.Equal(AcumuladosAntesDeDecidir(c, factura.Venta.Periodo), await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task SiLaConsultaNoEncuentraLaNotaSePideDeNuevoConLaFacturaAsociada()
    {
        var c = await PrepararAsync("ncreint");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        c.Reloj.Ahora = c.Reloj.Ahora.AddDays(2);
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70666666666666"));

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
        var cuerpo = Assert.Single(c.Wsfe.CuerposDeSolicitud);
        Assert.Equal("8", CampoDelCuerpo(cuerpo, "CbteTipo"));
        Assert.Equal("1", CampoDelCuerpo(cuerpo, "CbteDesde"));
        Assert.Equal(Yyyymmdd(FechaFiscalDe(c.Reloj.Ahora)), CampoDelCuerpo(cuerpo, "CbteFch"));
        var asociado = ComprobanteAsociadoDelPedido(cuerpo);
        Assert.Equal(("6", "1", factura.NumeroDeLaFactura.ToString()), (asociado["Tipo"], asociado["PtoVta"], asociado["Nro"]));
        Assert.Equal(("20111111112", Yyyymmdd(factura.FechaDeLaFactura)), (asociado["Cuit"], asociado["CbteFch"]));
        Assert.Equal(AcumuladosAntesDeDecidir(c, factura.Venta.Periodo), await LeerAcumuladosAsync(c));
    }

    // ===================================================================================================
    // Compuertas de la nota de crédito
    // ===================================================================================================

    private async Task AssertNotaEnEsperaAsync(Ctx c, FacturaAnulada factura, string codigo)
    {
        var decision = await LeerDecisionAsync(c, factura.IdDecisionNota);
        Assert.Equal(("pendiente", 1), (decision.Estado, decision.Intentos));
        Assert.StartsWith(codigo + ":", decision.UltimoError);
        Assert.Equal(c.Reloj.Ahora.AddMinutes(5), decision.ProximoIntento);
        Assert.Empty(await LeerNotasAsync(c));
        var proximo = await LeerProximoAsync(c, CodigoAfipNotaB);
        Assert.True(proximo is null or 1L, $"La nota no tenía que tomar número, la serie está en {proximo}.");
        Assert.Empty(c.Wsfe.Operaciones);
    }

    [Fact]
    public async Task LaNotaDeUnaVentaQueNoEstaAnuladaEsperaConSuCodigoYNoTomaNumero()
    {
        var c = await PrepararAsync("ncnoanul");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET estado = 'emitido' WHERE id_comprobante_venta = $1", factura.Venta.IdVenta);
        var acumulados = await LeerAcumuladosAsync(c);

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith(ServicioDeFacturacionDeVentas.NotaDeCreditoVentaNoAnulada + ":", estado.Error);
        await AssertNotaEnEsperaAsync(c, factura, ServicioDeFacturacionDeVentas.NotaDeCreditoVentaNoAnulada);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaNotaDeUnaVentaSinFacturaAprobadaEsperaConSuCodigoYNoPideNada(bool conEspejoPendiente)
    {
        var c = await PrepararAsync($"ncsinfac{conEspejoPendiente}");
        var venta = await SembrarVentaDecididaAsync(c);
        if (conEspejoPendiente)
        {
            c.Wsfe.Solicitar = RespondeHttp500();
            await ProcesarAsync(c.Admin, venta.IdFacturacion);
        }

        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET estado = 'anulado' WHERE id_comprobante_venta = $1", venta.IdVenta);
        var nota = await SembrarNotaDeCreditoAsync(c, venta.IdVenta, venta.IdFacturacion, idEspejoFactura: null);
        c.Wsfe.Reiniciar();

        var estado = await ProcesarAsync(c.Admin, nota.IdDecision);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith(ServicioDeFacturacionDeVentas.NotaDeCreditoSinFacturaAprobada + ":", estado.Error);
        await AssertNotaEnEsperaAsync(
            c, new FacturaAnulada(venta, nota.IdDecision, 0, 0, default), ServicioDeFacturacionDeVentas.NotaDeCreditoSinFacturaAprobada);
    }

    [Fact]
    public async Task LaNotaCuyoTotalNoCoincideConElDeLaFacturaEsperaConSuCodigoYNoTomaNumero()
    {
        var c = await PrepararAsync("ncdifer");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET total = total + 1 WHERE id_comprobante_venta = $1", factura.IdFactura);

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        await AssertNotaEnEsperaAsync(c, factura, "total_fiscal_no_coincide");
    }

    [Fact]
    public async Task LaNotaSinTipoDeNotaDeCreditoActivoParaLaLetraEsperaConSuCodigo()
    {
        var c = await PrepararAsync("nctipo");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        await using var restauracion = new Restauracion(
            () => EjecutarComoOwnerAsync("UPDATE tipos_comprobante SET activo = true WHERE codigo = 'NCB'"));
        await EjecutarComoOwnerAsync("UPDATE tipos_comprobante SET activo = false WHERE codigo = 'NCB'");

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        await AssertNotaEnEsperaAsync(c, factura, "tipo_fiscal_invalido");
    }

    // ===================================================================================================
    // Cupo de la factura
    // ===================================================================================================

    [Fact]
    public async Task LaNotaAprobadaNoDevuelveCupoSiLaDecisionDeLaFacturaNoEstabaEmitida()
    {
        var c = await PrepararAsync("ncorigen");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'cancelada' WHERE id_facturacion_venta = $1", factura.Venta.IdFacturacion);
        var acumulados = await LeerAcumuladosAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal("emitida", (await LeerDecisionAsync(c, factura.IdDecisionNota)).Estado);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
        Assert.Contains(
            c.Logs.Entradas,
            e => e.Nivel == LogLevel.Warning && e.Categoria.EndsWith(nameof(ServicioDeFacturacionDeVentas), StringComparison.Ordinal)
                && e.Mensaje.Contains("no se devolvió cupo", StringComparison.Ordinal));
    }

    // ===================================================================================================
    // Lote
    // ===================================================================================================

    [Fact]
    public async Task ElLoteEmiteLaNotaDeCreditoPendienteDeUnaVentaAnulada()
    {
        var c = await PrepararAsync("ncelote");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70333333333333"));

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal((1, 0, 0, 0), (lote.Emitidas, lote.Rechazadas, lote.Canceladas, lote.PendientesRestantes));
        var resultado = Assert.Single(lote.Resultados);
        Assert.Equal((factura.IdDecisionNota, factura.Venta.IdVenta), (resultado.IdFacturacion, resultado.IdComprobanteVenta));
        Assert.Equal(CodigoNotaB, resultado.Fiscal.Comprobante!.CodigoTipoComprobante);
        Assert.Equal("8", CampoDelCuerpo(Assert.Single(c.Wsfe.CuerposDeSolicitud), "CbteTipo"));
        Assert.Equal("emitida", (await LeerDecisionAsync(c, factura.IdDecisionNota)).Estado);
        Assert.Equal(AcumuladosAntesDeDecidir(c, factura.Venta.Periodo), await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task UnaNotaPendienteEnEsperaBloqueaSoloLaSerieDeNotasYNoLaDeFacturas()
    {
        var c = await PrepararAsync("ncserie");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        var sinRespuesta = await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, sinRespuesta.Estado);
        var otra = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        c.Wsfe.Reiniciar();
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70444444444444"));

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        // La nota espera su turno (su serie, la 8, queda bloqueada) y la factura de la otra venta sigue de largo por la serie 6.
        Assert.Equal((1, 0, 0), (lote.Emitidas, lote.Rechazadas, lote.Canceladas));
        Assert.Equal(otra.IdFacturacion, Assert.Single(lote.Resultados).IdFacturacion);
        var bloqueada = Assert.Single(lote.SeriesBloqueadas);
        Assert.Equal((CodigoAfipNotaB, ProcesadorDeFacturacionesPorLote.CabezaEnEspera), (bloqueada.CodigoAfip, bloqueada.Motivo));
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal("6", CampoDelCuerpo(Assert.Single(c.Wsfe.CuerposDeSolicitud), "CbteTipo"));
        Assert.Equal("pendiente", (await LeerDecisionAsync(c, factura.IdDecisionNota)).Estado);

        // Vencida la espera, la cabeza es la decisión de la nota (no la de su factura, que también es de esa venta).
        c.Reloj.Ahora = c.Reloj.Ahora.AddMinutes(5);
        c.Wsfe.Reiniciar();
        var pendiente = Assert.Single(await LeerNotasAsync(c));
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70777777777777", Yyyymmdd(FechaFiscalDe(pendiente.Fecha))));
        var segundo = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal(1, segundo.Emitidas);
        Assert.Equal(factura.IdDecisionNota, Assert.Single(segundo.Resultados).IdFacturacion);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
        Assert.Equal("emitida", (await LeerDecisionAsync(c, factura.IdDecisionNota)).Estado);
    }

    // ===================================================================================================
    // Carreras
    // ===================================================================================================

    [Fact]
    public async Task UnaLlamadaConLecturasViejasDevuelveLaNotaSiOtraYaLaEmitio()
    {
        var c = await PrepararAsync("ncviejaa");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);
        c.Wsfe.Solicitar = Responde(FecaeAprobado("70121212121212"));

        var segunda = ProcesarAsync(pausa.Cliente, factura.IdDecisionNota);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        var primera = await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, primera.Estado);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal("70121212121212", estado.Comprobante!.Cae);
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Single(await LeerNotasAsync(c));
        Assert.Equal(2L, await LeerProximoAsync(c, CodigoAfipNotaB));
        Assert.Equal(AcumuladosAntesDeDecidir(c, factura.Venta.Periodo), await LeerAcumuladosAsync(c));
    }

    /// <summary>Cláusula bajo prueba: la relectura de "esta venta ya tiene un espejo de nota" bajo el lock de la venta. La decisión
    /// sigue pendiente, así que es la única que frena a la segunda llamada: sin ella toparía con el índice único del espejo de
    /// la nota (o con la serie bloqueada) en lugar de reevaluar y consultar el espejo que ya existe.</summary>
    [Fact]
    public async Task UnaLlamadaConLecturasViejasNoInsertaUnSegundoEspejoDeNotaSiOtraDejoUnoPendiente()
    {
        var c = await PrepararAsync("ncviejap");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);
        c.Wsfe.Solicitar = RespondeHttp500();

        var segunda = ProcesarAsync(pausa.Cliente, factura.IdDecisionNota);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        var primera = await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, primera.Estado);
        var espejo = Assert.Single(await LeerNotasAsync(c));
        c.Wsfe.Consultar = Responde(ConsultarEncontrado("70131313131313", Yyyymmdd(FechaFiscalDe(espejo.Fecha))));
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(["FECAESolicitar", "FECompConsultar"], Pasos(c.Wsfe));
        Assert.Equal(espejo.Id, Assert.Single(await LeerNotasAsync(c)).Id);
        Assert.Equal(2L, await LeerProximoAsync(c, CodigoAfipNotaB));
    }

    [Fact]
    public async Task DosProcesadoresSobreLaMismaNotaCreanUnSoloEspejo()
    {
        var c = await PrepararAsync("ncdos");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var otro = await ClienteConServiciosAsync(c, _ => { });
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var estados = await ConTopeAsync(Task.WhenAll(
            ProcesarAsync(c.Admin, factura.IdDecisionNota), ProcesarAsync(otro, factura.IdDecisionNota)));

        Assert.All(
            estados,
            e => Assert.True(
                e.Estado == EstadosFiscalesDeVenta.Aprobado
                || (e.Estado == EstadosFiscalesDeVenta.Pendiente && e.Error!.StartsWith("serie_", StringComparison.Ordinal)),
                $"{e.Estado}: {e.Error}"));
        Assert.Contains(estados, e => e.Estado == EstadosFiscalesDeVenta.Aprobado);
        var nota = Assert.Single(await LeerNotasAsync(c));
        Assert.Equal("aprobado", nota.Resultado);
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(AcumuladosAntesDeDecidir(c, factura.Venta.Periodo), await LeerAcumuladosAsync(c));
    }

    // ===================================================================================================
    // Factura rechazada de una venta que después se anula
    // ===================================================================================================

    private async Task<Venta> SembrarFacturaRechazadaAsync(Ctx c)
    {
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeRechazado());
        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        c.Wsfe.Reiniciar();
        return venta;
    }

    private Task AnularPorSqlAsync(int idVenta) => EjecutarComoOwnerAsync(
        "UPDATE comprobantes_venta SET estado = 'anulado' WHERE id_comprobante_venta = $1", idVenta);

    [Fact]
    public async Task UnaFacturaRechazadaDeUnaVentaAnuladaTerminaDeRevertirElAcumuladoUnaSolaVez()
    {
        var c = await PrepararAsync("rechanul");
        var venta = await SembrarFacturaRechazadaAsync(c);

        // El rechazo devolvió solo el débito: la venta sigue devengando y contando como vendida.
        Assert.Equal(
            new[]
            {
                new FilaDeAcumulado(c.IdEfectivo, venta.Periodo, 67.25m, 400m, 200m),
                new FilaDeAcumulado(c.IdTransferencia, venta.Periodo, 35.8m, 171m, 10m)
            }.OrderBy(f => f.IdMedio),
            await LeerAcumuladosAsync(c));
        await AnularPorSqlAsync(venta.IdVenta);

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal("cancelada", decision.Estado);
        Assert.Contains("10015", decision.UltimoError);
        Assert.Equal(AcumuladosAntesDeDecidir(c, venta.Periodo), await LeerAcumuladosAsync(c));
        Assert.Empty(c.Wsfe.Operaciones);

        // Idempotente: procesarla de nuevo no revierte dos veces.
        var otra = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, otra.Estado);
        Assert.Equal(AcumuladosAntesDeDecidir(c, venta.Periodo), await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task ElLoteNoTomaLaFacturaRechazadaDeUnaVentaAnuladaYLaDecisionSueltaLaLiquida()
    {
        var c = await PrepararAsync("rechlote");
        var anulada = await SembrarFacturaRechazadaAsync(c);
        await AnularPorSqlAsync(anulada.IdVenta);
        var tras = await LeerAcumuladosAsync(c);

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Empty(lote.Resultados);
        Assert.Equal("rechazada", (await LeerDecisionAsync(c, anulada.IdFacturacion)).Estado);
        Assert.Equal(tras, await LeerAcumuladosAsync(c));

        var estado = await ProcesarAsync(c.Admin, anulada.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Equal("cancelada", (await LeerDecisionAsync(c, anulada.IdFacturacion)).Estado);
        Assert.NotEqual(tras, await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task UnaFacturaRechazadaDeUnaVentaQueNoSeAnuloNoSeToca()
    {
        var c = await PrepararAsync("rechvig");
        var venta = await SembrarFacturaRechazadaAsync(c);
        var acumulados = await LeerAcumuladosAsync(c);

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Empty(lote.Resultados);
        Assert.Equal("rechazada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
    }

    [Fact]
    public async Task UnaNotaDeCreditoRechazadaDeUnaVentaAnuladaNoSeCancelaNiSeLiquida()
    {
        var c = await PrepararAsync("ncrechanul");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        c.Wsfe.Solicitar = Responde(FecaeRechazado());
        await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        c.Wsfe.Reiniciar();
        var acumulados = await LeerAcumuladosAsync(c);

        var estado = await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Empty(lote.Resultados);
        Assert.Equal("rechazada", (await LeerDecisionAsync(c, factura.IdDecisionNota)).Estado);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
        Assert.Empty(c.Wsfe.Operaciones);
    }

    // ===================================================================================================
    // Verificaciones bajo lock: estados que ninguna llamada de la aplicación revierte, así que solo se alcanzan simulando el cambio
    // ===================================================================================================

    /// <summary>Cláusula bajo prueba: la relectura del estado de la decisión bajo su lock al liquidar una factura rechazada. Una
    /// segunda llamada con lecturas viejas (decisión todavía rechazada) llega después de que la primera la canceló y revirtió:
    /// no la revierte otra vez.</summary>
    [Fact]
    public async Task UnaLiquidacionConLecturasViejasNoRevierteElAcumuladoDosVeces()
    {
        var c = await PrepararAsync("liqvieja");
        var venta = await SembrarFacturaRechazadaAsync(c);
        await AnularPorSqlAsync(venta.IdVenta);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);

        var segunda = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        var primera = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, primera.Estado);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(segunda);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Equal(AcumuladosAntesDeDecidir(c, venta.Periodo), await LeerAcumuladosAsync(c));
    }

    /// <summary>Cláusula bajo prueba: la relectura del estado de la venta bajo su lock al liquidar. La venta se "des-anula" por SQL
    /// mientras la llamada espera (la aplicación no lo hace nunca): la liquidación no se aplica.</summary>
    [Fact]
    public async Task UnaLiquidacionVerificaBajoElLockQueLaVentaSigaAnulada()
    {
        var c = await PrepararAsync("liqventa");
        var venta = await SembrarFacturaRechazadaAsync(c);
        await AnularPorSqlAsync(venta.IdVenta);
        var acumulados = await LeerAcumuladosAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);

        var llamada = ProcesarAsync(pausa.Cliente, venta.IdFacturacion);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET estado = 'emitido' WHERE id_comprobante_venta = $1", venta.IdVenta);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(llamada);

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
        Assert.Equal("rechazada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Equal(acumulados, await LeerAcumuladosAsync(c));
    }

    /// <summary>Cláusula bajo prueba: la guarda de venta anulada de <c>ProcesarAsync</c> antes de liquidar. Con el lock de la venta
    /// retenido por otra sesión, procesar una rechazada de una venta que NO está anulada tiene que volver sin tomarlo.</summary>
    [Fact]
    public async Task ProcesarUnaRechazadaDeUnaVentaVigenteNoTomaElLockDeLaVenta()
    {
        var c = await PrepararAsync("liqsinlock");
        var venta = await SembrarFacturaRechazadaAsync(c);

        await using var sostenedor = await AbrirOwnerAsync();
        await using var transaccion = await sostenedor.BeginTransactionAsync();
        await using (var bloqueo = sostenedor.CreateCommand())
        {
            bloqueo.Transaction = transaccion;
            bloqueo.CommandText = "SELECT 1 FROM comprobantes_venta WHERE id_comprobante_venta = $1 FOR UPDATE";
            bloqueo.Parameters.Add(new NpgsqlParameter { Value = venta.IdVenta });
            await bloqueo.ExecuteScalarAsync();
        }

        var estado = await ConTopeAsync(ProcesarAsync(c.Admin, venta.IdFacturacion));

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, estado.Estado);
    }

    /// <summary>Cláusula bajo prueba: la venta anulada que exige la creación del espejo de la nota, bajo el lock de la venta. La
    /// venta vuelve a emitida por SQL mientras la llamada espera: no se crea el espejo ni se toma número, y la nota espera.</summary>
    [Fact]
    public async Task LaCreacionDeLaNotaVerificaBajoElLockQueLaVentaSigaAnulada()
    {
        var c = await PrepararAsync("ncpvent");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);

        var llamada = ProcesarAsync(pausa.Cliente, factura.IdDecisionNota);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET estado = 'emitido' WHERE id_comprobante_venta = $1", factura.Venta.IdVenta);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(llamada);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith(ServicioDeFacturacionDeVentas.NotaDeCreditoVentaNoAnulada + ":", estado.Error);
        await AssertNotaEnEsperaAsync(c, factura, ServicioDeFacturacionDeVentas.NotaDeCreditoVentaNoAnulada);
    }

    /// <summary>Cláusula bajo prueba: la factura aprobada que exige la creación del espejo de la nota, releída bajo el lock. La
    /// factura vuelve a pendiente por SQL mientras la llamada espera (una factura aprobada no vuelve a pendiente).</summary>
    [Fact]
    public async Task LaCreacionDeLaNotaVerificaBajoElLockQueLaFacturaSigaAprobada()
    {
        var c = await PrepararAsync("ncpfact");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var pausa = await PausarEnLaTransaccionAsync(c, 1);

        var llamada = ProcesarAsync(pausa.Cliente, factura.IdDecisionNota);
        await pausa.Iniciada.Task.WaitAsync(Tope);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET resultado_fiscal = 'pendiente', cae = NULL, cae_vencimiento = NULL " +
            "WHERE id_comprobante_venta = $1", factura.IdFactura);
        pausa.Continuar.TrySetResult();
        var estado = await ConTopeAsync(llamada);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith(ServicioDeFacturacionDeVentas.NotaDeCreditoSinFacturaAprobada + ":", estado.Error);
        await AssertNotaEnEsperaAsync(c, factura, ServicioDeFacturacionDeVentas.NotaDeCreditoSinFacturaAprobada);
    }

    /// <summary>Cláusula bajo prueba: la serie de una candidata que es nota de crédito sale de la letra de su factura. La cabeza de
    /// la serie de notas sigue pendiente tras intentarla, y las demás notas de la serie no se intentan.</summary>
    [Fact]
    public async Task UnaCabezaPendienteDeLaSerieDeNotasDejaSinIntentarLasDemasNotasDeSuSerie()
    {
        var c = await PrepararAsync("ncseriecand");
        var primera = await SembrarFacturaAprobadaYAnuladaAsync(c);
        var segunda = await SembrarFacturaAprobadaYAnuladaAsync(c, sembrarAcumulados: false);
        c.Wsfe.Solicitar = RespondeHttp500();
        var sinRespuesta = await ProcesarAsync(c.Admin, primera.IdDecisionNota);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, sinRespuesta.Estado);
        c.Reloj.Ahora = c.Reloj.Ahora.AddMinutes(5);
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = RespondeHttp500();

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal(primera.IdDecisionNota, Assert.Single(lote.Resultados).IdFacturacion);
        var bloqueada = Assert.Single(lote.SeriesBloqueadas);
        Assert.Equal((CodigoAfipNotaB, ProcesadorDeFacturacionesPorLote.CabezaPendiente), (bloqueada.CodigoAfip, bloqueada.Motivo));
        var decision = await LeerDecisionAsync(c, segunda.IdDecisionNota);
        Assert.Equal("pendiente", decision.Estado);
        Assert.Equal(0, decision.Intentos);
        Assert.Null(decision.UltimoError);
    }

    // ===================================================================================================
    // Filtros de clase de T8a, con filas de nota de crédito reales
    // ===================================================================================================

    /// <summary>Siembra el espejo de una nota de crédito sobre una venta que no tiene factura (o, con <paramref name="idFactura"/>,
    /// asociado a ella): el espejo suelto aísla un filtro de clase que, en producción, solo se distingue de la factura por el
    /// comprobante asociado.</summary>
    private Task<int> SembrarEspejoDeNotaAsync(int idVenta, int idAsociado, string resultado) => ScalarOwnerAsync<int>(
        "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_empleado, " +
        " id_cliente, subtotal, descuento_total, total, estado, resultado_fiscal, id_venta_origen, id_comprobante_asociado, " +
        " created_at, updated_at) " +
        "SELECT v.id_tenant, (SELECT id_tipo_comprobante FROM tipos_comprobante WHERE codigo = 'NCB'), v.numero + 600000, " +
        " v.fecha, v.id_punto_venta, v.id_empleado, v.id_cliente, v.subtotal, v.descuento_total, v.total, 'emitido', " +
        " $3::resultado_fiscal, v.id_comprobante_venta, $2, now(), now() " +
        "FROM comprobantes_venta v WHERE v.id_comprobante_venta = $1 RETURNING id_comprobante_venta",
        idVenta, idAsociado, resultado);

    /// <summary>Cláusula bajo prueba: el filtro de clase de la búsqueda del espejo en <c>ProcesarAsync</c>. El espejo pendiente de
    /// la nota queda físicamente ANTES del de la factura, así que sin el filtro la factura (ya emitida) tomaría ese espejo como
    /// suyo y consultaría a ARCA.</summary>
    [Fact]
    public async Task ProcesarLaDecisionDeLaFacturaIgnoraElEspejoPendienteDeLaNota()
    {
        var c = await PrepararAsync("ncfiltroproc");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET updated_at = now() WHERE id_comprobante_venta = $1", factura.IdFactura);
        c.Wsfe.Reiniciar();

        var estado = await ProcesarAsync(c.Admin, factura.Venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(factura.IdFactura, estado.Comprobante!.IdComprobante);
        Assert.Empty(c.Wsfe.Operaciones);
    }

    /// <summary>Cláusula bajo prueba: la clase en <c>ExisteEspejoDeLaVentaAsync</c>, en la cancelación. La venta anulada tiene el
    /// espejo de una nota y ninguno de factura: la factura pendiente se cancela. Es una combinación que la aplicación no
    /// produce (la nota exige la factura aprobada); se siembra para que sea el filtro lo único que separa las filas.</summary>
    [Fact]
    public async Task LaCancelacionDeLaFacturaPendienteNoTomaElEspejoDeLaNotaPorElDeLaFactura()
    {
        var c = await PrepararAsync("ncfiltrocancel");
        var venta = await SembrarVentaDecididaAsync(c);
        await AnularPorSqlAsync(venta.IdVenta);
        await SembrarEspejoDeNotaAsync(venta.IdVenta, venta.IdVenta, "pendiente");

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, estado.Estado);
        Assert.Equal("cancelada", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Equal(AcumuladosAntesDeDecidir(c, venta.Periodo), await LeerAcumuladosAsync(c));
    }

    /// <summary>Cláusula bajo prueba: <c>id_facturacion_origen IS NULL</c> de la decisión en <c>ServicioDeDecisionDeFacturacion</c>.
    /// La decisión de la nota queda físicamente antes que la de la factura.</summary>
    [Fact]
    public async Task DecidirDeNuevoUnaVentaConNotaDevuelveLaDecisionDeLaFactura()
    {
        var c = await PrepararAsync("ncfiltrodec");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);

        var estado = await DecidirAsync(c, factura.Venta.IdVenta);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(factura.Venta.IdFacturacion, estado.IdFacturacion);
        Assert.NotEqual(factura.IdDecisionNota, estado.IdFacturacion);
    }

    /// <summary>Cláusula bajo prueba: <c>id_comprobante_asociado IS NULL</c> de <c>TieneFacturaVigenteAsync</c>. El espejo de una
    /// nota pendiente no es la factura: la venta emitida todavía se puede anular.</summary>
    [Fact]
    public async Task ElEspejoDeUnaNotaNoEsLaFacturaVigenteDeLaVentaQueSeAnula()
    {
        var c = await PrepararAsync("ncfiltroanul");
        var venta = await SembrarVentaDecididaAsync(c);
        await SembrarEspejoDeNotaAsync(venta.IdVenta, venta.IdVenta, "pendiente");

        var respuesta = await c.Admin.PostAsync($"/api/ventas/{venta.IdVenta}/anulacion", null);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Cláusula bajo prueba: la clase en <c>LeerCabezasAsync</c>. Con la nota pendiente en espera la cabeza es la decisión
    /// de la nota; sin el cruce de clases la decisión de la factura, de la misma venta, se procesaría también como cabeza.</summary>
    [Fact]
    public async Task LaCabezaDeLaSerieDeNotasEsLaDecisionDeLaNotaYNoLaDeLaFacturaDeLaMismaVenta()
    {
        var c = await PrepararAsync("ncfiltrocabeza");
        var factura = await SembrarFacturaAprobadaYAnuladaAsync(c);
        c.Wsfe.Solicitar = RespondeHttp500();
        await ProcesarAsync(c.Admin, factura.IdDecisionNota);
        c.Wsfe.Reiniciar();

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Empty(lote.Resultados);
        var bloqueada = Assert.Single(lote.SeriesBloqueadas);
        Assert.Equal((CodigoAfipNotaB, ProcesadorDeFacturacionesPorLote.CabezaEnEspera), (bloqueada.CodigoAfip, bloqueada.Motivo));
        Assert.Empty(c.Wsfe.Operaciones);
    }
}
