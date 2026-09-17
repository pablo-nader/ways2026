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
        bool creditoIlimitado = false) =>
        ValidadorDePagos.Validar(total, pagos, tolerancia, esConsumidorFinal, saldo, limiteCredito, creditoIlimitado);

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

    // ---- 3: vuelto_no_justificado (decisión del dueño, 2026-09-16 — reemplaza el
    // vuelto_maximo fijo/parametrizado del legacy: el vuelto es válido si el efectivo entregado
    // es formable con billetes argentinos válidos, todos estrictamente mayores al vuelto) -------

    [Fact]
    public void ElEjemploDelDuenioTicket5500ConUnBilleteDe10000EsAceptado()
    {
        // Total 5500, entrega 10000 -> vuelto 4500. Un solo billete de 10000 (> 4500) alcanza.
        // El legacy rechazaba esto porque 4500 > 20 (el vuelto_maximo fijo) — el motivo original
        // del reclamo del dueño.
        Validar(5500m, [Efectivo(10000m, vuelto: 4500m)]);
    }

    [Fact]
    public void ElEjemploDelDuenioTicket5500ConTresBilletesDe2000EsAceptado()
    {
        // Total 5500, entrega 6000 -> vuelto 500. 6000 = 3 billetes de 2000 (> 500 cada uno).
        Validar(5500m, [Efectivo(6000m, vuelto: 500m)]);
    }

    [Fact]
    public void ElEjemploDelDuenioTicket5500ConVueltoDe20NoFormableSeRechaza()
    {
        // Total 5500, entrega 5520 -> vuelto 20. Todo billete estrictamente mayor a 20 es
        // múltiplo de 50 (50, 100, 200, ...) -> solo arman múltiplos de 50; 5520 no lo es.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(5500m, [Efectivo(5520m, vuelto: 20m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void ElEjemploDelDuenioTicket5500ConVueltoDe24500SinBilleteSuficienteSeRechaza()
    {
        // Total 5500, entrega 30000 -> vuelto 24500. Ningún billete (máximo 20000) es > 24500.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(5500m, [Efectivo(30000m, vuelto: 24500m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void VueltoCeroSiempreEsJustificadoSinImportarElEfectivo()
    {
        // Pago exacto, nada que justificar.
        Validar(100m, [Efectivo(100m, vuelto: 0m)]);
    }

    [Fact]
    public void VueltoIgualAlValorDeUnBilleteEsAceptadoConEseUnicoBillete()
    {
        // Boundary: total 8000, entrega 10000 -> vuelto 2000 (coincide con una denominación
        // real). Un solo billete de 10000 (> 2000) alcanza.
        Validar(8000m, [Efectivo(10000m, vuelto: 2000m)]);
    }

    [Fact]
    public void VueltoQueSoloEsFormableConUnBilleteNoPermitidoSeRechaza()
    {
        // Boundary: total 9000, entrega 11000 -> vuelto 2000. 11000 con billetes > 2000 (10000,
        // 20000) no arma exacto (10000 + 1000 usa un billete de 1000, no permitido).
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(9000m, [Efectivo(11000m, vuelto: 2000m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void VueltoIgualAUnBilleteNoAlcanzaParaJustificarUnEfectivoQueLoNecesitaria()
    {
        // Mutation-proof-tests: total 2000, entrega 4000 -> vuelto 2000. La ÚNICA forma de
        // completar 4000 en billetes reales usa como mínimo un billete <= 2000 (dos de 2000, o
        // combinaciones más chicas) — ningún billete estrictamente MAYOR a 2000 arma 4000 solo
        // (10000/20000 se pasan). Se rechaza.
        //
        // Evidencia de mutación (mutation-proof-tests regla 2): cambiar el filtro de
        // BilletesArgentinos.EsVueltoJustificado de "billete > vuelto" a "billete >= vuelto"
        // admite el billete de 2000 (justo el vuelto) y 4000 = 2×2000 pasaría a aceptarse
        // (rojo evitado). Mutado y revertido — ver reporte de la tarea.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(2000m, [Efectivo(4000m, vuelto: 2000m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void EfectivoConCentavosNuncaEsFormableConBilletes()
    {
        // El efectivo entregado tiene que ser un múltiplo entero de $10 (el billete más chico);
        // con centavos, ningún billete real puede componerlo.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(5000m, [Efectivo(5500.50m, vuelto: 500.50m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void ElTotalPuedeTenerCentavosMientrasElEfectivoSeaMultiploDeDiez()
    {
        // Total con centavos (5499.50), entrega 10000 (múltiplo de 10) -> vuelto 4500.50. Un
        // billete de 10000 (> 4500.50) alcanza igual.
        Validar(5499.50m, [Efectivo(10000m, vuelto: 4500.50m)]);
    }

    [Fact]
    public void EfectivoPorEncimaDelTechoAcotadoSeRechazaDePlano()
    {
        // La DP está acotada (BilletesArgentinos.EfectivoMaximo) — por encima de ese techo se
        // rechaza sin evaluar formabilidad, para no correr una DP sin límite.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(1m, [Efectivo(BilletesArgentinos.EfectivoMaximo + 10m, vuelto: BilletesArgentinos.EfectivoMaximo + 9m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void SinNingunPagoQueAdmitaVueltoElVueltoNuncaEsJustificado()
    {
        // Nadie entregó efectivo (AdmiteVuelto = false en el único pago) pero se declara un
        // vuelto > 0 -> no hay nada de donde "formarlo".
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(70m, [Tarjeta(100m, vuelto: 30m)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    // ---- 4: medio_no_admite_vuelto ---------------------------------------------------------

    [Fact]
    public void VueltoRechazadoSobreUnMedioSinAdmiteVuelto()
    {
        // Efectivo (AdmiteVuelto = true) entrega 200 sin vuelto propio; Tarjeta (AdmiteVuelto =
        // false) declara un vuelto de 20 -> el efectivo entregado (200) SÍ justificaría un
        // vuelto de 20 (bills > 20 arman 200 de sobra), así que la regla 3 no corta acá — la que
        // corta es la 4, porque el vuelto está sobre un medio que no lo admite.
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
        // Importe 100 contra total 100 (nada sobra), vuelto declarado 5 -> invalido. El efectivo
        // (100) sí sería formable contra un vuelto de 5 (regla 3 no corta), así que esta prueba
        // aísla la regla 8.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(100m, [Efectivo(100m, vuelto: 5m)]));
        Assert.Equal("vuelto_invalido", excepcion.Codigo);
    }

    [Fact]
    public void VueltoQueCoincideConLoQueSobraEsAceptado()
    {
        // Total 100, entrega 1000 (un solo billete) -> excedente 900 == vuelto declarado 900,
        // y 1000 es formable con ese único billete (> 900).
        Validar(100m, [Efectivo(1000m, vuelto: 900m)]);
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
        // Regla 3 (vuelto no justificado): el vuelto (30) está sobre una Tarjeta, que no admite
        // vuelto -> el efectivo entregado (Σ importe de pagos con AdmiteVuelto) es 0, así que
        // nunca hay de dónde "formarlo".
        // Regla 7 (referencia): además falta la referencia de un medio que la requiere.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Validar(70m, [Tarjeta(100m, vuelto: 30m, requiereReferencia: true, referencia: null)]));
        Assert.Equal("vuelto_no_justificado", excepcion.Codigo);
    }

    [Fact]
    public void UnaMezclaValidaConVariosMediosEsAceptada()
    {
        Validar(
            150m,
            [Efectivo(50m), Tarjeta(100m, requiereReferencia: true, referencia: "OP-1")],
            tolerancia: 0m);
    }
}
