using System.Globalization;

namespace Ways.Application.Plantel;

/// <summary>Importes de la anulación de un pago de personal: el asiento de ajuste devuelve al saldo del personal el total del
/// pago y el ajuste de cuenta corriente le devuelve al cliente la deuda que el descuento había bajado.</summary>
public sealed record PlanDeAnulacionDePagoDePersonal(decimal ImporteDelAjuste, decimal? DeudaDevuelta, string Detalle);

/// <summary>Regla pura, sin base de datos. Recibe los importes tal como están asentados (los dos negativos) y devuelve los
/// opuestos. Un importe que no es negativo es un defecto del llamador, nunca un error del cliente.</summary>
public static class ReglaDeAnulacionDePagoDePersonal
{
    public static PlanDeAnulacionDePagoDePersonal Planificar(
        decimal importeDelPago, DateOnly fechaDelPago, decimal? importeDelDescuento)
    {
        if (importeDelPago >= 0m)
        {
            throw new InvalidOperationException(
                "El asiento de un pago de personal tiene importe negativo — invariante de escritura violado.");
        }

        if (importeDelDescuento is >= 0m)
        {
            throw new InvalidOperationException(
                "El descuento de un pago de personal tiene importe negativo — invariante de escritura violado.");
        }

        return new PlanDeAnulacionDePagoDePersonal(
            -importeDelPago,
            -importeDelDescuento,
            $"Anulación del pago del {fechaDelPago.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
    }
}
