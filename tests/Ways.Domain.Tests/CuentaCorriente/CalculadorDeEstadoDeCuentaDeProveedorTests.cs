using Ways.Domain.CuentaCorriente;

namespace Ways.Domain.Tests.CuentaCorriente;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 4 (task 4.1, design.md:131-134) — pura, sin base de
/// datos. Espeja <c>CalculadorDeEstadoDeCuentaTests</c> (cliente, stage 7).
/// </summary>
public class CalculadorDeEstadoDeCuentaDeProveedorTests
{
    [Fact]
    public void UnAjusteSinComprobanteDeCompraSeEtiquetaComoManual()
    {
        Assert.Equal(
            EtiquetaDeAjuste.Manual, CalculadorDeEstadoDeCuentaDeProveedor.EtiquetarAjuste(idComprobanteCompra: null));
    }

    [Fact]
    public void UnAjusteConComprobanteDeCompraSeEtiquetaComoContramovimientoDeAnulacion()
    {
        Assert.Equal(
            EtiquetaDeAjuste.AnulacionContramovimiento,
            CalculadorDeEstadoDeCuentaDeProveedor.EtiquetarAjuste(idComprobanteCompra: 77));
    }
}
