using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

public class ReglaDeDevolucionesTests
{
    [Fact]
    public void UnTicketEmitidoSinFacturaSePuedeDevolver()
    {
        Assert.Null(ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [null, null]));
        ReglaDeDevoluciones.ExigirDevolvible(EstadoComprobante.Emitido, [null, null]);
    }

    [Theory]
    [InlineData(ResultadoFiscal.Aprobado)]
    [InlineData(ResultadoFiscal.AprobadoConObservaciones)]
    [InlineData(ResultadoFiscal.Pendiente)]
    public void UnTicketConFacturaAprobadaOEnTramiteNoSePuedeDevolverHastaQueExistaLaNotaDeCreditoFiscal(ResultadoFiscal factura)
    {
        Assert.Equal(MotivoDeNoDevolvible.ConFacturaFiscal, ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [null, factura]));

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeDevoluciones.ExigirDevolvible(EstadoComprobante.Emitido, [null, factura]));
        Assert.Equal("devolucion_requiere_nota_de_credito_fiscal", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnaFacturaRechazadaNoBloqueaPorqueArcaLaLibera()
    {
        Assert.Null(ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [null, ResultadoFiscal.Rechazado]));
    }

    [Fact]
    public void ElResultadoFiscalDelPropioTicketTambienBloquea()
    {
        Assert.Equal(
            MotivoDeNoDevolvible.ConFacturaFiscal,
            ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Emitido, [ResultadoFiscal.Aprobado, null]));
    }

    [Fact]
    public void UnTicketAnuladoNoSeDevuelveAunqueTengaFactura()
    {
        Assert.Equal(
            MotivoDeNoDevolvible.Anulado,
            ReglaDeDevoluciones.Evaluar("TX", EstadoComprobante.Anulado, [null, ResultadoFiscal.Aprobado]));

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeDevoluciones.ExigirDevolvible(EstadoComprobante.Anulado, [null, null]));
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
        Assert.Equal(MotivoDeNoDevolvible.NoEsUnTicketDeVenta, ReglaDeDevoluciones.Evaluar(codigo, EstadoComprobante.Emitido, [null]));
    }
}
