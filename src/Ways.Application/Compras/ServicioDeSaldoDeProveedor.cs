using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Application.Compras;

/// <summary>
/// El saldo del proveedor (design decisión 9, spec: saldo-de-proveedor MODIFIED) — dedicado,
/// nunca extiende <c>ServicioDeProveedores</c>. Re-sourceado sobre el ledger de
/// stage-15-cc-proveedores-ledger: <see cref="Saldo"/> viene de <c>proveedores.saldo</c> (la
/// caché de <c>EscriturasDeCuentaCorrienteProveedor</c>), NUNCA re-derivado de agregados — firma y
/// los tres records de respuesta se mantienen byte-idénticos a la forma pre-etapa 15 (task 4.10).
///
/// El estado de pago por-compra deriva de lo pagado por compra (<see cref="LectorDePagadoPorCompra"/>): desde
/// E3-S2 es la suma de las aplicaciones vivas de pagos a esa compra, no <c>pagos.id_comprobante_compra</c> ni los
/// ajustes imputados. Los movimientos <c>'pago'</c> del ledger no se cuentan acá. <c>ResolverEstadoPago</c> no cambia
/// una línea.
/// </summary>
public class ServicioDeSaldoDeProveedor(IWaysDbContext db)
{
    public async Task<SaldoDeProveedor> ObtenerAsync(int idProveedor, CancellationToken ct = default)
    {
        var saldo = await ResolverSaldoDeProveedorAsync(idProveedor, ct);

        var compras = await db.ComprobantesCompra
            .Where(c => c.IdProveedor == idProveedor && c.Estado == EstadoCompra.Confirmada)
            .Select(c => new { c.Id, c.NumeroExterno, c.Total })
            .ToListAsync(ct);

        var idsCompras = compras.Select(c => c.Id).ToList();

        var pagadoPorCompra = await LectorDePagadoPorCompra.LeerAsync(db, idsCompras, ct);

        var comprasConEstado = compras
            .Select(c =>
            {
                var pagado = pagadoPorCompra.GetValueOrDefault(c.Id, 0m);
                var estado = ResolverEstadoPago(pagado, c.Total);
                return new CompraConEstadoPago(c.Id, c.NumeroExterno, c.Total, pagado, estado);
            })
            .OrderBy(c => c.IdComprobanteCompra)
            .ToList();

        return new SaldoDeProveedor(idProveedor, saldo, comprasConEstado);
    }

    /// <summary>spec: A Fully Paid Compra / An Unlinked Pago Does Not Mark A Compra As Paid — sin
    /// pagos ligados es <c>impaga</c>, con el total exacto o mayor es <c>pagada</c> (un
    /// sobrepago no tiene remedio en esta etapa, ver design Open Questions), cualquier otro caso
    /// es <c>parcial</c>.</summary>
    private static EstadoPago ResolverEstadoPago(decimal pagado, decimal total)
    {
        if (pagado <= 0m)
        {
            return EstadoPago.Impaga;
        }

        return pagado >= total ? EstadoPago.Pagada : EstadoPago.Parcial;
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" (spec: Cross-Tenant
    /// Proveedor Saldo Is Invisible) — trae <c>Saldo</c> en la misma consulta de existencia
    /// (design decisión 9: <c>proveedores.saldo</c> es la fuente, ya no un agregado).</summary>
    private async Task<decimal> ResolverSaldoDeProveedorAsync(int idProveedor, CancellationToken ct)
    {
        var proveedor = await db.Proveedores
            .Where(p => p.Id == idProveedor)
            .Select(p => new { p.Saldo })
            .FirstOrDefaultAsync(ct);

        if (proveedor is null)
        {
            throw ErrorDominio.NoEncontrado($"No existe el proveedor {idProveedor}.");
        }

        return proveedor.Saldo;
    }
}

/// <summary>Estado de pago de una compra confirmada, derivado de las APLICACIONES de pagos a esa compra únicamente
/// (spec: saldo-de-proveedor / Per-Compra Payment Status From Linked Pagos Only, desde E3-S2 sobre
/// <c>aplicaciones_pago</c>) — un pago sin aplicar reduce el saldo total pero no resuelve el estado de ninguna compra
/// puntual.</summary>
public enum EstadoPago
{
    Impaga,
    Parcial,
    Pagada
}

/// <summary>Una compra confirmada del proveedor con su estado de pago derivado —
/// <see cref="Pagado"/> es la suma de las aplicaciones vivas de pagos a esta compra puntual (acotada al
/// total de la compra), nunca el saldo general del proveedor.</summary>
public sealed record CompraConEstadoPago(
    int IdComprobanteCompra, string? NumeroExterno, decimal Total, decimal Pagado, EstadoPago EstadoPago);

/// <summary>Respuesta de <c>GET /api/proveedores/{id}/saldo</c> (design: API Surface), byte-idéntica
/// a la forma pre-etapa 15 (task 4.10) — <see cref="Saldo"/> ahora es el INVARIANTE de
/// <c>proveedores.saldo</c> (spec: saldo-de-proveedor / Saldo Is The Single-Write-Authority Cache
/// Of The Ledger, REMOVED "Saldo Is An Approximation, Not An Invariant"): un pago sin ligar la
/// reduce igual, aunque no salde ninguna compra puntual.</summary>
public sealed record SaldoDeProveedor(int IdProveedor, decimal Saldo, IReadOnlyList<CompraConEstadoPago> Compras);
