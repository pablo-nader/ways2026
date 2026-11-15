using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

public class ReglaDeDevolucionesTests
{
    [Fact]
    public void UnTicketEmitidoSinFacturaNiDecisionPendienteSePuedeDevolver()
    {
        Assert.Null(ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [null, null], false));
        ReglaDeDevoluciones.ExigirDevolvible(EstadoComprobante.Emitido, [null, null], false);
    }

    [Theory]
    [InlineData(ResultadoFiscal.Aprobado)]
    [InlineData(ResultadoFiscal.AprobadoConObservaciones)]
    [InlineData(ResultadoFiscal.Pendiente)]
    public void UnTicketConFacturaAprobadaOEnTramiteNoSePuedeDevolverHastaQueExistaLaNotaDeCreditoFiscal(ResultadoFiscal factura)
    {
        Assert.Equal(
            MotivoDeNoDevolvible.ConFacturaFiscal, ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [null, factura], false));

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeDevoluciones.ExigirDevolvible(EstadoComprobante.Emitido, [null, factura], false));
        Assert.Equal("devolucion_requiere_nota_de_credito_fiscal", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnTicketConLaDecisionDeFacturarPendienteNoSePuedeDevolverPorqueLaFacturaNaceConTodosSusItems()
    {
        Assert.Equal(
            MotivoDeNoDevolvible.ConFacturaFiscal, ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [null, null], true));

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeDevoluciones.ExigirDevolvible(EstadoComprobante.Emitido, [null, null], true));
        Assert.Equal("devolucion_requiere_nota_de_credito_fiscal", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnaFacturaRechazadaNoBloqueaPorqueArcaLaLibera()
    {
        Assert.Null(ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [null, ResultadoFiscal.Rechazado], false));
    }

    [Fact]
    public void ElResultadoFiscalDelPropioTicketTambienBloquea()
    {
        Assert.Equal(
            MotivoDeNoDevolvible.ConFacturaFiscal,
            ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [ResultadoFiscal.Aprobado, null], false));
    }

    [Fact]
    public void UnTicketAnuladoNoSeDevuelveAunqueTengaFacturaODecisionPendiente()
    {
        Assert.Equal(
            MotivoDeNoDevolvible.Anulado,
            ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Anulado, [null, ResultadoFiscal.Aprobado], true));

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeDevoluciones.ExigirDevolvible(EstadoComprobante.Anulado, [null, null], false));
        Assert.Equal("comprobante_asociado_invalido", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Theory]
    [InlineData("NCX")]
    [InlineData("RC")]
    [InlineData("TXR")]
    [InlineData("FA")]
    public void SoloUnTicketDeVentaSePuedeDevolver(string codigo)
    {
        Assert.Equal(
            MotivoDeNoDevolvible.NoEsUnTicketDeVenta, ReglaDeDevoluciones.Evaluar(codigo, EstadoComprobante.Emitido, [null], false));
    }
}
