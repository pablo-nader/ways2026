using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// La única regla que decide qué pagos de venta se descuentan con la comisión que Mercado Pago informó al aprobar su cobro
/// (<c>cobros_mercadopago.comision_informada</c>) en lugar del porcentaje del medio. La usan el cierre del turno, la compensación de un cambio
/// de medios, la reconstrucción de las cuentas y la propuesta de diferencia de comisión de la conciliación, así que todos ven el mismo conjunto.
///
/// Un pago califica si es de un comprobante emitido que no es espejo fiscal, su cobro de Mercado Pago tiene comisión informada, es el único pago
/// vivo de un comprobante emitido ligado a ese cobro y su importe es el importe pagado del cobro. Todo lo que entra en la regla es inmutable una
/// vez cerrado el turno (la comisión informada se escribe solo al aprobar y un pago ligado a Mercado Pago no cambia de medio), salvo la
/// anulación de la venta, que lo saca a la vez del bruto del turno. Son lecturas comunes dentro de la transacción del llamador, sin locks propios.
/// </summary>
public static class ComisionesInformadasDePagos
{
    private sealed class PagoConComision
    {
        public int IdCobro { get; init; }

        public int? IdTurnoCaja { get; init; }

        public int IdMedioPago { get; init; }

        public decimal Importe { get; init; }

        public decimal Comision { get; init; }
    }

    private static IQueryable<PagoConComision> Calificados(IWaysDbContext db) =>
        from p in db.Cobros.AsNoTracking()
        join c in db.ComprobantesVenta.AsNoTracking().SinEspejosFiscales() on p.IdComprobanteVenta equals c.Id
        join m in db.CobrosMercadoPago.AsNoTracking() on p.IdCobroMercadoPago equals (int?)m.Id
        where c.Estado == EstadoComprobante.Emitido
              && m.ComisionInformada != null
              && p.Importe == m.ImportePagado
              && db.Cobros.Count(q => q.IdCobroMercadoPago == m.Id
                                      && db.ComprobantesVenta.Any(v => v.Id == q.IdComprobanteVenta && v.Estado == EstadoComprobante.Emitido)) == 1
        select new PagoConComision
        {
            IdCobro = p.Id, IdTurnoCaja = c.IdTurnoCaja, IdMedioPago = p.IdMedioPago, Importe = p.Importe, Comision = m.ComisionInformada!.Value
        };

    /// <summary>Por (turno, medio), la suma de los importes y de las comisiones informadas de los pagos que califican. Un par sin pagos que
    /// califiquen no aparece: su comisión conocida es cero.</summary>
    public static async Task<Dictionary<(int IdTurnoCaja, int IdMedioPago), ComisionConocida>> PorTurnoYMedioAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsDeTurno, IReadOnlyCollection<int> idsDeMedio, CancellationToken ct)
    {
        if (idsDeTurno.Count == 0 || idsDeMedio.Count == 0)
        {
            return [];
        }

        var filas = await Calificados(db)
            .Where(p => p.IdTurnoCaja != null && idsDeTurno.Contains(p.IdTurnoCaja.Value) && idsDeMedio.Contains(p.IdMedioPago))
            .GroupBy(p => new { IdTurno = p.IdTurnoCaja!.Value, p.IdMedioPago })
            .Select(g => new { g.Key.IdTurno, g.Key.IdMedioPago, Base = g.Sum(p => p.Importe), Comision = g.Sum(p => p.Comision) })
            .ToListAsync(ct);

        return filas.ToDictionary(f => (f.IdTurno, f.IdMedioPago), f => new ComisionConocida(f.Base, f.Comision));
    }

    /// <summary>Por pago de venta (<c>cobros.id_cobro</c>), la comisión informada de los que califican; los demás no aparecen.</summary>
    public static async Task<Dictionary<int, decimal>> PorPagoAsync(IWaysDbContext db, IReadOnlyCollection<int> idsDeCobro, CancellationToken ct)
    {
        if (idsDeCobro.Count == 0)
        {
            return [];
        }

        return await Calificados(db)
            .Where(p => idsDeCobro.Contains(p.IdCobro))
            .ToDictionaryAsync(p => p.IdCobro, p => p.Comision, ct);
    }
}
