using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.Compras;

/// <summary>
/// Lo pagado de cada compra, con la fórmula vinculante de <see cref="ServicioDeSaldoDeProveedor"/>:
/// suma de los gastos imputados a la compra más los ajustes de cuenta corriente imputados a ella
/// (con signo invertido). Los movimientos <c>pago</c> no se suman: ya están contados como gasto.
/// Es la única fuente del pagado por compra — el estado de pago, el saldo pendiente del detalle, del
/// listado y de la cuenta corriente, y el límite del pago leen todos de acá.
/// </summary>
public static class LectorDePagadoPorCompra
{
    public static async Task<IReadOnlyDictionary<int, decimal>> LeerAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsCompras, CancellationToken ct)
    {
        if (idsCompras.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        // Acotado por índice (ix_gastos_comprobante_compra). Sin filtro de categoría: el predicado
        // retirado que esta fórmula conserva verbatim.
        var pagadoPorGastos = await db.Gastos
            .Where(g => g.IdComprobanteCompra != null && idsCompras.Contains(g.IdComprobanteCompra.Value))
            .GroupBy(g => g.IdComprobanteCompra!.Value)
            .Select(grupo => new { IdComprobanteCompra = grupo.Key, Total = grupo.Sum(g => g.Importe) })
            .ToDictionaryAsync(g => g.IdComprobanteCompra, g => g.Total, ct);

        // Solo 'ajuste' (contramovimiento de anulación o ajuste manual imputado); 'pago' queda
        // excluido a propósito, ya está contado arriba vía gastos.
        var reversadoPorAjustes = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Ajuste && m.IdComprobanteCompra != null
                && idsCompras.Contains(m.IdComprobanteCompra.Value))
            .GroupBy(m => m.IdComprobanteCompra!.Value)
            .Select(grupo => new { IdComprobanteCompra = grupo.Key, Total = grupo.Sum(m => -m.Importe) })
            .ToDictionaryAsync(g => g.IdComprobanteCompra, g => g.Total, ct);

        return idsCompras.Distinct().ToDictionary(
            id => id, id => pagadoPorGastos.GetValueOrDefault(id, 0m) + reversadoPorAjustes.GetValueOrDefault(id, 0m));
    }
}
