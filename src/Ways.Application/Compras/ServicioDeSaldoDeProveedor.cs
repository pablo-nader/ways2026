using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;

namespace Ways.Application.Compras;

/// <summary>
/// El saldo derivado del proveedor (design decisión 11, spec: saldo-de-proveedor) — dedicado,
/// nunca extiende <c>ServicioDeProveedores</c> (un ABM plano que no tiene por qué depender de
/// dos agregados operativos para servir una lectura). Sin tabla, sin caché, sin estado propio:
/// <c>Σ compras confirmadas − Σ gastos (categoria = proveedor)</c>, con el estado de pago
/// por-compra derivado de los gastos LIGADOS únicamente (spec: Per-Compra Payment Status From
/// Linked Gastos Only).
///
/// Exactamente 2 consultas (Data Flow del design), nunca N+1: la segunda agrupa TODOS los gastos
/// de categoría proveedor del proveedor por <c>id_comprobante_compra</c> (incluida la fila NULL,
/// que agrupa los gastos sin ligar) — de ahí sale tanto el total a restar del saldo como el
/// desglose por-compra, en un solo <c>GROUP BY</c> (task 4.2: "a single grouped query... no
/// N+1").
/// </summary>
public class ServicioDeSaldoDeProveedor(IWaysDbContext db)
{
    public async Task<SaldoDeProveedor> ObtenerAsync(int idProveedor, CancellationToken ct = default)
    {
        await ResolverProveedorAsync(idProveedor, ct);

        var compras = await db.ComprobantesCompra
            .Where(c => c.IdProveedor == idProveedor && c.Estado == EstadoCompra.Confirmada)
            .Select(c => new { c.Id, c.NumeroExterno, c.Total })
            .ToListAsync(ct);

        // Agrupado por id_comprobante_compra — la fila con clave null agrupa los gastos SIN
        // ligar (spec: An Unlinked Gasto Still Reduces The Total Saldo). Una sola consulta sirve
        // tanto al total (spec: Saldo Is A Derived Read) como al desglose por-compra (spec:
        // Per-Compra Payment Status From Linked Gastos Only).
        var gastosPorCompra = await db.Gastos
            .Where(g => g.IdProveedor == idProveedor && g.Categoria == CategoriaGasto.Proveedor)
            .GroupBy(g => g.IdComprobanteCompra)
            .Select(grupo => new { IdComprobanteCompra = grupo.Key, Total = grupo.Sum(g => g.Importe) })
            .ToListAsync(ct);

        var totalCompras = compras.Sum(c => c.Total);
        var totalGastos = gastosPorCompra.Sum(g => g.Total);

        var pagadoPorCompra = gastosPorCompra
            .Where(g => g.IdComprobanteCompra is not null)
            .ToDictionary(g => g.IdComprobanteCompra!.Value, g => g.Total);

        var comprasConEstado = compras
            .Select(c =>
            {
                var pagado = pagadoPorCompra.GetValueOrDefault(c.Id, 0m);
                var estado = ResolverEstadoPago(pagado, c.Total);
                return new CompraConEstadoPago(c.Id, c.NumeroExterno, c.Total, pagado, estado);
            })
            .OrderBy(c => c.IdComprobanteCompra)
            .ToList();

        return new SaldoDeProveedor(idProveedor, totalCompras - totalGastos, comprasConEstado);
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

    private async Task ResolverProveedorAsync(int idProveedor, CancellationToken ct)
    {
        var existe = await db.Proveedores.AnyAsync(p => p.Id == idProveedor, ct);
        if (!existe)
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" (spec: Cross-Tenant
            // Proveedor Saldo Is Invisible).
            throw ErrorDominio.NoEncontrado($"No existe el proveedor {idProveedor}.");
        }
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

/// <summary>Respuesta de <c>GET /api/proveedores/{id}/saldo</c> (design: API Surface) —
/// <see cref="Saldo"/> es la aproximación declarada por el spec (saldo-de-proveedor / Saldo Is
/// An Approximation, Not An Invariant): un gasto sin ligar la reduce igual, aunque no salde
/// ninguna compra puntual.</summary>
public sealed record SaldoDeProveedor(int IdProveedor, decimal Saldo, IReadOnlyList<CompraConEstadoPago> Compras);
