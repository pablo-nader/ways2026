using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.CuentaCorriente;

/// <summary>
/// stage-7-cuenta-corriente, Slice 2 (task 2.8, design decisión 6, pinned: "sibling class" —
/// no una rama de <see cref="ValidadorDePagos"/>; spec: pagos-a-cuenta) — pura, sin base de
/// datos, mismo criterio que <c>Ways.Domain.Tests.Ventas.ValidadorDePagosTests</c>.
/// </summary>
public class ValidadorDePagoACuentaTests
{
    private static PagoAValidar Efectivo(decimal importe, decimal vuelto = 0m) =>
        new(1, ComportamientoMedioPago.Efectivo, AdmiteVuelto: true, RequiereReferencia: false, importe, vuelto, null);

    private static PagoAValidar Tarjeta(decimal importe, decimal vuelto = 0m, bool requiereReferencia = false, string? referencia = null) =>
        new(2, ComportamientoMedioPago.Electronico, AdmiteVuelto: false, requiereReferencia, importe, vuelto, referencia);

    private static PagoAValidar CuentaCorriente(decimal importe) =>
        new(3, ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto: false, RequiereReferencia: false, importe, 0m, null);

    // ---- 1: pago_importe_negativo -----------------------------------------------------------

    [Fact]
    public void UnPagoConImporteNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(-50m)], vueltoMaximo: 0m));
        Assert.Equal("pago_importe_negativo", excepcion.Codigo);
    }

    // ---- 2: vuelto_negativo -------------------------------------------------------------------

    [Fact]
    public void UnPagoConVueltoNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(100m, vuelto: -1m)], vueltoMaximo: 50m));
        Assert.Equal("vuelto_negativo", excepcion.Codigo);
    }

    // ---- 3: pago_a_cuenta_sin_medios_fisicos (spec: RC Forbids Cuenta Corriente Medios) -------

    [Fact]
    public void UnPagoConMedioCuentaCorrienteSeRechaza()
    {
        // A diferencia de ValidadorDePagos (regla 5, solo bloquea CC para Consumidor Final), acá
        // CC está prohibido sin importar el cliente — una deuda no puede pagar otra deuda.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([CuentaCorriente(100m)], vueltoMaximo: 0m));
        Assert.Equal("pago_a_cuenta_sin_medios_fisicos", excepcion.Codigo);
    }

    [Fact]
    public void UnaMezclaConUnMedioFisicoYUnoDeCuentaCorrienteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(100m), CuentaCorriente(50m)], vueltoMaximo: 0m));
        Assert.Equal("pago_a_cuenta_sin_medios_fisicos", excepcion.Codigo);
    }

    // ---- 4: medio_no_admite_vuelto -------------------------------------------------------------

    [Fact]
    public void VueltoRechazadoSobreUnMedioSinAdmiteVuelto()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Tarjeta(120m, vuelto: 20m)], vueltoMaximo: 50m));
        Assert.Equal("medio_no_admite_vuelto", excepcion.Codigo);
    }

    [Fact]
    public void SinVueltoSobreUnMedioSinAdmiteVueltoNoSeRechazaPorEstaRegla()
    {
        var excepcion = Record.Exception(() => ValidadorDePagoACuenta.Validar([Tarjeta(100m)], vueltoMaximo: 0m));
        Assert.True(excepcion is null || ((ErrorDominio)excepcion).Codigo != "medio_no_admite_vuelto");
    }

    // ---- 5: vuelto_excedido ---------------------------------------------------------------------

    [Fact]
    public void VueltoSobreElMaximoParametrizadoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(120m, vuelto: 25m)], vueltoMaximo: 20m));
        Assert.Equal("vuelto_excedido", excepcion.Codigo);
    }

    [Fact]
    public void VueltoExactoEnElMaximoEsAceptado()
    {
        var importeAplicado = ValidadorDePagoACuenta.Validar([Efectivo(120m, vuelto: 20m)], vueltoMaximo: 20m);
        Assert.Equal(100m, importeAplicado);
    }

    // ---- 6: referencia_de_pago_requerida ---------------------------------------------------

    [Fact]
    public void ReferenciaRequeridaYFaltanteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Tarjeta(100m, requiereReferencia: true, referencia: null)], vueltoMaximo: 0m));
        Assert.Equal("referencia_de_pago_requerida", excepcion.Codigo);
    }

    [Fact]
    public void ReferenciaVaciaOEnBlancoSeConsideraFaltante()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Tarjeta(100m, requiereReferencia: true, referencia: "   ")], vueltoMaximo: 0m));
        Assert.Equal("referencia_de_pago_requerida", excepcion.Codigo);
    }

    [Fact]
    public void ReferenciaProvistaEsAceptada()
    {
        var importeAplicado = ValidadorDePagoACuenta.Validar(
            [Tarjeta(100m, requiereReferencia: true, referencia: "CUPON-123")], vueltoMaximo: 0m);
        Assert.Equal(100m, importeAplicado);
    }

    // ---- 7: pago_a_cuenta_sin_importe (spec: derivación importeAplicado) ---------------------

    [Fact]
    public void SinPagosSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ValidadorDePagoACuenta.Validar([], vueltoMaximo: 0m));
        Assert.Equal("pago_a_cuenta_sin_importe", excepcion.Codigo);
    }

    [Fact]
    public void ImporteAplicadoCeroPorVueltoIgualAlImporteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(100m, vuelto: 100m)], vueltoMaximo: 100m));
        Assert.Equal("pago_a_cuenta_sin_importe", excepcion.Codigo);
    }

    // ---- Derivación de importeAplicado (design decisión 6) -----------------------------------

    [Fact]
    public void ImporteAplicadoEsLaSumaDeImportesMenosLaSumaDeVueltos()
    {
        var importeAplicado = ValidadorDePagoACuenta.Validar(
            [Efectivo(150m, vuelto: 10m), Tarjeta(50m)], vueltoMaximo: 20m);
        Assert.Equal(190m, importeAplicado);
    }

    // ---- Orden de rechazo observable ----------------------------------------------------------

    [Fact]
    public void UnPagoQueViolaLasReglas3Y5ReportaLaRegla3()
    {
        // Regla 3 (medio físico): un medio de cuenta corriente, prohibido de por sí.
        // Regla 5 (vuelto máximo): si se llegara a evaluar, el vuelto igual excedería el máximo.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([CuentaCorriente(100m), Efectivo(50m, vuelto: 999m)], vueltoMaximo: 20m));
        Assert.Equal("pago_a_cuenta_sin_medios_fisicos", excepcion.Codigo);
    }

    [Fact]
    public void UnaMezclaValidaConVariosMediosFisicosEsAceptada()
    {
        var importeAplicado = ValidadorDePagoACuenta.Validar(
            [Efectivo(100m), Tarjeta(50m, requiereReferencia: true, referencia: "OP-1")], vueltoMaximo: 0m);
        Assert.Equal(150m, importeAplicado);
    }
}
