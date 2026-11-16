using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Pagos;

namespace Ways.Application.Caja;

/// <summary>
/// Saldos de tesorería de una empresa (efectivo de la caja general y cada cuenta) calculados desde las fuentes —turnos, pagos y
/// ajustes—, no desde las cadenas <c>movimientos_tesoreria</c> / <c>movimientos_cuenta</c>. Lo comparten el libro de tesorería y el
/// reporte de valoración, para que los dos sumen exactamente lo mismo. El reparto de cada fuente está descripto en
/// <see cref="ServicioDeLibroDeTesoreria"/>.
/// </summary>
public class AcumuladorDeSaldosDeTesoreria(IWaysDbContext db)
{
    public sealed record CuentaBase(int Id, string Nombre, bool Activo, decimal SaldoInicial, bool Eliminada);

    public sealed record MedioBase(int Id, string Nombre, ComportamientoMedioPago Comportamiento, int? IdCuentaTesoreria);

    public sealed record PuntoVentaBase(int Id, string Nombre, bool Eliminado);

    public sealed record Acumulado(decimal Efectivo, IReadOnlyDictionary<int, decimal> PorCuenta);

    private sealed record LineaAgrupada(int IdMedioPago, int? IdMedioEfectivoDelTurno, decimal Total);

    /// <summary>Lo que hace falta leer una sola vez por empresa para acumular saldos a cualquier instante.</summary>
    public sealed record ContextoDeSaldos(
        int IdEmpresa,
        IReadOnlyList<CuentaBase> Cuentas,
        HashSet<int> IdsDeCuentasVivas,
        IReadOnlyDictionary<int, MedioBase> Medios,
        IReadOnlyList<PuntoVentaBase> PuntosVenta,
        IQueryable<TurnoCaja> TurnosCerrados,
        Func<int, int?, DestinoDeLinea> Rutear);

    private static readonly Expression<Func<MovimientoCuenta, bool>> EsDeCierre = m =>
        m.Tipo == TipoMovimientoCuenta.Cobro || m.Tipo == TipoMovimientoCuenta.Comision
        || m.Tipo == TipoMovimientoCuenta.RetencionIibb || m.Tipo == TipoMovimientoCuenta.RetencionIva
        || m.Tipo == TipoMovimientoCuenta.RetencionGanancias;

    public static Expression<Func<MovimientoCuenta, bool>> MovimientoDeCierre => EsDeCierre;

    public async Task<ContextoDeSaldos> CargarAsync(int idEmpresa, CancellationToken ct)
    {
        var cuentas = await db.CuentasTesoreria
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(c => c.IdEmpresa == idEmpresa)
            .Select(c => new CuentaBase(c.Id, c.Nombre, c.Activo, c.SaldoInicial, c.DeletedAt != null))
            .ToListAsync(ct);

        var idsDeCuentasVivas = cuentas.Where(c => !c.Eliminada).Select(c => c.Id).ToHashSet();

        var medios = (await db.MediosPago
                .Select(m => new MedioBase(m.Id, m.Nombre, m.Comportamiento, m.IdCuentaTesoreria))
                .ToListAsync(ct))
            .ToDictionary(m => m.Id);

        var puntosVenta = await db.PuntosVenta
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(pv => pv.IdEmpresa == idEmpresa)
            .Select(pv => new PuntoVentaBase(pv.Id, pv.Nombre, pv.DeletedAt != null))
            .ToListAsync(ct);
        var idsDePuntosVenta = puntosVenta.Select(pv => pv.Id).ToList();

        var turnosCerrados = db.TurnosCaja.Where(t =>
            t.Estado == EstadoTurno.Cerrado && t.FechaCierre != null && idsDePuntosVenta.Contains(t.IdPuntoVenta));

        DestinoDeLinea Rutear(int idMedioPago, int? idMedioEfectivoDelTurno)
        {
            var medio = medios.GetValueOrDefault(idMedioPago);
            var esEfectivo = ReglasDeLibroDeTesoreria.EsMedioEfectivo(medio?.Comportamiento, idMedioPago, idMedioEfectivoDelTurno);
            var cuentaDeLaEmpresa = medio?.IdCuentaTesoreria is { } id && idsDeCuentasVivas.Contains(id) ? id : (int?)null;
            return ReglasDeLibroDeTesoreria.RutearLineaDePago(esEfectivo, cuentaDeLaEmpresa);
        }

        return new ContextoDeSaldos(idEmpresa, cuentas, idsDeCuentasVivas, medios, puntosVenta, turnosCerrados, Rutear);
    }

    /// <summary>Todo lo anterior a <paramref name="corte"/> (o todo, si es <c>null</c>), agregado en SQL por fuente. Con
    /// <paramref name="inclusivo"/> el límite también entra (<c>&lt;=</c>); el libro pide el saldo anterior a su rango con el límite
    /// excluido (<c>&lt;</c>).</summary>
    public async Task<Acumulado> AcumularAsync(
        ContextoDeSaldos contexto, DateTimeOffset? corte, bool inclusivo, CancellationToken ct)
    {
        var idEmpresa = contexto.IdEmpresa;
        var rutear = contexto.Rutear;
        var efectivo = 0m;
        var porCuenta = new Dictionary<int, decimal>();

        void Sumar(DestinoDeLinea destino, decimal importe)
        {
            switch (destino.Destino)
            {
                case DestinoDeLineaDePago.Efectivo:
                    efectivo += importe;
                    break;
                case DestinoDeLineaDePago.Cuenta:
                    porCuenta[destino.IdCuenta!.Value] = porCuenta.GetValueOrDefault(destino.IdCuenta.Value) + importe;
                    break;
            }
        }

        var turnos = contexto.TurnosCerrados;
        if (corte is { } limiteDeTurno)
        {
            turnos = inclusivo
                ? turnos.Where(t => t.FechaCierre <= limiteDeTurno)
                : turnos.Where(t => t.FechaCierre < limiteDeTurno);
        }

        var idsDeTurnos = turnos.Select(t => t.Id);

        efectivo += await db.MovimientosCaja
            .Where(m => m.Tipo == TipoMovimientoCaja.Retiro && idsDeTurnos.Contains(m.IdTurnoCaja))
            .SumAsync(m => m.Importe, ct);

        var delCajon = await (
            from i in db.ItemsPago
            join p in db.Pagos on i.IdPago equals p.Id
            join t in turnos on p.IdTurnoCaja equals t.Id
            where p.OrigenFondos == OrigenFondosPago.CajaTurno
            group i by new { i.IdMedioPago, t.IdMedioPagoEfectivo } into g
            select new LineaAgrupada(g.Key.IdMedioPago, g.Key.IdMedioPagoEfectivo, g.Sum(x => x.Importe)))
            .ToListAsync(ct);
        foreach (var linea in delCajon)
        {
            var destino = rutear(linea.IdMedioPago, linea.IdMedioEfectivoDelTurno);
            if (destino.Destino == DestinoDeLineaDePago.Efectivo)
            {
                efectivo += linea.Total;
            }
        }

        var acreditado = await db.MovimientosCuenta
            .Where(EsDeCierre)
            .Where(m => m.IdTurnoCaja != null && idsDeTurnos.Contains((int)m.IdTurnoCaja))
            .GroupBy(m => m.IdCuentaTesoreria)
            .Select(g => new { IdCuenta = g.Key, Neto = g.Sum(m => m.Ingreso - m.Egreso) })
            .ToListAsync(ct);
        foreach (var cuenta in acreditado)
        {
            porCuenta[cuenta.IdCuenta] = porCuenta.GetValueOrDefault(cuenta.IdCuenta) + cuenta.Neto;
        }

        var pagos = db.Pagos.Where(p => p.IdEmpresa == idEmpresa);
        if (corte is { } fechaLimite)
        {
            pagos = inclusivo ? pagos.Where(p => p.Fecha <= fechaLimite) : pagos.Where(p => p.Fecha < fechaLimite);
        }

        var lineasDePagos = await (
            from i in db.ItemsPago
            join p in pagos on i.IdPago equals p.Id
            group i by new
            {
                i.IdMedioPago,
                IdMedioEfectivoDelTurno = db.TurnosCaja.Where(t => t.Id == p.IdTurnoCaja).Select(t => t.IdMedioPagoEfectivo).FirstOrDefault()
            } into g
            select new LineaAgrupada(g.Key.IdMedioPago, g.Key.IdMedioEfectivoDelTurno, g.Sum(x => x.Importe)))
            .ToListAsync(ct);
        foreach (var linea in lineasDePagos)
        {
            Sumar(rutear(linea.IdMedioPago, linea.IdMedioEfectivoDelTurno), -linea.Total);
        }

        var ajustesDeEfectivo = db.MovimientosTesoreria
            .Where(m => m.IdEmpresa == idEmpresa && m.Tipo == TipoMovimientoTesoreria.AjusteManual);
        if (corte is { } fechaDeAjuste)
        {
            ajustesDeEfectivo = inclusivo
                ? ajustesDeEfectivo.Where(m => m.Fecha <= fechaDeAjuste)
                : ajustesDeEfectivo.Where(m => m.Fecha < fechaDeAjuste);
        }

        efectivo += await ajustesDeEfectivo.SumAsync(m => m.Ingreso - m.Egreso, ct);

        var idsDeCuentas = contexto.Cuentas.Select(c => c.Id).ToList();
        var ajustesDeCuenta = db.MovimientosCuenta
            .Where(m => m.Tipo == TipoMovimientoCuenta.Ajuste && idsDeCuentas.Contains(m.IdCuentaTesoreria));
        if (corte is { } fechaDeAjusteDeCuenta)
        {
            ajustesDeCuenta = inclusivo
                ? ajustesDeCuenta.Where(m => m.Fecha <= fechaDeAjusteDeCuenta)
                : ajustesDeCuenta.Where(m => m.Fecha < fechaDeAjusteDeCuenta);
        }

        var ajustados = await ajustesDeCuenta
            .GroupBy(m => m.IdCuentaTesoreria)
            .Select(g => new { IdCuenta = g.Key, Neto = g.Sum(m => m.Ingreso - m.Egreso) })
            .ToListAsync(ct);
        foreach (var cuenta in ajustados)
        {
            porCuenta[cuenta.IdCuenta] = porCuenta.GetValueOrDefault(cuenta.IdCuenta) + cuenta.Neto;
        }

        return new Acumulado(efectivo, porCuenta);
    }
}
