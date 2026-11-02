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
/// La comisión estimada es la del cierre, no la del medio de hoy: el cierre guarda en su fila de comisión el porcentaje que aplicó
/// (<c>porcentaje_aplicado</c>) y la cuenta que acreditó, por turno y medio. Como el cierre redondea UNA vez por (turno, medio) sobre el bruto de todos
/// sus pagos, acá se redondea igual: la estimada de un grupo es <c>round(porcentaje × Σ importes de los pagos del grupo que esta conciliación trajo)</c>
/// (a centavos, el punto medio hacia afuera del cero), una vez por grupo, y la diferencia de una cuenta es Σ comisiones reales − Σ estimadas de sus grupos.
/// Las líneas reparten la estimada del grupo entre sus pagos por mayor resto (los centavos sobrantes van a los de mayor fracción, y a igual fracción
/// al de menor id del pago de venta, no del cobro de Mercado Pago: un cobro puede tener varios pagos), así que las líneas suman exactamente el total.
///
/// Aproximación que queda: si el reporte trae solo parte de los pagos de un (turno, medio), el cierre redondeó sobre el bruto completo y acá se redondea
/// sobre la parte, por lo que la estimada puede diferir en centavos de la porción que le corresponde al cierre. Un cobro cuyo turno todavía no acreditó
/// ese medio (no hay fila de cobro en la cuenta) no entra: no hay nada estimado que corregir y el cierre aún lo va a descontar con el porcentaje del
/// medio; un cobro sin comisión real tampoco entra (ni su importe se suma al bruto del grupo).
/// </summary>
public static class CalculadorDeDiferenciaDeComisionMercadoPago
{
    private const int Decimales = 2;

    private const decimal Centavo = 0.01m;

    public static ResultadoDeDiferenciaDeComision Calcular(
        IEnumerable<PagoConciliadoParaComision> pagos, IReadOnlyDictionary<(int IdTurnoCaja, int IdMedioPago), CierreDeMedioEnCuenta> cierres)
    {
        var entran = new List<(PagoConciliadoParaComision Pago, int IdTurno, CierreDeMedioEnCuenta Cierre, decimal Real)>();
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

            entran.Add((pago, idTurno, cierre, real));
        }

        var lineas = new List<LineaDeDiferenciaDeComision>();
        foreach (var grupo in entran.GroupBy(e => (e.IdTurno, e.Pago.IdMedioPago)))
        {
            lineas.AddRange(RepartirLaEstimadaDelGrupo([.. grupo.OrderBy(e => e.Pago.IdCobro)]));
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

    private static List<LineaDeDiferenciaDeComision> RepartirLaEstimadaDelGrupo(
        List<(PagoConciliadoParaComision Pago, int IdTurno, CierreDeMedioEnCuenta Cierre, decimal Real)> grupo)
    {
        var porcentaje = grupo[0].Cierre.PorcentajeComision;
        var estimadaDelGrupo = Math.Round(grupo.Sum(e => e.Pago.Importe) * porcentaje / 100m, Decimales, MidpointRounding.AwayFromZero);

        var partes = grupo
            .Select(e =>
            {
                var exacta = e.Pago.Importe * porcentaje / 100m;
                var piso = Math.Floor(exacta / Centavo) * Centavo;
                return (e.Pago, e.IdTurno, e.Cierre, e.Real, Piso: piso, Resto: exacta - piso);
            })
            .ToList();

        var sobrantes = (int)Math.Round((estimadaDelGrupo - partes.Sum(p => p.Piso)) / Centavo, MidpointRounding.AwayFromZero);
        var conCentavo = partes
            .OrderByDescending(p => p.Resto)
            .ThenBy(p => p.Pago.IdCobro)
            .Take(Math.Max(sobrantes, 0))
            .Select(p => p.Pago.IdCobro)
            .ToHashSet();

        return partes
            .Select(p =>
            {
                var estimada = p.Piso + (conCentavo.Contains(p.Pago.IdCobro) ? Centavo : 0m);
                return new LineaDeDiferenciaDeComision(
                    p.Pago.IdCobroMercadoPago, p.Pago.IdCobro, p.IdTurno, p.Pago.IdMedioPago, p.Cierre.IdCuentaTesoreria, p.Pago.Importe,
                    p.Cierre.PorcentajeComision, estimada, p.Real, p.Real - estimada);
            })
            .ToList();
    }
}
