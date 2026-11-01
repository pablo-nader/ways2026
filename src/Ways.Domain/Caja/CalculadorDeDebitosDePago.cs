namespace Ways.Domain.Caja;

/// <summary>Importe con signo de un pago en una (cuenta, medio): positivo es dinero que sale de la cuenta.</summary>
public sealed record ImporteDeCuentaYMedio(int IdCuenta, int IdMedioPago, decimal Importe);

/// <summary>Fila <see cref="TipoMovimientoCuenta.Pago"/> a escribir: exactamente uno de los dos lados es positivo.</summary>
public sealed record DebitoDePago(int IdCuenta, int IdMedioPago, decimal Ingreso, decimal Egreso);

/// <summary>
/// Débitos de un pago en las cuentas de tesorería (doc 10 §7). El objetivo es lo que el pago debería tener debitado en cada
/// (cuenta, medio) según sus ítems vivos; lo ya debitado, la suma de sus filas <c>pago</c> (egreso − ingreso). Por cada
/// diferencia distinta de cero devuelve una fila: egreso si el pago debita más, ingreso si debita menos. Una baja pasa un
/// objetivo vacío y devuelve todo lo debitado.
/// </summary>
public static class CalculadorDeDebitosDePago
{
    public static IReadOnlyList<DebitoDePago> Calcular(
        IEnumerable<ImporteDeCuentaYMedio> objetivo, IEnumerable<ImporteDeCuentaYMedio> yaDebitado)
    {
        var diferencias = new Dictionary<(int IdCuenta, int IdMedioPago), decimal>();

        foreach (var importe in objetivo)
        {
            var clave = (importe.IdCuenta, importe.IdMedioPago);
            diferencias[clave] = diferencias.GetValueOrDefault(clave) + importe.Importe;
        }

        foreach (var importe in yaDebitado)
        {
            var clave = (importe.IdCuenta, importe.IdMedioPago);
            diferencias[clave] = diferencias.GetValueOrDefault(clave) - importe.Importe;
        }

        return diferencias
            .Where(d => d.Value != 0m)
            .OrderBy(d => d.Key.IdCuenta).ThenBy(d => d.Key.IdMedioPago)
            .Select(d => d.Value > 0m
                ? new DebitoDePago(d.Key.IdCuenta, d.Key.IdMedioPago, 0m, d.Value)
                : new DebitoDePago(d.Key.IdCuenta, d.Key.IdMedioPago, -d.Value, 0m))
            .ToList();
    }
}
