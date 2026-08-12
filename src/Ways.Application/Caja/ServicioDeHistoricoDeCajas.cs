using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// G2 histórico de cierres (design: G2/G3 — minimal aggregation; spec historico-de-cajas: G2
/// Histórico Lists Closed Turnos Only, With Totals From Persisted Arqueos) — la ÚNICA agregación
/// nueva de la slice: para un turno YA CERRADO los totales salen de sumar las filas persistidas de
/// <see cref="ArqueoTurno"/>, jamás re-corriendo <see cref="Ways.Domain.Caja.CalculadorDeArqueo"/>
/// (que exige un turno todavía abierto). <see cref="EgresosDeTurno"/> reusa la MISMA definición que
/// <see cref="ServicioDeResumenDeTurno"/> — gastos por categoría/área más retiros — pero agrupada
/// de una sola vez para toda la página (nunca una consulta por fila, mismo criterio de "cantidad
/// fija de consultas agrupadas" que <see cref="LectorDeContenidoDeResumen"/>).
/// </summary>
public class ServicioDeHistoricoDeCajas(IWaysDbContext db)
{
    public async Task<PaginaDeHistoricoDeCajas> ListarCierresAsync(
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        // "histórico de cierres" (doc 01 G2) — solo turnos cerrados; un turno abierto NUNCA
        // aparece acá (spec: An Open Turno Is Excluded From The Listing), tiene su propia lectura
        // en /caja/turnos/abierto con totales parciales, no un cierre.
        var query = db.TurnosCaja.Where(t => t.Estado == EstadoTurno.Cerrado);

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(t => t.IdPuntoVenta == pv);
        }

        if (desde is { } d)
        {
            query = query.Where(t => t.FechaCierre >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(t => t.FechaCierre <= h);
        }

        var total = await query.CountAsync(ct);

        var turnosDeLaPagina = await query
            .OrderByDescending(t => t.FechaCierre)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(t => new { t.Id, t.IdPuntoVenta, t.FechaApertura, t.FechaCierre })
            .ToListAsync(ct);

        var ids = turnosDeLaPagina.Select(t => t.Id).ToList();

        if (ids.Count == 0)
        {
            return new PaginaDeHistoricoDeCajas([], total, pagina, tamanio);
        }

        // Totales — Σ de las filas YA persistidas del cierre, una consulta agrupada para toda la
        // página (design: "un GroupBy sobre ArqueosTurno para los ids de la página").
        var totalesPorTurno = await db.ArqueosTurno
            .Where(a => ids.Contains(a.IdTurnoCaja))
            .GroupBy(a => a.IdTurnoCaja)
            .Select(g => new
            {
                IdTurnoCaja = g.Key,
                Esperado = g.Sum(a => a.ImporteEsperado),
                Declarado = g.Sum(a => a.ImporteDeclarado),
                Diferencia = g.Sum(a => a.Diferencia)
            })
            .ToDictionaryAsync(x => x.IdTurnoCaja, ct);

        var egresosPorTurno = await LeerEgresosDeLaPaginaAsync(ids, ct);

        var items = turnosDeLaPagina
            .Select(t =>
            {
                var totales = totalesPorTurno.GetValueOrDefault(t.Id);
                var egresos = egresosPorTurno.GetValueOrDefault(t.Id, new EgresosDeTurno([], [], 0m));

                return new FilaDeHistoricoDeCajas(
                    t.Id, t.IdPuntoVenta, t.FechaApertura,
                    // ck_turnos_caja_cierre_consistente garantiza fecha_cierre no nula para un
                    // turno cerrado — el filtro Estado == Cerrado de arriba ya lo exige.
                    t.FechaCierre!.Value,
                    totales?.Esperado ?? 0m, totales?.Declarado ?? 0m, totales?.Diferencia ?? 0m,
                    egresos);
            })
            .ToList();

        return new PaginaDeHistoricoDeCajas(items, total, pagina, tamanio);
    }

    /// <summary>Egresos de toda la página en tres consultas agrupadas de cantidad FIJA (nunca una
    /// por turno) — mismo criterio que <see cref="LectorDeContenidoDeResumen"/>, pero agrupando
    /// también por <c>id_turno_caja</c> para repartir el resultado entre las filas de la página.
    /// </summary>
    private async Task<Dictionary<int, EgresosDeTurno>> LeerEgresosDeLaPaginaAsync(
        IReadOnlyList<int> ids, CancellationToken ct)
    {
        var gastosPorCategoria = await db.Gastos
            .Where(g => ids.Contains(g.IdTurnoCaja))
            .GroupBy(g => new { g.IdTurnoCaja, g.Categoria })
            .Select(g => new { g.Key.IdTurnoCaja, g.Key.Categoria, Total = g.Sum(x => x.Importe) })
            .ToListAsync(ct);

        var gastosPorArea = await db.Gastos
            .Where(g => ids.Contains(g.IdTurnoCaja))
            .GroupBy(g => new { g.IdTurnoCaja, g.IdArea })
            .Select(g => new { g.Key.IdTurnoCaja, g.Key.IdArea, Total = g.Sum(x => x.Importe) })
            .ToListAsync(ct);

        var retirosPorTurno = await db.MovimientosCaja
            .Where(m => ids.Contains(m.IdTurnoCaja) && m.Tipo == TipoMovimientoCaja.Retiro)
            .GroupBy(m => m.IdTurnoCaja)
            .Select(g => new { IdTurnoCaja = g.Key, Total = g.Sum(x => x.Importe) })
            .ToDictionaryAsync(x => x.IdTurnoCaja, x => x.Total, ct);

        var areas = await db.Areas.Select(a => new { a.Id, a.Nombre }).ToListAsync(ct);

        return ids.ToDictionary(
            id => id,
            id =>
            {
                var porCategoria = gastosPorCategoria
                    .Where(x => x.IdTurnoCaja == id)
                    .OrderBy(x => x.Categoria)
                    .Select(x => new EgresoPorCategoria(x.Categoria, x.Total))
                    .ToList();

                var porArea = gastosPorArea
                    .Where(x => x.IdTurnoCaja == id)
                    .OrderBy(x => x.IdArea)
                    .Select(x => new EgresoPorArea(
                        x.IdArea,
                        x.IdArea.HasValue
                            ? areas.FirstOrDefault(a => a.Id == x.IdArea)?.Nombre ?? $"Área #{x.IdArea}"
                            : "Sin área",
                        x.Total))
                    .ToList();

                return new EgresosDeTurno(porCategoria, porArea, retirosPorTurno.GetValueOrDefault(id));
            });
    }
}
