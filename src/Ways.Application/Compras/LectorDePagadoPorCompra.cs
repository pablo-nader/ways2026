using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;

namespace Ways.Application.Compras;

/// <summary>
/// Lo pagado de cada compra: la suma de sus aplicaciones vivas (<c>aplicaciones_pago</c>, doc 10 §5). Una aplicación
/// liberada (baja del pago o anulación de la compra) tiene <c>deleted_at</c> y el filtro global de baja lógica la excluye.
/// Ya no suma <c>pagos.id_comprobante_compra</c> ni los ajustes imputados: el único ajuste imputado a una compra es el
/// contramovimiento de la anulación, y una anulada no se lee acá (su saldo pendiente es 0 por regla). Como cada vínculo
/// pago-compra se aplica con tope al saldo, lo pagado nunca supera el total.
/// Es la única fuente del pagado por compra — el estado de pago, el saldo pendiente del detalle, del listado y de la
/// cuenta corriente, y el límite del pago leen todos de acá.
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

        // Una consulta acotada por ix_aplicaciones_pago_compra; la baja lógica la filtra el modelo.
        var pagadoPorAplicaciones = await db.AplicacionesPago
            .Where(a => idsCompras.Contains(a.IdComprobanteCompra))
            .GroupBy(a => a.IdComprobanteCompra)
            .Select(grupo => new { IdComprobanteCompra = grupo.Key, Total = grupo.Sum(a => a.Importe) })
            .ToDictionaryAsync(g => g.IdComprobanteCompra, g => g.Total, ct);

        return idsCompras.Distinct().ToDictionary(id => id, id => pagadoPorAplicaciones.GetValueOrDefault(id, 0m));
    }
}
