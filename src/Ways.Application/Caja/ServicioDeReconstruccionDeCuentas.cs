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
using Ways.Domain.Pagos;
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
/// Débitos de pagos. En el mismo rango (por <c>pago.Fecha</c>), cada pago vivo de la empresa que todavía no tiene ninguna fila
/// <c>pago</c> se debita en la cuenta ACTUAL de cada medio de sus ítems vivos que hoy tiene una cuenta viva de la empresa, con el
/// mismo cálculo y la misma fila que el reconciliador (<see cref="CalculadorDeDebitosDePago"/> sin nada debitado y
/// <see cref="DebitosDePagoEnCuenta.ArmarFila"/>), fechada con la fecha del pago. Un pago que ya tiene filas <c>pago</c> es del
/// reconciliador, que lleva sus ediciones y bajas, y no se toca. La aplicación toma <c>FOR KEY SHARE</c>, por id ascendente y antes
/// del advisory de tesorería (el mismo orden pago → advisory de la edición y la baja), sobre los pagos del rango que todavía no
/// tienen filas, y solo planea esos: una edición o una baja en curso, que toma el pago <c>FOR UPDATE</c>, termina antes o espera, y
/// su reconciliador ve las filas que se escriban acá. Las filas <c>pago</c> existentes se leen después del advisory de la empresa,
/// que toma primero todo escritor de filas <c>pago</c>; un pago creado después del lock queda para la próxima corrida.
///
/// Reconstruir la cadena: las filas nuevas entran en el orden <c>(fecha, id)</c> del libro, así que las filas existentes con fecha
/// igual o posterior a la primera nueva se leen (una sola vez, bajo el lock de la cuenta) y se recalculan con
/// <see cref="ReconstructorDeCadenaDeCuenta"/>, partiendo del saldo de la última fila anterior o del saldo inicial. Un ajuste manual
/// conserva su diferencia (ingreso o egreso): el libro no guarda el saldo objetivo, así que el saldo final de la cuenta sube
/// exactamente en el neto de las filas nuevas, pero el <c>inicio</c> y el <c>final</c> de ese ajuste cambian.
///
/// Conciliación (<see cref="GuardaDeConciliacion"/>). Los pagos con <c>Fecha</c> menor o igual a la marca no se debitan y se
/// informan en <see cref="PlanDeReconstruccion.DebitosCongelados"/>. Los turnos con <c>FechaCierre</c> menor o igual a la marca de la
/// empresa están congelados: reconstruirlos agregaría filas de cobro, comisión y retención en el pasado conciliado y cambiaría
/// las filas de caja del libro. No se rechaza el pedido: esos turnos se omiten y sus acreditaciones pendientes se informan en
/// <see cref="PlanDeReconstruccion.Congeladas"/> junto con la marca, para que el operador vea qué quedó sin acreditar. La aplicación
/// toma el lock compartido de conciliación como PRIMER lock (antes del advisory de tesorería y de cualquier lock de fila) y lee la
/// marca una sola vez, ya bajo ese lock; la vista previa la lee sin lock, como el resto de su plan.
///
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

    private sealed record DebitoPlaneado(DebitoDeReconstruccion Debito, DebitoDePago Fila);

    private sealed record Planeado(
        IReadOnlyList<AcreditacionDeReconstruccion> Acreditaciones,
        IReadOnlyList<FilaPlaneada> Filas,
        IReadOnlyList<DebitoPlaneado> Debitos,
        IReadOnlyDictionary<int, (string Nombre, decimal SaldoInicial)> Cuentas,
        IReadOnlyList<AcreditacionDeReconstruccion> Congeladas,
        IReadOnlyList<DebitoDeReconstruccion> DebitosCongelados)
    {
        /// <summary>Cuentas con filas nuevas, por id ascendente, con la cantidad de filas y su neto (ingreso − egreso).</summary>
        public IEnumerable<(int IdCuenta, int Filas, decimal Neto)> EfectoPorCuenta() =>
            Filas.Select(f => (IdCuenta: f.Acreditacion.IdCuentaTesoreria, Neto: f.Componente.Ingreso - f.Componente.Egreso))
                .Concat(Debitos.Select(d => (IdCuenta: d.Fila.IdCuenta, Neto: d.Fila.Ingreso - d.Fila.Egreso)))
                .GroupBy(f => f.IdCuenta)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count(), g.Sum(f => f.Neto)));
    }

    public async Task<PlanDeReconstruccion> VistaPreviaAsync(
        SolicitudDeReconstruccionDeCuentas solicitud, CancellationToken ct = default)
    {
        var (idEmpresa, rango) = await ValidarAsync(solicitud, ct);
        var idTenant = IdDelTenant();

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var medios = await LeerMediosConCuentaAsync(conexion, null, idTenant, idEmpresa, bloquear: false, ct);
        var marca = await GuardaDeConciliacion.LeerMarcaAsync(db, idEmpresa, ct);
        var planeado = await PlanearAsync(idEmpresa, rango, medios, marca, idsDePagoBloqueados: null, ct);

        var cuentas = new List<CuentaDeReconstruccion>();
        foreach (var (idCuenta, filas, neto) in planeado.EfectoPorCuenta())
        {
            var (nombre, saldoInicial) = planeado.Cuentas[idCuenta];
            var cola = await EscriturasDeCuentaDeTesoreria.LeerColaAsync(db, idTenant, idCuenta, saldoInicial, ct);
            cuentas.Add(new CuentaDeReconstruccion(idCuenta, nombre, filas, neto, cola.Saldo, cola.Saldo + neto));
        }

        return Armar(idEmpresa, rango, planeado, cuentas, marca);
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

            await GuardaDeConciliacion.TomarCompartidoAsync(db, idTenant, idEmpresa, ct);
            var marca = await GuardaDeConciliacion.LeerMarcaAsync(db, idEmpresa, ct);
            var idsDePagoBloqueados = await BloquearPagosSinDebitosAsync(conexion, transaccionCruda, idTenant, idEmpresa, rango, ct);
            await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, idEmpresa, ct);
            var medios = await LeerMediosConCuentaAsync(conexion, transaccionCruda, idTenant, idEmpresa, bloquear: true, ct);
            var bloqueadas = await EscriturasDeCuentaDeTesoreria.LeerYBloquearCuentasAsync(
                conexion, transaccionCruda, idTenant, idEmpresa, medios.Select(m => m.IdCuenta).Distinct().Order().ToList(), ct);

            // Un medio cuya cuenta se dio de baja entre la lectura y el lock no acredita, igual que en el cierre.
            var planeado = await PlanearAsync(
                idEmpresa, rango, medios.Where(m => bloqueadas.ContainsKey(m.IdCuenta)).ToList(), marca, idsDePagoBloqueados, ct);

            var cuentas = new List<CuentaDeReconstruccion>();
            foreach (var (idCuenta, filas, neto) in planeado.EfectoPorCuenta())
            {
                var saldoInicial = bloqueadas[idCuenta].SaldoInicial;
                var cola = await EscriturasDeCuentaDeTesoreria.LeerColaAsync(db, idTenant, idCuenta, saldoInicial, ct);
                var saldoDespues = await AgregarYReconstruirAsync(
                    idCuenta, saldoInicial, ArmarFilas(planeado, idCuenta, idTenant, idEmpleado), ct);
                cuentas.Add(new CuentaDeReconstruccion(
                    idCuenta, planeado.Cuentas[idCuenta].Nombre, filas, neto, cola.Saldo, saldoDespues));
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

            return Armar(idEmpresa, rango, planeado, cuentas, marca);
        });
    }

    private static PlanDeReconstruccion Armar(
        int idEmpresa, RangoDeReporte rango, Planeado planeado, IReadOnlyList<CuentaDeReconstruccion> cuentas,
        DateTimeOffset? marca) =>
        new(idEmpresa, rango.Desde, rango.Hasta, planeado.Acreditaciones.Select(a => a.IdTurnoCaja).Distinct().Count(),
            planeado.Debitos.Select(d => d.Debito.IdPago).Distinct().Count(), planeado.Filas.Count + planeado.Debitos.Count,
            planeado.Acreditaciones, planeado.Debitos.Select(d => d.Debito).ToList(), cuentas, marca, planeado.Congeladas,
            planeado.DebitosCongelados);

    /// <summary>Filas nuevas de una cuenta: las de cierre de los turnos y las de débito de los pagos.</summary>
    private static List<MovimientoCuenta> ArmarFilas(Planeado planeado, int idCuenta, int idTenant, int idEmpleado) =>
        planeado.Filas
            .Where(f => f.Acreditacion.IdCuentaTesoreria == idCuenta)
            .Select(f => EscriturasDeCuentaDeTesoreria.ArmarFila(
                idTenant, idCuenta, f.Acreditacion.FechaCierre, f.Componente, f.Acreditacion.IdTurnoCaja,
                f.Acreditacion.IdMedioPago, f.NombreMedio, inicio: 0m, idEmpleado))
            .Concat(planeado.Debitos
                .Where(d => d.Fila.IdCuenta == idCuenta)
                .Select(d => DebitosDePagoEnCuenta.ArmarFila(
                    idTenant, d.Fila, d.Debito.IdPago, d.Debito.Fecha, inicio: 0m, idEmpleado)))
            .ToList();

    /// <summary>Agrega las filas nuevas de una cuenta y recalcula <c>inicio</c>/<c>final</c> desde la primera hasta el final de la
    /// cadena. Las filas existentes se leen UNA vez, ya bajo el lock de la cuenta, y se modifican en el mismo tracker. Devuelve el
    /// saldo final de la cuenta.</summary>
    private async Task<decimal> AgregarYReconstruirAsync(
        int idCuenta, decimal saldoInicial, List<MovimientoCuenta> nuevas, CancellationToken ct)
    {
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
        int idEmpresa, RangoDeReporte rango, IReadOnlyList<MedioConCuenta> medios, DateTimeOffset? marca,
        IReadOnlySet<int>? idsDePagoBloqueados, CancellationToken ct)
    {
        if (medios.Count == 0)
        {
            return new Planeado([], [], [], new Dictionary<int, (string, decimal)>(), [], []);
        }

        var idsDeCuenta = medios.Select(m => m.IdCuenta).Distinct().ToArray();
        var cuentas = await db.CuentasTesoreria
            .Where(c => idsDeCuenta.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre, c.SaldoInicial })
            .ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.SaldoInicial), ct);

        var (acreditaciones, filas, congeladas) = await PlanearAcreditacionesAsync(idEmpresa, rango, medios, cuentas, marca, ct);
        var (debitos, debitosCongelados) = await PlanearDebitosAsync(
            idEmpresa, rango, medios, cuentas, marca, idsDePagoBloqueados, ct);

        return new Planeado(acreditaciones, filas, debitos, cuentas, congeladas, debitosCongelados);
    }

    private async Task<(List<AcreditacionDeReconstruccion> Acreditaciones, List<FilaPlaneada> Filas,
        List<AcreditacionDeReconstruccion> Congeladas)> PlanearAcreditacionesAsync(
        int idEmpresa, RangoDeReporte rango, IReadOnlyList<MedioConCuenta> medios,
        IReadOnlyDictionary<int, (string Nombre, decimal SaldoInicial)> cuentas, DateTimeOffset? marca, CancellationToken ct)
    {
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
            return ([], [], []);
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

        var acreditaciones = new List<AcreditacionDeReconstruccion>();
        var congeladas = new List<AcreditacionDeReconstruccion>();
        var filas = new List<FilaPlaneada>();
        foreach (var turno in turnos)
        {
            var congelado = ReglasDeConciliacion.EstaCongelado(turno.FechaCierre, marca);
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
                if (congelado)
                {
                    congeladas.Add(acreditacion);
                    continue;
                }

                acreditaciones.Add(acreditacion);
                filas.AddRange(componentes.Select(c => new FilaPlaneada(acreditacion, c, medio.Nombre)));
            }
        }

        return (acreditaciones, filas, congeladas);
    }

    /// <summary>Débitos de los pagos vivos de la empresa con fecha en el rango que todavía no tienen filas <c>pago</c>. Con
    /// <paramref name="idsDePagoBloqueados"/> (la aplicación) solo entran los pagos bloqueados; la vista previa los lee todos.</summary>
    private async Task<(List<DebitoPlaneado> Debitos, List<DebitoDeReconstruccion> Congelados)> PlanearDebitosAsync(
        int idEmpresa, RangoDeReporte rango, IReadOnlyList<MedioConCuenta> medios,
        IReadOnlyDictionary<int, (string Nombre, decimal SaldoInicial)> cuentas, DateTimeOffset? marca,
        IReadOnlySet<int>? idsDePagoBloqueados, CancellationToken ct)
    {
        var pagos = (await db.Pagos
                .Where(p => p.IdEmpresa == idEmpresa
                            && p.Fecha >= rango.DesdeUtc
                            && p.Fecha < rango.HastaUtcExclusivo
                            && !db.MovimientosCuenta.Any(m => m.IdPago == p.Id))
                .OrderBy(p => p.Fecha).ThenBy(p => p.Id)
                .Select(p => new { p.Id, p.Fecha, p.Categoria, p.Concepto })
                .ToListAsync(ct))
            .Where(p => idsDePagoBloqueados is null || idsDePagoBloqueados.Contains(p.Id))
            .ToList();
        if (pagos.Count == 0)
        {
            return ([], []);
        }

        var idsDePago = pagos.Select(p => p.Id).ToArray();
        var medioPorId = medios.ToDictionary(m => m.IdMedioPago);
        var idsDeMedio = medioPorId.Keys.ToArray();
        var importes = (await db.ItemsPago
                .Where(i => idsDePago.Contains(i.IdPago) && i.DeletedAt == null && idsDeMedio.Contains(i.IdMedioPago))
                .GroupBy(i => new { i.IdPago, i.IdMedioPago })
                .Select(g => new { g.Key.IdPago, g.Key.IdMedioPago, Importe = g.Sum(i => i.Importe) })
                .ToListAsync(ct))
            .ToLookup(i => i.IdPago);

        var debitos = new List<DebitoPlaneado>();
        var congelados = new List<DebitoDeReconstruccion>();
        foreach (var pago in pagos)
        {
            var objetivo = importes[pago.Id]
                .Where(i => cuentas.ContainsKey(medioPorId[i.IdMedioPago].IdCuenta))
                .Select(i => new ImporteDeCuentaYMedio(medioPorId[i.IdMedioPago].IdCuenta, i.IdMedioPago, i.Importe));
            var congelado = ReglasDeConciliacion.EstaCongelado(pago.Fecha, marca);

            foreach (var fila in CalculadorDeDebitosDePago.Calcular(objetivo, []))
            {
                var debito = new DebitoDeReconstruccion(
                    pago.Id, pago.Fecha, pago.Categoria, pago.Concepto, fila.IdMedioPago, medioPorId[fila.IdMedioPago].Nombre,
                    fila.IdCuenta, cuentas[fila.IdCuenta].Nombre, fila.Egreso - fila.Ingreso);
                if (congelado)
                {
                    congelados.Add(debito);
                    continue;
                }

                debitos.Add(new DebitoPlaneado(debito, fila));
            }
        }

        return (debitos, congelados);
    }

    /// <summary>Lock-only <c>FOR KEY SHARE</c>, por id ascendente, de los pagos vivos de la empresa con fecha en el rango que todavía
    /// no tienen filas <c>pago</c>: choca con el <c>FOR UPDATE</c> de la edición y la baja del pago. Un pago con filas las conserva
    /// (la FK es <c>RESTRICT</c>), así que filtrarlos acá no deja afuera a ninguno que el planeo, después del advisory, fuera a
    /// debitar.</summary>
    private static async Task<IReadOnlySet<int>> BloquearPagosSinDebitosAsync(
        DbConnection conexion, DbTransaction transaccion, int idTenant, int idEmpresa, RangoDeReporte rango, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT p.id_pago FROM pagos p " +
            "WHERE p.id_tenant = $1 AND p.id_empresa = $2 AND p.deleted_at IS NULL AND p.fecha >= $3 AND p.fecha < $4 " +
            "AND NOT EXISTS (SELECT 1 FROM movimientos_cuenta m WHERE m.id_tenant = p.id_tenant AND m.id_pago = p.id_pago) " +
            "ORDER BY p.id_pago FOR KEY SHARE OF p";
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idEmpresa);
        ParametrosDeComando.Agregar(comando, rango.DesdeUtc);
        ParametrosDeComando.Agregar(comando, rango.HastaUtcExclusivo);

        var ids = new HashSet<int>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            ids.Add(lector.GetInt32(0));
        }

        return ids;
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
