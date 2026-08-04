using Ways.Domain.Common;
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

    // ---- ValidarComprobanteAsociado: opcional, solo NCX, mismo punto de venta/cliente -------

    [Fact]
    public void SinComprobanteAsociadoEsSiempreValidoSinImportarElSigno()
    {
        // Escenario: devolución standalone sin comprobante original (spec: Standalone
        // devolución without an original) — id_comprobante_asociado NULL, no dispara nada.
        ReglaDeComprobantes.ValidarComprobanteAsociado(-1, null, null, idPuntoVenta: 1, idCliente: 10);
        ReglaDeComprobantes.ValidarComprobanteAsociado(1, null, null, idPuntoVenta: 1, idCliente: 10);
    }

    [Fact]
    public void UnTxNoPuedeTenerComprobanteAsociado()
    {
        var original = CrearComprobante();
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarComprobanteAsociado(1, original.Id, original, idPuntoVenta: 1, idCliente: 10));
        Assert.Equal("comprobante_asociado_no_permitido", excepcion.Codigo);
    }

    [Fact]
    public void UnNcxQueReferenciaUnTxEmitidoDelMismoPuntoDeVentaYClienteEsValido()
    {
        // spec: Devolución referencing an original — id_comprobante_asociado = 501.
        var original = CrearComprobante(id: 501, idPuntoVenta: 1, idCliente: 10);

        ReglaDeComprobantes.ValidarComprobanteAsociado(-1, original.Id, original, idPuntoVenta: 1, idCliente: 10);
    }

    [Fact]
    public void UnNcxCuyoAsociadoNoExisteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarComprobanteAsociado(-1, 999, null, idPuntoVenta: 1, idCliente: 10));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
        Assert.Equal(404, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnNcxCuyoAsociadoEstaAnuladoSeRechaza()
    {
        var original = CrearComprobante(estado: EstadoComprobante.Anulado);

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarComprobanteAsociado(-1, original.Id, original, idPuntoVenta: 1, idCliente: 10));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnNcxCuyoAsociadoEsDeOtroPuntoDeVentaSeRechaza()
    {
        var original = CrearComprobante(idPuntoVenta: 1, idCliente: 10);

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarComprobanteAsociado(-1, original.Id, original, idPuntoVenta: 2, idCliente: 10));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnNcxCuyoAsociadoEsDeOtroClienteSeRechaza()
    {
        var original = CrearComprobante(idPuntoVenta: 1, idCliente: 10);

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeComprobantes.ValidarComprobanteAsociado(-1, original.Id, original, idPuntoVenta: 1, idCliente: 99));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
    }
}
