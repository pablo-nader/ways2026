using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Listados;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// G2 histórico de cierres (design: G2/G3 — minimal aggregation; spec historico-de-cajas: G2
/// Histórico Lists Closed Turnos Only, With Totals From Persisted Arqueos) — la ÚNICA agregación
/// nueva de la slice: para un turno YA CERRADO los totales salen de sumar las filas persistidas de
/// <see cref="ArqueoTurno"/>, jamás re-corriendo <see cref="Ways.Domain.Caja.CalculadorDeArqueo"/>
/// (un recálculo administrativo ya dejó esas filas al día). <see cref="EgresosDeTurno"/> reusa la MISMA definición que
/// <see cref="ServicioDeResumenDeTurno"/> — pagos por categoría/área más retiros — pero agrupada
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
        string? orden = null,
        string? direccion = null,
        CancellationToken ct = default)
    {
        var ordenPedido = OrdenDeListado.Interpretar(orden, direccion, ClavesDeOrden);

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idPuntoVenta, desde, hasta);

        var total = await query.CountAsync(ct);

        var turnosDeLaPagina = await Ordenar(query, ordenPedido)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(t => new TurnoBase(t.Id, t.IdPuntoVenta, t.FechaApertura, t.FechaCierre, t.FechaRecalculo, t.IdEmpleadoRecalculo))
            .ToListAsync(ct);

        var items = await ArmarFilasAsync(turnosDeLaPagina, ct);

        return new PaginaDeHistoricoDeCajas(items, total, pagina, tamanio);
    }

    /// <summary>Sin orden pedido: cierre descendente (el orden de siempre del listado; el export sigue con él). Con
    /// orden: la columna pedida y SIEMPRE <c>Id</c> ascendente como desempate. El punto de venta ordena por su
    /// nombre y uno dado de baja o con FK colgante llega vacío (<c>db.PuntosVenta</c> ya excluye las bajas) y va al
    /// final; esperado, declarado y diferencia, por la misma suma de arqueos persistidos que muestra la fila (un
    /// turno sin arqueos es 0, como la celda).</summary>
    private IOrderedQueryable<TurnoCaja> Ordenar(
        IQueryable<TurnoCaja> query, OrdenDeListado<ColumnaDeOrdenDeHistoricoDeCajas>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderByDescending(t => t.FechaCierre);
        }

        var desc = pedido.Descendente;
        var ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeHistoricoDeCajas.Turno => OrdenarPor.Clave(query, t => t.Id, desc),
            ColumnaDeOrdenDeHistoricoDeCajas.PuntoVenta => OrdenarPor.ClaveConVacios(
                query,
                t => db.PuntosVenta.Where(pv => pv.Id == t.IdPuntoVenta).Select(pv => pv.Nombre).FirstOrDefault(),
                desc),
            ColumnaDeOrdenDeHistoricoDeCajas.Apertura => OrdenarPor.Clave(query, t => t.FechaApertura, desc),
            ColumnaDeOrdenDeHistoricoDeCajas.Cierre => OrdenarPor.Clave(query, t => t.FechaCierre, desc),
            ColumnaDeOrdenDeHistoricoDeCajas.Esperado => OrdenarPor.Clave(
                query, t => db.ArqueosTurno.Where(a => a.IdTurnoCaja == t.Id).Sum(a => a.ImporteEsperado), desc),
            ColumnaDeOrdenDeHistoricoDeCajas.Declarado => OrdenarPor.Clave(
                query, t => db.ArqueosTurno.Where(a => a.IdTurnoCaja == t.Id).Sum(a => a.ImporteDeclarado), desc),
            ColumnaDeOrdenDeHistoricoDeCajas.Diferencia => OrdenarPor.Clave(
                query, t => db.ArqueosTurno.Where(a => a.IdTurnoCaja == t.Id).Sum(a => a.Diferencia), desc),
            _ => query.OrderByDescending(t => t.FechaCierre),
        };

        return ordenado.ThenBy(t => t.Id);
    }

    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeHistoricoDeCajas> ClavesDeOrden =
        new Dictionary<string, ColumnaDeOrdenDeHistoricoDeCajas>(StringComparer.Ordinal)
        {
            ["turno"] = ColumnaDeOrdenDeHistoricoDeCajas.Turno,
            ["puntoVenta"] = ColumnaDeOrdenDeHistoricoDeCajas.PuntoVenta,
            ["apertura"] = ColumnaDeOrdenDeHistoricoDeCajas.Apertura,
            ["cierre"] = ColumnaDeOrdenDeHistoricoDeCajas.Cierre,
            ["esperado"] = ColumnaDeOrdenDeHistoricoDeCajas.Esperado,
            ["declarado"] = ColumnaDeOrdenDeHistoricoDeCajas.Declarado,
            ["diferencia"] = ColumnaDeOrdenDeHistoricoDeCajas.Diferencia,
        };

    /// <summary>
    /// Export sibling (stage-11-exportacion-reportes, Slice 5b) — a diferencia del resto de
    /// reportes agregados de la etapa (design decisión 6), un turno NO es un catálogo acotado como
    /// un punto de venta, un vendedor o un medio de pago: se acumula sin límite, igual que una
    /// venta. Reusar <see cref="ListarCierresAsync"/> tal cual (paginado, tope duro de 200 por el
    /// <c>Math.Clamp</c> de arriba) truncaría en silencio cualquier exportación con más de 200
    /// cierres sin que <see cref="GuardaDeTope"/> llegara a dispararse — exactamente el riesgo que
    /// esta etapa existe para evitar. Este método corre el mismo patrón <c>Contar → rechazar →
    /// Take(tope + 1) → rechazar</c> que <c>ServicioDeVentas.ListarParaExportacionAsync</c>
    /// (design decisión 7), reusando el MISMO <see cref="ConstruirQuery"/> que <see
    /// cref="ListarCierresAsync"/> — nunca una segunda declaración del predicado.
    /// </summary>
    public async Task<IReadOnlyList<FilaDeHistoricoDeCajas>> ListarCierresParaExportacionAsync(
        int? idPuntoVenta,
        DateTimeOffset? desde,
        DateTimeOffset? hasta,
        int topeDeFilas,
        CancellationToken ct = default)
    {
        var query = ConstruirQuery(idPuntoVenta, desde, hasta);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var turnos = await query
            .OrderByDescending(t => t.FechaCierre)
            .Take(topeDeFilas + 1)
            .Select(t => new TurnoBase(t.Id, t.IdPuntoVenta, t.FechaApertura, t.FechaCierre, t.FechaRecalculo, t.IdEmpleadoRecalculo))
            .ToListAsync(ct);

        GuardaDeTope.Exigir(turnos.Count, topeDeFilas);

        return await ArmarFilasAsync(turnos, ct);
    }

    /// <summary>Filtro compartido de <see cref="ListarCierresAsync"/> y
    /// <see cref="ListarCierresParaExportacionAsync"/> — "histórico de cierres" (doc 01 G2): solo
    /// turnos cerrados, un turno abierto NUNCA aparece acá (spec: An Open Turno Is Excluded From
    /// The Listing), tiene su propia lectura en <c>/caja/turnos/abierto</c> con totales parciales,
    /// no un cierre.</summary>
    private IQueryable<TurnoCaja> ConstruirQuery(int? idPuntoVenta, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
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

        return query;
    }

    private async Task<List<FilaDeHistoricoDeCajas>> ArmarFilasAsync(IReadOnlyList<TurnoBase> turnos, CancellationToken ct)
    {
        var ids = turnos.Select(t => t.Id).ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        // Totales — Σ de las filas YA persistidas del cierre, una consulta agrupada para todo el
        // conjunto (design: "un GroupBy sobre ArqueosTurno para los ids de la página").
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
        var facturadoPorTurno = await LectorDeFacturadoPorTurno.LeerAsync(db, ids, ct);

        return turnos
            .Select(t =>
            {
                var totales = totalesPorTurno.GetValueOrDefault(t.Id);
                var egresos = egresosPorTurno.GetValueOrDefault(t.Id, new EgresosDeTurno([], [], 0m));
                var facturado = facturadoPorTurno.GetValueOrDefault(t.Id);

                return new FilaDeHistoricoDeCajas(
                    t.Id, t.IdPuntoVenta, t.FechaApertura,
                    // ck_turnos_caja_cierre_consistente garantiza fecha_cierre no nula para un
                    // turno cerrado — ConstruirQuery ya exige Estado == Cerrado.
                    t.FechaCierre!.Value,
                    totales?.Esperado ?? 0m, totales?.Declarado ?? 0m, totales?.Diferencia ?? 0m,
                    egresos, t.FechaRecalculo, t.IdEmpleadoRecalculo,
                    facturado.Digitales, facturado.Efectivo, facturado.CuentaCorriente, facturado.Total);
            })
            .ToList();
    }

    private sealed record TurnoBase(
        int Id, int IdPuntoVenta, DateTimeOffset FechaApertura, DateTimeOffset? FechaCierre,
        DateTimeOffset? FechaRecalculo, int? IdEmpleadoRecalculo);

    /// <summary>Egresos de toda la página en tres consultas agrupadas de cantidad FIJA (nunca una
    /// por turno) — mismo criterio que <see cref="LectorDeContenidoDeResumen"/>, pero agrupando
    /// también por <c>id_turno_caja</c> para repartir el resultado entre las filas de la página.
    /// </summary>
    private async Task<Dictionary<int, EgresosDeTurno>> LeerEgresosDeLaPaginaAsync(
        IReadOnlyList<int> ids, CancellationToken ct)
    {
        // Pago.IdTurnoCaja ya es nullable (GastosOrigenFondosYTesoreriaPorEmpresa) — un pago sin
        // turno no pertenece a ninguna página de historico-de-turnos, así que se excluye con el
        // mismo `!= null` que cierra el hueco de tipos de ids.Contains<int>.
        var pagosPorCategoria = await db.Pagos
            .Where(g => g.IdTurnoCaja != null && ids.Contains(g.IdTurnoCaja.Value))
            .GroupBy(g => new { g.IdTurnoCaja, g.Categoria })
            .Select(g => new { g.Key.IdTurnoCaja, g.Key.Categoria, Total = g.Sum(x => x.Importe) })
            .ToListAsync(ct);

        var pagosPorArea = await db.Pagos
            .Where(g => g.IdTurnoCaja != null && ids.Contains(g.IdTurnoCaja.Value))
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
                var porCategoria = pagosPorCategoria
                    .Where(x => x.IdTurnoCaja == id)
                    .OrderBy(x => x.Categoria)
                    .Select(x => new EgresoPorCategoria(x.Categoria, x.Total))
                    .ToList();

                var porArea = pagosPorArea
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
