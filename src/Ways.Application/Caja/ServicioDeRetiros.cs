using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Listados;
using Ways.Domain.Auditoria;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using static Ways.Application.Busqueda.BusquedaSinAcentos;

namespace Ways.Application.Caja;

/// <summary>
/// Retiros de caja en el back office: el listado de todos los turnos y la corrección administrativa de un retiro manual (editar
/// importe y motivo, o darlo de baja lógica), con el turno abierto o cerrado. Solo los retiros: los refuerzos y las aperturas de
/// cajón siguen siendo append-only. El retiro que escribe el cierre por retiro no se corrige
/// (<see cref="ReglaDeEdicionDeRetiros.ExigirEditable"/>).
///
/// Con el turno cerrado, la corrección recalcula el arqueo (<see cref="RecalculoDeArqueoDeTurnoCerrado"/>, con la marca del
/// recálculo) y agrega a la cadena de tesorería un <see cref="TipoMovimientoTesoreria.Ajuste"/> por la diferencia: el cierre había
/// acreditado la suma de los retiros del turno. El libro de tesorería lee los retiros vivos directamente, así que refleja el
/// cambio sin más.
///
/// Sin reintento (<see cref="FabricaDeEstrategiaSinReintento"/>): la corrección agrega filas de tesorería, arqueo y auditoría sin
/// clave de idempotencia, y un reintento de una baja sobre un commit ambiguo respondería 404 a una baja que sí tuvo éxito.
/// </summary>
public class ServicioDeRetiros(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, LectorDeMovimientosDelTurno lectorDeMovimientos,
    ServicioDeAuditoria auditoria)
{
    // ---- listado --------------------------------------------------------------------------------------------------------

    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeRetiros> ClavesDeOrden =
        new Dictionary<string, ColumnaDeOrdenDeRetiros>(StringComparer.Ordinal)
        {
            ["fecha"] = ColumnaDeOrdenDeRetiros.Fecha,
            ["puntoVenta"] = ColumnaDeOrdenDeRetiros.PuntoVenta,
            ["turno"] = ColumnaDeOrdenDeRetiros.Turno,
            ["empleado"] = ColumnaDeOrdenDeRetiros.Empleado,
            ["importe"] = ColumnaDeOrdenDeRetiros.Importe,
            ["motivo"] = ColumnaDeOrdenDeRetiros.Motivo,
            ["estadoTurno"] = ColumnaDeOrdenDeRetiros.EstadoTurno,
            ["conciliado"] = ColumnaDeOrdenDeRetiros.Conciliado,
            ["esRetiroDeCierre"] = ColumnaDeOrdenDeRetiros.EsRetiroDeCierre,
        };

    /// <summary>Sin orden pedido: fecha descendente y, a igual fecha, id descendente. Con orden: la columna pedida y siempre el id
    /// ascendente como desempate.</summary>
    public async Task<PaginaDeRetiros> ListarAsync(
        FiltrosDeRetiros filtros, int pagina = 1, int tamanio = 25, string? orden = null, string? direccion = null,
        CancellationToken ct = default)
    {
        var ordenPedido = OrdenDeListado.Interpretar(orden, direccion, ClavesDeOrden);
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = Filtrar(Proyectar(), filtros);
        var total = await query.CountAsync(ct);

        var items = await Ordenar(query, ordenPedido)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(f => new FilaDeRetiro(
                f.Id, f.Fecha, f.IdTurnoCaja, f.IdPuntoVenta, f.PuntoVenta, f.IdEmpleado, f.Empleado, f.Importe, f.Motivo,
                f.TurnoCerrado, f.Conciliado, f.EsRetiroDeCierre))
            .ToListAsync(ct);

        return new PaginaDeRetiros(items, total, pagina, tamanio);
    }

    private sealed class FilaProyectada
    {
        public int Id { get; init; }
        public DateTimeOffset Fecha { get; init; }
        public int IdTurnoCaja { get; init; }
        public int? IdPuntoVenta { get; init; }
        public string? PuntoVenta { get; init; }
        public int? IdEmpleado { get; init; }
        public string? Empleado { get; init; }
        public decimal Importe { get; init; }
        public required string Motivo { get; init; }
        public bool TurnoCerrado { get; init; }
        public bool Conciliado { get; init; }
        public bool EsRetiroDeCierre { get; init; }
    }

    /// <summary>Los retiros vivos. La empresa del punto de venta se lee aunque el punto de venta esté dado de baja: es estructural
    /// y decide la conciliación. El punto de venta y el empleado que se muestran, en cambio, salen solo de filas vivas
    /// (dangling-fk-read-models): uno dado de baja se proyecta como <c>null</c>, igual que en sus filtros.
    ///
    /// <c>IgnoreQueryFilters(["BajaLogica"])</c> apaga ese filtro en TODA la consulta, no solo en la fuente donde se escribe; por
    /// eso las bajas lógicas del retiro, del punto de venta y del empleado se excluyen a mano.</summary>
    private IQueryable<FilaProyectada> Proyectar() =>
        from m in db.MovimientosCaja.IgnoreQueryFilters(["BajaLogica"])
        where m.Tipo == TipoMovimientoCaja.Retiro && m.DeletedAt == null
        join t in db.TurnosCaja on m.IdTurnoCaja equals t.Id
        join pv in db.PuntosVenta on t.IdPuntoVenta equals pv.Id
        select new FilaProyectada
        {
            Id = m.Id,
            Fecha = m.CreadoEl,
            IdTurnoCaja = m.IdTurnoCaja,
            IdPuntoVenta = pv.DeletedAt == null ? pv.Id : null,
            PuntoVenta = pv.DeletedAt == null ? pv.Nombre : null,
            IdEmpleado = db.Usuarios.Where(u => u.Id == m.IdEmpleado && u.DeletedAt == null).Select(u => (int?)u.Id).FirstOrDefault(),
            Empleado = db.Usuarios.Where(u => u.Id == m.IdEmpleado && u.DeletedAt == null).Select(u => u.NombreUsuario).FirstOrDefault(),
            Importe = m.Importe,
            Motivo = m.Motivo,
            TurnoCerrado = t.Estado == EstadoTurno.Cerrado,
            Conciliado = t.Estado == EstadoTurno.Cerrado
                && db.ConciliacionesTesoreria.Any(c => c.IdEmpresa == pv.IdEmpresa && c.Hasta >= t.FechaCierre),
            EsRetiroDeCierre = m.EsRetiroDeCierre
        };

    private static IQueryable<FilaProyectada> Filtrar(IQueryable<FilaProyectada> query, FiltrosDeRetiros filtros)
    {
        if (filtros.Desde is { } desde)
        {
            query = query.Where(f => f.Fecha >= desde);
        }

        if (filtros.Hasta is { } hasta)
        {
            query = query.Where(f => f.Fecha <= hasta);
        }

        if (filtros.IdPuntoVenta is { } idPuntoVenta)
        {
            query = query.Where(f => f.IdPuntoVenta == idPuntoVenta);
        }

        if (filtros.IdTurnoCaja is { } idTurnoCaja)
        {
            query = query.Where(f => f.IdTurnoCaja == idTurnoCaja);
        }

        if (filtros.IdEmpleado is { } idEmpleado)
        {
            query = query.Where(f => f.IdEmpleado == idEmpleado);
        }

        if (filtros.ImporteDesde is { } importeDesde)
        {
            query = query.Where(f => f.Importe >= importeDesde);
        }

        if (filtros.ImporteHasta is { } importeHasta)
        {
            query = query.Where(f => f.Importe <= importeHasta);
        }

        if (FiltrosDeListado.PatronONulo(filtros.Motivo) is { } patron)
        {
            query = query.Where(f => Coincide(f.Motivo, patron));
        }

        if (filtros.TurnoCerrado is { } turnoCerrado)
        {
            query = query.Where(f => f.TurnoCerrado == turnoCerrado);
        }

        if (filtros.Conciliado is { } conciliado)
        {
            query = query.Where(f => f.Conciliado == conciliado);
        }

        if (filtros.EsRetiroDeCierre is { } esRetiroDeCierre)
        {
            query = query.Where(f => f.EsRetiroDeCierre == esRetiroDeCierre);
        }

        return query;
    }

    private static IOrderedQueryable<FilaProyectada> Ordenar(
        IQueryable<FilaProyectada> query, OrdenDeListado<ColumnaDeOrdenDeRetiros>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderByDescending(f => f.Fecha).ThenByDescending(f => f.Id);
        }

        var desc = pedido.Descendente;
        var ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeRetiros.Fecha => OrdenarPor.Clave(query, f => f.Fecha, desc),
            ColumnaDeOrdenDeRetiros.PuntoVenta => OrdenarPor.ClaveConVacios(query, f => f.PuntoVenta, desc),
            ColumnaDeOrdenDeRetiros.Turno => OrdenarPor.Clave(query, f => f.IdTurnoCaja, desc),
            ColumnaDeOrdenDeRetiros.Empleado => OrdenarPor.ClaveConVacios(query, f => f.Empleado, desc),
            ColumnaDeOrdenDeRetiros.Importe => OrdenarPor.Clave(query, f => f.Importe, desc),
            ColumnaDeOrdenDeRetiros.Motivo => OrdenarPor.Clave(query, f => f.Motivo, desc),
            ColumnaDeOrdenDeRetiros.EstadoTurno => OrdenarPor.Clave(query, f => f.TurnoCerrado, desc),
            ColumnaDeOrdenDeRetiros.Conciliado => OrdenarPor.Clave(query, f => f.Conciliado, desc),
            ColumnaDeOrdenDeRetiros.EsRetiroDeCierre => OrdenarPor.Clave(query, f => f.EsRetiroDeCierre, desc),
            _ => throw new ArgumentOutOfRangeException(nameof(orden), pedido.Columna, null),
        };

        return ordenado.ThenBy(f => f.Id);
    }

    // ---- corrección -----------------------------------------------------------------------------------------------------

    /// <summary>Cambia el importe y el motivo de un retiro. Orden de respuestas: 404 (no existe, es de otro tenant, está dado de
    /// baja o no es un retiro), 409 si es el retiro del cierre, 400 si el cuerpo no cumple las reglas del alta; después, bajo los
    /// locks, 409 <c>periodo_conciliado</c>.</summary>
    public async Task<FilaDeRetiro> EditarAsync(int idMovimiento, SolicitudDeEdicionDeRetiro solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var inmutables = await LeerDatosInmutablesAsync(idMovimiento, ct);
        ReglaDeEdicionDeRetiros.ExigirEditable(inmutables.EsRetiroDeCierre);

        ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.Retiro, solicitud.Importe);
        ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.Retiro, solicitud.Motivo);
        var nuevo = new Correccion(solicitud.Importe, solicitud.Motivo!.Trim());

        await EjecutarAsync(idTenant, idMovimiento, inmutables, nuevo, ct);

        db.ChangeTracker.Clear();
        return await Proyectar()
            .Where(f => f.Id == idMovimiento)
            .Select(f => new FilaDeRetiro(
                f.Id, f.Fecha, f.IdTurnoCaja, f.IdPuntoVenta, f.PuntoVenta, f.IdEmpleado, f.Empleado, f.Importe, f.Motivo,
                f.TurnoCerrado, f.Conciliado, f.EsRetiroDeCierre))
            .FirstAsync(ct);
    }

    /// <summary>Baja lógica de un retiro: deja de contar para el arqueo, el resumen y el libro. Mismas respuestas que
    /// <see cref="EditarAsync"/>, sin el 400 del cuerpo.</summary>
    public async Task EliminarAsync(int idMovimiento, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var inmutables = await LeerDatosInmutablesAsync(idMovimiento, ct);
        ReglaDeEdicionDeRetiros.ExigirEditable(inmutables.EsRetiroDeCierre);

        await EjecutarAsync(idTenant, idMovimiento, inmutables, nuevo: null, ct);
    }

    private sealed record Correccion(decimal Importe, string Motivo);

    /// <summary>Columnas que ninguna escritura cambia, leídas antes de los locks (single-read-under-lock regla 6): la empresa es la
    /// clave del primer lock y el turno la del segundo. Es también la verificación de existencia que responde 404 antes de mirar el
    /// cuerpo. El punto de venta puede estar dado de baja (su empresa no cambia); el filtro de baja lógica se apaga para toda la
    /// consulta, así que la del retiro se excluye a mano.</summary>
    private sealed record DatosInmutables(int IdTurnoCaja, int IdPuntoVenta, int IdEmpresa, bool EsRetiroDeCierre);

    private async Task<DatosInmutables> LeerDatosInmutablesAsync(int idMovimiento, CancellationToken ct) =>
        await (
            from m in db.MovimientosCaja.IgnoreQueryFilters(["BajaLogica"])
            where m.Id == idMovimiento && m.Tipo == TipoMovimientoCaja.Retiro && m.DeletedAt == null
            join t in db.TurnosCaja on m.IdTurnoCaja equals t.Id
            join pv in db.PuntosVenta on t.IdPuntoVenta equals pv.Id
            select new DatosInmutables(m.IdTurnoCaja, t.IdPuntoVenta, pv.IdEmpresa, m.EsRetiroDeCierre))
            .FirstOrDefaultAsync(ct)
        ?? throw NoExiste(idMovimiento);

    private async Task EjecutarAsync(
        int idTenant, int idMovimiento, DatosInmutables inmutables, Correccion? nuevo, CancellationToken ct)
    {
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EscribirAsync(idTenant, idMovimiento, inmutables, nuevo, idEmpleado, momento, ct));
    }

    /// <summary>
    /// Orden de locks: 1) compartido de conciliación de la empresa (<see cref="GuardaDeConciliacion"/>, siempre el primero) →
    /// 2) turno <c>FOR UPDATE</c> →
    /// 3) el retiro <c>FOR UPDATE</c>, y bajo ese lock la ÚNICA lectura EF de la fila →
    /// 4) con el turno cerrado: filas del arqueo (cubiertas por el lock 2) y advisory de tesorería de la empresa, último.
    ///
    /// Análisis de ciclo: el cierre toma conciliación → turno (exclusivo) → advisory, el mismo orden relativo; el alta de un
    /// movimiento toma el turno <c>FOR SHARE</c> y no lockea movimientos existentes; la edición administrativa de un pago toma
    /// conciliación → turno → pago → advisory, y nunca lockea movimientos de caja. El lock 2 serializa además esta corrección con el
    /// cierre: si gana el cierre, se lee <c>cerrado</c> y se recalcula; si gana la corrección, el cierre deriva el arqueo con el
    /// retiro ya corregido.
    /// </summary>
    private async Task EscribirAsync(
        int idTenant, int idMovimiento, DatosInmutables inmutables, Correccion? nuevo, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var transaccionCruda = transaccion.GetDbTransaction();

        await GuardaDeConciliacion.TomarCompartidoAsync(db, idTenant, inmutables.IdEmpresa, ct);

        var turno = await BloquearTurnoAsync(conexion, transaccionCruda, inmutables.IdTurnoCaja, idTenant, ct);

        await BloquearRetiroAsync(conexion, transaccionCruda, idMovimiento, idTenant, ct);
        var retiro = await db.MovimientosCaja.FirstOrDefaultAsync(m => m.Id == idMovimiento, ct)
            ?? throw NoExiste(idMovimiento);

        await GuardaDeConciliacion.ExigirNoConciliadoAsync(
            db, idTenant, inmutables.IdEmpresa, ReglaDeEdicionDeRetiros.FechaQueCuentaParaElLibro(turno.FechaCierre, momento), ct);

        var importeAnterior = retiro.Importe;
        var motivoAnterior = retiro.Motivo;
        if (nuevo is null)
        {
            retiro.DeletedAt = momento;
        }
        else
        {
            retiro.Importe = nuevo.Importe;
            retiro.Motivo = nuevo.Motivo;
        }

        await db.SaveChangesAsync(ct);

        int? idTurnoRecalculado = null;
        if (turno.FechaCierre is not null)
        {
            if (await RecalculoDeArqueoDeTurnoCerrado.RecalcularAsync(
                    db, lectorDeMovimientos, idTenant, inmutables.IdTurnoCaja, turno.IdMedioPagoEfectivo, idEmpleado, momento, ct))
            {
                idTurnoRecalculado = inmutables.IdTurnoCaja;
            }

            if (ReglaDeEdicionDeRetiros.AjusteDeTesoreria(importeAnterior, nuevo?.Importe) is { } ajuste)
            {
                await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, inmutables.IdEmpresa, ct);
                await EscriturasDeTesoreria.ApendearAsync(
                    db, idTenant, inmutables.IdEmpresa, inmutables.IdPuntoVenta, momento, TipoMovimientoTesoreria.Ajuste,
                    inmutables.IdTurnoCaja, idPago: null,
                    nuevo is null ? $"Baja del retiro #{idMovimiento}" : $"Edición del retiro #{idMovimiento}",
                    ajuste.Ingreso, ajuste.Egreso, idEmpleado, ct);
            }
        }

        var (valorAnterior, valorNuevo) = nuevo is null
            ? PayloadDeAuditoria.BajaDeRetiro(importeAnterior, motivoAnterior, momento, idTurnoRecalculado)
            : PayloadDeAuditoria.EdicionDeRetiro(importeAnterior, motivoAnterior, nuevo.Importe, nuevo.Motivo, idTurnoRecalculado);
        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, inmutables.IdPuntoVenta, nuevo is null ? AccionAuditada.RetiroBaja : AccionAuditada.RetiroEdicion,
            idMovimiento, valorAnterior, valorNuevo));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);
    }

    private sealed record TurnoBloqueado(int? IdMedioPagoEfectivo, DateTimeOffset? FechaCierre);

    /// <summary>Un turno abierto no tiene <c>fecha_cierre</c> y uno cerrado siempre la tiene (<c>ck_turnos_caja_cierre_consistente</c>), así que la
    /// fecha decide el estado.</summary>
    private static async Task<TurnoBloqueado> BloquearTurnoAsync(
        DbConnection conexion, DbTransaction transaccion, int idTurnoCaja, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_medio_pago_efectivo, fecha_cierre FROM turnos_caja WHERE id_turno_caja = $1 AND id_tenant = $2 FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idTurnoCaja);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"El turno {idTurnoCaja} de un retiro existente no es visible — invariante de FK violado.");
        }

        return new TurnoBloqueado(
            lector.IsDBNull(0) ? null : lector.GetInt32(0), lector.IsDBNull(1) ? null : lector.GetFieldValue<DateTimeOffset>(1));
    }

    private static async Task BloquearRetiroAsync(
        DbConnection conexion, DbTransaction transaccion, int idMovimiento, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT 1 FROM movimientos_caja WHERE id_movimiento = $1 AND id_tenant = $2 FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idMovimiento);
        ParametrosDeComando.Agregar(comando, idTenant);
        await comando.ExecuteScalarAsync(ct);
    }

    private static ErrorDominio NoExiste(int idMovimiento) => ErrorDominio.NoEncontrado($"No existe el retiro {idMovimiento}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeRetiros requiere un actor de tenant; la política de la ruta no admite plataforma.");
}
