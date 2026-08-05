using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Application.Caja;

/// <summary>
/// Turno de caja: apertura, movimientos físicos fuera de la venta (retiro/refuerzo/apertura de
/// cajón) y lectura (design: API Surface). El cierre (design: The Cierre Transaction) llega en
/// Slice 4 — <see cref="ResolverTurnoAbiertoAsync"/> es el resolver compartido que Slice 3
/// (gastos) y Slice 5 (checkout) reutilizan (tasks.md, Orchestrator Decision 3), evitando tres
/// copias del mismo <c>409 turno_no_abierto</c>.
/// </summary>
public class ServicioDeTurnos(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
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

        var turno = await ResolverTurnoPorIdAbiertoAsync(idTurnoCaja, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var movimiento = await estrategia.ExecuteAsync(async () =>
            await InsertarMovimientoAsync(idTenant, turno.Id, solicitud, idEmpleado, momento, ct));

        return Proyectar(movimiento);
    }

    private async Task<MovimientoCaja> InsertarMovimientoAsync(
        int idTenant, int idTurnoCaja, SolicitudDeMovimiento solicitud, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
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

        return movimiento;
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
    /// del Z-report; Slice 4 le agrega los <c>arqueos_turno</c> cuando el cierre exista).</summary>
    public async Task<TurnoResumen> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var turno = await db.TurnosCaja.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el turno {id}.");

        return Proyectar(turno);
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
}
