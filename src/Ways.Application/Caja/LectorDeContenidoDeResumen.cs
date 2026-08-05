using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Gastos;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// Contenido de reporte del resumen parcial (follow-up de la etapa 6, "Resumen parcial
/// D6-content enrichment" — legacy doc 01 D6: tickets, ingresos por área, egresos por
/// categoría). Deliberadamente SEPARADO de <see cref="LectorDeMovimientosDelTurno"/>: ese lector
/// alimenta la ÚNICA fórmula que comparte el resumen con el cierre
/// (<see cref="CalculadorDeArqueo"/>) y su recuento de consultas está protegido por un test de
/// presupuesto — este lector solo arma contenido de lectura adicional, nunca toca esa fórmula
/// ni su set de insumos.
///
/// 7 consultas agrupadas de cantidad FIJA (nunca una por fila, mismo criterio que el lector
/// hermano): cantidad de tickets, primer ticket, último ticket, ingresos por área, catálogo de
/// áreas, egresos por categoría y egresos por área — el turno-con-más-tickets test (task 4.14
/// original) sigue pasando porque cada una de estas consultas es una agregación, no una lectura
/// por ticket.
/// </summary>
public class LectorDeContenidoDeResumen(IWaysDbContext db)
{
    public async Task<ContenidoDeResumen> LeerAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        var comprobantesDelTurno = db.ComprobantesVenta
            .Where(c => c.IdTurnoCaja == idTurnoCaja && c.Estado == EstadoComprobante.Emitido);

        // 1. cantidad de tickets — solo comprobantes emitido (mismo filtro que la derivación:
        // spec Anulados Are Excluded From The Derivation).
        var cantidadTickets = await comprobantesDelTurno.CountAsync(ct);

        // 2. primer ticket — orden por fecha, desempate estable por id. Join con tipos_comprobante
        // para traer el código (TX, RC, …): cada tipo numera su PROPIA serie independiente
        // (stage-7-cuenta-corriente, design decisión 7), así que "el primer ticket" mezcla series
        // sin relación entre sí — el código es lo que lo hace legible, no un dato accesorio.
        var primerTicket = await comprobantesDelTurno
            .OrderBy(c => c.Fecha).ThenBy(c => c.Id)
            .Join(db.TiposComprobante, c => c.IdTipoComprobante, t => t.Id, (c, t) => new TicketLimite(c.Numero, c.Fecha, t.Codigo))
            .FirstOrDefaultAsync(ct);

        // 3. último ticket — ídem, orden inverso.
        var ultimoTicket = await comprobantesDelTurno
            .OrderByDescending(c => c.Fecha).ThenByDescending(c => c.Id)
            .Join(db.TiposComprobante, c => c.IdTipoComprobante, t => t.Id, (c, t) => new TicketLimite(c.Numero, c.Fecha, t.Codigo))
            .FirstOrDefaultAsync(ct);

        // 4. ingresos por área — snapshot inmutable de ItemComprobanteVenta.IdArea (doc 10
        // principio 6), nunca re-derivado de articulos. Una RC (pago a cuenta) no tiene items —
        // cero por construcción (stage-7-cuenta-corriente) — así que su plata nunca aparece acá;
        // sí aparece en el esperado por medio del arqueo (paso 7 de LectorDeMovimientosDelTurno),
        // igual que el legacy: las filas tipo=3 tampoco traían líneas de artículo. Es paridad
        // deliberada, no un bug.
        var totalesPorArea = await db.ItemsComprobanteVenta
            .Join(
                db.ComprobantesVenta,
                i => i.IdComprobanteVenta,
                c => c.Id,
                (i, c) => new { i.IdArea, i.Total, c.IdTurnoCaja, c.Estado })
            .Where(x => x.IdTurnoCaja == idTurnoCaja && x.Estado == EstadoComprobante.Emitido)
            .GroupBy(x => x.IdArea)
            .Select(g => new { IdArea = g.Key, Total = g.Sum(x => x.Total) })
            .ToDictionaryAsync(x => x.IdArea, x => x.Total, ct);

        // 5. catálogo completo de áreas — universo fijo (chico, propio del tenant), mismo
        // criterio que el catálogo de medios del lector hermano (paso 7 de LectorDeMovimientosDelTurno).
        var areas = await db.Areas
            .Select(a => new { a.Id, a.Nombre })
            .ToListAsync(ct);

        var ingresosPorArea = totalesPorArea
            .Select(kv => new IngresoPorArea(
                kv.Key, areas.FirstOrDefault(a => a.Id == kv.Key)?.Nombre ?? $"Área #{kv.Key}", kv.Value))
            .OrderBy(i => i.IdArea)
            .ToList();

        // 6. egresos por categoría — gastos del turno agrupados; los retiros NO son un gasto
        // (spec: No Magic Tipo Encodes A Retiro As A Gasto) y llegan de afuera (insumos.Retiros,
        // ya leído por LectorDeMovimientosDelTurno para la misma llamada — nunca se re-consulta).
        var egresosPorCategoria = await db.Gastos
            .Where(g => g.IdTurnoCaja == idTurnoCaja)
            .GroupBy(g => g.Categoria)
            .OrderBy(g => g.Key)
            .Select(g => new EgresoPorCategoria(g.Key, g.Sum(x => x.Importe)))
            .ToListAsync(ct);

        // 7. egresos por área — mismo criterio que ingresos por área (catálogo ya cargado en el
        // paso 5), pero Gasto.IdArea es NULLABLE (a diferencia del snapshot inmutable del ítem de
        // venta): los gastos sin área declarada se agrupan bajo un bucket "Sin área" con IdArea
        // null, en vez de descartarlos.
        var totalesEgresoPorArea = await db.Gastos
            .Where(g => g.IdTurnoCaja == idTurnoCaja)
            .GroupBy(g => g.IdArea)
            .Select(g => new { IdArea = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync(ct);

        var egresosPorArea = totalesEgresoPorArea
            .Select(x => new EgresoPorArea(
                x.IdArea,
                x.IdArea.HasValue
                    ? areas.FirstOrDefault(a => a.Id == x.IdArea)?.Nombre ?? $"Área #{x.IdArea}"
                    : "Sin área",
                x.Total))
            .OrderBy(e => e.IdArea)
            .ToList();

        return new ContenidoDeResumen(
            cantidadTickets, primerTicket, ultimoTicket, ingresosPorArea, egresosPorCategoria, egresosPorArea);
    }
}

/// <summary>Salida de <see cref="LectorDeContenidoDeResumen"/> — todavía sin <c>Retiros</c>: ese
/// dato ya lo trae <see cref="InsumosDeArqueo"/> (el mismo insumo que <see
/// cref="CalculadorDeArqueo"/> usa), así que <see cref="ServicioDeResumenDeTurno"/> lo combina
/// desde ahí en vez de volver a consultarlo.</summary>
public sealed record ContenidoDeResumen(
    int CantidadTickets,
    TicketLimite? PrimerTicket,
    TicketLimite? UltimoTicket,
    IReadOnlyList<IngresoPorArea> IngresosPorArea,
    IReadOnlyList<EgresoPorCategoria> EgresosPorCategoria,
    IReadOnlyList<EgresoPorArea> EgresosPorArea);
