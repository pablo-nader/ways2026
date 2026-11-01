using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Pagos;

namespace Ways.Application.Caja;

/// <summary>
/// Libro de tesorería de una empresa: una fila por cierre de turno, por pago y por ajuste manual, con una columna para el efectivo y
/// una por cuenta digital, más el saldo anterior al rango y el saldo actual. Se calcula desde las fuentes (turnos, pagos y ajustes), no
/// desde las cadenas <c>movimientos_tesoreria</c> / <c>movimientos_cuenta</c>: hoy los pagos con un medio digital y las devoluciones
/// no debitan la cuenta, y la cadena de efectivo no registra los pagos del cajón.
///
/// Cómo se reparte cada fuente (todas con el aislamiento de la empresa pedida; tenant por filtro de EF y RLS):
/// <list type="bullet">
/// <item><b>Caja</b> (un turno cerrado de la empresa, fechado al cierre). Efectivo: los retiros del turno más las líneas en efectivo de
/// los pagos pagados desde el cajón (esas líneas también aparecen, negativas, en su fila de pago, así que el neto del par es el retiro).
/// Cada cuenta: la suma de cobro menos comisión y retenciones que el cierre acreditó. La diferencia de arqueo es declarado menos esperado.</item>
/// <item><b>Pago</b> (todo pago vivo de la empresa, hecho en el POS o desde administración): cada línea resta del efectivo, de la cuenta
/// de su medio o de <c>sinCuenta</c>. El origen de los fondos no cambia ese reparto: un pago de origen tesorería también se reparte por
/// medio, no se asume efectivo.</item>
/// <item><b>AjusteEfectivo</b>: solo los ajustes manuales (<see cref="TipoMovimientoTesoreria.AjusteManual"/>). Los ajustes que el
/// sistema escribe al editar o dar de baja un pago (<see cref="TipoMovimientoTesoreria.Ajuste"/>) se ignoran porque la fila del pago ya
/// muestra su valor vigente.</item>
/// <item><b>AjusteCuenta</b>: los movimientos de cuenta de tipo ajuste. Cobro, comisión y retenciones ya están dentro de la fila de caja;
/// transferencia, depósito y retiro de cuenta no tienen escritor y quedan fuera.</item>
/// </list>
/// Las consultas son de cantidad fija y agregan en SQL; los importes por fila se agrupan por turno / pago en una sola lectura por fuente.
/// </summary>
public class ServicioDeLibroDeTesoreria(IWaysDbContext db)
{
    private sealed record CuentaBase(int Id, string Nombre, bool Activo, decimal SaldoInicial, bool Eliminada);

    private sealed record MedioBase(int Id, string Nombre, ComportamientoMedioPago Comportamiento, int? IdCuentaTesoreria);

    private sealed record TurnoDelLibro(int Id, int IdPuntoVenta, DateTimeOffset FechaApertura, DateTimeOffset FechaCierre, int? IdMedioPagoEfectivo);

    private sealed record LineaAgrupada(int IdMedioPago, int? IdMedioEfectivoDelTurno, decimal Total);

    private sealed record PagoBase(
        int Id, DateTimeOffset Fecha, CategoriaPago Categoria, OrigenFondosPago OrigenFondos, int? IdTurnoCaja, string Concepto,
        int? IdProveedor, string? NombreProveedor, int? IdMovimientoPersonal, string? NombrePersonal, int? IdMedioEfectivoDelTurno);

    private sealed record LineaDePagoLeida(int IdPago, int IdMedioPago, decimal Importe);

    private sealed record Acumulado(decimal Efectivo, IReadOnlyDictionary<int, decimal> PorCuenta);

    public async Task<LibroDeTesoreria> ObtenerAsync(
        int idEmpresa, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct = default)
    {
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }

        ReglasDeLibroDeTesoreria.ValidarRango(desde, hasta);

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
            .Select(pv => new { pv.Id, pv.Nombre, Eliminado = pv.DeletedAt != null })
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

        var anterior = await AcumularAsync(idEmpresa, turnosCerrados, idsDeCuentasVivas, cuentas, Rutear, antesDe: desde, ct);
        var actual = await AcumularAsync(idEmpresa, turnosCerrados, idsDeCuentasVivas, cuentas, Rutear, antesDe: null, ct);

        var filas = new List<(FilaDelLibro Fila, int Orden, int Id)>();
        filas.AddRange(await FilasDeCajaAsync(idEmpresa, turnosCerrados, desde, hasta, puntosVenta.ToDictionary(p => p.Id, p => p.Eliminado ? null : p.Nombre), medios, Rutear, ct));
        filas.AddRange(await FilasDePagosAsync(idEmpresa, desde, hasta, Rutear, ct));
        filas.AddRange(await FilasDeAjusteDeEfectivoAsync(idEmpresa, desde, hasta, ct));
        filas.AddRange(await FilasDeAjusteDeCuentaAsync(cuentas.Select(c => c.Id).ToList(), desde, hasta, ct));

        var ordenadas = filas.OrderBy(f => f.Fila.Fecha).ThenBy(f => f.Orden).ThenBy(f => f.Id).Select(f => f.Fila).ToList();

        var referenciadas = ordenadas.SelectMany(f => f.Cuentas.Select(c => c.IdCuentaTesoreria))
            .Concat(anterior.PorCuenta.Keys).Concat(actual.PorCuenta.Keys).ToHashSet();
        var delLibro = cuentas
            .Where(c => !c.Eliminada || referenciadas.Contains(c.Id))
            .OrderBy(c => c.Nombre).ThenBy(c => c.Id)
            .ToList();

        var conciliadoHasta = await GuardaDeConciliacion.LeerMarcaAsync(db, idEmpresa, ct);

        return new LibroDeTesoreria(
            idEmpresa, desde, hasta, conciliadoHasta,
            delLibro.Select(c => new CuentaDelLibro(c.Id, c.Nombre, c.Activo)).ToList(),
            Saldos(anterior, delLibro),
            Saldos(actual, delLibro),
            ordenadas);
    }

    private static SaldosDelLibro Saldos(Acumulado acumulado, IReadOnlyList<CuentaBase> cuentas) =>
        new(
            acumulado.Efectivo,
            cuentas.Select(c => new ImporteDeCuenta(c.Id, c.SaldoInicial + acumulado.PorCuenta.GetValueOrDefault(c.Id))).ToList());

    private static readonly Expression<Func<MovimientoCuenta, bool>> EsDeCierre = m =>
        m.Tipo == TipoMovimientoCuenta.Cobro || m.Tipo == TipoMovimientoCuenta.Comision
        || m.Tipo == TipoMovimientoCuenta.RetencionIibb || m.Tipo == TipoMovimientoCuenta.RetencionIva
        || m.Tipo == TipoMovimientoCuenta.RetencionGanancias;

    // ---- saldos ---------------------------------------------------------------------------------

    /// <summary>Todo lo anterior a <paramref name="antesDe"/> (o todo, si es <c>null</c>), agregado en SQL por fuente.</summary>
    private async Task<Acumulado> AcumularAsync(
        int idEmpresa, IQueryable<TurnoCaja> turnosCerrados, HashSet<int> idsDeCuentasVivas, IReadOnlyList<CuentaBase> cuentas,
        Func<int, int?, DestinoDeLinea> rutear, DateTimeOffset? antesDe, CancellationToken ct)
    {
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

        var turnos = antesDe is { } limite ? turnosCerrados.Where(t => t.FechaCierre < limite) : turnosCerrados;
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
        if (antesDe is { } fechaLimite)
        {
            pagos = pagos.Where(p => p.Fecha < fechaLimite);
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
        if (antesDe is { } fechaDeAjuste)
        {
            ajustesDeEfectivo = ajustesDeEfectivo.Where(m => m.Fecha < fechaDeAjuste);
        }

        efectivo += await ajustesDeEfectivo.SumAsync(m => m.Ingreso - m.Egreso, ct);

        var idsDeCuentas = cuentas.Select(c => c.Id).ToList();
        var ajustesDeCuenta = db.MovimientosCuenta
            .Where(m => m.Tipo == TipoMovimientoCuenta.Ajuste && idsDeCuentas.Contains(m.IdCuentaTesoreria));
        if (antesDe is { } fechaDeAjusteDeCuenta)
        {
            ajustesDeCuenta = ajustesDeCuenta.Where(m => m.Fecha < fechaDeAjusteDeCuenta);
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

    // ---- filas ----------------------------------------------------------------------------------

    private async Task<List<(FilaDelLibro, int, int)>> FilasDeCajaAsync(
        int idEmpresa, IQueryable<TurnoCaja> turnosCerrados, DateTimeOffset desde, DateTimeOffset hasta,
        IReadOnlyDictionary<int, string?> nombresDePuntoVenta, IReadOnlyDictionary<int, MedioBase> medios,
        Func<int, int?, DestinoDeLinea> rutear, CancellationToken ct)
    {
        var enElRango = turnosCerrados.Where(t => t.FechaCierre >= desde && t.FechaCierre <= hasta);
        var idsEnElRango = enElRango.Select(t => t.Id);

        var turnos = await enElRango
            .OrderBy(t => t.FechaCierre).ThenBy(t => t.Id)
            .Select(t => new TurnoDelLibro(t.Id, t.IdPuntoVenta, t.FechaApertura, t.FechaCierre!.Value, t.IdMedioPagoEfectivo))
            .ToListAsync(ct);
        if (turnos.Count == 0)
        {
            return [];
        }

        var retiros = await db.MovimientosCaja
            .Where(m => m.Tipo == TipoMovimientoCaja.Retiro && idsEnElRango.Contains(m.IdTurnoCaja))
            .GroupBy(m => m.IdTurnoCaja)
            .Select(g => new { IdTurno = g.Key, Total = g.Sum(m => m.Importe) })
            .ToDictionaryAsync(x => x.IdTurno, x => x.Total, ct);

        var delCajon = (await (
                from i in db.ItemsPago
                join p in db.Pagos on i.IdPago equals p.Id
                where p.OrigenFondos == OrigenFondosPago.CajaTurno && p.IdTurnoCaja != null && idsEnElRango.Contains((int)p.IdTurnoCaja)
                group i by new { IdTurno = (int)p.IdTurnoCaja!, i.IdMedioPago } into g
                select new { g.Key.IdTurno, g.Key.IdMedioPago, Total = g.Sum(x => x.Importe) })
            .ToListAsync(ct))
            .ToLookup(x => x.IdTurno);

        var movimientosDeCuenta = (await db.MovimientosCuenta
                .Where(EsDeCierre)
                .Where(m => m.IdTurnoCaja != null && idsEnElRango.Contains((int)m.IdTurnoCaja))
                .GroupBy(m => new { IdTurno = (int)m.IdTurnoCaja!, m.IdCuentaTesoreria, m.IdMedioPago, m.Tipo })
                .Select(g => new
                {
                    g.Key.IdTurno, g.Key.IdCuentaTesoreria, g.Key.IdMedioPago, g.Key.Tipo,
                    Ingreso = g.Sum(m => m.Ingreso), Egreso = g.Sum(m => m.Egreso)
                })
                .ToListAsync(ct))
            .ToLookup(x => x.IdTurno);

        var diferencias = await db.ArqueosTurno
            .Where(a => idsEnElRango.Contains(a.IdTurnoCaja))
            .GroupBy(a => a.IdTurnoCaja)
            .Select(g => new { IdTurno = g.Key, Diferencia = g.Sum(a => a.ImporteDeclarado - a.ImporteEsperado) })
            .ToDictionaryAsync(x => x.IdTurno, x => x.Diferencia, ct);

        var tolerancias = await ResolverToleranciasAsync(
            idEmpresa, turnos.Where(t => diferencias.GetValueOrDefault(t.Id) != 0m).Select(t => t.IdPuntoVenta).Distinct().ToList(), ct);

        var resultado = new List<(FilaDelLibro, int, int)>();
        foreach (var turno in turnos)
        {
            var efectivo = retiros.GetValueOrDefault(turno.Id)
                + delCajon[turno.Id]
                    .Where(l => rutear(l.IdMedioPago, turno.IdMedioPagoEfectivo).Destino == DestinoDeLineaDePago.Efectivo)
                    .Sum(l => l.Total);

            var filasDeCuenta = movimientosDeCuenta[turno.Id]
                .Select(m => new FilaDeCuentaDeCaja(m.IdCuentaTesoreria, m.IdMedioPago, m.Tipo, m.Ingreso, m.Egreso))
                .ToList();
            var cuentas = filasDeCuenta
                .GroupBy(f => f.IdCuentaTesoreria)
                .OrderBy(g => g.Key)
                .Select(g => new ImporteDeCuenta(g.Key, g.Sum(f => f.Ingreso - f.Egreso)))
                .Where(c => c.Importe != 0m)
                .ToList();

            var desglose = ReglasDeLibroDeTesoreria.ArmarDesglose(filasDeCuenta)
                .Select(c => new DesgloseDeCuentaDelLibro(
                    c.IdCuentaTesoreria, c.Neto,
                    c.Medios.Select(m => new DesgloseDeMedioDelLibro(
                        m.IdMedioPago, m.IdMedioPago is { } id ? medios.GetValueOrDefault(id)?.Nombre : null,
                        m.Importe, m.Comision, m.Retenciones, m.Neto)).ToList()))
                .ToList();

            var diferencia = ReglasDeLibroDeTesoreria.DiferenciaDeArqueoVisible(
                diferencias.GetValueOrDefault(turno.Id), tolerancias.GetValueOrDefault(turno.IdPuntoVenta));

            resultado.Add((
                new FilaDelLibro(
                    turno.FechaCierre, TipoDeFilaDelLibro.Caja, efectivo, cuentas, SinCuenta: null,
                    new DatosDeCajaDelLibro(
                        turno.Id, turno.Id, turno.FechaApertura, turno.FechaCierre, turno.IdPuntoVenta,
                        nombresDePuntoVenta.GetValueOrDefault(turno.IdPuntoVenta), diferencia, desglose),
                    Pago: null, Ajuste: null),
                0, turno.Id));
        }

        return resultado;
    }

    /// <summary>La tolerancia de pago de cada punto de venta (punto de venta, luego empresa, luego el valor por defecto), en una sola
    /// lectura de los parámetros de la empresa.</summary>
    private async Task<Dictionary<int, decimal>> ResolverToleranciasAsync(
        int idEmpresa, IReadOnlyList<int> idsDePuntoVenta, CancellationToken ct)
    {
        if (idsDePuntoVenta.Count == 0)
        {
            return [];
        }

        var candidatos = await db.Parametros
            .Where(p => p.Clave == ParametroConocido.ToleranciaPago.Clave && p.IdEmpresa == idEmpresa)
            .ToListAsync(ct);

        return idsDePuntoVenta.ToDictionary(
            id => id,
            id => JsonSerializer.Deserialize<decimal>(
                ResolucionDeParametros.Resolver(ParametroConocido.ToleranciaPago.Clave, candidatos, id)));
    }

    private async Task<List<(FilaDelLibro, int, int)>> FilasDePagosAsync(
        int idEmpresa, DateTimeOffset desde, DateTimeOffset hasta, Func<int, int?, DestinoDeLinea> rutear, CancellationToken ct)
    {
        var pagosDelRango = db.Pagos.Where(p => p.IdEmpresa == idEmpresa && p.Fecha >= desde && p.Fecha <= hasta);

        var pagos = await pagosDelRango
            .Select(p => new PagoBase(
                p.Id, p.Fecha, p.Categoria, p.OrigenFondos, p.IdTurnoCaja, p.Concepto,
                p.IdProveedor,
                p.IdProveedor == null ? null : db.Proveedores.Where(x => x.Id == p.IdProveedor).Select(x => x.RazonSocial).FirstOrDefault(),
                p.IdMovimientoPersonal,
                p.IdMovimientoPersonal == null
                    ? null
                    : (from mp in db.MovimientosPersonal
                       join pe in db.Personal on mp.IdPersonal equals pe.Id
                       where mp.Id == p.IdMovimientoPersonal
                       select pe.Nombre).FirstOrDefault(),
                db.TurnosCaja.Where(t => t.Id == p.IdTurnoCaja).Select(t => t.IdMedioPagoEfectivo).FirstOrDefault()))
            .ToListAsync(ct);
        if (pagos.Count == 0)
        {
            return [];
        }

        var lineas = (await db.ItemsPago
                .Where(i => pagosDelRango.Select(p => p.Id).Contains(i.IdPago))
                .Select(i => new LineaDePagoLeida(i.IdPago, i.IdMedioPago, i.Importe))
                .ToListAsync(ct))
            .ToLookup(l => l.IdPago);

        var resultado = new List<(FilaDelLibro, int, int)>();
        foreach (var pago in pagos)
        {
            decimal? efectivo = null;
            decimal? sinCuenta = null;
            var porCuenta = new Dictionary<int, decimal>();

            foreach (var linea in lineas[pago.Id])
            {
                var destino = rutear(linea.IdMedioPago, pago.IdMedioEfectivoDelTurno);
                switch (destino.Destino)
                {
                    case DestinoDeLineaDePago.Efectivo:
                        efectivo = (efectivo ?? 0m) - linea.Importe;
                        break;
                    case DestinoDeLineaDePago.Cuenta:
                        porCuenta[destino.IdCuenta!.Value] = porCuenta.GetValueOrDefault(destino.IdCuenta.Value) - linea.Importe;
                        break;
                    default:
                        sinCuenta = (sinCuenta ?? 0m) - linea.Importe;
                        break;
                }
            }

            resultado.Add((
                new FilaDelLibro(
                    pago.Fecha, TipoDeFilaDelLibro.Pago, efectivo,
                    porCuenta.OrderBy(c => c.Key).Select(c => new ImporteDeCuenta(c.Key, c.Value)).Where(c => c.Importe != 0m).ToList(),
                    sinCuenta, Caja: null,
                    new DatosDePagoDelLibro(
                        pago.Id,
                        DetalleDePagoDelLibro.Armar(
                            pago.Categoria, pago.Concepto, pago.IdProveedor, pago.NombreProveedor, pago.IdMovimientoPersonal,
                            pago.NombrePersonal),
                        pago.Categoria, pago.OrigenFondos, pago.IdTurnoCaja),
                    Ajuste: null),
                1, pago.Id));
        }

        return resultado;
    }

    private async Task<List<(FilaDelLibro, int, int)>> FilasDeAjusteDeEfectivoAsync(
        int idEmpresa, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct)
    {
        var ajustes = await db.MovimientosTesoreria
            .Where(m => m.IdEmpresa == idEmpresa && m.Tipo == TipoMovimientoTesoreria.AjusteManual && m.Fecha >= desde && m.Fecha <= hasta)
            .Select(m => new { m.Id, m.Fecha, m.Concepto, Importe = m.Ingreso - m.Egreso })
            .ToListAsync(ct);

        return ajustes
            .Select(a => (
                new FilaDelLibro(
                    a.Fecha, TipoDeFilaDelLibro.AjusteEfectivo, a.Importe, [], SinCuenta: null, Caja: null, Pago: null,
                    new DatosDeAjusteDelLibro(a.Concepto)),
                2, a.Id))
            .ToList();
    }

    private async Task<List<(FilaDelLibro, int, int)>> FilasDeAjusteDeCuentaAsync(
        IReadOnlyList<int> idsDeCuentas, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct)
    {
        var ajustes = await db.MovimientosCuenta
            .Where(m => m.Tipo == TipoMovimientoCuenta.Ajuste && idsDeCuentas.Contains(m.IdCuentaTesoreria)
                && m.Fecha >= desde && m.Fecha <= hasta)
            .Select(m => new { m.Id, m.Fecha, m.IdCuentaTesoreria, m.Concepto, Importe = m.Ingreso - m.Egreso })
            .ToListAsync(ct);

        return ajustes
            .Select(a => (
                new FilaDelLibro(
                    a.Fecha, TipoDeFilaDelLibro.AjusteCuenta, Efectivo: null,
                    a.Importe == 0m ? [] : [new ImporteDeCuenta(a.IdCuentaTesoreria, a.Importe)],
                    SinCuenta: null, Caja: null, Pago: null, new DatosDeAjusteDelLibro(a.Concepto)),
                3, a.Id))
            .ToList();
    }
}
