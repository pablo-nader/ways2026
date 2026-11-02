using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.MercadoPago;
using Ways.Domain.Ventas;

namespace Ways.Application.MercadoPago;

/// <summary>Cuántas propuestas generó una importación y qué cobros quedaron fuera del cálculo de la comisión.</summary>
public sealed record PropuestasGeneradas(int Diferencias, int Retiros, int CobrosSinCierre, int CobrosSinComisionReal);

/// <summary>
/// Genera las propuestas de tesorería de una conciliación recién importada (E7). Corre dentro de la transacción de la importación, después de
/// guardar las filas y de fijar la comisión real de los cobros conciliados, así que nace junto con la conciliación y no se vuelve a calcular: la
/// elección de calcular al importar (y no al leer) es que el cálculo ve una foto consistente del libro y de los cobros bajo el lock de la
/// empresa, y que el GET es una lectura pura. Lo que no entra: un cobro conciliado cuyo turno todavía no acreditó su medio en la cuenta (el cierre
/// lo va a descontar después con el porcentaje del medio) no tiene una comisión estimada que corregir, y un turno que se cierra DESPUÉS de importar
/// no se suma a una conciliación ya hecha; los dos casos se cuentan en el detalle y en la auditoría de la importación.
///
/// La comisión estimada de un cobro es la que descontó el cierre del turno de su venta (<see cref="CalculadorDeDiferenciaDeComisionMercadoPago"/>):
/// no se guarda por pago, así que se rearma con el porcentaje que la fila de comisión del cierre dejó para ese turno y medio, aplicado al importe
/// del pago. Es una aproximación a centavos (el cierre redondea sobre el bruto de todo el turno y medio, esto lo hace pago por pago). Ver
/// <see cref="CalculadorDeDiferenciaDeComisionMercadoPago"/>.
/// </summary>
public static class GeneradorDePropuestasDeTesoreriaMercadoPago
{
    public const int MaximoDeLineas = 200;

    private static readonly JsonSerializerOptions OpcionesDeJson = new(JsonSerializerDefaults.Web);

    public static string SerializarDetalle(DetalleDeDiferenciaDeComision detalle) => JsonSerializer.Serialize(detalle, OpcionesDeJson);

    public static DetalleDeDiferenciaDeComision? LeerDetalle(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<DetalleDeDiferenciaDeComision>(json, OpcionesDeJson);

    /// <param name="cobrosAplicados">Los cobros de Mercado Pago que esta conciliación concilió, con la comisión real que se les guardó.</param>
    /// <param name="filasDeRetiro">Las filas guardadas de la conciliación cuyo tipo es retiro o payout (ya con su id).</param>
    public static async Task<PropuestasGeneradas> GenerarAsync(
        IWaysDbContext db,
        int idTenant,
        int idEmpresa,
        int idConciliacion,
        IReadOnlyDictionary<int, decimal?> cobrosAplicados,
        IReadOnlyList<FilaConciliacionMercadoPago> filasDeRetiro,
        DateTimeOffset ahora,
        CancellationToken ct)
    {
        var diferencias = await GenerarDiferenciasAsync(db, idTenant, idEmpresa, idConciliacion, cobrosAplicados, ahora, ct);
        var retiros = await GenerarRetirosAsync(db, idTenant, idEmpresa, idConciliacion, filasDeRetiro, ahora, ct);
        return new PropuestasGeneradas(diferencias.Propuestas, retiros, diferencias.SinCierre, diferencias.SinComisionReal);
    }

    private static async Task<(int Propuestas, int SinCierre, int SinComisionReal)> GenerarDiferenciasAsync(
        IWaysDbContext db, int idTenant, int idEmpresa, int idConciliacion, IReadOnlyDictionary<int, decimal?> cobrosAplicados,
        DateTimeOffset ahora, CancellationToken ct)
    {
        if (cobrosAplicados.Count == 0)
        {
            return (0, 0, 0);
        }

        var idsDeCobroMp = cobrosAplicados.Keys.ToList();
        var pagos = await (
                from p in db.Cobros.AsNoTracking()
                where p.IdCobroMercadoPago != null && idsDeCobroMp.Contains(p.IdCobroMercadoPago.Value) && p.DeletedAt == null
                join c in db.ComprobantesVenta.AsNoTracking().SinEspejosFiscales() on p.IdComprobanteVenta equals c.Id
                select new { IdCobroMercadoPago = p.IdCobroMercadoPago!.Value, IdCobro = p.Id, p.IdMedioPago, p.Importe, c.IdTurnoCaja })
            .ToListAsync(ct);

        var paraCalcular = pagos
            .Select(p => new PagoConciliadoParaComision(
                p.IdCobroMercadoPago, p.IdCobro, p.IdTurnoCaja, p.IdMedioPago, p.Importe, cobrosAplicados[p.IdCobroMercadoPago]))
            .ToList();

        var cierres = await LeerCierresAsync(db, paraCalcular, ct);
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(paraCalcular, cierres);
        var sinPago = cobrosAplicados.Count - pagos.Select(p => p.IdCobroMercadoPago).Distinct().Count();

        var lineasPorCuenta = resultado.Lineas.GroupBy(l => l.IdCuentaTesoreria).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var cuenta in resultado.PorCuenta.Where(c => Math.Abs(c.Importe) <= ReglasDeAjusteDeCuentaTesoreria.ImporteMaximo))
        {
            var lineas = lineasPorCuenta[cuenta.IdCuentaTesoreria]
                .OrderByDescending(l => Math.Abs(l.Diferencia))
                .ThenBy(l => l.IdCobroMercadoPago)
                .ToList();
            var detalle = new DetalleDeDiferenciaDeComision(
                cuenta.CantidadDeCobros, cuenta.ComisionRealTotal, cuenta.ComisionEstimadaTotal, resultado.CobrosSinCierre + sinPago,
                resultado.CobrosSinComisionReal, lineas.Take(MaximoDeLineas).ToList(), lineas.Count > MaximoDeLineas);

            db.PropuestasTesoreriaMercadoPago.Add(new PropuestaDeTesoreriaMercadoPago
            {
                IdTenant = idTenant,
                IdEmpresa = idEmpresa,
                IdConciliacion = idConciliacion,
                Tipo = TiposDePropuestaDeTesoreriaMercadoPago.DiferenciaComision,
                ClaveOrigen = PropuestaDeTesoreriaMercadoPago.ClaveDeCuenta(cuenta.IdCuentaTesoreria),
                Importe = cuenta.Importe,
                IdCuentaOrigen = cuenta.IdCuentaTesoreria,
                Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente,
                Detalle = SerializarDetalle(detalle),
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync(ct);
        return (resultado.PorCuenta.Count, resultado.CobrosSinCierre + sinPago, resultado.CobrosSinComisionReal);
    }

    /// <summary>Por cada (turno, medio) con fila de cobro en una cuenta: esa cuenta y el porcentaje de la fila de comisión (0 si el cierre no escribió
    /// ninguna, porque su porcentaje redondeaba a cero).</summary>
    private static async Task<Dictionary<(int IdTurnoCaja, int IdMedioPago), CierreDeMedioEnCuenta>> LeerCierresAsync(
        IWaysDbContext db, IReadOnlyList<PagoConciliadoParaComision> pagos, CancellationToken ct)
    {
        var turnos = pagos.Where(p => p.IdTurnoCaja is not null).Select(p => p.IdTurnoCaja!.Value).Distinct().ToList();
        var medios = pagos.Select(p => p.IdMedioPago).Distinct().ToList();
        if (turnos.Count == 0)
        {
            return [];
        }

        var filas = await db.MovimientosCuenta
            .AsNoTracking()
            .Where(m => m.IdTurnoCaja != null && turnos.Contains(m.IdTurnoCaja.Value) && m.IdMedioPago != null
                        && medios.Contains(m.IdMedioPago.Value)
                        && (m.Tipo == TipoMovimientoCuenta.Cobro || m.Tipo == TipoMovimientoCuenta.Comision))
            .Select(m => new { IdTurno = m.IdTurnoCaja!.Value, IdMedio = m.IdMedioPago!.Value, m.Tipo, m.IdCuentaTesoreria, m.PorcentajeAplicado })
            .ToListAsync(ct);

        var cierres = new Dictionary<(int, int), CierreDeMedioEnCuenta>();
        foreach (var cobro in filas.Where(f => f.Tipo == TipoMovimientoCuenta.Cobro))
        {
            var porcentaje = filas
                .Where(f => f.Tipo == TipoMovimientoCuenta.Comision && f.IdTurno == cobro.IdTurno && f.IdMedio == cobro.IdMedio)
                .Select(f => f.PorcentajeAplicado)
                .FirstOrDefault() ?? 0m;
            cierres[(cobro.IdTurno, cobro.IdMedio)] = new CierreDeMedioEnCuenta(cobro.IdCuentaTesoreria, porcentaje);
        }

        return cierres;
    }

    private static async Task<int> GenerarRetirosAsync(
        IWaysDbContext db, int idTenant, int idEmpresa, int idConciliacion, IReadOnlyList<FilaConciliacionMercadoPago> filasDeRetiro,
        DateTimeOffset ahora, CancellationToken ct)
    {
        var retiros = filasDeRetiro
            .Where(f => f.TipoTransaccion is TiposDeTransaccionMercadoPago.Withdrawal or TiposDeTransaccionMercadoPago.Payout
                        && f.Importe is { } importe && importe != 0m && Math.Abs(importe) <= ReglasDeAjusteDeCuentaTesoreria.ImporteMaximo)
            .ToList();
        if (retiros.Count == 0)
        {
            return 0;
        }

        var cuentaDeMercadoPago = await db.ConexionesMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == idEmpresa)
            .Select(c => (int?)c.IdCuentaTesoreria)
            .FirstOrDefaultAsync(ct);

        foreach (var fila in retiros)
        {
            db.PropuestasTesoreriaMercadoPago.Add(new PropuestaDeTesoreriaMercadoPago
            {
                IdTenant = idTenant,
                IdEmpresa = idEmpresa,
                IdConciliacion = idConciliacion,
                Tipo = TiposDePropuestaDeTesoreriaMercadoPago.Retiro,
                ClaveOrigen = PropuestaDeTesoreriaMercadoPago.ClaveDeLinea(fila.NumeroLinea),
                Importe = Math.Abs(fila.Importe!.Value),
                IdFila = fila.Id,
                IdCuentaOrigen = cuentaDeMercadoPago,
                Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync(ct);
        return retiros.Count;
    }
}
