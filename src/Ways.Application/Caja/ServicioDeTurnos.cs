using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Application.Caja;

/// <summary>
/// Turno de caja: apertura, movimientos físicos fuera de la venta (retiro/refuerzo/apertura de
/// cajón), cierre (design: The Cierre Transaction) y lectura (design: API Surface). <see
/// cref="ResolverTurnoAbiertoAsync"/> es el resolver compartido que Slice 3 (gastos) y Slice 5
/// (checkout) reutilizan (tasks.md, Orchestrator Decision 3), evitando tres copias del mismo
/// <c>409 turno_no_abierto</c>; <see cref="ExigirTurnoAbiertoBajoLockAsync"/> (Slice 4, task
/// 4.17) es el mismo tipo de pieza compartida para el guard <c>FOR SHARE</c> — reusado por
/// <c>ServicioDeGastos.RegistrarAsync</c>.
/// </summary>
public class ServicioDeTurnos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, LectorDeMovimientosDelTurno lector)
{
    /// <summary>Apertura (design decisión 7): INSERT llano detrás de <c>ux_turnos_caja_abierto
    /// (id_punto_venta) WHERE estado = 'abierto'</c> — sin lectura previa, sin advisory lock. La
    /// carrera se resuelve en el <c>23505</c> del <c>SaveChangesAsync</c>
    /// (<c>ManejadorDeErrores.ClasificarUnicidad</c> ya traduce ese índice a <c>409
    /// turno_ya_abierto</c>, groundwork de Slice 1). <see
    /// cref="FabricaDeEstrategiaSinReintento"/>: operación manual y rara, sin clave de
    /// idempotencia — mismo criterio que <c>ServicioDeStock.AjustarAsync</c>.</summary>
    public async Task<TurnoResumen> AbrirAsync(SolicitudDeApertura solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var turno = await estrategia.ExecuteAsync(async () =>
            await InsertarTurnoAsync(idTenant, solicitud, idEmpleado, momento, ct));

        return Proyectar(turno);
    }

    private async Task<TurnoCaja> InsertarTurnoAsync(
        int idTenant, SolicitudDeApertura solicitud, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var turno = new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdEmpleadoApertura = idEmpleado,
            FechaApertura = momento,
            FondoInicial = solicitud.FondoInicial,
            Estado = EstadoTurno.Abierto,
            Observaciones = solicitud.Observaciones,
            CreatedAt = momento,
            UpdatedAt = momento
        };

        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync(ct);

        return turno;
    }

    /// <summary>Resolver compartido (spec: Turno Is Always Server-Resolved, Never
    /// Client-Supplied): el turno abierto de un punto de venta se resuelve SIEMPRE desde
    /// <paramref name="idPuntoVenta"/>, nunca desde un id de turno que mande el cliente.
    /// Reutilizado por <c>ServicioDeGastos.RegistrarAsync</c> (Slice 3) y
    /// <c>ServicioDeVentas.EmitirAsync</c> (Slice 5, design decisión 11).</summary>
    public async Task<TurnoCaja> ResolverTurnoAbiertoAsync(int idPuntoVenta, CancellationToken ct = default) =>
        await db.TurnosCaja
            .Where(t => t.IdPuntoVenta == idPuntoVenta && t.Estado == EstadoTurno.Abierto)
            .FirstOrDefaultAsync(ct)
        ?? throw new ErrorDominio("turno_no_abierto", "No hay un turno abierto en este punto de venta.", 409);

    /// <summary>Movimiento físico de caja fuera de la venta — retiro, refuerzo o apertura de
    /// cajón (design decisión 8). A diferencia de <see cref="ResolverTurnoAbiertoAsync"/>, acá el
    /// turno lo identifica la ruta (<c>POST /api/caja/turnos/{id}/movimientos</c>, design: API
    /// Surface — mismo patrón que <c>GET …/{id}</c> y <c>POST …/{id}/cierre</c>, que también
    /// direccionan un turno puntual por id): el servidor igual decide con autoridad, nunca
    /// confiando en que el turno de esa url siga abierto — <see
    /// cref="ResolverTurnoPorIdAbiertoAsync"/> lo revalida contra el estado persistido y devuelve
    /// el mismo <c>409 turno_no_abierto</c> si no lo está.</summary>
    public async Task<MovimientoRegistrado> RegistrarMovimientoAsync(
        int idTurnoCaja, SolicitudDeMovimiento solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ReglaDeMovimientosDeCaja.ExigirImporteValido(solicitud.Tipo, solicitud.Importe);
        ReglaDeMovimientosDeCaja.ExigirMotivoValido(solicitud.Tipo, solicitud.Motivo);

        // Pre-chequeo barato, FUERA de la transacción de escritura (404 ADR-8 / 409 rápido) —
        // ver el doc-comment de EjecutarRegistroDeMovimientoAsync sobre por qué esto no alcanza
        // por sí solo como protección de la carrera contra un cierre concurrente.
        await ResolverTurnoPorIdAbiertoAsync(idTurnoCaja, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var movimiento = await estrategia.ExecuteAsync(async () =>
            await EjecutarRegistroDeMovimientoAsync(idTenant, idTurnoCaja, solicitud, idEmpleado, momento, ct));

        return Proyectar(movimiento);
    }

    /// <summary>task 4.17 (judgment-day, hallazgo de Slice 2, juez B): <see
    /// cref="ExigirTurnoAbiertoBajoLockAsync"/> como PRIMER statement de la transacción de
    /// escritura — el pre-chequeo de <see cref="ResolverTurnoPorIdAbiertoAsync"/> (arriba, sin
    /// lock) es solo UX rápida; sin este re-chequeo bajo <c>FOR SHARE</c>, una vez que existe
    /// <see cref="CerrarAsync"/> un retiro/refuerzo concurrente podría comitear dentro de un
    /// turno cuyo arqueo YA se derivó — exactamente la clase de defecto que design decisión 1
    /// mata. El lock EXCLUSIVO del cierre (su propio primer statement) y este <c>FOR SHARE</c>
    /// se excluyen mutuamente: quien pierde la carrera re-lee el estado ya comiteado.</summary>
    private async Task<MovimientoCaja> EjecutarRegistroDeMovimientoAsync(
        int idTenant, int idTurnoCaja, SolicitudDeMovimiento solicitud, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await ExigirTurnoAbiertoBajoLockAsync(idTurnoCaja, ct);

        var movimiento = new MovimientoCaja
        {
            IdTenant = idTenant,
            IdTurnoCaja = idTurnoCaja,
            Tipo = solicitud.Tipo,
            Importe = solicitud.Importe,
            // ReglaDeMovimientosDeCaja.ExigirMotivoValido ya garantizó no-nulo/no-vacío arriba.
            Motivo = solicitud.Motivo!.Trim(),
            IdEmpleado = idEmpleado,
            CreadoEl = momento
        };

        db.MovimientosCaja.Add(movimiento);
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        return movimiento;
    }

    /// <summary>Guard compartido (task 4.17) — reusado tal cual por
    /// <c>ServicioDeGastos.RegistrarAsync</c> (mismo criterio de reuso que <see
    /// cref="ResolverTurnoAbiertoAsync"/>, tasks.md Orchestrator Decision 3). DEBE llamarse como
    /// el PRIMER statement de una transacción YA abierta por el llamador — no abre ninguna
    /// transacción propia. <c>estado::text</c> en vez de leer el enum nativo directo: evita
    /// depender de que Npgsql resuelva el tipo mapeado sobre un <c>ExecuteScalarAsync</c> crudo,
    /// misma cautela que el resto de los statements ADO.NET de este proyecto (comparación
    /// literal, nunca <c>Contains</c>).</summary>
    public async Task ExigirTurnoAbiertoBajoLockAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText = "SELECT estado::text FROM turnos_caja WHERE id_turno_caja = $1 FOR SHARE";
        ParametrosDeComando.Agregar(comando, idTurnoCaja);

        var estado = (string?)await comando.ExecuteScalarAsync(ct);
        if (estado != "abierto")
        {
            throw new ErrorDominio("turno_no_abierto", "El turno no está abierto.", 409);
        }
    }

    /// <summary>Fuente de verdad del gate seam de <c>Pos.tsx</c> (design: API Surface, <c>GET
    /// /api/caja/turnos/abierto</c>): a diferencia de <see cref="ResolverTurnoAbiertoAsync"/>,
    /// nunca lanza — <c>null</c> es una respuesta válida (200), no un error.</summary>
    public async Task<TurnoResumen?> ObtenerAbiertoAsync(int idPuntoVenta, CancellationToken ct = default)
    {
        var turno = await db.TurnosCaja
            .Where(t => t.IdPuntoVenta == idPuntoVenta && t.Estado == EstadoTurno.Abierto)
            .FirstOrDefaultAsync(ct);

        return turno is null ? null : Proyectar(turno);
    }

    /// <summary>Turno por id (design: API Surface, <c>GET /api/caja/turnos/{id}</c> — el payload
    /// del Z-report): incluye sus <c>arqueos_turno</c>, vacío mientras el turno sigue abierto.
    /// <see cref="TurnoConArqueos"/> repite los mismos campos planos que <see cref="TurnoResumen"/>
    /// más <c>Arqueos</c> — la deserialización de Slice 2 contra este mismo endpoint sigue
    /// funcionando tal cual (System.Text.Json ignora la propiedad nueva).</summary>
    public async Task<TurnoConArqueos> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var turno = await db.TurnosCaja.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el turno {id}.");

        var arqueos = await db.ArqueosTurno
            .Where(a => a.IdTurnoCaja == id)
            .OrderBy(a => a.IdMedioPago)
            .ToListAsync(ct);

        return ProyectarConArqueos(turno, arqueos);
    }

    /// <summary>Cierre (design: The Cierre Transaction — orden de statements pineado; decisión 1
    /// declarada: el UPDATE guardado va PRIMERO, no derive-then-close). Irreversible: no existe
    /// reapertura ni edición de arqueo (spec: Cierre Is One Atomic, Irreversible Transaction).
    /// <see cref="FabricaDeEstrategiaSinReintento"/>: manual, raro, sin clave de idempotencia —
    /// un commit ambiguo tiene que llegar al operador como una falla que re-chequea, nunca como
    /// un reintento automático que reporte <c>409 turno_ya_cerrado</c> sobre un cierre que en
    /// verdad tuvo éxito (design: Failure Semantics).</summary>
    public async Task<TurnoConArqueos> CerrarAsync(
        int idTurnoCaja, SolicitudDeCierre solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        // Pineado ACÁ, nunca releído dentro de la lambda reintentable (design: The Cierre
        // Transaction, "momento := reloj.Ahora, pinned, never re-read").
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarCierreAsync(idTurnoCaja, idTenant, idEmpleado, momento, solicitud, ct));
    }

    private async Task<TurnoConArqueos> EjecutarCierreAsync(
        int idTurnoCaja, int idTenant, int idEmpleado, DateTimeOffset momento, SolicitudDeCierre solicitud,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // 1. UPDATE ... WHERE estado = 'abierto' RETURNING id_punto_venta — lock EXCLUSIVO de
        // fila, PRIMER statement (design decisión 1), sostenido hasta el COMMIT. 0 filas: ¿existe
        // el turno? no -> 404; sí -> 409 turno_ya_cerrado (Orchestrator Decision 2, tasks.md —
        // distinto de turno_no_abierto: el turno EXISTE, solo que ya no está abierto). El mismo
        // UPDATE también acumula solicitud.Observaciones a continuación de las de la apertura,
        // separadas por un salto de línea, sin pisar el texto existente.
        var observacionesCierre = string.IsNullOrWhiteSpace(solicitud.Observaciones)
            ? null
            : solicitud.Observaciones.Trim();
        var idPuntoVenta = await MarcarCerradoAsync(idTenant, idTurnoCaja, idEmpleado, momento, observacionesCierre, ct);
        if (idPuntoVenta is null)
        {
            var existe = await db.TurnosCaja.AnyAsync(t => t.Id == idTurnoCaja, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe el turno {idTurnoCaja}.");
            }

            throw new ErrorDominio("turno_ya_cerrado", "El turno ya está cerrado.", 409);
        }

        // 2. Insumos de la derivación (7 consultas agrupadas, cantidad fija) — bajo el lock.
        var insumos = await lector.LeerAsync(idTurnoCaja, ct);

        // 3. Ancla — puro, 409 caja_sin_medio_efectivo_unico si no es único.
        var idAncla = ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad);

        // 4. Cálculo (PURO, la única fórmula) + validación de los conteos declarados.
        var lineas = CalculadorDeArqueo.Calcular(insumos, idAncla);
        ValidadorDeConteos.Validar(lineas, insumos.Actividad, solicitud.Conteos);

        // 5. INSERT arqueos_turno — una fila por medio arqueable; Diferencia la calcula la
        // columna GENERATED (design decisión 6), nunca se asigna acá.
        var declaradoPorMedio = solicitud.Conteos.ToDictionary(c => c.IdMedioPago, c => c.ImporteDeclarado);
        var arqueos = lineas
            .Select(l => new ArqueoTurno
            {
                IdTenant = idTenant,
                IdTurnoCaja = idTurnoCaja,
                IdMedioPago = l.IdMedioPago,
                ImporteEsperado = l.ImporteEsperado,
                ImporteDeclarado = declaradoPorMedio[l.IdMedioPago]
            })
            .ToList();
        db.ArqueosTurno.AddRange(arqueos);
        await db.SaveChangesAsync(ct);

        // 6. Tesorería encadenada — UN único movimiento (tipo retiro_caja), inicio = final de la
        // última fila del mismo punto de venta (0 si no hay), egreso = Σ gastos sobre TODOS los
        // medios (design decisión 9, paridad legacy).
        var totalGastos = insumos.Actividad.Sum(a => a.Gastos);
        var inicio = await db.MovimientosTesoreria
            .Where(m => m.IdPuntoVenta == idPuntoVenta.Value)
            .OrderByDescending(m => m.Id)
            .Select(m => m.Final)
            .FirstOrDefaultAsync(ct);
        var final = inicio + insumos.Retiros - totalGastos;

        db.MovimientosTesoreria.Add(new MovimientoTesoreria
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta.Value,
            Fecha = momento,
            Tipo = TipoMovimientoTesoreria.RetiroCaja,
            IdTurnoCaja = idTurnoCaja,
            Concepto = "Cierre de turno",
            Inicio = inicio,
            Ingreso = insumos.Retiros,
            Egreso = totalGastos,
            Final = final,
            IdEmpleado = idEmpleado
        });
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        var turno = await db.TurnosCaja.AsNoTracking().FirstAsync(t => t.Id == idTurnoCaja, ct);
        return ProyectarConArqueos(turno, arqueos);
    }

    /// <summary>Design: The Cierre Transaction, statement 1 — único punto de transición de
    /// <c>estado</c> (mismo criterio que <c>MarcarAnuladoAsync</c> de <c>ServicioDeVentas</c>):
    /// <c>RETURNING id_punto_venta</c>, no <c>fondo_inicial</c> — <see
    /// cref="LectorDeMovimientosDelTurno"/> lo vuelve a leer por su cuenta (lo necesita también
    /// para el resumen parcial sobre un turno TODAVÍA abierto, sin este RETURNING disponible).
    /// Cuando <paramref name="observacionesCierre"/> no es nulo, el mismo UPDATE lo agrega a
    /// continuación de las observaciones de la apertura (separadas por un salto de línea) sin
    /// pisarlas; si es nulo, la columna queda intacta.</summary>
    private async Task<int?> MarcarCerradoAsync(
        int idTenant, int idTurnoCaja, int idEmpleadoCierre, DateTimeOffset momento, string? observacionesCierre,
        CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText =
            "UPDATE turnos_caja SET estado = $1, fecha_cierre = $2, id_empleado_cierre = $3, " +
            "observaciones = CASE WHEN $7::text IS NULL THEN observaciones " +
            "ELSE COALESCE(observaciones || E'\n', '') || $7::text END " +
            "WHERE id_turno_caja = $4 AND id_tenant = $5 AND estado = $6 " +
            "RETURNING id_punto_venta";

        ParametrosDeComando.Agregar(comando, EstadoTurno.Cerrado);
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, idEmpleadoCierre);
        ParametrosDeComando.Agregar(comando, idTurnoCaja);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, EstadoTurno.Abierto);
        ParametrosDeComando.Agregar(comando, (object?)observacionesCierre ?? DBNull.Value);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is null ? null : Convert.ToInt32(resultado);
    }

    /// <summary>Historial paginado (design: API Surface, <c>GET /api/caja/turnos</c>) — mismo
    /// criterio de paginado que <c>ServicioDeVentas.ListarAsync</c>.</summary>
    public async Task<PaginaDeTurnos> ListarAsync(
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.TurnosCaja.AsQueryable();

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(t => t.IdPuntoVenta == pv);
        }

        if (desde is { } d)
        {
            query = query.Where(t => t.FechaApertura >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(t => t.FechaApertura <= h);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(t => t.FechaApertura)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(t => new TurnoListado(t.Id, t.IdPuntoVenta, t.FechaApertura, t.FechaCierre, t.Estado))
            .ToListAsync(ct);

        return new PaginaDeTurnos(items, total, pagina, tamanio);
    }

    // ---- resolución interna ---------------------------------------------------------------

    /// <summary>Ver el doc-comment de <see cref="RegistrarMovimientoAsync"/> — resuelve por id de
    /// turno (el que trae la ruta), pero SIGUE decidiendo con autoridad server-side: <c>404</c>
    /// (ADR-8, no existe/es de otro tenant — el filtro de EF/RLS ya lo deja invisible) o <c>409
    /// turno_no_abierto</c> si existe pero no está <see cref="EstadoTurno.Abierto"/>.</summary>
    private async Task<TurnoCaja> ResolverTurnoPorIdAbiertoAsync(int idTurnoCaja, CancellationToken ct)
    {
        var turno = await db.TurnosCaja.FirstOrDefaultAsync(t => t.Id == idTurnoCaja, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el turno {idTurnoCaja}.");

        if (turno.Estado != EstadoTurno.Abierto)
        {
            throw new ErrorDominio("turno_no_abierto", "El turno no está abierto.", 409);
        }

        return turno;
    }

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" (filtro de EF + RLS ya
            // deja invisible un punto de venta ajeno) — mismo criterio que
            // ServicioDeStock.ResolverPuntoVentaAsync/ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // OperacionDePos (capa de API) ya exige un actor de tenant — un actor de plataforma
            // (root) nunca llega hasta acá. Defensa en profundidad, mismo criterio que
            // ServicioDeStock.ExigirTenantDeLaSesion.
            ?? throw new InvalidOperationException(
                "ServicioDeTurnos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    // ---- statements crudos (ADO.NET, misma convención que ServicioDeVentas) -------------------

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    // ---- proyecciones -----------------------------------------------------------------------

    private static TurnoResumen Proyectar(TurnoCaja turno) => new(
        turno.Id,
        turno.IdPuntoVenta,
        turno.IdEmpleadoApertura,
        turno.IdEmpleadoCierre,
        turno.FechaApertura,
        turno.FechaCierre,
        turno.FondoInicial,
        turno.Estado,
        turno.Observaciones);

    private static MovimientoRegistrado Proyectar(MovimientoCaja movimiento) => new(
        movimiento.Id,
        movimiento.IdTurnoCaja,
        movimiento.Tipo,
        movimiento.Importe,
        movimiento.Motivo,
        movimiento.IdEmpleado,
        movimiento.CreadoEl);

    private static TurnoConArqueos ProyectarConArqueos(TurnoCaja turno, IReadOnlyList<ArqueoTurno> arqueos) => new(
        turno.Id,
        turno.IdPuntoVenta,
        turno.IdEmpleadoApertura,
        turno.IdEmpleadoCierre,
        turno.FechaApertura,
        turno.FechaCierre,
        turno.FondoInicial,
        turno.Estado,
        turno.Observaciones,
        arqueos
            .Select(a => new LineaDeArqueoResumen(a.IdMedioPago, a.ImporteEsperado, a.ImporteDeclarado, a.Diferencia))
            .ToList());
}
