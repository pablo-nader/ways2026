namespace Ways.Application.MercadoPago;

/// <summary>
/// La comisión que Mercado Pago informa para el pago de un cobro: la suma de los <c>fee_details</c> con <c>fee_payer = collector</c> (las que
/// paga el comprador no salen de la cuenta). Solo vale para un pago <c>approved</c> cuyo <c>transaction_amount</c> es el importe pagado del cobro,
/// con cada comisión legible (importe no negativo, a lo sumo dos decimales, con quien la paga) y una suma que no pasa del importe. Sin comisiones
/// del vendedor es cero solo si el neto recibido es el importe completo. Cualquier otra cosa es null: el cierre usa el porcentaje del medio.
/// </summary>
public static class ComisionInformadaDeMercadoPago
{
    private const string Vendedor = "collector";

    public static decimal? Calcular(PagoDeMercadoPago pago, decimal? importePagado)
    {
        if (importePagado is not { } importe || pago.Status != "approved" || pago.TransactionAmount != importe)
        {
            return null;
        }

        var comisiones = pago.FeeDetails ?? [];
        if (comisiones.Any(c => c?.FeePayer is null))
        {
            return null;
        }

        var delVendedor = comisiones.Where(c => c!.FeePayer == Vendedor).Select(c => c!.Amount).ToList();
        if (delVendedor.Count == 0)
        {
            return pago.TransactionDetails?.NetReceivedAmount == importe ? 0m : null;
        }

        if (delVendedor.Any(a => a is not { } monto || monto < 0m || monto != Math.Round(monto, 2)))
        {
            return null;
        }

        var total = delVendedor.Sum(a => a!.Value);
        return total <= importe ? total : null;
    }
}
