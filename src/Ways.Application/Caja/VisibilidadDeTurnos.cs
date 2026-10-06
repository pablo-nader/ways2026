using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Application.Caja;

/// <summary>
/// Regla de visibilidad de los turnos de caja para las LECTURAS (<c>GET /api/caja/turnos</c> y sus
/// rutas por id) y de <c>GET /api/caja/turnos/abierto</c>. Admin y Supervisor ven todos los turnos
/// del tenant; cualquier otro rol (el Vendedor) ve un turno si lo abrió o lo cerró él, y además:
/// <list type="bullet">
/// <item>sesión de dispositivo POS: cualquier turno, de CUALQUIER estado, del punto de venta de ese
/// dispositivo (caja compartida y reimpresión de cierres ajenos);</item>
/// <item>sesión web (sin dispositivo): el turno ABIERTO de cualquier punto de venta de modo Web —
/// una sesión web solo puede vender contra esos y varios vendedores comparten el mismo, sin que
/// la venta compare contra quién abrió el turno—. Los turnos cerrados ajenos y los de puntos de
/// venta Escritorio siguen ocultos.</item>
/// </list>
/// Es la única fuente del predicado: el chequeo por id, el filtro del listado y el gate de
/// <c>/abierto</c> lo comparten, así que no pueden desalinearse. Las escrituras (apertura,
/// movimientos, cierres) no pasan por acá.
/// </summary>
public static class PoliticaDeVisibilidadDeTurnos
{
    /// <param name="idsPuntosVentaWeb">Ids de los puntos de venta vigentes de modo Web del tenant;
    /// solo los consulta la rama de sesión web. Un punto de venta dado de baja queda fuera (el
    /// filtro de EF lo oculta): ya no se puede vender contra él, así que su turno abierto
    /// tampoco se comparte.</param>
    public static Expression<Func<TurnoCaja, bool>> Predicado(
        RolConocido rol, int idEmpleado, int? idPuntoVentaDelDispositivo, IReadOnlyCollection<int> idsPuntosVentaWeb)
    {
        if (rol is RolConocido.Admin or RolConocido.Supervisor)
        {
            return turno => true;
        }

        if (idPuntoVentaDelDispositivo is { } idPuntoVenta)
        {
            return turno =>
                turno.IdEmpleadoApertura == idEmpleado
                || turno.IdEmpleadoCierre == idEmpleado
                || turno.IdPuntoVenta == idPuntoVenta;
        }

        var idsWeb = idsPuntosVentaWeb.ToArray();

        return turno =>
            turno.IdEmpleadoApertura == idEmpleado
            || turno.IdEmpleadoCierre == idEmpleado
            || (turno.Estado == EstadoTurno.Abierto && idsWeb.Contains(turno.IdPuntoVenta));
    }
}

/// <summary>Resuelve el predicado de <see cref="PoliticaDeVisibilidadDeTurnos"/> para la sesión
/// actual y lo aplica a los turnos del tenant. El empleado es <c>UsuarioId</c>, la misma
/// resolución que usa <c>ServicioDeTurnos.AbrirAsync</c> para <c>id_empleado_apertura</c>.</summary>
public class VisibilidadDeTurnos(IWaysDbContext db, IContextoDeUsuario contexto)
{
    public async Task<Expression<Func<TurnoCaja, bool>>> ResolverPredicadoAsync(CancellationToken ct = default)
    {
        int? idPuntoVentaDelDispositivo = null;
        IReadOnlyCollection<int> idsPuntosVentaWeb = [];

        if (contexto.Rol is not (RolConocido.Admin or RolConocido.Supervisor)
            && contexto.IdDispositivo is { } idDispositivo)
        {
            idPuntoVentaDelDispositivo =
                await PoliticaDeModoDePuntoVenta.ResolverIdPuntoVentaDelDispositivoAsync(db, idDispositivo, ct);
        }
        else if (contexto.Rol is not (RolConocido.Admin or RolConocido.Supervisor))
        {
            idsPuntosVentaWeb = await db.PuntosVenta
                .Where(pv => pv.Modo == ModoPuntoVenta.Web)
                .Select(pv => pv.Id)
                .ToArrayAsync(ct);
        }

        return PoliticaDeVisibilidadDeTurnos.Predicado(
            contexto.Rol, contexto.UsuarioId, idPuntoVentaDelDispositivo, idsPuntosVentaWeb);
    }

    /// <summary>404 ADR-8 (mismo error que un turno inexistente o de otro tenant) cuando el turno
    /// no es visible para la sesión. Debe correr ANTES de leer o derivar cualquier dato del
    /// turno.</summary>
    public async Task ExigirVisibleAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        var predicado = await ResolverPredicadoAsync(ct);

        var visible = await db.TurnosCaja
            .Where(t => t.Id == idTurnoCaja)
            .Where(predicado)
            .AnyAsync(ct);

        if (!visible)
        {
            throw ErrorDominio.NoEncontrado($"No existe el turno {idTurnoCaja}.");
        }
    }
}
