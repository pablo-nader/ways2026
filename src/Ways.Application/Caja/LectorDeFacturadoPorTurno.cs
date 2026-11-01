using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// Lo facturado por turno, repartido en efectivo, digitales y cuenta corriente (histórico de cajas): ventas (TX) y
/// notas de crédito (NCX) emitidas del turno, sin los recibos de cobranza. El reparto lo calcula
/// <see cref="ProrrateoDeFacturadoPorMedio"/>; una nota de crédito usa las proporciones de cobro de la venta que corrige
/// y el medio ancla del turno de ESA venta. Cantidad fija de consultas agrupadas para todos los turnos pedidos.
/// </summary>
public static class LectorDeFacturadoPorTurno
{
    private static readonly string[] CodigosFacturados = ["TX", "NCX"];

    public static async Task<Dictionary<int, FacturadoPorCubo>> LeerAsync(
        IWaysDbContext db, IReadOnlyList<int> idsTurno, CancellationToken ct)
    {
        var idsTipo = await db.TiposComprobante.AsNoTracking().IgnoreQueryFilters(["BajaLogica"])
            .Where(t => CodigosFacturados.Contains(t.Codigo))
            .Select(t => t.Id)
            .ToListAsync(ct);

        var comprobantes = await db.ComprobantesVenta.AsNoTracking()
            .Where(c => c.IdTurnoCaja != null && idsTurno.Contains(c.IdTurnoCaja.Value)
                && c.Estado == EstadoComprobante.Emitido && idsTipo.Contains(c.IdTipoComprobante))
            .Select(c => new { c.Id, IdTurno = c.IdTurnoCaja!.Value, c.Total, c.IdComprobanteAsociado })
            .ToListAsync(ct);

        var idsOriginales = comprobantes
            .Where(c => c.IdComprobanteAsociado != null)
            .Select(c => c.IdComprobanteAsociado!.Value)
            .Distinct()
            .ToList();

        var originales = idsOriginales.Count == 0
            ? []
            : await db.ComprobantesVenta.AsNoTracking()
                .Where(c => idsOriginales.Contains(c.Id))
                .Select(c => new { c.Id, c.IdTurnoCaja })
                .ToListAsync(ct);

        var idsConCobros = comprobantes.Select(c => c.Id).Concat(idsOriginales).Distinct().ToList();
        var cobros = idsConCobros.Count == 0
            ? []
            : await db.Cobros.AsNoTracking()
                .Where(p => idsConCobros.Contains(p.IdComprobanteVenta))
                .Select(p => new { p.IdComprobanteVenta, p.IdMedioPago, p.Importe, p.Vuelto })
                .ToListAsync(ct);

        var idsTurnoDeAncla = comprobantes.Select(c => c.IdTurno)
            .Concat(originales.Where(o => o.IdTurnoCaja != null).Select(o => o.IdTurnoCaja!.Value))
            .Distinct()
            .ToList();
        var anclaPorTurno = await db.TurnosCaja.AsNoTracking()
            .Where(t => idsTurnoDeAncla.Contains(t.Id))
            .Select(t => new { t.Id, t.IdMedioPagoEfectivo })
            .ToDictionaryAsync(t => t.Id, t => t.IdMedioPagoEfectivo, ct);

        var comportamiento = await db.MediosPago.AsNoTracking().IgnoreQueryFilters(["BajaLogica"])
            .Select(m => new { m.Id, m.Comportamiento })
            .ToDictionaryAsync(m => m.Id, m => m.Comportamiento, ct);

        var cobrosPorComprobante = cobros
            .GroupBy(p => p.IdComprobanteVenta)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CobroNetoDeVenta>)g
                    .Select(p => new CobroNetoDeVenta(p.IdMedioPago, p.Importe - p.Vuelto)).ToList());
        var turnoDelOriginal = originales.ToDictionary(o => o.Id, o => o.IdTurnoCaja);

        var resultado = idsTurno.ToDictionary(id => id, _ => default(FacturadoPorCubo));

        foreach (var c in comprobantes)
        {
            var idVentaOriginal = c.IdComprobanteAsociado ?? c.Id;
            var idTurnoDeLaVenta = c.IdComprobanteAsociado is { } idOriginal
                ? turnoDelOriginal.GetValueOrDefault(idOriginal) ?? c.IdTurno
                : c.IdTurno;

            var reparto = ProrrateoDeFacturadoPorMedio.Repartir(
                c.Total,
                cobrosPorComprobante.GetValueOrDefault(idVentaOriginal, []),
                comportamiento,
                anclaPorTurno.GetValueOrDefault(idTurnoDeLaVenta));
            resultado[c.IdTurno] += reparto;
        }

        return resultado;
    }
}
