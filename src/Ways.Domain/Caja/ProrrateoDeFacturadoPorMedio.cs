using Ways.Domain.Catalogos;

namespace Ways.Domain.Caja;

/// <summary>Un cobro de la venta, ya neto de vuelto (<c>importe − vuelto</c>), con el medio que lo recibió.</summary>
public readonly record struct CobroNetoDeVenta(int IdMedioPago, decimal Neto);

/// <summary>Cómo se reparte lo facturado de un comprobante entre los tres cubos del cierre. <see cref="Total"/> es la
/// suma exacta de los tres.</summary>
public readonly record struct FacturadoPorCubo(decimal Efectivo, decimal Digitales, decimal CuentaCorriente)
{
    public decimal Total => Efectivo + Digitales + CuentaCorriente;

    public static FacturadoPorCubo operator +(FacturadoPorCubo a, FacturadoPorCubo b) =>
        new(a.Efectivo + b.Efectivo, a.Digitales + b.Digitales, a.CuentaCorriente + b.CuentaCorriente);
}

/// <summary>
/// Reparte el <c>Total</c> de un comprobante (venta o nota de crédito, con su signo) entre efectivo, digitales y cuenta
/// corriente en proporción a lo cobrado por cada cubo en la venta. Clasifica como el ticket de cierre: el medio ancla
/// del turno de la venta es efectivo, un medio de cuenta corriente va a cuenta corriente y el resto es digital (un medio
/// de efectivo que no es el ancla es digital, su dinero no pasó por el cajón).
///
/// El reparto es proporcional y no la suma de los cobros: una venta puede quedar por debajo de su total dentro de la
/// tolerancia de pago, o sobrepagada sin vuelto, y la nota de crédito no lleva cobros. Los importes se redondean a
/// centavos y el resto del redondeo va al cubo de mayor participación (empate: efectivo, digitales, cuenta corriente),
/// así que la suma de los cubos es exactamente el total. Sin cobros positivos, todo va a efectivo.
/// </summary>
public static class ProrrateoDeFacturadoPorMedio
{
    public static FacturadoPorCubo Repartir(
        decimal total,
        IReadOnlyList<CobroNetoDeVenta> cobrosDeLaVenta,
        IReadOnlyDictionary<int, ComportamientoMedioPago> comportamientoActual,
        int? idMedioAncla)
    {
        var efectivo = 0m;
        var digitales = 0m;
        var cuentaCorriente = 0m;

        foreach (var cobro in cobrosDeLaVenta)
        {
            var comportamiento = comportamientoActual.GetValueOrDefault(cobro.IdMedioPago, ComportamientoMedioPago.Electronico);

            if (EsEfectivo(cobro.IdMedioPago, comportamiento, idMedioAncla))
            {
                efectivo += cobro.Neto;
            }
            else if (comportamiento == ComportamientoMedioPago.CuentaCorriente)
            {
                cuentaCorriente += cobro.Neto;
            }
            else
            {
                digitales += cobro.Neto;
            }
        }

        efectivo = Math.Max(efectivo, 0m);
        digitales = Math.Max(digitales, 0m);
        cuentaCorriente = Math.Max(cuentaCorriente, 0m);
        var suma = efectivo + digitales + cuentaCorriente;

        if (suma <= 0m)
        {
            return new FacturadoPorCubo(total, 0m, 0m);
        }

        var parteEfectivo = Redondear(total * efectivo / suma);
        var parteDigitales = Redondear(total * digitales / suma);
        var parteCuentaCorriente = Redondear(total * cuentaCorriente / suma);
        var resto = total - (parteEfectivo + parteDigitales + parteCuentaCorriente);

        if (resto != 0m)
        {
            if (efectivo >= digitales && efectivo >= cuentaCorriente)
            {
                parteEfectivo += resto;
            }
            else if (digitales >= cuentaCorriente)
            {
                parteDigitales += resto;
            }
            else
            {
                parteCuentaCorriente += resto;
            }
        }

        return new FacturadoPorCubo(parteEfectivo, parteDigitales, parteCuentaCorriente);
    }

    private static bool EsEfectivo(int idMedioPago, ComportamientoMedioPago comportamiento, int? idMedioAncla) =>
        idMedioAncla is { } ancla
            ? idMedioPago == ancla
            : comportamiento == ComportamientoMedioPago.Efectivo;

    private static decimal Redondear(decimal valor) => Math.Round(valor, 2, MidpointRounding.AwayFromZero);
}
