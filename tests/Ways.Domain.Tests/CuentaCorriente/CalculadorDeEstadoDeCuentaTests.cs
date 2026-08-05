using Ways.Domain.CuentaCorriente;

namespace Ways.Domain.Tests.CuentaCorriente;

/// <summary>
/// stage-7-cuenta-corriente, Slice 4 (task 4.5, design decisión 9; spec: estado-de-cuenta / Header
/// Computes Disponibilidad Server-Side, ajustes-de-cuenta-corriente / Ajuste Is Distinct From The
/// Anulación Contramovimiento) — pura, sin base de datos.
/// </summary>
public class CalculadorDeEstadoDeCuentaTests
{
    // ---- Disponibilidad ------------------------------------------------------------------------

    [Fact]
    public void DisponibilidadParaUnClienteConCreditoLimitadoEsElAcuerdoMenosElSaldo()
    {
        var disponibilidad = CalculadorDeEstadoDeCuenta.CalcularDisponibilidad(
            saldo: 300m, limiteCredito: 1000m, creditoIlimitado: false);
        Assert.Equal(700m, disponibilidad);
    }

    [Fact]
    public void DisponibilidadEsNuloCuandoElCreditoEsIlimitado()
    {
        var disponibilidad = CalculadorDeEstadoDeCuenta.CalcularDisponibilidad(
            saldo: 300m, limiteCredito: 0m, creditoIlimitado: true);
        Assert.Null(disponibilidad);
    }

    [Fact]
    public void DisponibilidadPuedeSerNegativaCuandoElSaldoSuperaElLimite()
    {
        var disponibilidad = CalculadorDeEstadoDeCuenta.CalcularDisponibilidad(
            saldo: 1200m, limiteCredito: 1000m, creditoIlimitado: false);
        Assert.Equal(-200m, disponibilidad);
    }

    // ---- Etiqueta de ajuste (derivación estructural, sin columna nueva) ------------------------

    [Fact]
    public void UnAjusteSinComprobanteSeEtiquetaComoManual()
    {
        Assert.Equal(EtiquetaDeAjuste.Manual, CalculadorDeEstadoDeCuenta.EtiquetarAjuste(null));
    }

    [Fact]
    public void UnAjusteConComprobanteSeEtiquetaComoContramovimientoDeAnulacion()
    {
        Assert.Equal(
            EtiquetaDeAjuste.AnulacionContramovimiento, CalculadorDeEstadoDeCuenta.EtiquetarAjuste(idComprobanteVenta: 42));
    }
}
