using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Reportes;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// Acredita en las cuentas de tesorería, con la fecha de cierre del turno, lo cobrado con medios electrónicos en turnos que ya
/// estaban cerrados cuando el medio todavía no tenía cuenta (doc 10 §7). La autorización (Admin) se aplica en la capa de API.
///
/// Un turno cerrado de la empresa, con fecha de cierre dentro del rango (días de calendario en la zona horaria de la empresa), se
/// acredita por cada medio que HOY tiene una cuenta viva de la misma empresa, tiene cobros netos distintos de cero y todavía no
/// tiene filas de cierre (cobro, comisión o retenciones) para ese turno y medio. Las filas son las del cierre
/// (<see cref="CalculadorDeMovimientosDeCuenta"/> y <see cref="EscriturasDeCuentaDeTesoreria.ArmarFila"/>) con los porcentajes
/// vigentes del medio, porque los porcentajes no tienen historia. Un turno ya acreditado al cierre no se toca, aunque hoy su medio
/// tenga otros porcentajes.
///
/// Vista previa y aplicación comparten <c>PlanearAsync</c>: la vista previa lee sin lock; la aplicación toma los locks y recién
/// después lee. Orden de locks, igual que el cierre: lock de empresa (advisory) → medios de la empresa con cuenta <c>FOR SHARE</c>
/// por id ascendente → cuentas de la empresa <c>FOR NO KEY UPDATE</c> por id ascendente. Se bloquean todos los medios con cuenta y
/// todas las cuentas de la empresa, no solo los del plan, porque el plan se arma con lecturas posteriores al lock. Sin reintento
/// (<see cref="FabricaDeEstrategiaSinReintento"/>): tras un commit ambiguo, reintentar releería, vería las filas ya escritas y
/// respondería "nada para hacer" sobre una escritura que sí ocurrió; el 503 <c>resultado_incierto</c> central le pide al operador
/// que mire el libro.
///
/// Reconstruir la cadena: las filas nuevas entran en el orden <c>(fecha, id)</c> del libro, así que las filas existentes con fecha
/// igual o posterior a la primera nueva se leen (una sola vez, bajo el lock de la cuenta) y se recalculan con
/// <see cref="ReconstructorDeCadenaDeCuenta"/>, partiendo del saldo de la última fila anterior o del saldo inicial. Un ajuste manual
/// conserva su diferencia (ingreso o egreso): el libro no guarda el saldo objetivo, así que el saldo final de la cuenta sube
/// exactamente en el neto de las filas nuevas, pero el <c>inicio</c> y el <c>final</c> de ese ajuste cambian.
/// Es idempotente: la segunda pasada no encuentra nada para escribir. El índice <c>ux_movimientos_cuenta_cierre</c> es el respaldo
/// si un escritor se salteara los locks. No registra auditoría: el ajuste manual de cuenta, su antecedente más cercano, tampoco.
/// </summary>
public class ServicioDeReconstruccionDeCuentas(
    IWaysDbContext db, IContextoDeUsuario contexto, ServicioDeParametros parametros)
{
    private static readonly TipoMovimientoCuenta[] TiposDeCierre =
    [
        TipoMovimientoCuenta.Cobro, TipoMovimientoCuenta.Comision, TipoMovimientoCuenta.RetencionIibb,
        TipoMovimientoCuenta.RetencionIva, TipoMovimientoCuenta.RetencionGanancias
    ];

    private sealed record MedioConCuenta(
        int IdMedioPago, string Nombre, int IdCuenta, PorcentajesDeCobro Porcentajes);

    private sealed record TurnoCerrado(int Id, DateTimeOffset FechaCierre);

    private sealed record FilaPlaneada(
        AcreditacionDeReconstruccion Acreditacion, ComponenteDeMovimientoDeCuenta Componente, string NombreMedio);

    private sealed record Planeado(
        IReadOnlyList<AcreditacionDeReconstruccion> Acreditaciones,
        IReadOnlyList<FilaPlaneada> Filas,
        IReadOnlyDictionary<int, (string Nombre, decimal SaldoInicial)> Cuentas);

    public async Task<PlanDeReconstruccion> VistaPreviaAsync(
        SolicitudDeReconstruccionDeCuentas solicitud, CancellationToken ct = default)
    {
        var (idEmpresa, rango) = await ValidarAsync(solicitud, ct);
        var idTenant = IdDelTenant();

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var medios = await LeerMediosConCuentaAsync(conexion, null, idTenant, idEmpresa, bloquear: false, ct);
        var planeado = await PlanearAsync(idEmpresa, rango, medios, ct);

        var cuentas = new List<CuentaDeReconstruccion>();
        foreach (var grupo in planeado.Filas.GroupBy(f => f.Acreditacion.IdCuentaTesoreria).OrderBy(g => g.Key))
        {
            var (nombre, saldoInicial) = planeado.Cuentas[grupo.Key];
            var cola = await EscriturasDeCuentaDeTesoreria.LeerColaAsync(db, idTenant, grupo.Key, saldoInicial, ct);
            var neto = grupo.Sum(f => f.Componente.Ingreso - f.Componente.Egreso);
            cuentas.Add(new CuentaDeReconstruccion(grupo.Key, nombre, grupo.Count(), neto, cola.Saldo, cola.Saldo + neto));
        }

        return Armar(idEmpresa, rango, planeado, cuentas);
    }

    public async Task<PlanDeReconstruccion> AplicarAsync(
        SolicitudDeReconstruccionDeCuentas solicitud, CancellationToken ct = default)
    {
        var (idEmpresa, rango) = await ValidarAsync(solicitud, ct);
        var idTenant = IdDelTenant();
        var idEmpleado = contexto.UsuarioId;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            var conexion = db.Database.GetDbConnection();
            var transaccionCruda = transaccion.GetDbTransaction();

            await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, idEmpresa, ct);
            var medios = await LeerMediosConCuentaAsync(conexion, transaccionCruda, idTenant, idEmpresa, bloquear: true, ct);
            var bloqueadas = await EscriturasDeCuentaDeTesoreria.LeerYBloquearCuentasAsync(
                conexion, transaccionCruda, idTenant, idEmpresa, medios.Select(m => m.IdCuenta).Distinct().Order().ToList(), ct);

            // Un medio cuya cuenta se dio de baja entre la lectura y el lock no acredita, igual que en el cierre.
            var planeado = await PlanearAsync(idEmpresa, rango, medios.Where(m => bloqueadas.ContainsKey(m.IdCuenta)).ToList(), ct);

            var cuentas = new List<CuentaDeReconstruccion>();
            foreach (var grupo in planeado.Filas.GroupBy(f => f.Acreditacion.IdCuentaTesoreria).OrderBy(g => g.Key))
            {
                var saldoInicial = bloqueadas[grupo.Key].SaldoInicial;
                var cola = await EscriturasDeCuentaDeTesoreria.LeerColaAsync(db, idTenant, grupo.Key, saldoInicial, ct);
                var saldoDespues = await AgregarYReconstruirAsync(idTenant, idEmpleado, grupo.Key, saldoInicial, grupo.ToList(), ct);
                cuentas.Add(new CuentaDeReconstruccion(
                    grupo.Key, planeado.Cuentas[grupo.Key].Nombre, grupo.Count(),
                    grupo.Sum(f => f.Componente.Ingreso - f.Componente.Egreso), cola.Saldo, saldoDespues));
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is DbException { SqlState: "23505" } interna
                && interna.Message.Contains("ux_movimientos_cuenta_cierre", StringComparison.Ordinal))
            {
                throw ErrorDominio.Conflicto(
                    "reconstruccion_concurrente",
                    "Otro cierre o reconstrucción acreditó esos turnos mientras se aplicaba. No se escribió nada: repetí la vista previa.");
            }

            await transaccion.CommitAsync(ct);

            return Armar(idEmpresa, rango, planeado, cuentas);
        });
    }

    private static PlanDeReconstruccion Armar(
        int idEmpresa, RangoDeReporte rango, Planeado planeado, IReadOnlyList<CuentaDeReconstruccion> cuentas) =>
        new(idEmpresa, rango.Desde, rango.Hasta, planeado.Acreditaciones.Select(a => a.IdTurnoCaja).Distinct().Count(),
            planeado.Filas.Count, planeado.Acreditaciones, cuentas);

    /// <summary>Agrega las filas nuevas de una cuenta y recalcula <c>inicio</c>/<c>final</c> desde la primera hasta el final de la
    /// cadena. Las filas existentes se leen UNA vez, ya bajo el lock de la cuenta, y se modifican en el mismo tracker. Devuelve el
    /// saldo final de la cuenta.</summary>
    private async Task<decimal> AgregarYReconstruirAsync(
        int idTenant, int idEmpleado, int idCuenta, decimal saldoInicial, List<FilaPlaneada> filas, CancellationToken ct)
    {
        var nuevas = filas
            .Select(f => EscriturasDeCuentaDeTesoreria.ArmarFila(
                idTenant, idCuenta, f.Acreditacion.FechaCierre, f.Componente, f.Acreditacion.IdTurnoCaja,
                f.Acreditacion.IdMedioPago, f.NombreMedio, inicio: 0m, idEmpleado))
            .ToList();

        var primeraFecha = nuevas.Min(n => n.Fecha);

        var anterior = await db.MovimientosCuenta
            .Where(m => m.IdCuentaTesoreria == idCuenta && m.Fecha < primeraFecha)
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Select(m => (decimal?)m.Final)
            .FirstOrDefaultAsync(ct) ?? saldoInicial;

        var existentes = await db.MovimientosCuenta
            .Where(m => m.IdCuentaTesoreria == idCuenta && m.Fecha >= primeraFecha)
            .OrderBy(m => m.Fecha).ThenBy(m => m.Id)
            .ToListAsync(ct);

        // Las filas nuevas tendrán ids mayores que todas las existentes y OrderBy es estable: ante una fecha igual, la existente va primero.
        var enOrden = existentes.Concat(nuevas).OrderBy(m => m.Fecha).ToList();
        var saldos = ReconstructorDeCadenaDeCuenta.Recalcular(
            anterior, enOrden.Select(m => new DiferenciaDeFila(m.Ingreso, m.Egreso)).ToList());

        for (var i = 0; i < enOrden.Count; i++)
        {
            enOrden[i].Inicio = saldos[i].Inicio;
            enOrden[i].Final = saldos[i].Final;
        }

        foreach (var nueva in nuevas.OrderBy(n => n.Fecha))
        {
            db.MovimientosCuenta.Add(nueva);
        }

        return saldos[^1].Final;
    }

    private async Task<Planeado> PlanearAsync(
        int idEmpresa, RangoDeReporte rango, IReadOnlyList<MedioConCuenta> medios, CancellationToken ct)
    {
        var vacio = new Planeado([], [], new Dictionary<int, (string, decimal)>());
        if (medios.Count == 0)
        {
            return vacio;
        }

        var turnos = await (
            from t in db.TurnosCaja
            join pv in db.PuntosVenta on t.IdPuntoVenta equals pv.Id
            where pv.IdEmpresa == idEmpresa
                  && t.Estado == EstadoTurno.Cerrado
                  && t.FechaCierre >= rango.DesdeUtc
                  && t.FechaCierre < rango.HastaUtcExclusivo
            orderby t.FechaCierre, t.Id
            select new TurnoCerrado(t.Id, t.FechaCierre!.Value))
            .ToListAsync(ct);
        if (turnos.Count == 0)
        {
            return vacio;
        }

        var idsDeTurno = turnos.Select(t => t.Id).ToArray();
        var idsDeMedio = medios.Select(m => m.IdMedioPago).ToArray();

        // Mismo conjunto que LectorDeMovimientosDelTurno.Pagos: cobros de comprobantes emitidos del turno, por medio.
        var cobros = await (
            from p in db.Cobros
            join c in db.ComprobantesVenta on p.IdComprobanteVenta equals c.Id
            where c.IdTurnoCaja != null && idsDeTurno.Contains(c.IdTurnoCaja.Value)
                  && c.Estado == EstadoComprobante.Emitido
                  && idsDeMedio.Contains(p.IdMedioPago)
            group p.Importe by new { IdTurno = c.IdTurnoCaja!.Value, p.IdMedioPago } into g
            select new { g.Key.IdTurno, g.Key.IdMedioPago, Bruto = g.Sum() })
            .ToListAsync(ct);

        var acreditados = (await db.MovimientosCuenta
            .Where(m => m.IdTurnoCaja != null && m.IdMedioPago != null
                        && idsDeTurno.Contains(m.IdTurnoCaja.Value) && TiposDeCierre.Contains(m.Tipo))
            .Select(m => new { m.IdTurnoCaja, m.IdMedioPago })
            .Distinct()
            .ToListAsync(ct))
            .Select(m => (m.IdTurnoCaja!.Value, m.IdMedioPago!.Value))
            .ToHashSet();

        var brutoPorTurnoYMedio = cobros.ToDictionary(c => (c.IdTurno, c.IdMedioPago), c => c.Bruto);
        var idsDeCuenta = medios.Select(m => m.IdCuenta).Distinct().ToArray();
        var cuentas = await db.CuentasTesoreria
            .Where(c => idsDeCuenta.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre, c.SaldoInicial })
            .ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.SaldoInicial), ct);

        var acreditaciones = new List<AcreditacionDeReconstruccion>();
        var filas = new List<FilaPlaneada>();
        foreach (var turno in turnos)
        {
            foreach (var medio in medios)
            {
                if (!brutoPorTurnoYMedio.TryGetValue((turno.Id, medio.IdMedioPago), out var bruto)
                    || bruto == 0m
                    || acreditados.Contains((turno.Id, medio.IdMedioPago))
                    || !cuentas.TryGetValue(medio.IdCuenta, out var cuenta))
                {
                    continue;
                }

                var componentes = CalculadorDeMovimientosDeCuenta.Calcular(bruto, medio.Porcentajes);
                var acreditacion = new AcreditacionDeReconstruccion(
                    turno.Id, turno.FechaCierre, medio.IdMedioPago, medio.Nombre, medio.IdCuenta, cuenta.Nombre, bruto,
                    componentes.Sum(c => c.Ingreso - c.Egreso),
                    componentes.Select(c => new ComponenteDeReconstruccion(c.Tipo, c.Ingreso, c.Egreso, c.PorcentajeAplicado)).ToList());
                acreditaciones.Add(acreditacion);
                filas.AddRange(componentes.Select(c => new FilaPlaneada(acreditacion, c, medio.Nombre)));
            }
        }

        return new Planeado(acreditaciones, filas, cuentas);
    }

    private async Task<(int IdEmpresa, RangoDeReporte Rango)> ValidarAsync(
        SolicitudDeReconstruccionDeCuentas solicitud, CancellationToken ct)
    {
        if (solicitud.IdEmpresa is not { } idEmpresa)
        {
            throw new ErrorDominio("solicitud_invalida", "Indicá la empresa.", 400);
        }

        if (solicitud.Desde is not { } desde || solicitud.Hasta is not { } hasta)
        {
            throw new ErrorDominio("solicitud_invalida", "Indicá 'desde' y 'hasta'.", 400);
        }

        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }

        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, null, ct);
        var zona = TimeZoneInfo.FindSystemTimeZoneById(JsonSerializer.Deserialize<string>(resuelto.Valor)!);

        return (idEmpresa, RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona));
    }

    private int IdDelTenant() =>
        contexto.IdTenant
        ?? throw new InvalidOperationException(
            "ServicioDeReconstruccionDeCuentas requiere un actor de tenant; la política de la ruta no admite plataforma.");

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    /// <summary>Medios del tenant cuya cuenta es una cuenta viva de la empresa, con sus porcentajes vigentes. Con
    /// <paramref name="bloquear"/> toma <c>FOR SHARE</c> por id ascendente, como el cierre, para que guardar un medio espere a que la
    /// reconstrucción termine.</summary>
    private static async Task<List<MedioConCuenta>> LeerMediosConCuentaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idEmpresa, bool bloquear, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT mp.id_medio_pago, mp.nombre, mp.id_cuenta_tesoreria, mp.comision_porcentaje, " +
            "mp.retencion_iibb_porcentaje, mp.retencion_iva_porcentaje, mp.retencion_ganancias_porcentaje " +
            "FROM medios_pago mp JOIN cuentas_tesoreria ct " +
            "ON ct.id_cuenta_tesoreria = mp.id_cuenta_tesoreria AND ct.id_tenant = mp.id_tenant " +
            "WHERE mp.id_tenant = $1 AND ct.id_empresa = $2 AND ct.deleted_at IS NULL " +
            "ORDER BY mp.id_medio_pago" + (bloquear ? " FOR SHARE OF mp" : string.Empty);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idEmpresa);

        var medios = new List<MedioConCuenta>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            medios.Add(new MedioConCuenta(
                lector.GetInt32(0), lector.GetString(1), lector.GetInt32(2),
                new PorcentajesDeCobro(
                    lector.GetDecimal(3), lector.GetDecimal(4), lector.GetDecimal(5), lector.GetDecimal(6))));
        }

        return medios;
    }
}
