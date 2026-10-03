using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// Turno de caja: apertura, movimientos físicos fuera de la venta (retiro/refuerzo/apertura de
/// cajón), cierre (design: The Cierre Transaction) y lectura (design: API Surface). <see
/// cref="ResolverTurnoAbiertoAsync"/> es el resolver compartido que Slice 3 (gastos) y Slice 5
/// (checkout) reutilizan (tasks.md, Orchestrator Decision 3), evitando tres copias del mismo
/// <c>409 turno_no_abierto</c>; <see cref="ExigirTurnoAbiertoBajoLockAsync"/> (Slice 4, task
/// 4.17) es el mismo tipo de pieza compartida para el guard <c>FOR SHARE</c> — reusado por
/// <c>ServicioDeGastos.RegistrarAsync</c>. <see cref="CerrarPorRetiroAsync"/> (etapa 5, "cierre por
/// retiro") es el segundo modo de cierre — práctica del dueño: el cajero retira el efectivo
/// contado y DEJA el fondo inicial en el cajón, nada se cuenta — y reusa
/// <see cref="InsertarArqueosYTesoreriaAsync"/>, el mismo statement 5/6 que <see
/// cref="EjecutarCierreAsync"/> (spec arqueo-de-cierre: Cierre Por Retiro).
///
/// Los DOS modos de cierre comparten la guarda de rendición de cola de dispositivos
/// (<see cref="ResolverRendicionDeDispositivosAsync"/>, statement 1.5 de las dos transacciones) y
/// su override supervisado (<see cref="ValidarOverrideDeRendicion"/>): un turno no se cierra
/// mientras un POS de escritorio del punto de venta tenga ventas sin drenar en su cola local.
/// </summary>
public class ServicioDeTurnos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, LectorDeMovimientosDelTurno lector,
    LectorDeResumenDeCierrePorRetiro lectorDeResumenDeCierrePorRetiro,
    LectorDeRendicionDeDispositivos lectorDeRendicion, ServicioDeAuditoria auditoria)
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
    /// reapertura ni edición directa del arqueo (spec: Cierre Is One Atomic, Irreversible
    /// Transaction). La única excepción es el recálculo administrativo del esperado cuando un admin
    /// edita o da de baja un gasto de caja del turno ya cerrado (<c>ServicioDeGastos</c>), que
    /// reusa esta misma derivación con el ancla pineada y deja marcado el turno.
    /// <see cref="FabricaDeEstrategiaSinReintento"/>: manual, raro, sin clave de idempotencia —
    /// un commit ambiguo tiene que llegar al operador como una falla que re-chequea, nunca como
    /// un reintento automático que reporte <c>409 turno_ya_cerrado</c> sobre un cierre que en
    /// verdad tuvo éxito (design: Failure Semantics).</summary>
    public async Task<TurnoConArqueos> CerrarAsync(
        int idTurnoCaja, SolicitudDeCierre solicitud, CancellationToken ct = default)
    {
        var motivoSinRendicion = ValidarOverrideDeRendicion(solicitud.ForzarSinRendicion, solicitud.MotivoSinRendicion);

        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        // Pineado ACÁ, nunca releído dentro de la lambda reintentable (design: The Cierre
        // Transaction, "momento := reloj.Ahora, pinned, never re-read").
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarCierreAsync(idTurnoCaja, idTenant, idEmpleado, momento, solicitud, motivoSinRendicion, ct));
    }

    private async Task<TurnoConArqueos> EjecutarCierreAsync(
        int idTurnoCaja, int idTenant, int idEmpleado, DateTimeOffset momento, SolicitudDeCierre solicitud,
        string? motivoSinRendicion, CancellationToken ct)
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

        // 1.5. Guarda de rendición de dispositivos — acá y no antes de abrir la transacción: tiene
        // que correr DENTRO de ella y ANTES de escribir cualquier otra cosa, así un rechazo deshace
        // también el UPDATE guardado de arriba. No comparte snapshot con la derivación (READ
        // COMMITTED, y el lock es solo de la fila del turno): ver el doc-comment de
        // ResolverRendicionDeDispositivosAsync.
        await ResolverRendicionDeDispositivosAsync(
            idTenant, idTurnoCaja, idPuntoVenta.Value, momento, solicitud.ForzarSinRendicion, motivoSinRendicion, ct);

        // 2. Insumos de la derivación (7 consultas agrupadas, cantidad fija) — bajo el lock.
        var insumos = await lector.LeerAsync(idTurnoCaja, ct);

        // 3. Ancla — puro, 409 caja_sin_medio_efectivo_unico si no es único.
        var idAncla = ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad);

        // 4. Cálculo (PURO, la única fórmula) + validación de los conteos declarados.
        var lineas = CalculadorDeArqueo.Calcular(insumos, idAncla);
        ValidadorDeConteos.Validar(lineas, insumos.Actividad, solicitud.Conteos);

        // 5/6/7. Fijar el ancla + INSERT arqueos_turno + tesorería encadenada — statements
        // compartidos con CerrarPorRetiroAsync, ver InsertarArqueosYTesoreriaAsync.
        var declaradoPorMedio = solicitud.Conteos.ToDictionary(c => c.IdMedioPago, c => c.ImporteDeclarado);
        var arqueos = await InsertarArqueosYTesoreriaAsync(
            idTenant, idTurnoCaja, idPuntoVenta.Value, idEmpleado, momento, insumos, lineas, idAncla,
            l => declaradoPorMedio[l.IdMedioPago], ct);

        await transaccion.CommitAsync(ct);

        var turno = await db.TurnosCaja.AsNoTracking().FirstAsync(t => t.Id == idTurnoCaja, ct);
        return ProyectarConArqueos(turno, arqueos);
    }

    /// <summary>Cierre por retiro (etapa 5, práctica del dueño): el cajero retira el efectivo
    /// contado y DEJA el fondo inicial en el cajón — nada se cuenta al cierre. Mismo orden de
    /// statements y mismas garantías de lock/carrera que <see cref="EjecutarCierreAsync"/> (design:
    /// The Cierre Transaction; spec arqueo-de-cierre: Cierre Por Retiro — Is Atomic And Reuses The
    /// Cierre Lock): el UPDATE guardado va PRIMERO, y el retiro de cierre (si lo hay) se inserta
    /// DESPUÉS de ganar ese lock exclusivo y ANTES de derivar, así que <see
    /// cref="LectorDeMovimientosDelTurno"/> ya lo ve como parte de <c>insumos.Retiros</c> — cuenta
    /// como retiro/ingreso de tesorería igual que cualquier otro (spec: "the closing withdrawal
    /// counts as retiro/ingreso"). <see cref="FabricaDeEstrategiaSinReintento"/> por el mismo motivo
    /// que <see cref="CerrarAsync"/>: manual, raro, sin clave de idempotencia — un commit ambiguo
    /// tiene que llegar como <c>503 resultado_incierto</c> (ef-retry-safe-writes regla 4), nunca
    /// como un reintento silencioso que duplique el retiro de cierre.</summary>
    public async Task<ResumenDeCierrePorRetiro> CerrarPorRetiroAsync(
        int idTurnoCaja, SolicitudDeCierrePorRetiro solicitud, CancellationToken ct = default)
    {
        if (solicitud.ImporteRetirado < 0)
        {
            throw new ErrorDominio(
                "importe_retirado_invalido", "El importe retirado no puede ser negativo.", 400);
        }

        var motivoSinRendicion = ValidarOverrideDeRendicion(solicitud.ForzarSinRendicion, solicitud.MotivoSinRendicion);

        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var turno = await estrategia.ExecuteAsync(async () =>
            await EjecutarCierrePorRetiroAsync(
                idTurnoCaja, idTenant, idEmpleado, momento, solicitud, motivoSinRendicion, ct));

        return await lectorDeResumenDeCierrePorRetiro.LeerAsync(turno, ct);
    }

    private async Task<TurnoCaja> EjecutarCierrePorRetiroAsync(
        int idTurnoCaja, int idTenant, int idEmpleado, DateTimeOffset momento, SolicitudDeCierrePorRetiro solicitud,
        string? motivoSinRendicion, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // 1. Mismo UPDATE guardado que el cierre clásico — mismo lock, mismos 404/409.
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

        // 1.5. Guarda de rendición de dispositivos — mismo lugar exacto que en el cierre clásico:
        // dentro de la transacción y antes de escribir nada más, para que un rechazo la aborte
        // entera.
        await ResolverRendicionDeDispositivosAsync(
            idTenant, idTurnoCaja, idPuntoVenta.Value, momento, solicitud.ForzarSinRendicion, motivoSinRendicion, ct);

        // 2. El retiro de cierre, SI lo hay — ANTES de derivar, para que insumos.Retiros ya lo
        // cuente (spec: "insert a MovimientoCaja Retiro ... BEFORE deriving"). Importe 0 no
        // inserta nada: ck_movimientos_caja_importe exige > 0 para tipo <> apertura_cajon, y un
        // cajero que no retira nada no generó ningún movimiento físico.
        if (solicitud.ImporteRetirado > 0)
        {
            db.MovimientosCaja.Add(new MovimientoCaja
            {
                IdTenant = idTenant,
                IdTurnoCaja = idTurnoCaja,
                Tipo = TipoMovimientoCaja.Retiro,
                Importe = solicitud.ImporteRetirado,
                Motivo = "Retiro de cierre de turno",
                IdEmpleado = idEmpleado,
                CreadoEl = momento
            });
            await db.SaveChangesAsync(ct);
        }

        // 3. Insumos de la derivación — mismo lector, mismas 7 consultas; ya ve el retiro de
        // cierre recién insertado (misma transacción/conexión).
        var insumos = await lector.LeerAsync(idTurnoCaja, ct);

        // 4. Ancla — puro, 409 caja_sin_medio_efectivo_unico si no es único.
        var idAncla = ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad);

        // 5. Cálculo (misma fórmula que el cierre clásico) — SIN ValidadorDeConteos: acá no hay
        // conteos del cliente que validar, el servidor declara los dos únicos valores posibles
        // (design: el ancla se declara con el fondo inicial porque se queda en el cajón; el resto,
        // con su propio esperado, porque no hay nada físico que contar en un medio no-efectivo).
        var lineas = CalculadorDeArqueo.Calcular(insumos, idAncla);

        // 6/7/8. Fijar el ancla + INSERT arqueos_turno + tesorería encadenada — mismos statements
        // que el cierre clásico (InsertarArqueosYTesoreriaAsync), declarado = fondo inicial en el
        // ancla, esperado en cualquier otro medio arqueable.
        await InsertarArqueosYTesoreriaAsync(
            idTenant, idTurnoCaja, idPuntoVenta.Value, idEmpleado, momento, insumos, lineas, idAncla,
            l => l.IdMedioPago == idAncla ? insumos.FondoInicial : l.ImporteEsperado, ct);

        await transaccion.CommitAsync(ct);

        return await db.TurnosCaja.AsNoTracking().FirstAsync(t => t.Id == idTurnoCaja, ct);
    }

    /// <summary>Statements 5/6/7 del cierre clásico (design: The Cierre Transaction) —
    /// compartidos TAL CUAL por <see cref="EjecutarCierreAsync"/> y <see
    /// cref="EjecutarCierrePorRetiroAsync"/>: fijar <see cref="TurnoCaja.IdMedioPagoEfectivo"/>
    /// (judgment-day JD-E5a-2 — el mismo <paramref name="idAncla"/> con el que se derivaron
    /// <paramref name="lineas"/>, NUNCA re-resuelto después), INSERT de <c>arqueos_turno</c> (una
    /// fila por medio arqueable; <paramref name="declarar"/> es la ÚNICA diferencia entre los dos
    /// modos — de dónde sale <c>ImporteDeclarado</c>) y la tesorería encadenada (UN único
    /// movimiento tipo <c>retiro_caja</c>, inicio = final de la última fila de la misma empresa).
    ///
    /// stage-gastos-origen-fondos-pos (PR2): <c>Egreso</c> pasa a ser SIEMPRE <c>0</c> acá — antes
    /// (PR1) restaba la Σ de TODOS los gastos del turno (paridad legacy, design decisión 9); ahora
    /// un gasto de origen <see cref="Ways.Domain.Gastos.OrigenFondosGasto.CajaTurno"/> ya salió del
    /// EFECTIVO DEL CAJÓN (reduce lo que el cajero cuenta, nunca la tesorería) y un gasto de origen
    /// <see cref="Ways.Domain.Gastos.OrigenFondosGasto.Tesoreria"/> ya escribió SU PROPIO movimiento
    /// de tesorería en el momento en que se registró (<c>ServicioDeGastos</c>) — restarlo de nuevo
    /// acá sería descontarlo DOS veces. <paramref name="insumos"/>.Actividad[].Gastos ya viene
    /// filtrado a solo <c>CajaTurno</c> (<see cref="LectorDeMovimientosDelTurno"/>), así que ni
    /// siquiera se lee para la tesorería — solo alimenta <see cref="CalculadorDeArqueo"/>.
    ///
    /// Debe llamarse DENTRO de la transacción ya abierta por el llamador, después del UPDATE
    /// guardado (statement 1, que YA transicionó <c>estado</c> a <c>cerrado</c> — satisface
    /// <c>ck_turnos_caja_medio_efectivo_solo_cerrado</c>) y de derivar el ancla.</summary>
    private async Task<IReadOnlyList<ArqueoTurno>> InsertarArqueosYTesoreriaAsync(
        int idTenant, int idTurnoCaja, int idPuntoVenta, int idEmpleado, DateTimeOffset momento,
        InsumosDeArqueo insumos, IReadOnlyList<LineaDeArqueo> lineas, int idAncla,
        Func<LineaDeArqueo, decimal> declarar, CancellationToken ct)
    {
        await FijarMedioPagoEfectivoAsync(idTenant, idTurnoCaja, idAncla, ct);

        var arqueos = lineas
            .Select(l => new ArqueoTurno
            {
                IdTenant = idTenant,
                IdTurnoCaja = idTurnoCaja,
                IdMedioPago = l.IdMedioPago,
                ImporteEsperado = l.ImporteEsperado,
                ImporteDeclarado = declarar(l)
            })
            .ToList();
        db.ArqueosTurno.AddRange(arqueos);
        await db.SaveChangesAsync(ct);

        // GastosOrigenFondosYTesoreriaPorEmpresa: IdEmpresa sale de una proyección escalar de una
        // columna inmutable de PuntoVenta (single-read-under-lock regla 6) — hace falta ANTES de
        // poder tomar el lock de la cadena, y no es una entidad trackeada.
        var idEmpresa = await db.PuntosVenta
            .Where(pv => pv.Id == idPuntoVenta)
            .Select(pv => pv.IdEmpresa)
            .FirstAsync(ct);

        // La cadena inicio→final es por (id_tenant, id_empresa) — varios puntos de venta de la
        // misma empresa comparten un solo fondo de tesorería, así que dos cierres concurrentes de
        // PVs DISTINTOS de la MISMA empresa tienen que serializarse ANTES de leer el `inicio`
        // compartido (single-read-under-lock regla 1). Mismo lock que un gasto de origen Tesoreria
        // toma en `ServicioDeGastos` — ver el doc-comment de clase de `EscriturasDeTesoreria` para
        // el análisis de orden que evita el ciclo entre los dos escritores.
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();
        await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, idEmpresa, ct);

        await EscriturasDeTesoreria.ApendearAsync(
            db, idTenant, idEmpresa, idPuntoVenta, momento, TipoMovimientoTesoreria.RetiroCaja, idTurnoCaja,
            idGasto: null, concepto: "Cierre de turno", ingreso: insumos.Retiros, egreso: 0m, idEmpleado, ct);

        return arqueos;
    }

    /// <summary>judgment-day JD-E5a-2 (DB CHANGE GATE aprobado): pinea <see
    /// cref="TurnoCaja.IdMedioPagoEfectivo"/> UNA sola vez, al cierre — statement crudo, mismo
    /// patrón ADO que <see cref="MarcarCerradoAsync"/>/<see
    /// cref="ExigirTurnoAbiertoBajoLockAsync"/>. Sin <c>WHERE estado = 'cerrado'</c> explícito:
    /// esta fila ya está bajo el lock EXCLUSIVO que el UPDATE guardado (statement 1) tomó y
    /// todavía sostiene hasta el commit, así que ninguna otra transacción pudo haberla tocado
    /// entremedio — el UPDATE guardado YA dejó <c>estado = 'cerrado'</c>, lo que satisface
    /// <c>ck_turnos_caja_medio_efectivo_solo_cerrado</c> por construcción.</summary>
    private async Task FijarMedioPagoEfectivoAsync(
        int idTenant, int idTurnoCaja, int idMedioPagoEfectivo, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText =
            "UPDATE turnos_caja SET id_medio_pago_efectivo = $1 WHERE id_turno_caja = $2 AND id_tenant = $3";
        ParametrosDeComando.Agregar(comando, idMedioPagoEfectivo);
        ParametrosDeComando.Agregar(comando, idTurnoCaja);
        ParametrosDeComando.Agregar(comando, idTenant);
        await comando.ExecuteNonQueryAsync(ct);
    }

    // ---- guarda de rendición de cola de dispositivos (compartida por los DOS modos de cierre) ----

    /// <summary>Validación de los dos campos del override, ANTES de abrir la transacción (mismo
    /// lugar que <c>ImporteRetirado &lt; 0</c>): dto-contract-honesty, cada campo con un único
    /// destino. Devuelve el motivo ya recortado cuando el forzado es válido, <c>null</c> cuando no
    /// hay forzado — el llamador lo pasa tal cual al rastro de auditoría, así que un motivo
    /// aceptado nunca puede quedar sin escribirse.
    ///
    /// El gate de rol vive ACÁ, y es el ÚNICO lugar donde vive, porque una policy de ASP.NET Core
    /// no puede ser condicional a un campo del cuerpo: las dos rutas de cierre están bajo
    /// <c>Politicas.OperacionDePos</c> (un Vendedor tiene que poder cerrar su turno). Deliberadamente
    /// sin una constante espejo en <c>Politicas</c>: una policy registrada que ninguna ruta apila no
    /// la evalúa nadie, así que no habría "un solo lugar donde cambiar" sino dos, y el inerte sería
    /// el que lleva nombre de control de seguridad.</summary>
    private string? ValidarOverrideDeRendicion(bool forzar, string? motivo)
    {
        if (!forzar)
        {
            if (!string.IsNullOrWhiteSpace(motivo))
            {
                throw new ErrorDominio(
                    "motivo_sin_forzado",
                    "motivoSinRendicion solo se acepta junto con forzarSinRendicion.",
                    400);
            }

            return null;
        }

        if (contexto.Rol is not (RolConocido.Supervisor or RolConocido.Admin))
        {
            throw new ErrorDominio(
                "prohibido", "Forzar el cierre sin rendición requiere un supervisor.", 403);
        }

        if (string.IsNullOrWhiteSpace(motivo))
        {
            throw new ErrorDominio(
                "motivo_requerido", "Forzar el cierre sin rendición requiere un motivo.", 400);
        }

        return motivo.Trim();
    }

    /// <summary>La guarda en sí, dentro de la transacción del cierre: bloquea el cierre mientras
    /// algún dispositivo del punto de venta tenga cola sin drenar (o no pueda probar que no la
    /// tiene), y deja rastro auditable cuando un supervisor la fuerza.
    ///
    /// Con <paramref name="forzar"/> en true escribe la fila de auditoría incluso si la guarda no
    /// encontró nada: es lo que garantiza que el motivo declarado nunca se descarte
    /// (dto-contract-honesty) y "apretó el override sin necesidad" es en sí mismo un dato útil. Esa
    /// fila vive DENTRO de la transacción del cierre, así que existe si y solo si el cierre comitea —
    /// un forzado válido cuyo cierre después falla (<c>caja_sin_medio_efectivo_unico</c>,
    /// <c>arqueo_incompleto</c>, cualquier rollback) no deja rastro, y está bien que sea así: no hubo
    /// cierre forzado que auditar. Se usa el overload raw-ADO de
    /// <see cref="ServicioDeAuditoria.RegistrarAsync"/> porque el cierre es una transacción ADO;
    /// <c>ef-retry-safe-writes</c> forma (b): el cierre corre bajo
    /// <see cref="FabricaDeEstrategiaSinReintento"/>, así que no hace falta ningún reset del
    /// ChangeTracker (la lambda nunca se reintenta).
    ///
    /// Un forzado también SALDA los bloques ABANDONADOS que informó como bloqueantes y solo esos
    /// (<see cref="MarcarRendicionSaldadaAsync"/>, que explica por qué el vivo queda afuera): sus
    /// números sin rendir quedan explícitamente aceptados como tales, y por eso dejan de bloquear los
    /// cierres siguientes. Es el único mecanismo que saca un bloque de la guarda además de revocar el
    /// dispositivo.
    ///
    /// Carrera irreducible y ACEPTADA: el dispositivo puede rendir limpio, perder señal y vender
    /// mientras alguien cierra. Nada de esto corre bajo un lock que cubra
    /// <c>reservas_numeracion</c>/<c>dispositivos</c>/<c>comprobantes_venta</c> —el cierre lockea la
    /// fila del turno y corre en READ COMMITTED— y no se agrega ninguno, porque la ventana está
    /// acotada por construcción: un dispositivo ONLINE manda sus ventas directo al servidor (caen
    /// dentro del turno o rebotan con <c>409 turno_no_abierto</c>, nunca quedan en la cola), y un
    /// dispositivo que se quedó OFFLINE deja de poder rendir, así que el reporte de su bloque VIVO
    /// —el único al que la frescura se le aplica— envejece y en
    /// <see cref="ReglaDeRendicionDeCola.VentanaDeFrescura"/> el cierre se vuelve a bloquear solo.
    /// </summary>
    private async Task ResolverRendicionDeDispositivosAsync(
        int idTenant, int idTurnoCaja, int idPuntoVenta, DateTimeOffset momento, bool forzar, string? motivo,
        CancellationToken ct)
    {
        var pendientes = await lectorDeRendicion.LeerPendientesAsync(idTenant, idPuntoVenta, momento, ct);

        if (!forzar)
        {
            if (pendientes.Count > 0)
            {
                throw new ErrorDominio(
                    "rendicion_de_dispositivo_pendiente", DescribirPendientes(pendientes), 409);
            }

            return;
        }

        // ValidarOverrideDeRendicion ya garantizó no-nulo/no-vacío cuando forzar es true.
        await RegistrarCierreForzadoAsync(idTenant, idTurnoCaja, idPuntoVenta, motivo!, pendientes, ct);
        await MarcarRendicionSaldadaAsync(pendientes, momento, ct);
    }

    /// <summary>Marca como saldados EXACTAMENTE los bloques ABANDONADOS que
    /// <see cref="ResolverRendicionDeDispositivosAsync"/> acaba de informar como bloqueantes — por
    /// id, nunca "todos los del punto de venta": un bloque que no bloqueaba nada no tiene números
    /// sin rendir que alguien esté aceptando, y saldarlo lo volvería ciego para el cierre siguiente.
    /// Sin bloqueos abandonados no hay statement (el forzado innecesario igual queda auditado).
    ///
    /// El filtro por bloque VIVO es la mitad indispensable de esta regla (judgment-day, SEVERE):
    /// saldar el bloque vivo del dispositivo lo volvía invisible para SIEMPRE —el único conjunto de
    /// alcance de la guarda es <c>rendicion_saldada_at IS NULL</c>— mientras el dispositivo seguía
    /// vendiendo sobre él, o sea exactamente la corrupción que esta guarda existe para evitar. Un
    /// bloque vivo todavía acumula evidencia: no hay nada terminado que aceptar, así que se queda sin
    /// saldar y sigue bloqueando hasta que rote. La convergencia, dicha en voz alta: un hueco en un
    /// bloque VIVO cuesta un forzado por cada cierre hasta que el bloque rote, y exactamente UNO
    /// después de rotar —ahí su evidencia quedó congelada y, si todavía muestra el hueco, ese forzado
    /// lo salda de forma permanente.
    ///
    /// SQL crudo sobre la conexión/transacción del cierre, misma convención que el resto del
    /// método: cae en el mismo COMMIT, así que un cierre que falla después no deja ningún bloque
    /// saldado a medias. Sin conjunto de <c>id_tenant</c>, misma decisión que el <c>UPDATE</c> de
    /// <c>numeraciones_comprobante</c> (ver el doc-comment de
    /// <see cref="Ventas.AsignadorDeNumeroComprobante"/>): la clave del <c>WHERE</c> ya es la PK
    /// global y el aislamiento por tenant lo impone RLS sobre esta misma conexión.</summary>
    private async Task MarcarRendicionSaldadaAsync(
        IReadOnlyList<RendicionPendiente> pendientes, DateTimeOffset momento, CancellationToken ct)
    {
        var abandonados = pendientes.Where(p => !p.BloqueVivo).Select(p => p.IdReserva).ToArray();

        if (abandonados.Length == 0)
        {
            return;
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE reservas_numeracion SET rendicion_saldada_at = $1, updated_at = $1 " +
            "WHERE id_reserva_numeracion = ANY($2)";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, abandonados);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private async Task RegistrarCierreForzadoAsync(
        int idTenant, int idTurnoCaja, int idPuntoVenta, string motivo,
        IReadOnlyList<RendicionPendiente> pendientes, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // Payload armado acá y no con una fábrica de PayloadDeAuditoria: lleva una LISTA (un punto
        // de venta puede tener varios dispositivos bloqueando a la vez), mismo criterio que
        // ServicioDeVentas usa para venta.discrepancia.
        //
        // id_reserva, techo_verificado y bloque_vivo son parte del rastro y no adornos
        // (judgment-day): sin techo_verificado la fila no dice qué rango se aceptó como sin rendir
        // (el declarado puede quedar por debajo, y el verificado es el que informa el mensaje del
        // hueco); id_reserva identifica la fila, y bloque_vivo dice si ESA fila quedó saldada o no
        // —MarcarRendicionSaldadaAsync salda solo las abandonadas, así que sin este campo un
        // bloqueo vivo, que sigue bloqueando el cierre siguiente, se leería como aceptado.
        var payload = new Dictionary<string, object?>
        {
            ["motivo"] = motivo,
            ["bloqueos"] = pendientes
                .Select(p => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["id_reserva"] = p.IdReserva,
                    ["id_dispositivo"] = p.IdDispositivo,
                    ["dispositivo"] = p.NombreDispositivo,
                    ["tipo_comprobante"] = p.TipoComprobante,
                    // El enum crudo, NO su ToString(): así lo serializa SerializadorDeAuditoria con
                    // su JsonStringEnumConverter(SnakeCaseLower) y esta fila guarda la misma etiqueta
                    // de base que cualquier otro enum auditado ("ventas_sin_llegar"), en vez del
                    // PascalCase que produce C#.
                    ["bloqueo"] = p.Motivo,
                    ["pendientes"] = p.Pendientes,
                    ["desde"] = p.Desde,
                    ["entregado_hasta"] = p.EntregadoHasta,
                    ["techo_verificado"] = p.TechoVerificado,
                    ["bloque_vivo"] = p.BloqueVivo
                })
                .ToList()
        };

        await auditoria.RegistrarAsync(
            conexion, transaccionCruda,
            new RegistroDeAuditoria(
                idTenant, idPuntoVenta, AccionAuditada.CierreForzadoSinRendicion, idTurnoCaja,
                valorAnterior: null, valorNuevo: payload),
            ct);
    }

    private static string DescribirPendientes(IReadOnlyList<RendicionPendiente> pendientes) =>
        "No se puede cerrar el turno: " + string.Join("; ", pendientes.Select(Describir)) + ".";

    private static string Describir(RendicionPendiente pendiente) => pendiente.Motivo switch
    {
        MotivoDeRendicionPendiente.SinReporte =>
            $"el dispositivo '{pendiente.NombreDispositivo}' no reportó el estado de su cola " +
            $"({pendiente.TipoComprobante})",
        MotivoDeRendicionPendiente.ReporteVencido =>
            $"el último reporte del dispositivo '{pendiente.NombreDispositivo}' está vencido " +
            $"({pendiente.TipoComprobante})",
        MotivoDeRendicionPendiente.VentasSinLlegar =>
            $"el dispositivo '{pendiente.NombreDispositivo}' tiene {pendiente.Pendientes} venta(s) sin " +
            $"sincronizar ({pendiente.TipoComprobante})",
        // El rango que se informa es el VERIFICADO, no el declarado: cuando el dispositivo declaró
        // menos de lo que ya llegó, el declarado describiría un rango vacío (o más chico que el
        // hueco real) y el mensaje mentiría.
        MotivoDeRendicionPendiente.HuecoDeComprobantes =>
            $"faltan comprobantes del dispositivo '{pendiente.NombreDispositivo}' en el rango " +
            $"{pendiente.Desde}-{pendiente.TechoVerificado} ({pendiente.TipoComprobante})",
        _ => throw new ArgumentOutOfRangeException(nameof(pendiente), pendiente.Motivo, "Motivo no reconocido.")
    };

    /// <summary>Resumen de un cierre ya persistido (<c>GET …/resumen-de-cierre</c>) — reimpresión o
    /// recuperación tras una falla de red ambigua sobre <see cref="CerrarPorRetiroAsync"/>: sirve
    /// para CUALQUIER turno cerrado (por retiro o por el cierre clásico), porque la derivación no
    /// depende de qué endpoint lo cerró. <c>404</c> ADR-8 si no existe/es de otro tenant; <c>409
    /// turno_no_cerrado</c> si todavía está abierto.</summary>
    public async Task<ResumenDeCierrePorRetiro> ObtenerResumenDeCierreAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        var turno = await db.TurnosCaja.AsNoTracking().FirstOrDefaultAsync(t => t.Id == idTurnoCaja, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el turno {idTurnoCaja}.");

        if (turno.Estado != EstadoTurno.Cerrado)
        {
            throw new ErrorDominio("turno_no_cerrado", "El turno todavía no está cerrado.", 409);
        }

        return await lectorDeResumenDeCierrePorRetiro.LeerAsync(turno, ct);
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
            .Select(a => new LineaDeArqueoResumen(
                a.IdMedioPago, a.ImporteEsperado, a.ImporteDeclarado, a.Diferencia, a.ImporteEsperadoOriginal))
            .ToList(),
        turno.FechaRecalculo,
        turno.IdEmpleadoRecalculo);
}
