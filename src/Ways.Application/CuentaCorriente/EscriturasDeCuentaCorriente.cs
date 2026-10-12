using System.Data.Common;
using Ways.Application.Abstracciones;
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
        CancellationToken ct) =>
        await ActualizarSaldoAsync(conexion, transaccion, idTenant, idCliente, importe, exigirVivo: false, ct)
            ?? throw new InvalidOperationException($"No se pudo actualizar el saldo del cliente {idCliente}.");

    /// <summary>Mismo <c>UPDATE</c> que <see cref="ActualizarSaldoClienteAsync"/> con un conjunto más en el
    /// <c>WHERE</c>: el cliente tiene que seguir vivo (<c>deleted_at IS NULL</c>). Devuelve <c>null</c> si no hay
    /// fila viva, sin tocar nada. Lo usa el descuento de un pago de personal, que no debe acreditar a un cliente
    /// dado de baja aunque la baja ocurra entre que se resolvió el vínculo y este statement: la baja de un cliente
    /// es un <c>UPDATE</c> común de <c>deleted_at</c>, así que este statement la espera y la reevalúa.</summary>
    public static Task<decimal?> ActualizarSaldoDeClienteVivoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, decimal importe,
        CancellationToken ct) =>
        ActualizarSaldoAsync(conexion, transaccion, idTenant, idCliente, importe, exigirVivo: true, ct);

    private static async Task<decimal?> ActualizarSaldoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, decimal importe,
        bool exigirVivo, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE clientes SET saldo = saldo + $1 WHERE id_cliente = $2 AND id_tenant = $3" +
            (exigirVivo ? " AND deleted_at IS NULL" : string.Empty) +
            " RETURNING saldo";

        ParametrosDeComando.Agregar(comando, importe);
        ParametrosDeComando.Agregar(comando, idCliente);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is null ? null : Convert.ToDecimal(resultado);
    }

    /// <summary><c>id_comprobante_venta</c>/<c>id_pago_comprobante</c> nullable per-tipo (design
    /// decisión 5): un <see cref="TipoMovimientoCc.Consumo"/> siempre trae ambos poblados, un
    /// <see cref="TipoMovimientoCc.Pago"/> (RC) trae <c>id_comprobante_venta</c> pero nunca
    /// <c>id_pago_comprobante</c> (el pago físico de la RC no es "el que generó" el movimiento —
    /// el movimiento lo genera el comprobante entero), y un <see cref="TipoMovimientoCc.Ajuste"/>
    /// manual (Slice 4) no trae ninguno de los dos. El llamador decide qué pasa; esta clase solo
    /// escribe.
    ///
    /// stage-7-cuenta-corriente (Slice 3): <c>RETURNING id_movimiento</c> — la reliquidación
    /// (<c>ServicioDeReliquidacion</c>) necesita el id de la fila recién insertada para el paso 8
    /// de su transacción (marcar cada consumo cubierto con un self-FK a esta fila). Los llamadores
    /// preexistentes (<c>ServicioDeVentas</c>/<c>ServicioDeCuentaCorriente</c>) siguen sin usar el
    /// valor devuelto — <c>await</c> sin asignar sigue compilando igual. <paramref name="detalle"/>
    /// nuevo (Slice 3): <c>NULL</c> para <see cref="TipoMovimientoCc.Consumo"/>/<see cref="TipoMovimientoCc.Pago"/>,
    /// el JSON auditable para <see cref="TipoMovimientoCc.ActualizacionPrecios"/> (design: Table
    /// Shapes A — columna <c>text</c>, doc-10 §8).</summary>
    public static Task<int> InsertarMovimientoCcAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, DateTimeOffset fecha,
        int idPuntoVenta, int idEmpleado, TipoMovimientoCc tipo, int? idComprobanteVenta, int? idPagoComprobante,
        decimal importe, decimal saldoResultante, string? detalle, CancellationToken ct) =>
        InsertarAsync(
            conexion, transaccion, idTenant, idCliente, fecha, idPuntoVenta, idEmpleado, tipo, idComprobanteVenta,
            idPagoComprobante, idMovimientoPersonal: null, importe, saldoResultante, detalle, ct);

    /// <summary>La parte de un pago de personal que baja la deuda del cliente vinculado al empleado. Tiene su
    /// propio método porque su forma es única: siempre apunta al movimiento <c>pago</c> del libro de personal,
    /// nunca lleva comprobante y el importe es negativo.</summary>
    public static Task<int> InsertarDescuentoDePersonalAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, DateTimeOffset fecha,
        int idPuntoVenta, int idEmpleado, int idMovimientoPersonal, decimal importe, decimal saldoResultante,
        string? detalle, CancellationToken ct) =>
        InsertarAsync(
            conexion, transaccion, idTenant, idCliente, fecha, idPuntoVenta, idEmpleado,
            TipoMovimientoCc.DescuentoPersonal, idComprobanteVenta: null, idPagoComprobante: null,
            idMovimientoPersonal, importe, saldoResultante, detalle, ct);

    private static async Task<int> InsertarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, DateTimeOffset fecha,
        int idPuntoVenta, int idEmpleado, TipoMovimientoCc tipo, int? idComprobanteVenta, int? idPagoComprobante,
        int? idMovimientoPersonal, decimal importe, decimal saldoResultante, string? detalle, CancellationToken ct)
    {
        ValidarFormaPorTipo(tipo, idComprobanteVenta, idPagoComprobante, idMovimientoPersonal, importe);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente " +
            "(id_tenant, id_cliente, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_venta, " +
            " id_pago_comprobante, importe, saldo_resultante, detalle, id_movimiento_personal) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) " +
            "RETURNING id_movimiento";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idCliente);
        ParametrosDeComando.Agregar(comando, fecha);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, tipo);
        ParametrosDeComando.AgregarNulo(comando, idComprobanteVenta);
        ParametrosDeComando.AgregarNulo(comando, idPagoComprobante);
        ParametrosDeComando.Agregar(comando, importe);
        ParametrosDeComando.Agregar(comando, saldoResultante);
        ParametrosDeComando.AgregarNulo(comando, detalle);
        ParametrosDeComando.AgregarNulo(comando, idMovimientoPersonal);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("No se pudo insertar el movimiento de cuenta corriente.");
        return Convert.ToInt32(resultado);
    }

    /// <summary>Defensa en profundidad, infraestructura pura (nunca un <c>ErrorDominio</c> 4xx):
    /// pinea acá, en el único escritor, la forma nullable por tipo que hoy solo garantizan los
    /// llamadores (design: decision 5 — movement shape per tipo). <see cref="TipoMovimientoCc.Consumo"/>,
    /// <see cref="TipoMovimientoCc.Pago"/> y (Slice 3) <see cref="TipoMovimientoCc.ActualizacionPrecios"/>
    /// tienen una forma única y fija (el resto — un <c>Ajuste</c> puede ser un contramovimiento de
    /// anulación o un ajuste manual — es estructuralmente dual, no viola nada acá).</summary>
    private static void ValidarFormaPorTipo(
        TipoMovimientoCc tipo, int? idComprobanteVenta, int? idPagoComprobante, int? idMovimientoPersonal,
        decimal importe)
    {
        if (tipo == TipoMovimientoCc.DescuentoPersonal && (idMovimientoPersonal is null || importe >= 0m))
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo DescuentoPersonal se escribe con InsertarDescuentoDePersonalAsync, apunta a un " +
                "movimiento de personal y su importe es negativo — invariante de escritura violado.");
        }

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

        if (tipo == TipoMovimientoCc.ActualizacionPrecios && (idComprobanteVenta is not null || idPagoComprobante is not null))
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo ActualizacionPrecios no lleva id_comprobante_venta ni id_pago_comprobante — " +
                "invariante de escritura violado.");
        }
    }
}
