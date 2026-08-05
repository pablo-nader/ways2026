using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// El único lector de los insumos de la derivación (design decisión 5; The Cierre Transaction,
/// paso 2) — 7 consultas agrupadas de cantidad FIJA, nunca una por fila (Testing Strategy:
/// Integration (budget), tasks 4.3/4.14): pagos por medio, vueltos por medio, gastos por medio,
/// refuerzos, retiros, fondo inicial y el catálogo completo de medios. Compartido tal cual por
/// <c>ServicioDeTurnos.CerrarAsync</c> y <c>ServicioDeResumenDeTurno</c> — la única fuente de
/// <see cref="InsumosDeArqueo"/> que existe en el sistema (spec: Resumen Parcial Uses The Same
/// Derivation As Cierre).
/// </summary>
public class LectorDeMovimientosDelTurno(IWaysDbContext db)
{
    public async Task<InsumosDeArqueo> LeerAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        // PagoComprobante no tiene navigation property a ComprobanteVenta (convención del
        // proyecto: FKs sin propiedad de navegación, ver PagoComprobanteConfiguration) — join
        // explícito por id_comprobante_venta en vez de una propiedad de navegación inexistente.
        var pagosDelTurno = db.PagosComprobante
            .Join(
                db.ComprobantesVenta,
                p => p.IdComprobanteVenta,
                c => c.Id,
                (p, c) => new { p.IdMedioPago, p.Importe, p.Vuelto, c.IdTurnoCaja, c.Estado })
            .Where(x => x.IdTurnoCaja == idTurnoCaja && x.Estado == EstadoComprobante.Emitido);

        // 1. pagos por medio — solo comprobantes emitido (spec: Anulados Are Excluded From The
        // Derivation); un NCX aporta un importe negativo sin rama especial (stage-5 decisión 4).
        var pagosPorMedio = await pagosDelTurno
            .GroupBy(x => x.IdMedioPago)
            .Select(g => new { IdMedioPago = g.Key, Total = g.Sum(x => x.Importe) })
            .ToDictionaryAsync(x => x.IdMedioPago, x => x.Total, ct);

        // 2. vueltos por medio — mismo filtro; CalculadorDeArqueo suma esto sobre TODOS los
        // medios para restarlo únicamente en la línea del ancla (design decisión 2).
        var vueltosPorMedio = await pagosDelTurno
            .GroupBy(x => x.IdMedioPago)
            .Select(g => new { IdMedioPago = g.Key, Total = g.Sum(x => x.Vuelto) })
            .ToDictionaryAsync(x => x.IdMedioPago, x => x.Total, ct);

        // 3. gastos por medio — el gasto resta según SU PROPIO id_medio_pago, nunca todo al ancla.
        var gastosPorMedio = await db.Gastos
            .Where(g => g.IdTurnoCaja == idTurnoCaja)
            .GroupBy(g => g.IdMedioPago)
            .Select(g => new { IdMedioPago = g.Key, Total = g.Sum(x => x.Importe) })
            .ToDictionaryAsync(x => x.IdMedioPago, x => x.Total, ct);

        // 4. refuerzos — solo del ancla (aplicado en CalculadorDeArqueo).
        var refuerzos = await db.MovimientosCaja
            .Where(m => m.IdTurnoCaja == idTurnoCaja && m.Tipo == TipoMovimientoCaja.Refuerzo)
            .SumAsync(m => m.Importe, ct);

        // 5. retiros — ídem.
        var retiros = await db.MovimientosCaja
            .Where(m => m.IdTurnoCaja == idTurnoCaja && m.Tipo == TipoMovimientoCaja.Retiro)
            .SumAsync(m => m.Importe, ct);

        // 6. fondo inicial del turno — lectura propia (no depende del RETURNING de la UPDATE
        // atómica del cierre): el resumen parcial necesita este mismo dato sobre un turno
        // TODAVÍA abierto, así que el lector siempre lo pide de nuevo.
        var fondoInicial = await db.TurnosCaja
            .Where(t => t.Id == idTurnoCaja)
            .Select(t => t.FondoInicial)
            .FirstAsync(ct);

        // 7. catálogo completo de medios — TODAS las filas, sin filtrar Activo (design decisión
        // 3: un medio desactivado a mitad de turno puede seguir teniendo pagos). Universo
        // completo de ActividadDeMedio: todo medio del catálogo aparece acá, tenga o no
        // actividad — CalculadorDeArqueo decide qué queda.
        var medios = await db.MediosPago
            .Select(m => new { m.Id, m.Comportamiento })
            .ToListAsync(ct);

        var actividad = medios
            .Select(m => new ActividadDeMedio(
                m.Id,
                m.Comportamiento,
                pagosPorMedio.GetValueOrDefault(m.Id),
                vueltosPorMedio.GetValueOrDefault(m.Id),
                gastosPorMedio.GetValueOrDefault(m.Id),
                pagosPorMedio.ContainsKey(m.Id) || gastosPorMedio.ContainsKey(m.Id)))
            .ToList();

        return new InsumosDeArqueo(fondoInicial, refuerzos, retiros, actividad);
    }
}
