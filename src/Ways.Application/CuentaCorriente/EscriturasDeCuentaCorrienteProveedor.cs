using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// Los dos statements crudos que son la ÚNICA autoridad de escritura sobre
/// <c>proveedores.saldo</c>/<c>movimientos_cuenta_corriente_proveedor</c> (design decisión 1:
/// "una sola <c>EscriturasDeCuentaCorrienteProveedor</c>", stage-15-cc-proveedores-ledger) —
/// copia estructural VERBATIM de <see cref="EscriturasDeCuentaCorriente"/>: misma forma
/// <c>static</c>, misma postura de conexión/transacción del llamador (nunca abre, flushea ni
/// comitea nada), mismos parámetros por <see cref="ParametrosDeComando"/>. El único cambio real
/// es el <c>ValidarFormaPorTipo</c> propio, porque la matriz 4×3 de esta tabla (design.md:113-118)
/// no es la de <c>TipoMovimientoCc</c>. <c>ServicioDeCompras</c> (confirm/anulación) y, en slice 3,
/// <c>ServicioDeGastos</c> (pago) y el ajuste manual (slice 5) llaman a esta clase en vez de
/// duplicar el SQL: así queda exactamente UN <c>UPDATE proveedores ... saldo ... RETURNING</c> y
/// UN <c>INSERT movimientos_cuenta_corriente_proveedor</c> en todo el codebase — "la extracción es
/// lo que compra seguridad" (design: Technical Approach, decisión 1).
/// </summary>
public static class EscriturasDeCuentaCorrienteProveedor
{
    /// <summary><c>UPDATE ... RETURNING</c> crudo: nunca vía una entidad <c>Proveedor</c>
    /// trackeada (un <c>proveedor.Saldo += x</c> por <c>SaveChangesAsync</c> duplicaría el
    /// incremento en un reintento de <c>CreateExecutionStrategy</c> — design decisión 2).
    /// <c>id_tenant</c> en el <c>WHERE</c> además de <c>id</c>: RLS ya aísla por tenant, esto es
    /// una segunda capa barata (mismo criterio que <c>EscriturasDeCuentaCorriente</c>). Es el
    /// ÚLTIMO lock de fila (<c>for update</c>) de cualquier transacción que lo llame (design:
    /// Transactions, "Total order").</summary>
    public static async Task<decimal> ActualizarSaldoProveedorAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idProveedor, decimal importe,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE proveedores SET saldo = saldo + $1 WHERE id_proveedor = $2 AND id_tenant = $3 RETURNING saldo";

        ParametrosDeComando.Agregar(comando, importe);
        ParametrosDeComando.Agregar(comando, idProveedor);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException($"No se pudo actualizar el saldo del proveedor {idProveedor}.");

        return Convert.ToDecimal(resultado);
    }

    /// <summary>El ÚNICO <c>INSERT</c> del ledger de proveedores. <paramref name="idPuntoVenta"/>/
    /// <paramref name="idEmpleado"/> son <c>int?</c> SOLO para que
    /// <see cref="TipoMovimientoCcProveedor.Apertura"/> sea representable en el tipo; ningún
    /// llamador de producción de esta etapa pasa null en esas dos posiciones (la única escritora
    /// de <c>apertura</c> es la migración) y <c>ValidarFormaPorTipo</c> lo refuerza (design
    /// decisión 15: "apertura is refused at three layers").</summary>
    public static async Task<int> InsertarMovimientoCcProveedorAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idProveedor, DateTimeOffset fecha,
        int? idPuntoVenta, int? idEmpleado, TipoMovimientoCcProveedor tipo, int? idComprobanteCompra,
        int? idGasto, decimal importe, decimal saldoResultante, string? detalle, CancellationToken ct)
    {
        ValidarFormaPorTipo(tipo, idComprobanteCompra, idGasto, idPuntoVenta, idEmpleado);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, " +
            " id_gasto, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) " +
            "RETURNING id_movimiento";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idProveedor);
        ParametrosDeComando.Agregar(comando, fecha);
        ParametrosDeComando.AgregarNulo(comando, idPuntoVenta);
        ParametrosDeComando.AgregarNulo(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, tipo);
        ParametrosDeComando.AgregarNulo(comando, idComprobanteCompra);
        ParametrosDeComando.AgregarNulo(comando, idGasto);
        ParametrosDeComando.Agregar(comando, importe);
        ParametrosDeComando.Agregar(comando, saldoResultante);
        ParametrosDeComando.AgregarNulo(comando, detalle);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("No se pudo insertar el movimiento de cuenta corriente de proveedor.");
        return Convert.ToInt32(resultado);
    }

    /// <summary>Defensa en profundidad, infraestructura pura (nunca un <c>ErrorDominio</c> 4xx: una
    /// violación es un defecto de un call site, no un error de cliente) — pinea acá, en el único
    /// escritor, la forma por tipo de la matriz 4×3 (design.md:113-118, gate §B CHECK). Un arm por
    /// clausula, mismo criterio que <c>EscriturasDeCuentaCorriente.ValidarFormaPorTipo</c>, para que
    /// cada mutation target (#12, #13) tenga una línea propia para borrar.</summary>
    private static void ValidarFormaPorTipo(
        TipoMovimientoCcProveedor tipo, int? idComprobanteCompra, int? idGasto, int? idPuntoVenta, int? idEmpleado)
    {
        if (tipo == TipoMovimientoCcProveedor.Apertura && (idPuntoVenta is not null || idEmpleado is not null))
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Apertura no lleva id_punto_venta ni id_empleado — invariante de escritura violado.");
        }

        if (tipo == TipoMovimientoCcProveedor.Apertura && idComprobanteCompra is not null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Apertura no lleva id_comprobante_compra — invariante de escritura violado.");
        }

        if (tipo == TipoMovimientoCcProveedor.Apertura && idGasto is not null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Apertura no lleva id_gasto — invariante de escritura violado.");
        }

        if (tipo == TipoMovimientoCcProveedor.Compra && idComprobanteCompra is null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Compra requiere id_comprobante_compra — invariante de escritura violado.");
        }

        if (tipo == TipoMovimientoCcProveedor.Compra && idGasto is not null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Compra nunca lleva id_gasto — invariante de escritura violado.");
        }

        if (tipo == TipoMovimientoCcProveedor.Pago && idGasto is null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Pago requiere id_gasto — invariante de escritura violado.");
        }

        if (tipo == TipoMovimientoCcProveedor.Ajuste && idGasto is not null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Ajuste nunca lleva id_gasto — invariante de escritura violado.");
        }

        if (tipo != TipoMovimientoCcProveedor.Apertura && (idPuntoVenta is null || idEmpleado is null))
        {
            throw new InvalidOperationException(
                $"Un movimiento de tipo {tipo} requiere id_punto_venta e id_empleado — invariante de escritura violado.");
        }
    }
}
