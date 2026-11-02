namespace Ways.Domain.MercadoPago;

/// <summary>Un cobro de Mercado Pago conciliado, con el pago de venta que lo cobró y el turno de esa venta.</summary>
public readonly record struct PagoConciliadoParaComision(
    int IdCobroMercadoPago, int IdCobro, int? IdTurnoCaja, int IdMedioPago, decimal Importe, decimal? ComisionReal);

/// <summary>Lo que el cierre de un turno dejó para un medio: la cuenta que acreditó y el porcentaje de comisión con el que descontó.</summary>
public readonly record struct CierreDeMedioEnCuenta(int IdCuentaTesoreria, decimal PorcentajeComision);

public sealed record LineaDeDiferenciaDeComision(
    int IdCobroMercadoPago,
    int IdCobro,
    int IdTurnoCaja,
    int IdMedioPago,
    int IdCuentaTesoreria,
    decimal Importe,
    decimal PorcentajeEstimado,
    decimal ComisionEstimada,
    decimal ComisionReal,
    decimal Diferencia);

/// <summary><see cref="Importe"/> es el efecto sobre la cuenta (negativo si la comisión real superó la estimada); <see cref="CantidadDeCobros"/>
/// cuenta los cobros de esa cuenta, y los dos totales son la comisión real y la estimada de esos cobros.</summary>
public sealed record DiferenciaDeComisionDeCuenta(
    int IdCuentaTesoreria, decimal Importe, int CantidadDeCobros, decimal ComisionRealTotal, decimal ComisionEstimadaTotal);

public sealed record ResultadoDeDiferenciaDeComision(
    IReadOnlyList<DiferenciaDeComisionDeCuenta> PorCuenta,
    IReadOnlyList<LineaDeDiferenciaDeComision> Lineas,
    int CobrosSinCierre,
    int CobrosSinComisionReal);

/// <summary>
/// Diferencia entre la comisión real que informa Mercado Pago y la que el cierre de turno ya descontó de la cuenta (E7). Pura, sin base de datos.
///
/// La comisión estimada de un cobro es la del cierre, no la del medio de hoy: el cierre guarda en su fila de comisión el porcentaje que aplicó
/// (<c>porcentaje_aplicado</c>) y la cuenta que acreditó, por turno y medio. Se aplica ese porcentaje al importe del pago con el mismo
/// redondeo del cierre (a centavos, el punto medio hacia afuera del cero), pago por pago: el cierre redondea sobre el bruto de todo el
/// turno y medio, así que la suma de las comisiones estimadas puede diferir en centavos de lo que el cierre descontó. Un cobro cuyo turno
/// todavía no acreditó ese medio (no hay fila de cobro en la cuenta) no entra: no hay nada estimado que corregir, y el cierre aún lo va a descontar con
/// el porcentaje del medio; un cobro sin comisión real tampoco entra.
/// </summary>
public static class CalculadorDeDiferenciaDeComisionMercadoPago
{
    private const int Decimales = 2;

    public static ResultadoDeDiferenciaDeComision Calcular(
        IEnumerable<PagoConciliadoParaComision> pagos, IReadOnlyDictionary<(int IdTurnoCaja, int IdMedioPago), CierreDeMedioEnCuenta> cierres)
    {
        var lineas = new List<LineaDeDiferenciaDeComision>();
        var sinCierre = 0;
        var sinComisionReal = 0;

        foreach (var pago in pagos)
        {
            if (pago.IdTurnoCaja is not { } idTurno || !cierres.TryGetValue((idTurno, pago.IdMedioPago), out var cierre))
            {
                sinCierre++;
                continue;
            }

            if (pago.ComisionReal is not { } real)
            {
                sinComisionReal++;
                continue;
            }

            var estimada = Math.Round(pago.Importe * cierre.PorcentajeComision / 100m, Decimales, MidpointRounding.AwayFromZero);
            lineas.Add(new LineaDeDiferenciaDeComision(
                pago.IdCobroMercadoPago, pago.IdCobro, idTurno, pago.IdMedioPago, cierre.IdCuentaTesoreria, pago.Importe,
                cierre.PorcentajeComision, estimada, real, real - estimada));
        }

        var porCuenta = lineas
            .GroupBy(l => l.IdCuentaTesoreria)
            .OrderBy(g => g.Key)
            .Select(g => new DiferenciaDeComisionDeCuenta(
                g.Key, -g.Sum(l => l.Diferencia), g.Count(), g.Sum(l => l.ComisionReal), g.Sum(l => l.ComisionEstimada)))
            .Where(c => c.Importe != 0m)
            .ToList();

        return new ResultadoDeDiferenciaDeComision(porCuenta, lineas, sinCierre, sinComisionReal);
    }
}
