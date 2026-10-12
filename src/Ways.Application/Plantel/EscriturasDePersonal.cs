using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Plantel;

namespace Ways.Application.Plantel;

/// <summary>
/// Los dos statements crudos que son la ÚNICA autoridad de escritura sobre <c>personal.saldo</c> y
/// <c>movimientos_personal</c>. El llamador abre la conexión y la transacción y las comitea: esta clase
/// nunca lo hace. Mismo criterio que <c>EscriturasDeCuentaCorrienteProveedor</c>.
/// </summary>
public static class EscriturasDePersonal
{
    /// <summary>Saldo nuevo del personal y el cliente vinculado en el instante en que la fila quedó bloqueada.</summary>
    public sealed record SaldoActualizado(decimal Saldo, int? IdCliente);

    /// <summary><c>UPDATE ... RETURNING</c> crudo: nunca vía una entidad <c>Personal</c> trackeada, que
    /// duplicaría el incremento en un reintento. <c>deleted_at IS NULL</c> en el <c>WHERE</c> hace que
    /// una baja concurrente gane limpiamente: después de esperar el lock de la fila, Postgres reevalúa
    /// el predicado y el resultado es <c>null</c> (el llamador responde 404). Devuelve el saldo nuevo.</summary>
    public static async Task<decimal?> ActualizarSaldoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPersonal, decimal importe,
        CancellationToken ct) =>
        (await ActualizarSaldoYLeerVinculoAsync(conexion, transaccion, idTenant, idPersonal, importe, ct))?.Saldo;

    /// <summary>El mismo <c>UPDATE</c> que <see cref="ActualizarSaldoAsync"/>, que además devuelve el cliente
    /// vinculado leído bajo el lock de la fila: una edición del vínculo espera a este <c>UPDATE</c>, así que el
    /// pago que descuenta de la cuenta corriente usa el cliente que de verdad estaba vinculado al bloquear.</summary>
    public static async Task<SaldoActualizado?> ActualizarSaldoYLeerVinculoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPersonal, decimal importe,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE personal SET saldo = saldo + $1 " +
            "WHERE id_personal = $2 AND id_tenant = $3 AND deleted_at IS NULL RETURNING saldo, id_cliente";

        ParametrosDeComando.Agregar(comando, importe);
        ParametrosDeComando.Agregar(comando, idPersonal);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new SaldoActualizado(lector.GetDecimal(0), lector.IsDBNull(1) ? null : lector.GetInt32(1));
    }

    /// <summary>El ÚNICO <c>INSERT</c> del libro de personal.</summary>
    public static async Task<int> InsertarMovimientoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPersonal, int idEmpleado,
        DateOnly fecha, TipoMovimientoPersonal tipo, decimal? horas, decimal? valorHora, decimal importe,
        decimal saldoResultante, string? detalle, DateTimeOffset creadoEn, CancellationToken ct)
    {
        ValidarFormaPorTipo(tipo, horas, valorHora);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_personal " +
            "(id_tenant, id_personal, id_empleado, fecha, tipo, horas, valor_hora, importe, " +
            " saldo_resultante, detalle, created_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) " +
            "RETURNING id_movimiento";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPersonal);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, fecha);
        ParametrosDeComando.Agregar(comando, tipo);
        ParametrosDeComando.AgregarNulo(comando, horas);
        ParametrosDeComando.AgregarNulo(comando, valorHora);
        ParametrosDeComando.Agregar(comando, importe);
        ParametrosDeComando.Agregar(comando, saldoResultante);
        ParametrosDeComando.AgregarNulo(comando, detalle);
        ParametrosDeComando.Agregar(comando, creadoEn);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("No se pudo insertar el movimiento de personal.");

        return Convert.ToInt32(resultado);
    }

    /// <summary>Defensa en profundidad, infraestructura pura (nunca un <c>ErrorDominio</c> 4xx): una
    /// violación es un defecto del llamador. Espeja la forma por tipo de los CHECK de la tabla.</summary>
    private static void ValidarFormaPorTipo(TipoMovimientoPersonal tipo, decimal? horas, decimal? valorHora)
    {
        if (tipo == TipoMovimientoPersonal.Horas && (horas is null || valorHora is null))
        {
            throw new InvalidOperationException(
                "Un movimiento de tipo Horas requiere horas y valor_hora — invariante de escritura violado.");
        }

        if (tipo != TipoMovimientoPersonal.Horas && (horas is not null || valorHora is not null))
        {
            throw new InvalidOperationException(
                $"Un movimiento de tipo {tipo} no lleva horas ni valor_hora — invariante de escritura violado.");
        }
    }
}
