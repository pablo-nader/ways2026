using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Domain.Catalogos;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.Pagos;

/// <summary>
/// El redondeo de la cuenta corriente del proveedor (E4 de pagos y aplicaciones, doc 10 §5): los movimientos
/// <see cref="TipoMovimientoCcProveedor.Redondeo"/> son el ÚNICO lugar donde vive lo perdonado o absorbido. Sus dos
/// destinos y sus signos:
/// <list type="bullet">
/// <item>Lado compra (faltó pagar): importe NEGATIVO con <c>id_comprobante_compra</c>. Lo pagado de la compra suma
/// <c>−Σ importe</c> (<see cref="Ways.Application.Compras.LectorDePagadoPorCompra"/>) y el saldo del proveedor baja lo
/// mismo, así que pagar 1500 una compra de 1503,50 la deja Pagada y el saldo neto en cero.</item>
/// <item>Lado pago (sobró): importe POSITIVO con <c>id_pago</c>. Lo disponible del pago resta <c>Σ importe</c>
/// (<see cref="LectorDeDisponiblePorPago"/>) y el saldo del proveedor sube lo mismo.</item>
/// </list>
/// El ledger no se reescribe: la reversa de un redondeo es OTRO movimiento <c>redondeo</c> con el signo opuesto, y los
/// lectores suman todos, así que un redondeo revertido pesa 0. Toda escritura va por la conexión y la transacción
/// del llamador y es el último paso de lock de fila (el proveedor), igual que el resto de los escritores de la cuenta.
/// </summary>
public static class RedondeoDeProveedor
{
    /// <summary>Tolerancia vigente para el punto de venta (punto de venta → empresa → default). Una sola lectura.</summary>
    public static async Task<decimal> ResolverToleranciaAsync(
        IWaysDbContext db, int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var clave = ParametroConocido.ToleranciaRedondeoPagoProveedor.Clave;
        var candidatos = await db.Parametros
            .Where(p => p.Clave == clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        return JsonSerializer.Deserialize<decimal>(ResolucionDeParametros.Resolver(clave, candidatos, idPuntoVenta));
    }

    /// <summary>Suma con signo de los redondeos de la compra (negativa mientras haya algo perdonado vigente).</summary>
    public static async Task<decimal> NetoDeLaCompraAsync(IWaysDbContext db, int idCompra, CancellationToken ct) =>
        await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Redondeo && m.IdComprobanteCompra == idCompra)
            .SumAsync(m => (decimal?)m.Importe, ct) ?? 0m;

    /// <summary>Suma con signo de los redondeos del pago, por proveedor (el proveedor del pago puede haber cambiado entre el
    /// redondeo y su reversa; cada reversa va al proveedor que recibió el redondeo y con su punto de venta).</summary>
    public static async Task<IReadOnlyList<(int IdProveedor, int IdPuntoVenta, decimal Neto)>> NetoDelPagoPorProveedorAsync(
        IWaysDbContext db, int idPago, CancellationToken ct)
    {
        var filas = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Redondeo && m.IdPago == idPago)
            .GroupBy(m => m.IdProveedor)
            .Select(g => new { IdProveedor = g.Key, IdPuntoVenta = g.Max(m => m.IdPuntoVenta), Neto = g.Sum(m => m.Importe) })
            .ToListAsync(ct);

        return filas.Select(f => (f.IdProveedor, f.IdPuntoVenta ?? 0, f.Neto)).ToList();
    }

    /// <summary>Escribe un movimiento de redondeo y mueve el saldo del proveedor lo mismo. Devuelve el saldo resultante.</summary>
    public static async Task<decimal> RegistrarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idProveedor, DateTimeOffset momento,
        int idPuntoVenta, int idEmpleado, int? idCompra, int? idPago, decimal importe, string detalle,
        CancellationToken ct)
    {
        var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
            conexion, transaccion, idTenant, idProveedor, importe, ct);

        await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
            conexion, transaccion, idTenant, idProveedor, momento, idPuntoVenta, idEmpleado,
            TipoMovimientoCcProveedor.Redondeo, idCompra, idPago, importe, nuevoSaldo, detalle, ct);

        return nuevoSaldo;
    }

    /// <summary>
    /// Deja sin efecto el redondeo vigente de una compra, si lo hay: escribe la reversa (signo opuesto al neto) para que lo
    /// pagado de la compra vuelva a ser solo lo aplicado. Se llama cuando la compra pierde aplicaciones (desaplicar, baja o
    /// recorte de un pago) o se anula: el redondeo perdonaba una diferencia que dependía de lo aplicado, y sin esas
    /// aplicaciones perdonaría de más. Devuelve el importe revertido (positivo; 0 si no había nada).
    /// </summary>
    public static async Task<decimal> RevertirDeLaCompraAsync(
        IWaysDbContext db, DbConnection conexion, DbTransaction? transaccion, int idTenant, int idProveedor, int idCompra,
        int idPuntoVenta, int idEmpleado, DateTimeOffset momento, string motivo, CancellationToken ct)
    {
        var neto = await NetoDeLaCompraAsync(db, idCompra, ct);
        if (neto == 0m)
        {
            return 0m;
        }

        await RegistrarAsync(
            conexion, transaccion, idTenant, idProveedor, momento, idPuntoVenta, idEmpleado, idCompra, idPago: null,
            -neto, $"Reversa del redondeo de la compra #{idCompra}: {motivo}", ct);

        return Math.Abs(neto);
    }

    /// <summary>
    /// Deja sin efecto el redondeo vigente de un pago, si lo hay (edición o baja del pago): el disponible vuelve a ser
    /// importe − aplicado. Cada proveedor con neto distinto de cero recibe su reversa, con el punto de venta del redondeo
    /// original. Devuelve el importe revertido en total (0 si no había nada).
    /// </summary>
    public static async Task<decimal> RevertirDelPagoAsync(
        IWaysDbContext db, DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPago, int idEmpleado,
        DateTimeOffset momento, string motivo, CancellationToken ct)
    {
        var netos = await NetoDelPagoPorProveedorAsync(db, idPago, ct);
        var revertido = 0m;

        foreach (var (idProveedor, idPuntoVenta, neto) in netos.Where(n => n.Neto != 0m).OrderBy(n => n.IdProveedor))
        {
            await RegistrarAsync(
                conexion, transaccion, idTenant, idProveedor, momento, idPuntoVenta, idEmpleado, idCompra: null, idPago,
                -neto, $"Reversa del redondeo del pago #{idPago}: {motivo}", ct);
            revertido += Math.Abs(neto);
        }

        return revertido;
    }
}
