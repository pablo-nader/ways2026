using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>
/// El cálculo de una propuesta de diferencia de comisión (<see cref="CalculadorDeDiferenciaDeComisionMercadoPago"/>). <see cref="CantidadDeCobros"/> y los
/// dos totales son de la cuenta de la propuesta; <see cref="CobrosSinCierre"/> (cobros conciliados cuyo turno todavía no acreditó el medio, que el
/// cierre va a descontar con el porcentaje del medio) y <see cref="CobrosSinComisionReal"/> son de TODA la conciliación, no de esa cuenta.
/// <see cref="Lineas"/> trae a lo sumo las que más pesan (<see cref="GeneradorDePropuestasDeTesoreriaMercadoPago.MaximoDeLineas"/>);
/// <see cref="LineasTruncado"/> avisa si hay más.
/// </summary>
public sealed record DetalleDeDiferenciaDeComision(
    int CantidadDeCobros,
    decimal ComisionRealTotal,
    decimal ComisionEstimadaTotal,
    int CobrosSinCierre,
    int CobrosSinComisionReal,
    IReadOnlyList<LineaDeDiferenciaDeComision> Lineas,
    bool LineasTruncado);

/// <summary>
/// Una propuesta de la conciliación a la tesorería. <see cref="Importe"/> es, en una diferencia de comisión, el efecto con signo sobre la cuenta
/// de origen (negativo si Mercado Pago cobró más de lo que el cierre descontó) y, en un retiro, el monto positivo que sale de ella.
/// <see cref="IdCuentaOrigen"/> es nulo en un retiro de una empresa sin conexión con Mercado Pago (no se puede aplicar). El destino, el movimiento
/// escrito y quién resolvió solo existen cuando ya se resolvió. <see cref="NumeroLinea"/>, <see cref="SourceId"/> y <see cref="FechaTransaccion"/>
/// son de la fila del reporte de un retiro; <see cref="Detalle"/> es solo de una diferencia de comisión.
/// </summary>
public sealed record PropuestaDeTesoreria(
    int Id,
    string Tipo,
    string Estado,
    decimal Importe,
    int? IdCuentaOrigen,
    string? NombreCuentaOrigen,
    int? IdCuentaDestino,
    string? NombreCuentaDestino,
    int? NumeroLinea,
    string? SourceId,
    DateTimeOffset? FechaTransaccion,
    DetalleDeDiferenciaDeComision? Detalle,
    int? IdMovimientoCuenta,
    int? IdMovimientoCuentaDestino,
    DateTimeOffset? ResueltaEl,
    string? ResueltaPor);

/// <summary>Aplica una propuesta. <see cref="IdCuentaDestino"/> es la cuenta bancaria activa de la misma empresa que recibe un retiro; es obligatoria
/// en un retiro y se rechaza en una diferencia de comisión.</summary>
public sealed record SolicitudDeAplicacionDePropuesta(int? IdCuentaDestino);

public static class CodigosDeErrorDePropuestaDeTesoreria
{
    public const string YaAplicada = "propuesta_ya_aplicada";

    public const string YaDescartada = "propuesta_ya_descartada";

    public const string Descartada = "propuesta_descartada";

    public const string CuentaDestinoRequerida = "cuenta_destino_requerida";

    public const string CuentaDestinoNoAplica = "cuenta_destino_no_aplica";

    public const string CuentaDestinoInvalida = "cuenta_destino_invalida";

    public const string CuentasIguales = "cuentas_iguales";

    public const string SinCuentaOrigen = "propuesta_sin_cuenta_origen";

    public const string CuentaOrigenNoDisponible = "cuenta_origen_no_disponible";
}
