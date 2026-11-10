namespace Ways.Domain.Caja;

/// <summary>Los cuatro porcentajes de un medio de pago electrónico (0 a 100, hasta 3 decimales).</summary>
public readonly record struct PorcentajesDeCobro(
    decimal Comision, decimal RetencionIibb, decimal RetencionIva, decimal RetencionGanancias);

/// <summary>La parte del bruto de un medio cuya comisión ya informó el proveedor: <see cref="Base"/> es la suma de los importes de esos pagos y
/// <see cref="Comision"/> la suma de sus comisiones informadas.</summary>
public readonly record struct ComisionConocida(decimal Base, decimal Comision);

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
    /// devuelve su parte proporcional. Hoy ningún camino produce un bruto negativo (los importes de pago nunca son negativos y
    /// <c>ValidadorDePagos</c> rechaza los pagos en una nota de crédito): es una capacidad que nadie ejerce todavía. Cada importe se redondea por separado, con el
    /// punto medio hacia afuera del cero, y no hay un componente que compense el redondeo: la suma de los componentes puede
    /// pasar del bruto por centavos (bruto 0,50 con cuatro porcentajes de 25 % da cuatro egresos de 0,13 y un neto de −0,02),
    /// y eso se acepta. Un componente que redondea a cero se omite, y un bruto cero no genera ninguno.</summary>
    public static IReadOnlyList<ComponenteDeMovimientoDeCuenta> Calcular(decimal bruto, PorcentajesDeCobro porcentajes) =>
        Calcular(bruto, porcentajes, default);

    /// <summary>Como <see cref="Calcular(decimal, PorcentajesDeCobro)"/>, pero la comisión es la informada de <paramref name="conocida"/> más el
    /// porcentaje sobre el resto del bruto, redondeado una vez: <c>conocida.Comision + round((bruto − conocida.Base) × porcentaje)</c>. La fila
    /// conserva el porcentaje del medio y el bruto completo como base; las retenciones se calculan sobre el bruto completo, como siempre. Sin
    /// comisión conocida (base y comisión cero) el resultado es idéntico al de la otra sobrecarga. Una base negativa o mayor que el bruto, o una
    /// comisión negativa, no la produce ningún llamador (la base sale de un subconjunto de los mismos pagos) y se rechaza.</summary>
    public static IReadOnlyList<ComponenteDeMovimientoDeCuenta> Calcular(decimal bruto, PorcentajesDeCobro porcentajes, ComisionConocida conocida)
    {
        if (conocida.Base < 0m || conocida.Base > Math.Max(bruto, 0m) || conocida.Comision < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(conocida), conocida, "La comisión conocida no corresponde al bruto del medio.");
        }

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

        AgregarComponente(
            componentes, TipoMovimientoCuenta.Comision, bruto, porcentajes.Comision,
            conocida.Comision + Redondear((bruto - conocida.Base) * porcentajes.Comision / 100m));
        AgregarDescuento(componentes, TipoMovimientoCuenta.RetencionIibb, bruto, porcentajes.RetencionIibb);
        AgregarDescuento(componentes, TipoMovimientoCuenta.RetencionIva, bruto, porcentajes.RetencionIva);
        AgregarDescuento(componentes, TipoMovimientoCuenta.RetencionGanancias, bruto, porcentajes.RetencionGanancias);

        return componentes;
    }

    private static decimal Redondear(decimal valor) => Math.Round(valor, Decimales, MidpointRounding.AwayFromZero);

    private static void AgregarDescuento(
        List<ComponenteDeMovimientoDeCuenta> componentes, TipoMovimientoCuenta tipo, decimal bruto, decimal porcentaje) =>
        AgregarComponente(componentes, tipo, bruto, porcentaje, Redondear(bruto * porcentaje / 100m));

    private static void AgregarComponente(
        List<ComponenteDeMovimientoDeCuenta> componentes, TipoMovimientoCuenta tipo, decimal bruto, decimal porcentaje, decimal importe)
    {
        if (importe == 0m)
        {
            return;
        }

        componentes.Add(importe > 0m
            ? new(tipo, 0m, importe, bruto, porcentaje)
            : new(tipo, -importe, 0m, bruto, porcentaje));
    }
}
