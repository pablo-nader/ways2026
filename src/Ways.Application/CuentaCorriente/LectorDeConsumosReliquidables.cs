using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Ventas;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// El escaneo de elegibilidad de la reliquidación (design decision 3, task 3.3): 2 consultas fijas
/// (elegibilidad + items), independiente de N — el mismo presupuesto constante que el resto de la
/// cuenta corriente. DEBE llamarse DESPUÉS de que el llamador ya tomó el lock del cliente (design
/// decisión 4: "scan runs after it, inside the same transaction") — este lector no toma ningún
/// lock propio, confía en el del llamador.
/// </summary>
public class LectorDeConsumosReliquidables(IWaysDbContext db)
{
    /// <summary>Uno más que el cap real (<see cref="ReliquidadorDeConsumos.LimiteConsumosPorCorrida"/>)
    /// — el sentinel que le permite al calculador puro derivar <c>HayMas</c> de su propio input,
    /// sin una segunda consulta de conteo (design: Eligibility — "capped at 500 consumos per
    /// run").</summary>
    private const int LimiteDeLaConsulta = ReliquidadorDeConsumos.LimiteConsumosPorCorrida + 1;

    /// <summary>Elegibilidad (design: Eligibility, todos los predicados requeridos): <c>tipo =
    /// 'consumo'</c>, <c>id_movimiento_actualizacion IS NULL</c> (el índice parcial
    /// <c>ix_movimientos_cuenta_corriente_consumos_pendientes</c> ES esta predicate), <c>importe
    /// &gt; 0</c>, el comprobante <c>estado = 'emitido'</c> (un anulado NUNCA se reliquida — deja
    /// pasar la deuda contra-movida por la anulación) y <c>comprobante.total &gt; 0</c>. Ordenado
    /// por <c>fecha ASC, id ASC</c> — el desempate por <c>id</c> hace determinístico el corte del
    /// slot 500 cuando dos consumos comparten <c>fecha</c>.</summary>
    public async Task<IReadOnlyList<ConsumoAReliquidar>> LeerElegiblesAsync(int idCliente, CancellationToken ct)
    {
        var elegibles = await (
                from m in db.MovimientosCuentaCorriente
                join c in db.ComprobantesVenta on m.IdComprobanteVenta equals (int?)c.Id
                where m.IdCliente == idCliente
                    && m.Tipo == TipoMovimientoCc.Consumo
                    && m.IdMovimientoActualizacion == null
                    && m.Importe > 0
                    && c.Estado == EstadoComprobante.Emitido
                    && c.Total > 0
                orderby m.Fecha ascending, m.Id ascending
                select new { m.Id, IdComprobanteVenta = c.Id, m.Importe, c.Total })
            .Take(LimiteDeLaConsulta)
            .ToListAsync(ct);

        if (elegibles.Count == 0)
        {
            return [];
        }

        var idsComprobante = elegibles.Select(e => e.IdComprobanteVenta).Distinct().ToList();
        var items = await db.ItemsComprobanteVenta
            .Where(i => idsComprobante.Contains(i.IdComprobanteVenta))
            .Select(i => new { i.IdComprobanteVenta, i.IdArticulo, i.Cantidad, i.PrecioUnitario, i.Descuento, i.Total })
            .ToListAsync(ct);

        var itemsPorComprobante = items.ToLookup(i => i.IdComprobanteVenta);

        return elegibles
            .Select(e => new ConsumoAReliquidar(
                e.Id, e.IdComprobanteVenta, e.Importe, e.Total,
                itemsPorComprobante[e.IdComprobanteVenta]
                    .Select(i => new LineaAReliquidar(i.IdArticulo, i.Cantidad, i.PrecioUnitario, i.Descuento, i.Total))
                    .ToList()))
            .ToList();
    }
}
