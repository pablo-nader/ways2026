using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Saldo pendiente de una compra y validación de un pago contra él. Es una regla pura: el servicio
/// la aplica con el total y lo pagado leídos bajo el lock de la compra, así que dos pagos
/// concurrentes nunca ven el mismo saldo.
/// </summary>
public static class ReglaDePagoDeCompra
{
    /// <summary>Lo que falta pagar de una compra confirmada. Una compra sobrepagada (un gasto
    /// editado hacia arriba después de imputarse) no tiene saldo negativo: queda en cero.</summary>
    public static decimal SaldoPendiente(EstadoCompra estado, decimal total, decimal pagado) =>
        estado == EstadoCompra.Confirmada ? Math.Max(0m, total - pagado) : 0m;

    /// <summary>Concepto del gasto cuando el pedido no trae uno: <c>Pago &lt;tipo&gt; &lt;número&gt;</c>,
    /// con el id de la compra si no tiene número externo.</summary>
    public static string ConceptoPorDefecto(string? nombreDelTipo, string? numeroExterno, int idComprobanteCompra)
    {
        var tipo = string.IsNullOrWhiteSpace(nombreDelTipo) ? "compra" : nombreDelTipo.Trim();
        var numero = string.IsNullOrWhiteSpace(numeroExterno) ? $"#{idComprobanteCompra}" : numeroExterno.Trim();
        return $"Pago {tipo} {numero}";
    }

    public static void ExigirPagoValido(decimal importe, decimal saldoPendiente)
    {
        if (saldoPendiente <= 0m)
        {
            throw new ErrorDominio(
                "compra_sin_saldo_pendiente", "La compra no tiene saldo pendiente de pago.", 409);
        }

        if (importe > saldoPendiente)
        {
            throw new ErrorDominio(
                "pago_excede_saldo_pendiente", "El importe del pago supera el saldo pendiente de la compra.", 409);
        }
    }
}
