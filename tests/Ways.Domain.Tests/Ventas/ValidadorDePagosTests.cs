using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// stage-5-pos-ventas, Slice 3 (task 3.14, design decisión 5, design: Checkout Contract — orden
/// de rechazo pineado 1-8; spec: comprobantes-venta / Payment Validation Rejection Order,
/// Cuenta Corriente Payment Gating; consumo-cuenta-corriente / Credit-Limit Evaluation) — pura,
/// sin base de datos, mismo criterio que <see cref="Ofertas.ReglaDeOfertasTests"/>.
/// </summary>
public class ValidadorDePagosTests
{
    private static PagoAValidar Efectivo(decimal importe, decimal vuelto = 0m) =>
        new(1, ComportamientoMedioPago.Efectivo, AdmiteVuelto: true, RequiereReferencia: false, importe, vuelto, null);

    private static PagoAValidar Tarjeta(decimal importe, decimal vuelto = 0m, bool requiereReferencia = false, string? referencia = null) =>
        new(2, ComportamientoMedioPago.Electronico, AdmiteVuelto: false, requiereReferencia, importe, vuelto, referencia);

    private static PagoAValidar CuentaCorriente(decimal importe) =>
        new(3, ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto: false, RequiereReferencia: false, importe, 0m, null);

    private static void Validar(
        decimal total,
        IReadOnlyList<PagoAValidar> pagos,
        decimal tolerancia = 0m,
        decimal vueltoMaximo = 0m,
        bool esConsumidorFinal = false,
        decimal saldo = 0m,
        decimal limiteCredito = 0m,
        bool creditoIlimitado = false) =>
        ValidadorDePagos.Validar(total, pagos, tolerancia, vueltoMaximo, esConsumidorFinal, saldo, limiteCredito, creditoIlimitado);

    // ---- 0: pago_importe_negativo -----------------------------------------------------------

    [Fact]
    public void UnPagoDeCuentaCorrienteNegativoQueCompensaOtroPagoSeRechaza()
    {
        // El exploit: {Efectivo, 150}, {CuentaCorriente, -50} sobre un total de 100 -> Σ importe
        // da 100 (pasaría la regla 2) y consumoCuentaCorriente da -50 (nunca dispara las reglas
        // 5/6, que exigen "> 0m"). Sin la regla 0 esto se aceptaba.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Efectivo(150m), CuentaCorriente(-50m)], esConsumidorFinal: true));
        Assert.Equal("pago_importe_negativo", excepcion.Codigo);
    }

    [Fact]
    public void UnSoloPagoEnEfectivoNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, [Efectivo(-50m)]));
        Assert.Equal("pago_importe_negativo", excepcion.Codigo);
    }

    [Fact]
    public void UnPagoConImporteExactamenteCeroNoDisparaLaRegla0()
    {
        // Boundary: 0 no es negativo, así que la regla 0 lo deja pasar — no tiene significado
        // propio de negocio (ni resta ni suma), así que queda como no-op frente al resto de las
        // reglas (mismo comportamiento que si no se hubiera incluido en la lista); no se lo
        // rechaza de forma explícita para no reñir con la regla 1 (que sí lo cubre cuando es el
        // ÚNICO pago) ni con la 5/6 (que ya lo tratan como "sin consumo" al no ser > 0m).
        Validar(100m, [Efectivo(100m), CuentaCorriente(0m)], esConsumidorFinal: true);
    }

    // ---- 1: pago_no_ingresado -------------------------------------------------------------

    [Fact]
    public void TodosLosMediosEnCeroConTotalPositivoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, [Efectivo(0m)]));
        Assert.Equal("pago_no_ingresado", excepcion.Codigo);
    }

    [Fact]
    public void SinPagosConTotalPositivoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, []));
        Assert.Equal("pago_no_ingresado", excepcion.Codigo);
    }

    [Fact]
    public void SinPagosConTotalCeroNoDisparaPagoNoIngresado()
    {
        // total <= 0 no dispara la regla 1 — igual puede rechazarse por otra regla, pero no ésta.
        var excepcion = Record.Exception(() => Validar(0m, []));
        Assert.True(excepcion is null || ((ErrorDominio)excepcion).Codigo != "pago_no_ingresado");
    }

    // ---- 2: tolerancia_de_pago_superada (spec: within/below tolerancia) -------------------

    [Fact]
    public void PagoDentroDeLaToleranciaEsAceptado()
    {
        // tolerancia_pago = 10, total = 100, pago efectivo = 95 -> 95 + 10 >= 100.
        Validar(100m, [Efectivo(95m)], tolerancia: 10m, vueltoMaximo: 20m);
    }

    [Fact]
    public void PagoPorDebajoDeLaToleranciaSeRechaza()
    {
        // tolerancia_pago = 10, total = 100, pago efectivo = 85 -> 85 + 10 < 100.
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, [Efectivo(85m)], tolerancia: 10m));
        Assert.Equal("tolerancia_de_pago_superada", excepcion.Codigo);
    }

    [Fact]
    public void PagoExactoEnElLimiteDeLaToleranciaEsAceptado()
    {
        // 90 + 10 == 100 -> límite inclusive.
        Validar(100m, [Efectivo(90m)], tolerancia: 10m);
    }

    // ---- 3: vuelto_excedido (spec: vuelto over the parametrized maximum) ------------------

    [Fact]
    public void VueltoSobreElMaximoParametrizadoSeRechaza()
    {
        // vuelto_maximo = 20, total = 50, pago efectivo = 75 (vuelto 25) -> 25 > 20.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(50m, [Efectivo(75m, vuelto: 25m)], vueltoMaximo: 20m));
        Assert.Equal("vuelto_excedido", excepcion.Codigo);
    }

    [Fact]
    public void VueltoExactoEnElMaximoEsAceptado()
    {
        Validar(50m, [Efectivo(70m, vuelto: 20m)], vueltoMaximo: 20m);
    }

    [Fact]
    public void ToleranciaYVueltoMaximoResuelvenPorPuntoDeVenta()
    {
        // La resolución punto de venta > empresa > default es responsabilidad de
        // ServicioDeParametros (Slice 4) — acá solo se prueba que el validador acepta
        // vuelto_maximo = 30 (el valor YA resuelto) donde 20 (el default) hubiera rechazado.
        Validar(50m, [Efectivo(75m, vuelto: 25m)], vueltoMaximo: 30m);
    }

    // ---- 4: medio_no_admite_vuelto ---------------------------------------------------------

    [Fact]
    public void VueltoRechazadoSobreUnMedioSinAdmiteVuelto()
    {
        // Tarjeta (AdmiteVuelto = false) paga 120 contra un total de 100 (vuelto 20).
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Tarjeta(120m, vuelto: 20m)], vueltoMaximo: 50m));
        Assert.Equal("medio_no_admite_vuelto", excepcion.Codigo);
    }

    [Fact]
    public void SinVueltoSobreUnMedioSinAdmiteVueltoNoSeRechazaPorEstaRegla()
    {
        var excepcion = Record.Exception(() => Validar(100m, [Tarjeta(100m)]));
        Assert.True(excepcion is null || ((ErrorDominio)excepcion).Codigo != "medio_no_admite_vuelto");
    }

    // ---- 5: cuenta_corriente_no_permitida (CF gating) --------------------------------------

    [Fact]
    public void ConsumidorFinalNoPuedePagarPorCuentaCorriente()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [CuentaCorriente(100m)], esConsumidorFinal: true, limiteCredito: 1000m, creditoIlimitado: true));
        Assert.Equal("cuenta_corriente_no_permitida", excepcion.Codigo);
    }

    [Fact]
    public void ConsumidorFinalConCreditoIlimitadoSigueBloqueado()
    {
        // El gating de CF corta ANTES de evaluar CreditoIlimitado — nunca lo bypasea.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [CuentaCorriente(100m)], esConsumidorFinal: true, creditoIlimitado: true));
        Assert.Equal("cuenta_corriente_no_permitida", excepcion.Codigo);
    }

    // ---- 6: limite_credito_excedido (spec: consumo-cuenta-corriente / Credit-Limit) -------

    [Fact]
    public void LimiteCreditoExcedidoSeRechaza()
    {
        // saldo = 800, limite = 1000, consumo = 300 -> 1100 > 1000.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(300m, [CuentaCorriente(300m)], saldo: 800m, limiteCredito: 1000m));
        Assert.Equal("limite_credito_excedido", excepcion.Codigo);
    }

    [Fact]
    public void LimiteExactoEsAceptado()
    {
        // saldo = 700, limite = 1000, consumo = 300 -> 1000 == 1000, inclusive.
        Validar(300m, [CuentaCorriente(300m)], saldo: 700m, limiteCredito: 1000m);
    }

    [Fact]
    public void UnPesoSobreElLimiteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(300.01m, [CuentaCorriente(300.01m)], saldo: 700m, limiteCredito: 1000m));
        Assert.Equal("limite_credito_excedido", excepcion.Codigo);
    }

    [Fact]
    public void CreditoIlimitadoBypaseaElLimite()
    {
        // saldo = 5000, limite = 1000, credito_ilimitado = true, consumo = 2000.
        Validar(2000m, [CuentaCorriente(2000m)], saldo: 5000m, limiteCredito: 1000m, creditoIlimitado: true);
    }

    // ---- 7: referencia_de_pago_requerida ---------------------------------------------------

    [Fact]
    public void ReferenciaRequeridaYFaltanteSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Tarjeta(100m, requiereReferencia: true, referencia: null)]));
        Assert.Equal("referencia_de_pago_requerida", excepcion.Codigo);
    }

    [Fact]
    public void ReferenciaVaciaOEnBlancoSeConsideraFaltante()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Tarjeta(100m, requiereReferencia: true, referencia: "   ")]));
        Assert.Equal("referencia_de_pago_requerida", excepcion.Codigo);
    }

    [Fact]
    public void ReferenciaProvistaEsAceptada()
    {
        Validar(100m, [Tarjeta(100m, requiereReferencia: true, referencia: "CUPON-123")]);
    }

    // ---- 8: vuelto_invalido (Σ vuelto > max(0, Σ importe - total)) -------------------------

    [Fact]
    public void VueltoQueNoCoincideConLoQueSobraDelPagoSeRechaza()
    {
        // Importe 100 contra total 100 (nada sobra), vuelto declarado 5 -> invalido, aunque
        // esté por debajo de vuelto_maximo.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Efectivo(100m, vuelto: 5m)], vueltoMaximo: 50m));
        Assert.Equal("vuelto_invalido", excepcion.Codigo);
    }

    [Fact]
    public void VueltoQueCoincideConLoQueSobraEsAceptado()
    {
        Validar(100m, [Efectivo(120m, vuelto: 20m)], vueltoMaximo: 50m);
    }

    // ---- Orden de rechazo observable --------------------------------------------------------

    [Fact]
    public void UnPagoQueViolaLasReglas2Y6ReportaLaRegla2()
    {
        // Regla 2 (tolerancia): pago CC de 100 contra un total de 1000, sin tolerancia ->
        // 100 + 0 < 1000, ya rechaza acá.
        // Regla 6 (límite): si se llegara a evaluar, saldo 950 + consumo 100 = 1050 > 1000
        // también violaría el límite de crédito.
        // El resultado tiene que ser el código de la regla 2, nunca el de la 6.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(1000m, [CuentaCorriente(100m)], saldo: 950m, limiteCredito: 1000m));
        Assert.Equal("tolerancia_de_pago_superada", excepcion.Codigo);
    }

    [Fact]
    public void UnPagoQueViolaLasReglas3Y7ReportaLaRegla3()
    {
        // Regla 3 (vuelto excedido): vuelto 30 > vuelto_maximo 20.
        // Regla 7 (referencia): además falta la referencia de un medio que la requiere.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(70m, [Tarjeta(100m, vuelto: 30m, requiereReferencia: true, referencia: null)], vueltoMaximo: 20m));
        Assert.Equal("vuelto_excedido", excepcion.Codigo);
    }

    [Fact]
    public void UnaMezclaValidaConVariosMediosEsAceptada()
    {
        Validar(
            150m,
            [Efectivo(50m), Tarjeta(100m, requiereReferencia: true, referencia: "OP-1")],
            tolerancia: 0m,
            vueltoMaximo: 0m);
    }
}
