using System.Data.Common;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// Los dos statements crudos que son la ÚNICA autoridad de escritura sobre
/// <c>clientes.saldo</c>/<c>movimientos_cuenta_corriente</c> (design decisión 1: "una sola
/// <c>EscriturasDeCuentaCorriente</c>") — extraídos VERBATIM de
/// <c>ServicioDeVentas.ActualizarSaldoClienteAsync</c>/<c>InsertarMovimientoCcAsync</c>
/// (stage-5-pos-ventas), con un único cambio: <c>id_comprobante_venta</c>/<c>id_pago_comprobante</c>
/// pasan a <see cref="Nullable{T}"/> porque un <see cref="TipoMovimientoCc.Pago"/> (RC, stage 7)
/// nunca lleva <c>id_pago_comprobante</c> (a diferencia de un <see cref="TipoMovimientoCc.Consumo"/>,
/// que siempre lo lleva) — el SQL en sí no cambia una coma.
/// <c>ServicioDeVentas</c> (TX/NCX, contramovimiento de anulación) y
/// <c>ServicioDeCuentaCorriente</c> (RC, ajuste manual — stage 7) llaman a esta clase en vez de
/// duplicar el SQL: así queda exactamente UN <c>UPDATE clientes ... saldo ... RETURNING</c> y UN
/// <c>INSERT movimientos_cuenta_corriente</c> en todo el codebase, el invariante que design llama
/// "la extracción es lo que compra seguridad" (Technical Approach, "containment").
/// </summary>
public static class EscriturasDeCuentaCorriente
{
    /// <summary><c>UPDATE ... RETURNING</c> crudo: nunca vía una entidad <c>Cliente</c>
    /// trackeada (un <c>cliente.Saldo += x</c> por <c>SaveChangesAsync</c> duplicaría el
    /// incremento en un reintento de <c>CreateExecutionStrategy</c>). <c>id_tenant</c> en el
    /// <c>WHERE</c> además de <c>id</c>: RLS ya aísla por tenant, esto es una segunda capa
    /// barata (mismo criterio que el resto del proyecto), no la única defensa.</summary>
    public static async Task<decimal> ActualizarSaldoClienteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, decimal importe,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE clientes SET saldo = saldo + $1 WHERE id_cliente = $2 AND id_tenant = $3 RETURNING saldo";

        AgregarParametro(comando, importe);
        AgregarParametro(comando, idCliente);
        AgregarParametro(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException($"No se pudo actualizar el saldo del cliente {idCliente}.");

        return Convert.ToDecimal(resultado);
    }

    /// <summary><c>id_comprobante_venta</c>/<c>id_pago_comprobante</c> nullable per-tipo (design
    /// decisión 5): un <see cref="TipoMovimientoCc.Consumo"/> siempre trae ambos poblados, un
    /// <see cref="TipoMovimientoCc.Pago"/> (RC) trae <c>id_comprobante_venta</c> pero nunca
    /// <c>id_pago_comprobante</c> (el pago físico de la RC no es "el que generó" el movimiento —
    /// el movimiento lo genera el comprobante entero), y un <see cref="TipoMovimientoCc.Ajuste"/>
    /// manual (Slice 4) no trae ninguno de los dos. El llamador decide qué pasa; esta clase solo
    /// escribe.</summary>
    public static async Task InsertarMovimientoCcAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, DateTimeOffset fecha,
        int idPuntoVenta, int idEmpleado, TipoMovimientoCc tipo, int? idComprobanteVenta, int? idPagoComprobante,
        decimal importe, decimal saldoResultante, CancellationToken ct)
    {
        ValidarFormaPorTipo(tipo, idPagoComprobante);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente " +
            "(id_tenant, id_cliente, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_venta, " +
            " id_pago_comprobante, importe, saldo_resultante) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idCliente);
        AgregarParametro(comando, fecha);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idEmpleado);
        AgregarParametro(comando, tipo);
        AgregarParametroNullable(comando, idComprobanteVenta);
        AgregarParametroNullable(comando, idPagoComprobante);
        AgregarParametro(comando, importe);
        AgregarParametro(comando, saldoResultante);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Defensa en profundidad, infraestructura pura (nunca un <c>ErrorDominio</c> 4xx):
    /// pinea acá, en el único escritor, la forma nullable por tipo que hoy solo garantizan los
    /// llamadores (design: decision 5 — movement shape per tipo). Solo <see cref="TipoMovimientoCc.Consumo"/>
    /// y <see cref="TipoMovimientoCc.Pago"/> tienen una forma única y fija (el resto — un
    /// <c>Ajuste</c> puede ser un contramovimiento de anulación o un ajuste manual — es
    /// estructuralmente dual, no viola nada acá).</summary>
    private static void ValidarFormaPorTipo(TipoMovimientoCc tipo, int? idPagoComprobante)
    {
        if (tipo == TipoMovimientoCc.Consumo && idPagoComprobante is null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Consumo requiere id_pago_comprobante — invariante de escritura violado.");
        }

        if (tipo == TipoMovimientoCc.Pago && idPagoComprobante is not null)
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Pago nunca lleva id_pago_comprobante — invariante de escritura violado.");
        }
    }

    private static void AgregarParametro(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }

    private static void AgregarParametroNullable(DbCommand comando, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor ?? DBNull.Value;
        comando.Parameters.Add(parametro);
    }
}
