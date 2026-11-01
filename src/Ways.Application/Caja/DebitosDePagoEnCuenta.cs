using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// Lleva las filas <c>pago</c> de <c>movimientos_cuenta</c> de un pago a lo que sus ítems vivos dicen (doc 10 §7). El objetivo de
/// cada (cuenta, medio) es la suma de los ítems vivos del medio, ruteados por la cuenta ACTUAL del medio, siempre que la cuenta
/// esté viva y sea de la empresa del pago; lo ya debitado es la suma de las filas <c>pago</c> del pago en esa (cuenta, medio)
/// (egreso − ingreso). Cada diferencia distinta de cero escribe una fila (<see cref="CalculadorDeDebitosDePago"/>), así que el alta
/// debita, la edición escribe solo la diferencia y la baja, sin ítems vivos, devuelve todo. Las filas en una cuenta dada de baja o
/// de otra empresa no se tocan. Lo llaman todos los escritores de ítems de pago, al final de su transacción y antes del commit.
///
/// Orden de locks, el mismo que el cierre: advisory de la empresa → medios de los ítems con cuenta <c>FOR SHARE</c> por id
/// ascendente → cuentas <c>FOR NO KEY UPDATE</c> por id ascendente. Los dos advisory son reentrantes, así que un pago que ya tomó
/// el de la tesorería de efectivo no espera de nuevo. Las filas <c>pago</c> existentes se leen después del advisory: sus únicos
/// escritores son esta función, con el lock del pago (edición y baja) o sobre un pago recién insertado, y la reconstrucción del
/// historial, que toma el mismo advisory; las colas de las cuentas, después del lock de la cuenta.
///
/// Sin ningún medio con cuenta en los ítems y sin débitos previos no se toma ningún lock: un pago en efectivo no se serializa con
/// los demás pagos de la empresa. Esa decisión se toma con una lectura sin lock del medio; si en ese instante se le asigna una
/// cuenta, el pago queda como uno anterior a la asignación (el libro de tesorería, que rutea por la cuenta actual, sí lo cuenta).
/// Los ítems del pago se leen una vez: solo los cambia esta misma transacción.
/// </summary>
public static class DebitosDePagoEnCuenta
{
    public static async Task ReconciliarAsync(
        IWaysDbContext db, DbConnection conexion, DbTransaction? transaccion, int idTenant, int idEmpresa, int idPago,
        int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var importePorMedio = await db.ItemsPago
            .Where(i => i.IdPago == idPago && i.DeletedAt == null)
            .GroupBy(i => i.IdMedioPago)
            .Select(g => new { IdMedioPago = g.Key, Importe = g.Sum(i => i.Importe) })
            .ToDictionaryAsync(x => x.IdMedioPago, x => x.Importe, ct);

        var idsDeMedio = importePorMedio.Keys.ToList();
        var algunMedioConCuenta = idsDeMedio.Count > 0
            && await db.MediosPago.IgnoreQueryFilters(["BajaLogica"]).AnyAsync(m => idsDeMedio.Contains(m.Id) && m.IdCuentaTesoreria != null, ct);
        if (!algunMedioConCuenta && !await db.MovimientosCuenta.AnyAsync(m => m.IdPago == idPago, ct))
        {
            return;
        }

        await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccion, idTenant, idEmpresa, ct);

        var cuentaPorMedio = await LeerYBloquearCuentaDeMediosAsync(conexion, transaccion, idTenant, idsDeMedio, ct);

        var yaDebitado = (await db.MovimientosCuenta
                .Where(m => m.IdPago == idPago)
                .GroupBy(m => new { m.IdCuentaTesoreria, m.IdMedioPago })
                .Select(g => new
                {
                    g.Key.IdCuentaTesoreria, g.Key.IdMedioPago, Neto = g.Sum(m => m.Egreso - m.Ingreso)
                })
                .ToListAsync(ct))
            .Select(x => new ImporteDeCuentaYMedio(
                x.IdCuentaTesoreria,
                x.IdMedioPago ?? throw new InvalidOperationException($"Una fila de débito del pago {idPago} no tiene medio."),
                x.Neto))
            .ToList();

        var idsDeCuenta = cuentaPorMedio.Values.Concat(yaDebitado.Select(y => y.IdCuenta)).Distinct().Order().ToList();
        var cuentas = await EscriturasDeCuentaDeTesoreria.LeerYBloquearCuentasAsync(
            conexion, transaccion, idTenant, idEmpresa, idsDeCuenta, ct);

        var objetivo = cuentaPorMedio
            .Where(m => cuentas.ContainsKey(m.Value))
            .Select(m => new ImporteDeCuentaYMedio(m.Value, m.Key, importePorMedio[m.Key]));
        var debitos = CalculadorDeDebitosDePago.Calcular(objetivo, yaDebitado.Where(y => cuentas.ContainsKey(y.IdCuenta)));

        foreach (var deLaCuenta in debitos.GroupBy(d => d.IdCuenta))
        {
            var cola = await EscriturasDeCuentaDeTesoreria.LeerColaAsync(
                db, idTenant, deLaCuenta.Key, cuentas[deLaCuenta.Key].SaldoInicial, ct);
            var fecha = EscriturasDeCuentaDeTesoreria.FechaMonotona(momento, cola.Fecha);
            var saldo = cola.Saldo;

            foreach (var debito in deLaCuenta)
            {
                var fila = ArmarFila(idTenant, debito, idPago, fecha, saldo, idEmpleado);
                db.MovimientosCuenta.Add(fila);
                saldo = fila.Final;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Fila <c>pago</c> del libro para un débito: la arman tanto el reconciliador como la reconstrucción del historial, así
    /// que las dos escriben el mismo concepto. <c>Final</c> sale de <paramref name="inicio"/> más la diferencia del débito.</summary>
    internal static MovimientoCuenta ArmarFila(
        int idTenant, DebitoDePago debito, int idPago, DateTimeOffset fecha, decimal inicio, int idEmpleado) =>
        new()
        {
            IdTenant = idTenant,
            IdCuentaTesoreria = debito.IdCuenta,
            Fecha = fecha,
            Tipo = TipoMovimientoCuenta.Pago,
            IdMedioPago = debito.IdMedioPago,
            IdPago = idPago,
            Concepto = $"Pago {idPago}",
            Inicio = inicio,
            Ingreso = debito.Ingreso,
            Egreso = debito.Egreso,
            Final = inicio + debito.Ingreso - debito.Egreso,
            IdEmpleado = idEmpleado
        };

    /// <summary>Cuenta actual de cada medio con cuenta, leída bajo <c>FOR SHARE</c> por id ascendente: guardar el medio espera
    /// hasta el commit, así que la cuenta no cambia entre esta lectura y la escritura.</summary>
    private static async Task<Dictionary<int, int>> LeerYBloquearCuentaDeMediosAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, List<int> idsDeMedio, CancellationToken ct)
    {
        var cuentaPorMedio = new Dictionary<int, int>();
        if (idsDeMedio.Count == 0)
        {
            return cuentaPorMedio;
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_medio_pago, id_cuenta_tesoreria FROM medios_pago " +
            "WHERE id_tenant = $1 AND id_medio_pago = ANY($2) AND id_cuenta_tesoreria IS NOT NULL " +
            "ORDER BY id_medio_pago FOR SHARE";
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idsDeMedio.ToArray());

        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            cuentaPorMedio[lector.GetInt32(0)] = lector.GetInt32(1);
        }

        return cuentaPorMedio;
    }
}
