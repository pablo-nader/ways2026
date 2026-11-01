using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Caja;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

public partial class ServicioDePagos
{
    /// <summary>Último paso de todo escritor de ítems de pago (alta del POS, alta administrativa, pago de compra, pago de sueldo,
    /// edición y baja), después del advisory de la tesorería de efectivo si lo tomó y antes del commit: lleva los débitos del pago
    /// en sus cuentas de tesorería a lo que dicen sus ítems vivos (<see cref="DebitosDePagoEnCuenta"/>). Las cuentas son el último
    /// lock de la transacción.</summary>
    private async Task ReconciliarDebitosEnCuentasAsync(
        int idTenant, Pago pago, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        await DebitosDePagoEnCuenta.ReconciliarAsync(
            db, conexion, db.Database.CurrentTransaction?.GetDbTransaction(), idTenant, pago.IdEmpresa, pago.Id, idEmpleado,
            momento, ct);
    }
}
