namespace Ways.Domain.Dinero;

/// <summary>
/// Única regla de redondeo de los importes de VENTA calculados: al peso entero más cercano, con
/// los puntos medios alejándose de cero (<see cref="MidpointRounding.AwayFromZero"/>, nunca el
/// banker's rounding por defecto de .NET). Compras, costos, percepciones y el desglose fiscal
/// neto/IVA conservan centavos y no pasan por acá.
/// </summary>
public static class RedondeoDePeso
{
    public static decimal APesoEntero(decimal importe) =>
        Math.Round(importe, 0, MidpointRounding.AwayFromZero);
}
