using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>
/// Lo aplicado y lo disponible de cada pago de proveedor (doc 10 §5). Aplicado es la suma de sus aplicaciones vivas;
/// disponible es lo que queda del pago sin aplicar a ninguna compra, o sea su crédito a favor. Una aplicación liberada
/// (baja de la compra o anulación) no cuenta: el filtro global de baja lógica la excluye.
/// </summary>
public static class LectorDeDisponiblePorPago
{
    /// <summary>
    /// Punto único de la fórmula del disponible. E4 le restará el redondeo absorbido por el pago acá, sin tocar a los
    /// llamadores.
    /// </summary>
    public static decimal Disponible(decimal importe, decimal aplicado) => importe - aplicado;

    /// <summary>Aplicado de cada pago pedido, en UNA consulta; un pago sin aplicaciones figura con 0.</summary>
    public static async Task<IReadOnlyDictionary<int, decimal>> LeerAplicadoAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsPagos, CancellationToken ct)
    {
        if (idsPagos.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var aplicado = await db.AplicacionesPago
            .Where(a => idsPagos.Contains(a.IdPago))
            .GroupBy(a => a.IdPago)
            .Select(grupo => new { IdPago = grupo.Key, Total = grupo.Sum(a => a.Importe) })
            .ToDictionaryAsync(g => g.IdPago, g => g.Total, ct);

        return idsPagos.Distinct().ToDictionary(id => id, id => aplicado.GetValueOrDefault(id, 0m));
    }

    /// <summary>Disponible de cada pago pedido, con UNA consulta de importes y una de aplicaciones.</summary>
    public static async Task<IReadOnlyDictionary<int, decimal>> LeerDisponibleAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsPagos, CancellationToken ct)
    {
        if (idsPagos.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var importes = await db.Pagos
            .Where(p => idsPagos.Contains(p.Id))
            .Select(p => new { p.Id, p.Importe })
            .ToDictionaryAsync(p => p.Id, p => p.Importe, ct);

        var aplicado = await LeerAplicadoAsync(db, idsPagos, ct);

        return importes.ToDictionary(p => p.Key, p => Disponible(p.Value, aplicado.GetValueOrDefault(p.Key, 0m)));
    }

    /// <summary>Crédito total de un proveedor: la suma del disponible de todos sus pagos de proveedor vivos.</summary>
    public static async Task<decimal> LeerDisponibleDelProveedorAsync(
        IWaysDbContext db, int idProveedor, CancellationToken ct)
    {
        var importe = await db.Pagos
            .Where(p => p.Categoria == CategoriaPago.Proveedor && p.IdProveedor == idProveedor)
            .SumAsync(p => (decimal?)p.Importe, ct) ?? 0m;

        var aplicado = await db.AplicacionesPago
            .Where(a => db.Pagos.Any(p =>
                p.Id == a.IdPago && p.Categoria == CategoriaPago.Proveedor && p.IdProveedor == idProveedor))
            .SumAsync(a => (decimal?)a.Importe, ct) ?? 0m;

        return Disponible(importe, aplicado);
    }
}
