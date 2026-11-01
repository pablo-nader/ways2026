using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// Acredita al cierre de un turno lo cobrado con cada medio electrónico en su cuenta de tesorería, neto de comisión y
/// retenciones (doc 10 §7). Es el único escritor del libro <c>movimientos_cuenta</c> por esta vía y corre dentro de la
/// transacción del cierre, ya con el lock exclusivo del turno y con el lock de empresa de la tesorería de efectivo.
///
/// Orden de locks: turno (exclusivo) → empresa (advisory) → medios <c>FOR SHARE</c> por id ascendente → cuentas de la
/// empresa del turno <c>FOR NO KEY UPDATE</c> por id ascendente. El medio se lee una sola vez bajo su lock, así que su
/// cuenta y sus porcentajes no cambian entre la lectura y el commit; guardar un medio espera a ese lock hasta que el cierre
/// termina. Solo se bloquean las cuentas de la empresa del turno: una cuenta de otra empresa no se acredita y por lo tanto
/// tampoco se bloquea. <c>FOR NO KEY UPDATE</c> choca con el <c>FOR UPDATE</c> del ABM de cuentas y con otro cierre que
/// acredite la misma cuenta, que es lo que serializa su cadena, pero no con el <c>FOR KEY SHARE</c> de los chequeos de FK, así
/// que insertar un movimiento o asignar la cuenta a un medio no queda bloqueado. Ninguno de esos escritores forma un ciclo
/// con este: el ABM de cuentas solo lee los medios, el guardado de un medio pide <c>FOR KEY SHARE</c> sobre la cuenta (que no
/// choca) y los pagos solo toman el lock de empresa. El saldo de partida se lee de la fila de la cuenta ya bloqueada y la
/// cola de la cadena después, nunca antes.
///
/// Que el medio sea electrónico no se vuelve a chequear: <c>ck_medios_pago_cuenta_solo_electronico</c> impide que un
/// medio de otro comportamiento tenga cuenta, así que "tiene cuenta" ya implica "es electrónico".
///
/// Fechas monótonas: la <c>fecha</c> de toda fila que se agrega a una cuenta, de este escritor o del ajuste manual, es
/// <c>max(hora propia, fecha del último movimiento de la cuenta)</c> (<see cref="FechaMonotona"/>), leída junto con el
/// <c>final</c> bajo el lock. Cada escritor fija su hora antes de esperar ese lock, así que sin esto un cierre podía comitear un id
/// mayor con una fecha anterior. Con la regla, para cada cuenta que solo tuvo estos escritores el orden por fecha (con empates) es el orden por id, y el libro por
/// rango puede definir su saldo de apertura como el último movimiento anterior al rango. La reconstrucción del historial
/// (<see cref="ServicioDeReconstruccionDeCuentas"/>) es el único escritor que inserta filas con fecha anterior a la cola: por eso la cadena
/// se ordena por <c>(fecha, id)</c> y no solo por <c>id</c>, y la cola es la última fila en ese orden. Las filas de un cierre para una misma
/// cuenta comparten la fecha resultante. La fila de la tesorería de efectivo conserva la hora del cierre. Consecuencia: si el último
/// movimiento de una cuenta tiene fecha futura (reloj desfasado o una fila mal cargada), toda fila posterior de esa cuenta lleva esa
/// fecha hasta que la hora real la alcance; se cura sola y no se detecta (la pantalla avisa si el ajuste cae fuera del rango mostrado).
///
/// Lo que el libro todavía NO refleja: (a) una devolución por nota de crédito de una venta cobrada con un medio digital
/// no descuenta de la cuenta, que queda por encima de lo real en el importe devuelto (neto de comisión y retenciones) hasta
/// un ajuste manual; (b) un pago pagado con un medio digital reduce el esperado de ese medio en el arqueo pero no debita la
/// cuenta. El soporte de un bruto negativo (cobro por egreso, comisión y retenciones por ingreso) es una capacidad que ningún
/// camino actual ejerce: los importes de pago nunca son negativos y <c>ValidadorDePagos</c> rechaza los pagos en una nota de
/// crédito.
/// </summary>
public static class EscriturasDeCuentaDeTesoreria
{
    private sealed record MedioAcreditable(
        int IdMedioPago, string Nombre, int IdCuenta, decimal Bruto, PorcentajesDeCobro Porcentajes);

    internal sealed record CuentaBloqueada(decimal SaldoInicial);

    /// <summary>Saldo vigente de la cuenta y fecha de su último movimiento (<c>null</c> sin movimientos).</summary>
    public sealed record ColaDeCuenta(decimal Saldo, DateTimeOffset? Fecha);

    /// <summary>Sin cuenta (o con una cuenta dada de baja o de otra empresa que la del turno) un medio electrónico no escribe nada y no
    /// frena el cierre. Un bruto cero no genera filas. Una cuenta inactiva igual recibe movimientos: desactivarla solo
    /// impide asignarla a medios nuevos.</summary>
    public static async Task AcreditarAsync(
        IWaysDbContext db, DbConnection conexion, DbTransaction? transaccion, int idTenant, int idEmpresa,
        int idTurnoCaja, DateTimeOffset momento, int idEmpleado, IReadOnlyList<ActividadDeMedio> actividad,
        CancellationToken ct)
    {
        var brutoPorMedio = actividad
            .Where(a => a.Pagos != 0m)
            .ToDictionary(a => a.IdMedioPago, a => a.Pagos);
        if (brutoPorMedio.Count == 0)
        {
            return;
        }

        var medios = await LeerYBloquearMediosAsync(conexion, transaccion, idTenant, brutoPorMedio, ct);
        if (medios.Count == 0)
        {
            return;
        }

        var cuentas = await LeerYBloquearCuentasAsync(
            conexion, transaccion, idTenant, idEmpresa, medios.Select(m => m.IdCuenta).Distinct().Order().ToList(), ct);

        var saldos = new Dictionary<int, decimal>();
        var fechas = new Dictionary<int, DateTimeOffset>();
        foreach (var (idCuenta, cuenta) in cuentas.OrderBy(c => c.Key))
        {
            var cola = await LeerColaAsync(db, idTenant, idCuenta, cuenta.SaldoInicial, ct);
            saldos[idCuenta] = cola.Saldo;
            fechas[idCuenta] = FechaMonotona(momento, cola.Fecha);
        }

        foreach (var medio in medios.Where(m => saldos.ContainsKey(m.IdCuenta)))
        {
            foreach (var componente in CalculadorDeMovimientosDeCuenta.Calcular(medio.Bruto, medio.Porcentajes))
            {
                var inicio = saldos[medio.IdCuenta];
                var final = inicio + componente.Ingreso - componente.Egreso;
                saldos[medio.IdCuenta] = final;

                db.MovimientosCuenta.Add(ArmarFila(
                    idTenant, medio.IdCuenta, fechas[medio.IdCuenta], componente, idTurnoCaja, medio.IdMedioPago, medio.Nombre,
                    inicio, idEmpleado));
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Compensa en las cuentas de tesorería el cambio de medios de una venta de un turno ya cerrado. Mismo orden de locks que
    /// <see cref="AcreditarAsync"/>: el llamador ya tiene el turno (<c>FOR UPDATE</c>) y el lock de empresa; acá se toman los
    /// medios <c>FOR SHARE</c> y las cuentas de la empresa del turno <c>FOR NO KEY UPDATE</c>, en id ascendente, y recién bajo
    /// ese lock se lee qué medios ya acreditó el turno.
    ///
    /// Por cada medio con cuenta cuya variación de bruto no es cero:
    /// <list type="bullet">
    /// <item>si el turno ya le escribió sus filas de cierre (cobro, comisión, retenciones), se agrega una fila <c>ajuste</c> con el
    /// neto de la variación (comisión y retenciones con los porcentajes actuales del medio, como el cierre), con el turno y el
    /// medio para rastrearla. Las filas de cierre no se tocan: su índice único las admite una sola vez por turno y medio;</item>
    /// <item>si no las tiene y la variación es positiva, se escriben las filas de cierre con todo lo que ese medio cobra ahora en el
    /// turno: lo mismo que haría la reconstrucción del historial, que después ya no vuelve a acreditar ese par;</item>
    /// <item>si no las tiene y la variación es negativa, no se escribe nada: la cuenta nunca recibió ese cobro.</item>
    /// </list>
    /// Las fechas siguen <see cref="FechaMonotona"/>.
    /// </summary>
    public static async Task CompensarCambioDeMediosAsync(
        IWaysDbContext db, DbConnection conexion, DbTransaction? transaccion, int idTenant, int idEmpresa,
        int idTurnoCaja, int idComprobanteVenta, DateTimeOffset momento, int idEmpleado,
        IReadOnlyDictionary<int, decimal> variacionDeBrutoPorMedio, CancellationToken ct)
    {
        var variaciones = variacionDeBrutoPorMedio.Where(v => v.Value != 0m).ToDictionary(v => v.Key, v => v.Value);
        if (variaciones.Count == 0)
        {
            return;
        }

        var medios = await LeerYBloquearMediosAsync(conexion, transaccion, idTenant, variaciones, ct);
        if (medios.Count == 0)
        {
            return;
        }

        var cuentas = await LeerYBloquearCuentasAsync(
            conexion, transaccion, idTenant, idEmpresa, medios.Select(m => m.IdCuenta).Distinct().Order().ToList(), ct);

        var idsDeMedio = medios.Select(m => m.IdMedioPago).ToList();
        var acreditados = (await db.MovimientosCuenta
                .Where(m => m.IdTurnoCaja == idTurnoCaja && m.IdMedioPago != null && idsDeMedio.Contains(m.IdMedioPago.Value)
                            && TiposDeCierre.Contains(m.Tipo))
                .Select(m => m.IdMedioPago!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var brutoActual = await BrutoDelTurnoPorMedioAsync(
            db, idTurnoCaja, medios.Where(m => !acreditados.Contains(m.IdMedioPago) && m.Bruto > 0m).Select(m => m.IdMedioPago).ToList(),
            ct);

        var saldos = new Dictionary<int, decimal>();
        var fechas = new Dictionary<int, DateTimeOffset>();
        foreach (var (idCuenta, cuenta) in cuentas.OrderBy(c => c.Key))
        {
            var cola = await LeerColaAsync(db, idTenant, idCuenta, cuenta.SaldoInicial, ct);
            saldos[idCuenta] = cola.Saldo;
            fechas[idCuenta] = FechaMonotona(momento, cola.Fecha);
        }

        foreach (var medio in medios.Where(m => saldos.ContainsKey(m.IdCuenta)))
        {
            if (acreditados.Contains(medio.IdMedioPago))
            {
                var neto = CalculadorDeMovimientosDeCuenta.Calcular(medio.Bruto, medio.Porcentajes).Sum(c => c.Ingreso - c.Egreso);
                if (neto == 0m)
                {
                    continue;
                }

                var inicio = saldos[medio.IdCuenta];
                var fila = new MovimientoCuenta
                {
                    IdTenant = idTenant,
                    IdCuentaTesoreria = medio.IdCuenta,
                    Fecha = fechas[medio.IdCuenta],
                    Tipo = TipoMovimientoCuenta.Ajuste,
                    IdTurnoCaja = idTurnoCaja,
                    IdMedioPago = medio.IdMedioPago,
                    Concepto = $"Cambio de medio de la venta {idComprobanteVenta} ({medio.Nombre}) - turno {idTurnoCaja}",
                    Inicio = inicio,
                    Ingreso = neto > 0m ? neto : 0m,
                    Egreso = neto < 0m ? -neto : 0m,
                    Final = inicio + neto,
                    IdEmpleado = idEmpleado,
                    BaseBruta = medio.Bruto
                };
                saldos[medio.IdCuenta] = fila.Final;
                db.MovimientosCuenta.Add(fila);
                continue;
            }

            var bruto = brutoActual.GetValueOrDefault(medio.IdMedioPago);
            if (bruto > 0m)
            {
                foreach (var componente in CalculadorDeMovimientosDeCuenta.Calcular(bruto, medio.Porcentajes))
                {
                    var inicio = saldos[medio.IdCuenta];
                    saldos[medio.IdCuenta] = inicio + componente.Ingreso - componente.Egreso;
                    db.MovimientosCuenta.Add(ArmarFila(
                        idTenant, medio.IdCuenta, fechas[medio.IdCuenta], componente, idTurnoCaja, medio.IdMedioPago, medio.Nombre,
                        inicio, idEmpleado));
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static readonly TipoMovimientoCuenta[] TiposDeCierre =
    [
        TipoMovimientoCuenta.Cobro, TipoMovimientoCuenta.Comision, TipoMovimientoCuenta.RetencionIibb,
        TipoMovimientoCuenta.RetencionIva, TipoMovimientoCuenta.RetencionGanancias
    ];

    /// <summary>Mismo conjunto que <see cref="LectorDeMovimientosDelTurno"/>: cobros vivos de comprobantes emitidos del turno.</summary>
    private static async Task<Dictionary<int, decimal>> BrutoDelTurnoPorMedioAsync(
        IWaysDbContext db, int idTurnoCaja, List<int> idsDeMedio, CancellationToken ct)
    {
        if (idsDeMedio.Count == 0)
        {
            return [];
        }

        return await (
                from p in db.Cobros
                join c in db.ComprobantesVenta.SinEspejosFiscales() on p.IdComprobanteVenta equals c.Id
                where c.IdTurnoCaja == idTurnoCaja && c.Estado == EstadoComprobante.Emitido
                      && idsDeMedio.Contains(p.IdMedioPago)
                group p.Importe by p.IdMedioPago into g
                select new { IdMedioPago = g.Key, Bruto = g.Sum() })
            .ToDictionaryAsync(x => x.IdMedioPago, x => x.Bruto, ct);
    }

    /// <summary>Bloquea una sola cuenta viva del tenant con el mismo <c>FOR NO KEY UPDATE</c> que el cierre y devuelve su saldo
    /// vigente: el <c>final</c> del último movimiento, leído después del lock, o <c>saldo_inicial</c> si no tiene. <c>null</c>
    /// si la cuenta no existe, está dada de baja o es de otro tenant. La empresa no se filtra: un ajuste manual no pertenece a
    /// un turno. Exige una transacción abierta, porque el lock se libera al terminar el statement.</summary>
    public static async Task<ColaDeCuenta?> BloquearYLeerColaAsync(
        IWaysDbContext db, DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCuenta,
        CancellationToken ct)
    {
        var cuentas = await LeerYBloquearCuentasAsync(conexion, transaccion, idTenant, idEmpresa: null, [idCuenta], ct);

        return cuentas.TryGetValue(idCuenta, out var cuenta)
            ? await LeerColaAsync(db, idTenant, idCuenta, cuenta.SaldoInicial, ct)
            : null;
    }

    internal static async Task<ColaDeCuenta> LeerColaAsync(
        IWaysDbContext db, int idTenant, int idCuenta, decimal saldoInicial, CancellationToken ct)
    {
        var ultimo = await db.MovimientosCuenta
            .Where(m => m.IdCuentaTesoreria == idCuenta && m.IdTenant == idTenant)
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Select(m => new { m.Final, m.Fecha })
            .FirstOrDefaultAsync(ct);

        return ultimo is null ? new ColaDeCuenta(saldoInicial, null) : new ColaDeCuenta(ultimo.Final, ultimo.Fecha);
    }

    /// <summary>Fecha con la que una fila se agrega a la cadena de una cuenta: nunca anterior a la del último movimiento. Cada
    /// escritor fija su hora ANTES de esperar el lock de la cuenta, así que un cierre que esperó puede traer una hora más vieja que
    /// la de un ajuste ya comiteado; subirla a la de la cola mantiene el orden por fecha igual al orden por id. Con una cola fechada en el
    /// futuro, todas las filas siguientes llevan esa fecha hasta que la hora real la pase.</summary>
    public static DateTimeOffset FechaMonotona(DateTimeOffset candidata, DateTimeOffset? fechaDeLaCola) =>
        fechaDeLaCola is { } cola && cola > candidata ? cola : candidata;

    /// <summary>Fila del libro para un componente del cobro de un turno: la arma tanto el cierre como la reconstrucción del
    /// historial, así que las dos escriben el mismo concepto y el mismo snapshot. <c>Final</c> sale de <paramref name="inicio"/>
    /// más la diferencia del componente.</summary>
    internal static MovimientoCuenta ArmarFila(
        int idTenant, int idCuenta, DateTimeOffset fecha, ComponenteDeMovimientoDeCuenta componente, int idTurnoCaja,
        int idMedioPago, string nombreMedio, decimal inicio, int idEmpleado) =>
        new()
        {
            IdTenant = idTenant,
            IdCuentaTesoreria = idCuenta,
            Fecha = fecha,
            Tipo = componente.Tipo,
            IdTurnoCaja = idTurnoCaja,
            IdMedioPago = idMedioPago,
            Concepto = Concepto(componente, nombreMedio, idTurnoCaja),
            Inicio = inicio,
            Ingreso = componente.Ingreso,
            Egreso = componente.Egreso,
            Final = inicio + componente.Ingreso - componente.Egreso,
            IdEmpleado = idEmpleado,
            BaseBruta = componente.BaseBruta,
            PorcentajeAplicado = componente.PorcentajeAplicado
        };

    private static string Concepto(ComponenteDeMovimientoDeCuenta componente, string medio, int idTurnoCaja)
    {
        var devolucion = componente.Ingreso > 0m && componente.Tipo != TipoMovimientoCuenta.Cobro;
        var descripcion = componente.Tipo switch
        {
            TipoMovimientoCuenta.Cobro when componente.Egreso > 0m => $"Devoluciones netas con {medio}",
            TipoMovimientoCuenta.Cobro => $"Cobros con {medio}",
            TipoMovimientoCuenta.Comision => $"{(devolucion ? "Devolución de comisión" : "Comisión")} de {medio}",
            TipoMovimientoCuenta.RetencionIibb => $"{(devolucion ? "Devolución de retención" : "Retención")} de IIBB de {medio}",
            TipoMovimientoCuenta.RetencionIva => $"{(devolucion ? "Devolución de retención" : "Retención")} de IVA de {medio}",
            TipoMovimientoCuenta.RetencionGanancias =>
                $"{(devolucion ? "Devolución de retención" : "Retención")} de Ganancias de {medio}",
            _ => throw new InvalidOperationException($"El cierre no escribe movimientos de tipo {componente.Tipo}.")
        };

        return $"{descripcion} - turno {idTurnoCaja}";
    }

    private static async Task<List<MedioAcreditable>> LeerYBloquearMediosAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, Dictionary<int, decimal> brutoPorMedio,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_medio_pago, nombre, id_cuenta_tesoreria, comision_porcentaje, retencion_iibb_porcentaje, " +
            "retencion_iva_porcentaje, retencion_ganancias_porcentaje FROM medios_pago " +
            "WHERE id_tenant = $1 AND id_medio_pago = ANY($2) AND id_cuenta_tesoreria IS NOT NULL " +
            "ORDER BY id_medio_pago FOR SHARE";
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, brutoPorMedio.Keys.ToArray());

        var medios = new List<MedioAcreditable>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var id = lector.GetInt32(0);
            medios.Add(new MedioAcreditable(
                id, lector.GetString(1), lector.GetInt32(2), brutoPorMedio[id],
                new PorcentajesDeCobro(
                    lector.GetDecimal(3), lector.GetDecimal(4), lector.GetDecimal(5), lector.GetDecimal(6))));
        }

        return medios;
    }

    internal static async Task<Dictionary<int, CuentaBloqueada>> LeerYBloquearCuentasAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int? idEmpresa, List<int> idsDeCuenta,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_cuenta_tesoreria, saldo_inicial FROM cuentas_tesoreria " +
            (idEmpresa is null
                ? "WHERE id_tenant = $1 AND id_cuenta_tesoreria = ANY($2) AND deleted_at IS NULL "
                : "WHERE id_tenant = $1 AND id_cuenta_tesoreria = ANY($2) AND deleted_at IS NULL AND id_empresa = $3 ") +
            "ORDER BY id_cuenta_tesoreria FOR NO KEY UPDATE";
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idsDeCuenta.ToArray());
        if (idEmpresa is { } empresa)
        {
            ParametrosDeComando.Agregar(comando, empresa);
        }

        var cuentas = new Dictionary<int, CuentaBloqueada>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            cuentas[lector.GetInt32(0)] = new CuentaBloqueada(lector.GetDecimal(1));
        }

        return cuentas;
    }
}
