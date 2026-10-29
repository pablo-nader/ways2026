using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>
/// Lo aplicado y lo disponible de cada pago de proveedor (doc 10 §5). Aplicado es la suma de sus aplicaciones vivas;
/// disponible es lo que queda del pago sin aplicar a ninguna compra, o sea su crédito a favor, menos lo que un redondeo
/// del pago absorbió (E4: movimientos <c>redondeo</c> con <c>id_pago</c>, importe positivo; su reversa lo resta). Una
/// aplicación liberada (baja de la compra o anulación) no cuenta: el filtro global de baja lógica la excluye.
/// </summary>
public static class LectorDeDisponiblePorPago
{
    /// <summary>Punto único de la fórmula del disponible: importe − aplicado − redondeo absorbido por el pago.</summary>
    public static decimal Disponible(decimal importe, decimal aplicado, decimal redondeoAbsorbido = 0m) =>
        importe - aplicado - redondeoAbsorbido;

    /// <summary>Redondeo absorbido (neto) de cada pago pedido, en UNA consulta; un pago sin redondeo figura con 0.</summary>
    public static async Task<IReadOnlyDictionary<int, decimal>> LeerRedondeoAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsPagos, CancellationToken ct)
    {
        if (idsPagos.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var redondeo = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Redondeo && m.IdPago != null && idsPagos.Contains(m.IdPago.Value))
            .GroupBy(m => m.IdPago!.Value)
            .Select(grupo => new { IdPago = grupo.Key, Total = grupo.Sum(m => m.Importe) })
            .ToDictionaryAsync(g => g.IdPago, g => g.Total, ct);

        return idsPagos.Distinct().ToDictionary(id => id, id => redondeo.GetValueOrDefault(id, 0m));
    }

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

    /// <summary>Disponible de cada pago pedido, con UNA consulta de importes, una de aplicaciones y una de redondeo.</summary>
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
        var redondeo = await LeerRedondeoAsync(db, idsPagos, ct);

        return importes.ToDictionary(
            p => p.Key, p => Disponible(p.Value, aplicado.GetValueOrDefault(p.Key, 0m), redondeo.GetValueOrDefault(p.Key, 0m)));
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

        var redondeo = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Redondeo && m.IdPago != null && m.IdProveedor == idProveedor
                && db.Pagos.Any(p => p.Id == m.IdPago && p.Categoria == CategoriaPago.Proveedor && p.IdProveedor == idProveedor))
            .SumAsync(m => (decimal?)m.Importe, ct) ?? 0m;

        return Disponible(importe, aplicado, redondeo);
    }
}
