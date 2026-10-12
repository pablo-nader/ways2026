namespace Ways.Domain.Caja;

/// <summary>Los cuatro porcentajes de un medio de pago electrónico (0 a 100, hasta 3 decimales).</summary>
public readonly record struct PorcentajesDeCobro(
    decimal Comision, decimal RetencionIibb, decimal RetencionIva, decimal RetencionGanancias);

/// <summary>Una fila a agregar al libro de la cuenta: un solo lado distinto de cero y el snapshot con el que se calculó.
/// <see cref="PorcentajeAplicado"/> es null en el cobro.</summary>
public readonly record struct ComponenteDeMovimientoDeCuenta(
    TipoMovimientoCuenta Tipo, decimal Ingreso, decimal Egreso, decimal BaseBruta, decimal? PorcentajeAplicado);

/// <summary>
/// Lo cobrado con un medio electrónico en un turno entra a su cuenta neto de comisión y retenciones (doc 10 §7). Pura, sin
/// base de datos. Los importes de comisión y retenciones no son importes de venta: se redondean a centavos, no al peso
/// de <see cref="Dinero.RedondeoDePeso"/>.
/// </summary>
public static class CalculadorDeMovimientosDeCuenta
{
    private const int Decimales = 2;

    /// <summary>Devuelve, en orden, el cobro y luego comisión, IIBB, IVA y Ganancias. El cobro va por ingreso si el bruto es
    /// positivo y por egreso si es negativo; la comisión y las retenciones van del lado contrario, porque un bruto negativo
    /// devuelve su parte proporcional. Hoy ningún camino produce un bruto negativo (un pago nunca es negativo y una nota de
    /// crédito no lleva pagos): es una capacidad que nadie ejerce todavía. Cada importe se redondea por separado, con el
    /// punto medio hacia afuera del cero, y no hay un componente que compense el redondeo: la suma de los componentes puede
    /// pasar del bruto por centavos (bruto 0,50 con cuatro porcentajes de 25 % da cuatro egresos de 0,13 y un neto de −0,02),
    /// y eso se acepta. Un componente que redondea a cero se omite, y un bruto cero no genera ninguno.</summary>
    public static IReadOnlyList<ComponenteDeMovimientoDeCuenta> Calcular(decimal bruto, PorcentajesDeCobro porcentajes)
    {
        if (bruto == 0m)
        {
            return [];
        }

        var componentes = new List<ComponenteDeMovimientoDeCuenta>(5)
        {
            bruto > 0m
                ? new(TipoMovimientoCuenta.Cobro, bruto, 0m, bruto, null)
                : new(TipoMovimientoCuenta.Cobro, 0m, -bruto, bruto, null)
        };

        AgregarDescuento(componentes, TipoMovimientoCuenta.Comision, bruto, porcentajes.Comision);
        AgregarDescuento(componentes, TipoMovimientoCuenta.RetencionIibb, bruto, porcentajes.RetencionIibb);
        AgregarDescuento(componentes, TipoMovimientoCuenta.RetencionIva, bruto, porcentajes.RetencionIva);
        AgregarDescuento(componentes, TipoMovimientoCuenta.RetencionGanancias, bruto, porcentajes.RetencionGanancias);

        return componentes;
    }

    private static void AgregarDescuento(
        List<ComponenteDeMovimientoDeCuenta> componentes, TipoMovimientoCuenta tipo, decimal bruto, decimal porcentaje)
    {
        var importe = Math.Round(bruto * porcentaje / 100m, Decimales, MidpointRounding.AwayFromZero);
        if (importe == 0m)
        {
            return;
        }

        componentes.Add(importe > 0m
            ? new(tipo, 0m, importe, bruto, porcentaje)
            : new(tipo, -importe, 0m, bruto, porcentaje));
    }
}
