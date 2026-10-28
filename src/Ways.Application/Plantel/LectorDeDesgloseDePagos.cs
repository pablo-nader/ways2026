using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;

namespace Ways.Application.Plantel;

/// <summary>
/// Lee de qué se compuso cada pago del libro de personal: una línea por ítem de cada pago que apunta a su movimiento
/// y el descuento de cuenta corriente que también lo hace. El nombre del medio de pago se resuelve con una subquery correlacionada (nunca un
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

        var lineas = await db.Pagos
            .Where(g => g.IdMovimientoPersonal != null && idsDeMovimientos.Contains(g.IdMovimientoPersonal.Value))
            .Join(db.ItemsPago, g => g.Id, i => i.IdPago, (g, i) => new { Pago = g, Item = i })
            .OrderBy(x => x.Pago.Id)
            .ThenBy(x => x.Item.Id)
            .Select(x => new
            {
                IdMovimiento = x.Pago.IdMovimientoPersonal!.Value,
                Linea = new LineaDeDesglose(
                    x.Pago.Id,
                    x.Item.Id,
                    x.Item.IdMedioPago,
                    db.MediosPago.Where(m => m.Id == x.Item.IdMedioPago).Select(m => m.Nombre).FirstOrDefault(),
                    x.Item.Importe,
                    x.Pago.OrigenFondos,
                    x.Item.Referencia ?? x.Pago.Detalle)
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
