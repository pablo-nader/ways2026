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

    /// <summary>Toma el lock de la fila del personal vivo y devuelve su valor hora vigente, leído bajo ese lock
    /// (un cambio concurrente del valor espera a esta transacción). <c>null</c> si no existe, está dada de baja
    /// o es de otro tenant: el llamador responde 404. Las escrituras que leen el libro del día antes de decidir
    /// qué asentar toman este lock primero y en orden ascendente de id para no abrazarse.</summary>
    public static async Task<decimal?> BloquearPersonalAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPersonal, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT valor_hora FROM personal " +
            "WHERE id_personal = $1 AND id_tenant = $2 AND deleted_at IS NULL FOR UPDATE";

        ParametrosDeComando.Agregar(comando, idPersonal);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is null or DBNull ? null : Convert.ToDecimal(resultado);
    }

    /// <summary>Los asientos de tipo horas de un personal en las fechas dadas, en orden de inserción. Se lee
    /// con la fila del personal ya bloqueada: antes del lock el resultado puede quedar viejo.</summary>
    public static async Task<IReadOnlyDictionary<DateOnly, List<PlanificadorDeHorasDelDia.AsientoExistente>>>
        LeerAsientosDeHorasAsync(
            DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPersonal,
            IReadOnlyCollection<DateOnly> fechas, CancellationToken ct)
    {
        var resultado = new Dictionary<DateOnly, List<PlanificadorDeHorasDelDia.AsientoExistente>>();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT fecha, horas, valor_hora FROM movimientos_personal " +
            "WHERE id_personal = $1 AND id_tenant = $2 AND tipo = 'horas' AND fecha = ANY($3) " +
            "ORDER BY id_movimiento";

        ParametrosDeComando.Agregar(comando, idPersonal);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, fechas.ToArray());

        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var fecha = lector.GetFieldValue<DateOnly>(0);
            if (!resultado.TryGetValue(fecha, out var asientos))
            {
                asientos = [];
                resultado[fecha] = asientos;
            }

            asientos.Add(new PlanificadorDeHorasDelDia.AsientoExistente(lector.GetDecimal(1), lector.GetDecimal(2)));
        }

        return resultado;
    }

    /// <summary>Marca el día como confirmado con su total. Upsert: la unicidad de <c>(id_personal, fecha)</c>
    /// nunca se viola desde la aplicación. El llamador pasa el total ya calculado bajo el lock del personal.</summary>
    public static async Task UpsertarJornadaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPersonal, int idEmpleado,
        DateOnly fecha, decimal horas, DateTimeOffset ahora, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO jornadas_personal " +
            "(id_tenant, id_personal, id_empleado, fecha, horas, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $6) " +
            "ON CONFLICT (id_personal, id_tenant, fecha) DO UPDATE " +
            "SET horas = EXCLUDED.horas, id_empleado = EXCLUDED.id_empleado, updated_at = EXCLUDED.updated_at";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPersonal);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, fecha);
        ParametrosDeComando.Agregar(comando, horas);
        ParametrosDeComando.Agregar(comando, ahora);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Empleado dueño del asiento, leído SIN lock: solo sirve para decidir qué filas de personal bloquear y en qué
    /// orden. <c>null</c> si el asiento no existe o es de otro tenant. Después de los locks se confirma con
    /// <see cref="BloquearMovimientoAsync"/>.</summary>
    public static async Task<int?> LeerPersonalDelMovimientoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idMovimiento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_personal FROM movimientos_personal WHERE id_movimiento = $1 AND id_tenant = $2";

        ParametrosDeComando.Agregar(comando, idMovimiento);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is null or DBNull ? null : Convert.ToInt32(resultado);
    }

    /// <summary>Empleado e importe del asiento, leídos bajo su <c>FOR UPDATE</c>. <c>null</c> si no existe.</summary>
    public sealed record MovimientoBloqueado(int IdPersonal, decimal Importe);

    public static async Task<MovimientoBloqueado?> BloquearMovimientoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idMovimiento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_personal, importe FROM movimientos_personal " +
            "WHERE id_movimiento = $1 AND id_tenant = $2 FOR UPDATE";

        ParametrosDeComando.Agregar(comando, idMovimiento);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        return await lector.ReadAsync(ct) ? new MovimientoBloqueado(lector.GetInt32(0), lector.GetDecimal(1)) : null;
    }

    /// <summary>
    /// Mueve un asiento de un libro al de otro empleado y corrige los saldos resultantes DESPLAZÁNDOLOS, nunca
    /// recalculándolos desde cero: las filas posteriores del origen suman <c>-importe</c> (el asiento deja de
    /// pesar), el asiento toma el saldo de la fila anterior del destino más su importe, y las filas posteriores del
    /// destino suman <c>importe</c>. Es la única excepción a "el saldo resultante nunca cambia". No toca
    /// <c>personal.saldo</c> (lo corrige el llamador con <see cref="ActualizarSaldoAsync"/>). El llamador tiene
    /// bloqueadas las dos filas de personal y el asiento.
    /// </summary>
    public static async Task ReasignarMovimientoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idMovimiento, int idPersonalOrigen,
        int idPersonalDestino, decimal importe, CancellationToken ct)
    {
        await EjecutarAsync(
            conexion, transaccion,
            "UPDATE movimientos_personal SET saldo_resultante = saldo_resultante - $1 " +
            "WHERE id_personal = $2 AND id_tenant = $3 AND id_movimiento > $4",
            ct, importe, idPersonalOrigen, idTenant, idMovimiento);

        await EjecutarAsync(
            conexion, transaccion,
            "UPDATE movimientos_personal SET id_personal = $1 WHERE id_movimiento = $2 AND id_tenant = $3",
            ct, idPersonalDestino, idMovimiento, idTenant);

        await EjecutarAsync(
            conexion, transaccion,
            "UPDATE movimientos_personal SET saldo_resultante = $1 + COALESCE((" +
            "SELECT anterior.saldo_resultante FROM movimientos_personal anterior " +
            "WHERE anterior.id_personal = $2 AND anterior.id_tenant = $3 AND anterior.id_movimiento < $4 " +
            "ORDER BY anterior.id_movimiento DESC LIMIT 1), 0) " +
            "WHERE id_movimiento = $4 AND id_tenant = $3",
            ct, importe, idPersonalDestino, idTenant, idMovimiento);

        await EjecutarAsync(
            conexion, transaccion,
            "UPDATE movimientos_personal SET saldo_resultante = saldo_resultante + $1 " +
            "WHERE id_personal = $2 AND id_tenant = $3 AND id_movimiento > $4",
            ct, importe, idPersonalDestino, idTenant, idMovimiento);
    }

    private static async Task EjecutarAsync(
        DbConnection conexion, DbTransaction? transaccion, string sql, CancellationToken ct, params object[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            ParametrosDeComando.Agregar(comando, parametro);
        }

        await comando.ExecuteNonQueryAsync(ct);
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
