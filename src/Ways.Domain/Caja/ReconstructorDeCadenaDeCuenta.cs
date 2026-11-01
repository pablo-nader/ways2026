namespace Ways.Domain.Caja;

/// <summary>Lo que una fila del libro suma o resta al saldo; no cambia al reconstruir la cadena.</summary>
public readonly record struct DiferenciaDeFila(decimal Ingreso, decimal Egreso);

/// <summary>Saldos recalculados de una fila: <c>Final = Inicio + Ingreso − Egreso</c>.</summary>
public readonly record struct SaldosDeFila(decimal Inicio, decimal Final);

/// <summary>
/// Recalcula <c>inicio</c>/<c>final</c> de un tramo del libro de una cuenta (doc 10 §7) cuando se insertan filas con fecha
/// anterior a las últimas. Pura, sin base de datos. El orden de la cadena es <c>(fecha, id)</c>: quien llama entrega las
/// filas en ese orden y el saldo anterior al tramo. Cada fila conserva su diferencia (ingreso y egreso): un ajuste manual no
/// guarda el saldo objetivo, así que su diferencia se mantiene y lo que cambia es el saldo sobre el que se aplica.
/// </summary>
public static class ReconstructorDeCadenaDeCuenta
{
    public static IReadOnlyList<SaldosDeFila> Recalcular(
        decimal saldoAnteriorAlTramo, IReadOnlyList<DiferenciaDeFila> filasEnOrden)
    {
        var saldos = new List<SaldosDeFila>(filasEnOrden.Count);
        var saldo = saldoAnteriorAlTramo;
        foreach (var fila in filasEnOrden)
        {
            var final = saldo + fila.Ingreso - fila.Egreso;
            saldos.Add(new SaldosDeFila(saldo, final));
            saldo = final;
        }

        return saldos;
    }
}
