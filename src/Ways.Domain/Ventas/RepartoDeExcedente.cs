using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>Un pago de la venta visto por <see cref="RepartoDeExcedente"/>. <see cref="Divisible"/> es <c>false</c> para un
/// pago que no puede pasar en parte a un recibo: cuenta corriente (una deuda no paga otra) y un pago de un medio con
/// proveedor (su importe es el de la orden cobrada).</summary>
public readonly record struct PagoARepartir(decimal Importe, decimal Vuelto, bool AdmiteVuelto, bool Divisible);

/// <summary>Lo que sale del reparto: el importe que conserva cada pago de la venta (mismo orden que el pedido, puede quedar
/// en cero) y lo que cada pago cede al pago a cuenta, con el índice del pago de origen.</summary>
public sealed record ExcedenteRepartido(
    decimal Excedente,
    IReadOnlyList<decimal> ImportesDeLaVenta,
    IReadOnlyList<PagoCedidoACuenta> PagosACuenta);

public readonly record struct PagoCedidoACuenta(int IndiceDelPago, decimal Importe);

/// <summary>
/// Excedente de una venta a cuenta corriente: lo que se cobró por encima del total (Σ importe − vuelto − total) deja de ser
/// cobro de la venta y pasa a un pago a cuenta (RC) del cliente, con los mismos medios. La venta conserva exactamente su
/// total, así el cierre del turno cuenta cada peso una sola vez. Pura, sin base de datos.
///
/// El excedente sale primero de los medios sin vuelto y después del efectivo, en cada grupo desde el último pago. El
/// efectivo que sobra normalmente vuelve como vuelto; solo llega acá si el cajero eligió dejarlo a cuenta.
/// </summary>
public static class RepartoDeExcedente
{
    public static ExcedenteRepartido Repartir(decimal total, IReadOnlyList<PagoARepartir> pagos)
    {
        var excedente = pagos.Sum(p => p.Importe - p.Vuelto) - total;
        if (excedente <= 0m)
        {
            throw new ErrorDominio(
                "pago_a_cuenta_sin_excedente", "Lo cobrado no supera el total: no hay diferencia para dejar a cuenta.", 400);
        }

        var importes = pagos.Select(p => p.Importe).ToArray();
        var cedidos = new List<PagoCedidoACuenta>();
        var pendiente = excedente;

        var indicesEnOrden = Enumerable.Range(0, pagos.Count).Reverse().Where(i => !pagos[i].AdmiteVuelto)
            .Concat(Enumerable.Range(0, pagos.Count).Reverse().Where(i => pagos[i].AdmiteVuelto));

        foreach (var indice in indicesEnOrden)
        {
            if (pendiente == 0m)
            {
                break;
            }

            var pago = pagos[indice];
            if (!pago.Divisible)
            {
                continue;
            }

            var cedido = Math.Min(Math.Max(0m, pago.Importe - pago.Vuelto), pendiente);
            if (cedido == 0m)
            {
                continue;
            }

            importes[indice] -= cedido;
            pendiente -= cedido;
            cedidos.Add(new PagoCedidoACuenta(indice, cedido));
        }

        if (pendiente > 0m)
        {
            throw new ErrorDominio(
                "pago_a_cuenta_no_divisible",
                "La diferencia sale de un pago que no se puede dejar a cuenta (cuenta corriente o cobro de Mercado Pago).", 400);
        }

        return new ExcedenteRepartido(excedente, importes, cedidos);
    }
}
