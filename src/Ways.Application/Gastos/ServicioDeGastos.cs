using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Domain.Common;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;

namespace Ways.Application.Gastos;

/// <summary>
/// Captura de gastos contra un turno abierto (design: Table Shapes — write path C; tasks.md
/// Slice 3). Reutiliza <see cref="ServicioDeTurnos.ResolverTurnoAbiertoAsync"/> (tasks.md,
/// Orchestrator Decision 3) en vez de escribir su propia consulta de turno abierto — mismo
/// criterio que <c>ServicioDeVentas.EmitirAsync</c> (Slice 5).
/// </summary>
public class ServicioDeGastos(
    IWaysDbContext db, ServicioDeTurnos servicioDeTurnos, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    /// <summary>Resuelve el punto de venta (404 ADR-8) antes que el turno abierto (spec: Gasto
    /// Requires An Open Turno) — mismo orden que <c>ServicioDeVentas.EmitirAsync</c> (design
    /// decisión 11): un punto de venta apócrifo tiene que dar 404, nunca el 409 de "sin turno
    /// abierto" de un punto de venta que ni siquiera existe.</summary>
    public async Task<GastoRegistrado> RegistrarAsync(SolicitudDeGasto solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);

        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        var turno = await servicioDeTurnos.ResolverTurnoAbiertoAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var gasto = await estrategia.ExecuteAsync(async () =>
            await InsertarGastoAsync(idTenant, turno.Id, solicitud, idEmpleado, momento, ct));

        return Proyectar(gasto);
    }

    /// <summary>Historial paginado (design: API Surface, <c>GET /api/gastos</c>) — mismo criterio
    /// de paginado que <c>ServicioDeTurnos.ListarAsync</c>.</summary>
    public async Task<PaginaDeGastos> ListarAsync(
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Gastos.AsQueryable();

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(g => g.IdPuntoVenta == pv);
        }

        if (desde is { } d)
        {
            query = query.Where(g => g.Fecha >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(g => g.Fecha <= h);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(g => g.Fecha)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(g => new GastoListado(g.Id, g.IdPuntoVenta, g.Fecha, g.Categoria, g.IdMedioPago, g.Importe))
            .ToListAsync(ct);

        return new PaginaDeGastos(items, total, pagina, tamanio);
    }

    // ---- validación de dominio -------------------------------------------------------------

    /// <summary>Mismo código que la CHECK de esquema <c>ck_gastos_importe_positivo</c> (design:
    /// Backstop Map, Slice 1 task 1.7): esta validación de servicio es la UX rápida, la CHECK es
    /// el contrato real (db-error-backstops — nunca tratar el pre-check como la protección).
    /// (spec: Importe Must Be Positive).</summary>
    private static void ExigirImporteValido(decimal importe)
    {
        if (importe <= 0m)
        {
            throw new ErrorDominio("gasto_importe_invalido", "El importe del gasto tiene que ser positivo.", 400);
        }
    }

    // ---- persistencia -------------------------------------------------------------------------

    private async Task<Gasto> InsertarGastoAsync(
        int idTenant, int idTurnoCaja, SolicitudDeGasto solicitud, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        var gasto = new Gasto
        {
            IdTenant = idTenant,
            Fecha = momento,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdTurnoCaja = idTurnoCaja,
            IdEmpleado = idEmpleado,
            Categoria = solicitud.Categoria,
            IdProveedor = solicitud.IdProveedor,
            IdArea = solicitud.IdArea,
            Concepto = solicitud.Concepto,
            Detalle = solicitud.Detalle,
            IdMedioPago = solicitud.IdMedioPago,
            NumeroFactura = solicitud.NumeroFactura,
            Importe = solicitud.Importe,
            CreatedAt = momento,
            UpdatedAt = momento
        };

        db.Gastos.Add(gasto);
        await db.SaveChangesAsync(ct);

        return gasto;
    }

    // ---- resolución interna ---------------------------------------------------------------

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ServicioDeTurnos.ResolverPuntoVentaAsync/ServicioDeStock.ResolverPuntoVentaAsync/
            // ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // OperacionDePos (capa de API) ya exige un actor de tenant — un actor de plataforma
            // (root) nunca llega hasta acá. Defensa en profundidad, mismo criterio que
            // ServicioDeTurnos.ExigirTenantDeLaSesion.
            ?? throw new InvalidOperationException(
                "ServicioDeGastos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    // ---- proyecciones -----------------------------------------------------------------------

    private static GastoRegistrado Proyectar(Gasto gasto) => new(
        gasto.Id,
        gasto.IdTurnoCaja,
        gasto.IdPuntoVenta,
        gasto.Fecha,
        gasto.Categoria,
        gasto.IdProveedor,
        gasto.IdArea,
        gasto.Concepto,
        gasto.Detalle,
        gasto.IdMedioPago,
        gasto.NumeroFactura,
        gasto.Importe,
        gasto.IdEmpleado);
}
