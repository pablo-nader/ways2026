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
        bool esConsumidorFinal = false,
        decimal saldo = 0m,
        decimal limiteCredito = 0m,
        bool creditoIlimitado = false,
        bool exigirLimiteDeCredito = true,
        bool exigirCobroSinExcedente = true) =>
        ValidadorDePagos.Validar(
            total, pagos, tolerancia, esConsumidorFinal, saldo, limiteCredito, creditoIlimitado, exigirLimiteDeCredito,
            exigirCobroSinExcedente);

    private static PagoAValidar Qr(decimal importe) =>
        new(4, ComportamientoMedioPago.Electronico, AdmiteVuelto: false, RequiereReferencia: false, importe, 0m, null, "QR");

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

    // ---- 0b: vuelto_negativo ----------------------------------------------------------------

    [Fact]
    public void UnPagoConVueltoNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Efectivo(100m, vuelto: -1m)]));
        Assert.Equal("vuelto_negativo", excepcion.Codigo);
    }

    [Fact]
    public void UnPagoConVueltoExactamenteCeroNoDisparaLaRegla0b()
    {
        // Boundary: 0 no es negativo, así que la regla 0b lo deja pasar.
        Validar(100m, [Efectivo(100m, vuelto: 0m)]);
    }

    // ---- 0c: pago_en_comprobante_negativo -----------------------------------------------------

    [Fact]
    public void UnComprobanteConTotalNegativoConUnPagoPositivoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-100m, [Tarjeta(100m)]));
        Assert.Equal("pago_en_comprobante_negativo", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnComprobanteConTotalNegativoConVariosPagosSeRechazaAunqueSoloUnoSeaPositivo()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(-100m, [Efectivo(0m), Tarjeta(0m), CuentaCorriente(1m)], esConsumidorFinal: false));
        Assert.Equal("pago_en_comprobante_negativo", excepcion.Codigo);
    }

    [Fact]
    public void UnComprobanteConTotalNegativoConUnVueltoPositivoSeRechazaAunConImporteCero()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-100m, [Efectivo(0m, vuelto: 5m)]));
        Assert.Equal("pago_en_comprobante_negativo", excepcion.Codigo);
    }

    [Fact]
    public void UnComprobanteConTotalNegativoSoloConFilasEnCeroSeAcepta()
    {
        // Una fila en cero no mueve dinero (igual que en una venta común, donde no se rechaza por sí sola).
        Validar(-100m, [Efectivo(0m), Tarjeta(0m)]);
    }

    [Fact]
    public void UnComprobanteConTotalNegativoSinPagosSeAcepta()
    {
        Validar(-100m, []);
    }

    [Fact]
    public void UnComprobanteConTotalExactamenteCeroNoDisparaLaReglaDeTotalNegativo()
    {
        Validar(0m, [Efectivo(10m, vuelto: 10m)]);
    }

    [Fact]
    public void UnImporteNegativoEnUnComprobanteNegativoSigueDandoPagoImporteNegativo()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(-100m, [Efectivo(-5m), Tarjeta(10m)]));
        Assert.Equal("pago_importe_negativo", excepcion.Codigo);
    }

    [Fact]
    public void ConTotalPositivoUnPagoPositivoSigueSiendoLoNormal()
    {
        Validar(100m, [Efectivo(100m)]);
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, [Efectivo(0m)]));
        Assert.Equal("pago_no_ingresado", excepcion.Codigo);
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
        Validar(100m, [Efectivo(95m)], tolerancia: 10m);
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

    // ---- 3: vuelto_supera_el_pago (sin regla de billetes, decisión del dueño 2026-11-02) ----

    [Fact]
    public void UnVueltoMayorAlImporteDeSuFilaSeRechazaAunqueOtraFilaCubraElExcedente()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Efectivo(10m, vuelto: 40m), Tarjeta(130m)]));
        Assert.Equal("vuelto_supera_el_pago", excepcion.Codigo);
    }

    [Fact]
    public void UnVueltoIgualAlImporteDeSuFilaNoSeRechazaPorEstaRegla()
    {
        Validar(100m, [Efectivo(50m, vuelto: 50m), Tarjeta(100m)]);
    }

    [Fact]
    public void UnTicketDe7500PagadoCon10500EnEfectivoAceptaUnVueltoDe3000()
    {
        Validar(7500m, [Efectivo(10500m, vuelto: 3000m)]);
    }

    [Fact]
    public void UnVueltoQueNingunBilleteJustificariaEsAceptadoSiCoincideConElExcedente()
    {
        Validar(5500m, [Efectivo(30000m, vuelto: 24500m)]);
    }

    [Fact]
    public void UnEfectivoConCentavosAceptaUnVueltoQueCoincideConElExcedente()
    {
        Validar(5000m, [Efectivo(5500.50m, vuelto: 500.50m)]);
    }

    // ---- 4: medio_no_admite_vuelto ---------------------------------------------------------

    [Fact]
    public void VueltoRechazadoSobreUnMedioSinAdmiteVuelto()
    {
        // Efectivo (AdmiteVuelto = true) entrega 200 sin vuelto propio; Tarjeta (AdmiteVuelto =
        // false) declara un vuelto de 20 sobre un medio que no lo admite.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(300m, [Efectivo(200m), Tarjeta(120m, vuelto: 20m)]));
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

    // ---- 6 salteada: venta offline registrada sin validar el límite ---------------------------

    /// <summary>La cláusula: <c>exigirLimiteDeCredito &amp;&amp;</c> en la regla 6. Mismo pago que
    /// <see cref="LimiteCreditoExcedidoSeRechaza"/>, que con el límite exigido se rechaza.</summary>
    [Fact]
    public void SinExigirLimiteUnConsumoQueLoSuperaSeAcepta()
    {
        Validar(300m, [CuentaCorriente(300m)], saldo: 800m, limiteCredito: 1000m, exigirLimiteDeCredito: false);
    }

    [Fact]
    public void SinExigirLimiteElConsumidorFinalSigueSinPoderPagarPorCuentaCorriente()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(300m, [CuentaCorriente(300m)], esConsumidorFinal: true, saldo: 800m, limiteCredito: 1000m,
                exigirLimiteDeCredito: false));
        Assert.Equal("cuenta_corriente_no_permitida", excepcion.Codigo);
    }

    [Fact]
    public void SinExigirLimiteLasReglasPosterioresSiguenEvaluandose()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(300m, [CuentaCorriente(200m), Tarjeta(100m, requiereReferencia: true, referencia: null)],
                saldo: 800m, limiteCredito: 1000m, exigirLimiteDeCredito: false));
        Assert.Equal("referencia_de_pago_requerida", excepcion.Codigo);
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
        // Importe 100 contra total 100 (nada sobra), vuelto declarado 5 -> invalido.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Efectivo(100m, vuelto: 5m)]));
        Assert.Equal("vuelto_invalido", excepcion.Codigo);
    }

    [Fact]
    public void VueltoQueCoincideConLoQueSobraEsAceptado()
    {
        // Total 100, entrega 1000 -> excedente 900 == vuelto declarado 900.
        Validar(100m, [Efectivo(1000m, vuelto: 900m)]);
    }

    [Fact]
    public void UnVueltoMayorAlExcedenteSeRechazaAunqueElEfectivoEntregadoLoCubra()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(7500m, [Efectivo(10500m, vuelto: 3500m)]));
        Assert.Equal("vuelto_invalido", excepcion.Codigo);
    }

    // ---- 9: importe_supera_el_total / cobro_supera_el_total ---------------------------------

    [Fact]
    public void UnQrConUnCeroDeMasSeRechazaConElNombreDelMedio()
    {
        // Venta 250 del relevamiento: total 22200 cobrado con un QR de 222000 y vuelto 0.
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(22200m, [Qr(222000m)]));

        Assert.Equal("importe_supera_el_total", excepcion.Codigo);
        Assert.Equal("El importe de QR supera el total de la venta.", excepcion.Message);
    }

    [Fact]
    public void UnMedioSinVueltoQueSuperaElTotalJuntoAEfectivoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, [Efectivo(20m), Tarjeta(101m)]));

        Assert.Equal("importe_supera_el_total", excepcion.Codigo);
        Assert.Equal("El importe de un pago sin vuelto supera el total de la venta.", excepcion.Message);
    }

    [Fact]
    public void MediosSinVueltoQueJuntosSuperanElTotalSeRechazan()
    {
        // Ninguno supera el total por sí solo; juntos cobran 150 sobre 100.
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, [Tarjeta(80m), Qr(70m)]));

        Assert.Equal("cobro_supera_el_total", excepcion.Codigo);
    }

    [Fact]
    public void UnExcedenteDeEfectivoSinVueltoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => Validar(100m, [Efectivo(200m)]));

        Assert.Equal("cobro_supera_el_total", excepcion.Codigo);
    }

    [Fact]
    public void ElExcedenteDeEfectivoQueVuelveComoVueltoSeAcepta() =>
        Validar(100m, [Qr(60m), Efectivo(100m, vuelto: 60m)]);

    [Fact]
    public void LaToleranciaTambienAcotaElExcedente()
    {
        Validar(100m, [Qr(110m)], tolerancia: 10m);

        Assert.Equal("importe_supera_el_total", Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Qr(110.01m)], tolerancia: 10m)).Codigo);
        Assert.Equal("cobro_supera_el_total", Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Qr(100m), Efectivo(10.01m)], tolerancia: 10m)).Codigo);
    }

    [Fact]
    public void ElReenvioDeUnaVentaYaEmitidaNoSeJuzgaContraElExcedente() =>
        Validar(22200m, [Qr(222000m)], exigirCobroSinExcedente: false);

    [Fact]
    public void SinExigirElExcedenteLasDemasReglasSiguen() =>
        Assert.Equal("medio_no_admite_vuelto", Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Tarjeta(150m, vuelto: 50m)], exigirCobroSinExcedente: false)).Codigo);

    [Fact]
    public void UnPagoQueNoCubreElTotalReportaLaRegla2AntesQueLa9() =>
        Assert.Equal("tolerancia_de_pago_superada", Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Efectivo(50m, vuelto: 50m)])).Codigo);

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
    public void UnPagoQueViolaLasReglas4Y7ReportaLaRegla4()
    {
        // Regla 4: el vuelto (30) está sobre una Tarjeta, que no admite vuelto.
        // Regla 7 (referencia): además falta la referencia de un medio que la requiere.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(70m, [Tarjeta(100m, vuelto: 30m, requiereReferencia: true, referencia: null)]));
        Assert.Equal("medio_no_admite_vuelto", excepcion.Codigo);
    }

    [Fact]
    public void UnaMezclaValidaConVariosMediosEsAceptada()
    {
        Validar(
            150m,
            [Efectivo(50m), Tarjeta(100m, requiereReferencia: true, referencia: "OP-1")],
            tolerancia: 0m);
    }

    /// <summary>Un cliente de alta rápida (límite 0, sin crédito ilimitado, saldo 0) no puede
    /// consumir cuenta corriente: regla 6.</summary>
    [Fact]
    public void UnClienteConLimiteCeroNoPuedePagarEnCuentaCorriente()
    {
        var ex = Assert.Throws<ErrorDominio>(() => ValidadorDePagos.Validar(
            100m, [CuentaCorriente(100m)], 0m, esConsumidorFinal: false,
            saldoCliente: 0m, limiteCredito: 0m, creditoIlimitado: false));

        Assert.Equal("limite_credito_excedido", ex.Codigo);
    }
}
