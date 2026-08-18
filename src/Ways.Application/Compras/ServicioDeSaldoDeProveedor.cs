using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.Compras;

/// <summary>
/// El saldo del proveedor (design decisión 9, spec: saldo-de-proveedor MODIFIED) — dedicado,
/// nunca extiende <c>ServicioDeProveedores</c>. Re-sourceado sobre el ledger de
/// stage-15-cc-proveedores-ledger: <see cref="Saldo"/> viene de <c>proveedores.saldo</c> (la
/// caché de <c>EscriturasDeCuentaCorrienteProveedor</c>), NUNCA re-derivado de agregados — firma y
/// los tres records de respuesta se mantienen byte-idénticos a la forma pre-etapa 15 (task 4.10).
///
/// El estado de pago por-compra usa la fórmula VINCULANTE de <c>state.yaml</c> OD7 (tasks.md
/// decisión 4) — NO la del proposal (<c>SUM(importe) ... &lt;= 0 ⇒ pagada</c>, lee `pagada` una
/// compra pre-cutover sin movimiento propio) ni la del design (<c>−Σ importe WHERE tipo &lt;&gt;
/// 'compra'</c>, pierde un pago parcial pre-cutover porque nunca consulta <c>gastos</c>):
/// <c>pagado(X) = SUM(gastos.importe) WHERE gastos.id_comprobante_compra = X</c> (el mecanismo
/// retirado, predicado verbatim — sigue siendo verdad para TODO el tiempo porque el pago SIGUE
/// siendo un gasto) <c>+ SUM(-importe) WHERE movimientos_cuenta_corriente_proveedor.
/// id_comprobante_compra = X AND tipo = 'ajuste'</c> (contramovimientos y ajustes manuales
/// imputados). Los movimientos <c>'pago'</c> NO se cuentan acá — ya están contados como gasto;
/// sumarlos de nuevo sería double-count (mutation target #24 REDEFINIDO). <c>ResolverEstadoPago</c>
/// no cambia una línea.
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

        // Primer término de OD7 — el mecanismo retirado, verbatim: SUM(gastos.importe) por
        // id_comprobante_compra, SIN filtro de categoria acá (distinto del predicado que escribe
        // el movimiento 'pago' en ServicioDeGastos — ese sí filtra categoria = proveedor). Acotado
        // a las compras de ESTE proveedor por índice (ix_gastos_comprobante_compra).
        var pagadoPorGastos = await db.Gastos
            .Where(g => g.IdComprobanteCompra != null && idsCompras.Contains(g.IdComprobanteCompra.Value))
            .GroupBy(g => g.IdComprobanteCompra!.Value)
            .Select(grupo => new { IdComprobanteCompra = grupo.Key, Total = grupo.Sum(g => g.Importe) })
            .ToDictionaryAsync(g => g.IdComprobanteCompra, g => g.Total, ct);

        // Segundo término de OD7 — SOLO 'ajuste' (contramovimiento de anulación o ajuste manual
        // imputado); 'pago' queda EXCLUIDO a propósito (ya contado arriba vía gastos — target #24).
        var reversadoPorAjustes = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Ajuste && m.IdComprobanteCompra != null
                && idsCompras.Contains(m.IdComprobanteCompra.Value))
            .GroupBy(m => m.IdComprobanteCompra!.Value)
            .Select(grupo => new { IdComprobanteCompra = grupo.Key, Total = grupo.Sum(m => -m.Importe) })
            .ToDictionaryAsync(g => g.IdComprobanteCompra, g => g.Total, ct);

        var comprasConEstado = compras
            .Select(c =>
            {
                var pagado = pagadoPorGastos.GetValueOrDefault(c.Id, 0m) + reversadoPorAjustes.GetValueOrDefault(c.Id, 0m);
                var estado = ResolverEstadoPago(pagado, c.Total);
                return new CompraConEstadoPago(c.Id, c.NumeroExterno, c.Total, pagado, estado);
            })
            .OrderBy(c => c.IdComprobanteCompra)
            .ToList();

        return new SaldoDeProveedor(idProveedor, saldo, comprasConEstado);
    }

    /// <summary>spec: A Fully Paid Compra / An Unlinked Gasto Does Not Mark A Compra As Paid — sin
    /// gastos ligados es <c>impaga</c>, con el total exacto o mayor es <c>pagada</c> (un
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

/// <summary>Estado de pago de una compra confirmada, derivado de sus gastos LIGADOS únicamente
/// (spec: saldo-de-proveedor / Per-Compra Payment Status From Linked Gastos Only) — un gasto sin
/// ligar reduce el saldo total pero no resuelve el estado de ninguna compra puntual.</summary>
public enum EstadoPago
{
    Impaga,
    Parcial,
    Pagada
}

/// <summary>Una compra confirmada del proveedor con su estado de pago derivado —
/// <see cref="Pagado"/> es la suma de los gastos LIGADOS a esta compra puntual, nunca el saldo
/// general del proveedor.</summary>
public sealed record CompraConEstadoPago(
    int IdComprobanteCompra, string? NumeroExterno, decimal Total, decimal Pagado, EstadoPago EstadoPago);

/// <summary>Respuesta de <c>GET /api/proveedores/{id}/saldo</c> (design: API Surface), byte-idéntica
/// a la forma pre-etapa 15 (task 4.10) — <see cref="Saldo"/> ahora es el INVARIANTE de
/// <c>proveedores.saldo</c> (spec: saldo-de-proveedor / Saldo Is The Single-Write-Authority Cache
/// Of The Ledger, REMOVED "Saldo Is An Approximation, Not An Invariant"): un gasto sin ligar la
/// reduce igual, aunque no salde ninguna compra puntual.</summary>
public sealed record SaldoDeProveedor(int IdProveedor, decimal Saldo, IReadOnlyList<CompraConEstadoPago> Compras);
