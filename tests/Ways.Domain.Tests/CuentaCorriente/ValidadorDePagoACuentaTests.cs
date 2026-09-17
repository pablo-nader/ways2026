using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.CuentaCorriente;

/// <summary>
/// stage-7-cuenta-corriente, Slice 2 (task 2.8, design decisión 6, pinned: "sibling class" —
/// no una rama de <see cref="ValidadorDePagos"/>; spec: pagos-a-cuenta) — pura, sin base de
/// datos, mismo criterio que <c>Ways.Domain.Tests.Ventas.ValidadorDePagosTests</c>. La regla 5
/// (decisión del dueño, 2026-09-16) usa la misma regla de billetes que
/// <see cref="ValidadorDePagos"/> regla 3 — ver <c>ValidadorDePagosTests</c> para el catálogo
/// completo de casos de <see cref="BilletesArgentinos.EsVueltoJustificado"/>.
/// </summary>
public class ValidadorDePagoACuentaTests
{
    private static PagoAValidar Efectivo(decimal importe, decimal vuelto = 0m) =>
        new(1, ComportamientoMedioPago.Efectivo, AdmiteVuelto: true, RequiereReferencia: false, importe, vuelto, null);

    private static PagoAValidar Tarjeta(decimal importe, decimal vuelto = 0m, bool requiereReferencia = false, string? referencia = null) =>
        new(2, ComportamientoMedioPago.Electronico, AdmiteVuelto: false, requiereReferencia, importe, vuelto, referencia);

    private static PagoAValidar CuentaCorriente(decimal importe) =>
        new(3, ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto: false, RequiereReferencia: false, importe, 0m, null);

    /// <summary>Simula una configuración de catálogo atípica pero posible: `AdmiteVuelto` es un
    /// flag por medio (ABM), no atado a `Comportamiento` — mismo helper que
    /// <c>ValidadorDePagosTests</c>. La regla 5 tiene que seguir ignorando su importe como
    /// "billetes" aunque este flag esté prendido (solo billetes físicos reales cuentan); la
    /// regla 4 sí lo trata como cualquier medio con `AdmiteVuelto = true`.</summary>
    private static PagoAValidar TransferenciaQueAdmiteVuelto(decimal importe, decimal vuelto = 0m) =>
        new(5, ComportamientoMedioPago.Electronico, AdmiteVuelto: true, RequiereReferencia: false, importe, vuelto, null);

    // ---- 1: pago_importe_negativo -----------------------------------------------------------

    [Fact]
    public void UnPagoConImporteNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(-50m)]));
        Assert.Equal("pago_importe_negativo", excepcion.Codigo);
    }

    // ---- 2: vuelto_negativo -------------------------------------------------------------------

    [Fact]
    public void UnPagoConVueltoNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(100m, vuelto: -1m)]));
        Assert.Equal("vuelto_negativo", excepcion.Codigo);
    }

    // ---- 3: pago_a_cuenta_sin_medios_fisicos (spec: RC Forbids Cuenta Corriente Medios) -------

    [Fact]
    public void UnPagoConMedioCuentaCorrienteSeRechaza()
    {
        // A diferencia de ValidadorDePagos (regla 5, solo bloquea CC para Consumidor Final), acá
        // CC está prohibido sin importar el cliente — una deuda no puede pagar otra deuda.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([CuentaCorriente(100m)]));
        Assert.Equal("pago_a_cuenta_sin_medios_fisicos", excepcion.Codigo);
    }

    [Fact]
    public void UnaMezclaConUnMedioFisicoYUnoDeCuentaCorrienteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(100m), CuentaCorriente(50m)]));
        Assert.Equal("pago_a_cuenta_sin_medios_fisicos", excepcion.Codigo);
    }

    // ---- 4: medio_no_admite_vuelto -------------------------------------------------------------

    [Fact]
    public void VueltoRechazadoSobreUnMedioSinAdmiteVuelto()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Tarjeta(120m, vuelto: 20m)]));
        Assert.Equal("medio_no_admite_vuelto", excepcion.Codigo);
    }

    [Fact]
    public void SinVueltoSobreUnMedioSinAdmiteVueltoNoSeRechazaPorEstaRegla()
    {
        var excepcion = Record.Exception(() => ValidadorDePagoACuenta.Validar([Tarjeta(100m)]));
        Assert.True(excepcion is null || ((ErrorDominio)excepcion).Codigo != "medio_no_admite_vuelto");
    }

    // ---- 5: vuelto_no_justificado (decisión del dueño, 2026-09-16 — reemplaza el vuelto_maximo
    // fijo/parametrizado: mismo criterio que la regla 3 de ValidadorDePagos, "efectivo entregado"
    // es Σ importe de los pagos cuyo Comportamiento es Efectivo) --------------------------------

    [Fact]
    public void ElEjemploDelDuenioTicket5500ConUnBilleteDe10000EsAceptado()
    {
        // Total 5500, entrega 10000 -> vuelto 4500. Un solo billete de 10000 (> 4500) alcanza.
        // El legacy/vuelto_maximo rechazaba esto porque 4500 > 20 — el motivo original del
        // reclamo del dueño (mismo ejemplo que ValidadorDePagosTests).
        var importeAplicado = ValidadorDePagoACuenta.Validar([Efectivo(10000m, vuelto: 4500m)]);
        Assert.Equal(5500m, importeAplicado);
    }

    [Fact]
    public void ElEjemploDelDuenioTicket5500ConVueltoDe20NoFormableSeRechaza()
    {
        // Total 5500, entrega 5520 -> vuelto 20. Todo billete estrictamente mayor a 20 es
        // múltiplo de 50 -> solo arman múltiplos de 50; 5520 no lo es.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(5520m, vuelto: 20m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void ElEjemploDelDuenioTicket5500ConVueltoDe24500SinBilleteSuficienteSeRechaza()
    {
        // Total 5500, entrega 30000 -> vuelto 24500. Ningún billete (máximo 20000) es > 24500.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(30000m, vuelto: 24500m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void ElImporteDeUnMedioElectronicoQueAdmiteVueltoNoCuentaComoEfectivoParaLaRegla5()
    {
        // Un medio Electronico con AdmiteVuelto=true (config atípica de catálogo) sigue sin
        // contar como "billetes" para esta regla — mismo criterio que ValidadorDePagos regla 3.
        // Con el único pago Electronico, el efectivo entregado es 0, así que un vuelto de 4500
        // nunca puede justificarse (nadie entregó billetes físicos).
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([TransferenciaQueAdmiteVuelto(10000m, vuelto: 4500m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void VueltoIgualAlImporteEntregadoSeRechazaPorLaRegla5AntesDeLlegarALaRegla7()
    {
        // Antes (vuelto_maximo): vuelto == importe pasaba la regla 5 (100 <= 100) y llegaba a la
        // regla 7 (importeAplicado == 0) como pago_a_cuenta_sin_importe. Con la regla de
        // billetes, ningún billete estrictamente mayor a 100 arma exactamente 100 -> se rechaza
        // antes, en la regla 5 — el orden observable cambió con la regla.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Efectivo(100m, vuelto: 100m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    // ---- 6: referencia_de_pago_requerida ---------------------------------------------------

    [Fact]
    public void ReferenciaRequeridaYFaltanteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Tarjeta(100m, requiereReferencia: true, referencia: null)]));
        Assert.Equal("referencia_de_pago_requerida", excepcion.Codigo);
    }

    [Fact]
    public void ReferenciaVaciaOEnBlancoSeConsideraFaltante()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([Tarjeta(100m, requiereReferencia: true, referencia: "   ")]));
        Assert.Equal("referencia_de_pago_requerida", excepcion.Codigo);
    }

    [Fact]
    public void ReferenciaProvistaEsAceptada()
    {
        var importeAplicado = ValidadorDePagoACuenta.Validar(
            [Tarjeta(100m, requiereReferencia: true, referencia: "CUPON-123")]);
        Assert.Equal(100m, importeAplicado);
    }

    // ---- 7: pago_a_cuenta_sin_importe (spec: derivación importeAplicado) ---------------------

    [Fact]
    public void SinPagosSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ValidadorDePagoACuenta.Validar([]));
        Assert.Equal("pago_a_cuenta_sin_importe", excepcion.Codigo);
    }

    [Fact]
    public void ImporteAplicadoCeroSinVueltoSeRechaza()
    {
        // Aislado de la regla 5 a propósito (vuelto = 0 siempre justificado) — cubre
        // exclusivamente la derivación importeAplicado <= 0.
        var excepcion = Assert.Throws<ErrorDominio>(() => ValidadorDePagoACuenta.Validar([Tarjeta(0m)]));
        Assert.Equal("pago_a_cuenta_sin_importe", excepcion.Codigo);
    }

    // ---- Derivación de importeAplicado (design decisión 6) -----------------------------------

    [Fact]
    public void ImporteAplicadoEsLaSumaDeImportesMenosLaSumaDeVueltos()
    {
        // Efectivo entregado 150, vuelto 10: formable (100 + 50, ambos > 10).
        var importeAplicado = ValidadorDePagoACuenta.Validar(
            [Efectivo(150m, vuelto: 10m), Tarjeta(50m)]);
        Assert.Equal(190m, importeAplicado);
    }

    // ---- Orden de rechazo observable ----------------------------------------------------------

    [Fact]
    public void UnPagoQueViolaLasReglas3Y5ReportaLaRegla3()
    {
        // Regla 3 (medio físico): un medio de cuenta corriente, prohibido de por sí.
        // Regla 5 (vuelto no justificado): si se llegara a evaluar, un vuelto de 999 tampoco
        // sería formable con los 50 de efectivo entregado.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ValidadorDePagoACuenta.Validar([CuentaCorriente(100m), Efectivo(50m, vuelto: 999m)]));
        Assert.Equal("pago_a_cuenta_sin_medios_fisicos", excepcion.Codigo);
    }

    [Fact]
    public void UnaMezclaValidaConVariosMediosFisicosEsAceptada()
    {
        var importeAplicado = ValidadorDePagoACuenta.Validar(
            [Efectivo(100m), Tarjeta(50m, requiereReferencia: true, referencia: "OP-1")]);
        Assert.Equal(150m, importeAplicado);
    }
}
