using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// stage-5-pos-ventas, Slice 3 (task 3.16, design decisión 4, spec: comprobantes-venta /
/// Devoluciones As NCX Comprobantes, ambos escenarios) — pura, sin base de datos.
/// </summary>
public class ReglaDeComprobantesTests
{
    private static ComprobanteVenta CrearComprobante(
        int id = 501, int idTenant = 1, int idPuntoVenta = 1, int idCliente = 10,
        EstadoComprobante estado = EstadoComprobante.Emitido)
    {
        var ahora = DateTimeOffset.UtcNow;
        return new ComprobanteVenta
        {
            Id = id,
            IdTenant = idTenant,
            IdTipoComprobante = 1,
            Numero = 1,
            Fecha = ahora,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleado = 1,
            IdCliente = idCliente,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
    }

    // ---- ValidarAnulacionFiscal: CAE aprobado o pendiente bloquea la anulación ------------------

    [Theory]
    [InlineData(ResultadoFiscal.Aprobado)]
    [InlineData(ResultadoFiscal.AprobadoConObservaciones)]
    public void UnComprobanteConCaeAprobadoNoSeAnulaYExigeNotaDeCredito(ResultadoFiscal resultado)
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeComprobantes.ValidarAnulacionFiscal(resultado));
        Assert.Equal("comprobante_fiscal_requiere_nota_de_credito", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
        Assert.Contains("nota de crédito", excepcion.Message);
    }

    [Fact]
    public void UnComprobanteConCaePendienteNoSeAnula()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarAnulacionFiscal(ResultadoFiscal.Pendiente));
        Assert.Equal("comprobante_fiscal_cae_pendiente", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ResultadoFiscal.Rechazado)]
    public void UnComprobanteNoFiscalOConCaeRechazadoSigueAnulable(ResultadoFiscal? resultado) =>
        ReglaDeComprobantes.ValidarAnulacionFiscal(resultado);

    // ---- ValidarSignoDeLineas: signo vs tipos_comprobante.signo ----------------------------

    [Fact]
    public void UnTxConTodasLasCantidadesPositivasEsValido() =>
        ReglaDeComprobantes.ValidarSignoDeLineas(1, [1m, 2m, 3.5m]);

    [Fact]
    public void UnTxConUnaCantidadNegativaSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarSignoDeLineas(1, [1m, -2m]));
        Assert.Equal("signo_de_linea_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnTxConUnaCantidadCeroSeRechaza()
    {
        Assert.Throws<ErrorDominio>(() => ReglaDeComprobantes.ValidarSignoDeLineas(1, [1m, 0m]));
    }

    [Fact]
    public void UnNcxConTodasLasCantidadesNegativasEsValido() =>
        ReglaDeComprobantes.ValidarSignoDeLineas(-1, [-1m, -2m, -3.5m]);

    [Fact]
    public void UnNcxConUnaCantidadPositivaSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarSignoDeLineas(-1, [-1m, 2m]));
        Assert.Equal("signo_de_linea_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnNcxConUnaCantidadCeroSeRechaza()
    {
        Assert.Throws<ErrorDominio>(() => ReglaDeComprobantes.ValidarSignoDeLineas(-1, [-1m, 0m]));
    }

    // ---- ValidarTransicionAEstado: única transición Emitido -> Anulado ---------------------

    [Fact]
    public void EmitidoAAnuladoEsUnaTransicionValida() =>
        ReglaDeComprobantes.ValidarTransicionAEstado(EstadoComprobante.Emitido, EstadoComprobante.Anulado);

    [Fact]
    public void AnuladoNoPuedeVolverAAnularse()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarTransicionAEstado(EstadoComprobante.Anulado, EstadoComprobante.Anulado));
        Assert.Equal("comprobante_ya_anulado", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void AnuladoNoPuedeVolverAEmitido()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarTransicionAEstado(EstadoComprobante.Anulado, EstadoComprobante.Emitido));
        Assert.Equal("comprobante_ya_anulado", excepcion.Codigo);
    }

    [Fact]
    public void EmitidoNoPuedeQuedarseEnEmitidoComoTransicion()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarTransicionAEstado(EstadoComprobante.Emitido, EstadoComprobante.Emitido));
        Assert.Equal("transicion_de_estado_invalida", excepcion.Codigo);
    }

    // ---- ValidarComprobanteAsociado: obligatorio en un NCX, que devuelve un TX emitido del mismo punto de venta/cliente -------

    private static void Validar(
        short signo, int? id, ComprobanteVenta? asociado, int idPuntoVenta = 1, int idCliente = 10, string? codigoTipo = "TX") =>
        ReglaDeComprobantes.ValidarComprobanteAsociado(signo, id, asociado, codigoTipo, idPuntoVenta, idCliente);

    [Fact]
    public void UnTxSinComprobanteAsociadoEsValido()
    {
        Validar(1, null, null);
    }

    [Fact]
    public void UnNcxSinComprobanteAsociadoSeRechaza()
    {
        // Una devolución siempre sale de un ticket de origen: la devolución sin ticket ya no existe.
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-1, null, null));

        Assert.Equal("comprobante_asociado_requerido", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnTxNoPuedeTenerComprobanteAsociado()
    {
        var original = CrearComprobante();
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(1, original.Id, original));
        Assert.Equal("comprobante_asociado_no_permitido", excepcion.Codigo);
    }

    [Fact]
    public void UnNcxQueReferenciaUnTxEmitidoDelMismoPuntoDeVentaYClienteEsValido()
    {
        var original = CrearComprobante(id: 501, idPuntoVenta: 1, idCliente: 10);

        Validar(-1, original.Id, original);
    }

    [Fact]
    public void UnNcxCuyoAsociadoNoExisteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-1, 999, null, codigoTipo: null));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
        Assert.Equal(404, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnNcxCuyoAsociadoEstaAnuladoSeRechaza()
    {
        var original = CrearComprobante(estado: EstadoComprobante.Anulado);

        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-1, original.Id, original));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnNcxCuyoAsociadoEsDeOtroPuntoDeVentaSeRechaza()
    {
        var original = CrearComprobante(idPuntoVenta: 1, idCliente: 10);

        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-1, original.Id, original, idPuntoVenta: 2));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnNcxCuyoAsociadoEsDeOtroClienteSeRechaza()
    {
        var original = CrearComprobante(idPuntoVenta: 1, idCliente: 10);

        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-1, original.Id, original, idCliente: 99));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
    }

    [Theory]
    [InlineData("NCX")]
    [InlineData("RC")]
    [InlineData("TXR")]
    [InlineData("FA")]
    [InlineData(null)]
    public void UnNcxCuyoAsociadoNoEsUnTicketDeVentaSeRechaza(string? codigoTipo)
    {
        var original = CrearComprobante();

        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-1, original.Id, original, codigoTipo: codigoTipo));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnNcxCuyoAsociadoEsUnEspejoFiscalSeRechazaParaNoDevolverDosVeces()
    {
        var espejo = CrearComprobante();
        espejo.IdVentaOrigen = 77;

        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-1, espejo.Id, espejo));
        Assert.Equal("comprobante_asociado_es_espejo_fiscal", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    // ---- ValidarAnulacionDeVenta: un espejo no se anula por la vía de ventas -----------------

    [Fact]
    public void UnEspejoFiscalNoSeAnulaPorLaViaDeVentas()
    {
        var espejo = CrearComprobante();
        espejo.IdVentaOrigen = 77;

        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeComprobantes.ValidarAnulacionDeVenta(espejo));
        Assert.Equal("comprobante_es_espejo_fiscal", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnTicketSeAnulaPorLaViaDeVentas()
    {
        ReglaDeComprobantes.ValidarAnulacionDeVenta(CrearComprobante());
    }

    // ---- ResolverEfectoFiscalDeAnulacion: la anulación de una venta facturada emite la nota de crédito ----------

    [Fact]
    public void UnaVentaSinEspejoDeFacturaSeAnulaSinEfectoFiscal()
    {
        Assert.Equal(EfectoFiscalDeAnulacion.Ninguno, ReglaDeComprobantes.ResolverEfectoFiscalDeAnulacion(null));
    }

    [Fact]
    public void UnaFacturaRechazadaSeLiquidaAlAnular()
    {
        Assert.Equal(
            EfectoFiscalDeAnulacion.LiquidarFacturaRechazada,
            ReglaDeComprobantes.ResolverEfectoFiscalDeAnulacion(ResultadoFiscal.Rechazado));
    }

    [Theory]
    [InlineData(ResultadoFiscal.Aprobado)]
    [InlineData(ResultadoFiscal.AprobadoConObservaciones)]
    public void UnaFacturaAprobadaEmiteLaNotaDeCredito(ResultadoFiscal resultado)
    {
        Assert.Equal(
            EfectoFiscalDeAnulacion.EmitirNotaDeCredito, ReglaDeComprobantes.ResolverEfectoFiscalDeAnulacion(resultado));
    }

    [Fact]
    public void UnaFacturaEnTramiteImpideAnular()
    {
        var excepcion = Assert.Throws<ErrorDominio>(
            () => ReglaDeComprobantes.ResolverEfectoFiscalDeAnulacion(ResultadoFiscal.Pendiente));
        Assert.Equal("venta_con_factura_en_tramite", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }
}
