using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;

namespace Ways.Application.Plantel;

/// <summary>
/// Lee de qué se compuso cada pago del libro de personal: los gastos y el descuento de cuenta corriente que
/// apuntan a su movimiento. El nombre del medio de pago se resuelve con una subquery correlacionada (nunca un
/// join), así que un medio dado de baja deja el nombre en <c>null</c> y no hace desaparecer la línea.
/// </summary>
public static class LectorDeDesgloseDePagos
{
    public static async Task<IReadOnlyDictionary<int, DesgloseDePago>> LeerAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsDeMovimientos, CancellationToken ct)
    {
        if (idsDeMovimientos.Count == 0)
        {
            return new Dictionary<int, DesgloseDePago>();
        }

        var lineas = await db.Gastos
            .Where(g => g.IdMovimientoPersonal != null && idsDeMovimientos.Contains(g.IdMovimientoPersonal.Value))
            .OrderBy(g => g.Id)
            .Select(g => new
            {
                IdMovimiento = g.IdMovimientoPersonal!.Value,
                Linea = new LineaDeDesglose(
                    g.Id,
                    g.IdMedioPago,
                    db.MediosPago.Where(m => m.Id == g.IdMedioPago).Select(m => m.Nombre).FirstOrDefault(),
                    g.Importe,
                    g.OrigenFondos,
                    g.Detalle)
            })
            .ToListAsync(ct);

        var descuentos = await db.MovimientosCuentaCorriente
            .Where(m => m.IdMovimientoPersonal != null && idsDeMovimientos.Contains(m.IdMovimientoPersonal.Value))
            .Select(m => new { IdMovimiento = m.IdMovimientoPersonal!.Value, m.Importe })
            .ToListAsync(ct);

        return idsDeMovimientos.ToDictionary(
            id => id,
            id => new DesgloseDePago(
                descuentos.Where(d => d.IdMovimiento == id).Select(d => (decimal?)-d.Importe).SingleOrDefault(),
                lineas.Where(l => l.IdMovimiento == id).Select(l => l.Linea).ToList()));
    }
}
