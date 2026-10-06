using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Application.Caja;

/// <summary>
/// Regla de visibilidad de los turnos de caja para las LECTURAS (<c>GET /api/caja/turnos</c> y sus
/// rutas por id). Admin y Supervisor ven todos los turnos del tenant; cualquier otro rol (el
/// Vendedor) ve un turno solo si lo abrió o lo cerró él, o si la sesión es de un dispositivo POS y
/// el turno es de CUALQUIER estado y pertenece al punto de venta de ese dispositivo (caja compartida
/// y reimpresión de cierres ajenos). Es la única fuente del predicado: el chequeo por id y el
/// filtro del listado lo comparten, así que no pueden desalinearse. Las escrituras (apertura,
/// movimientos, cierres) no pasan por acá.
/// </summary>
public static class PoliticaDeVisibilidadDeTurnos
{
    public static Expression<Func<TurnoCaja, bool>> Predicado(
        RolConocido rol, int idEmpleado, int? idPuntoVentaDelDispositivo)
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

        return turno => turno.IdEmpleadoApertura == idEmpleado || turno.IdEmpleadoCierre == idEmpleado;
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

        if (contexto.Rol is not (RolConocido.Admin or RolConocido.Supervisor)
            && contexto.IdDispositivo is { } idDispositivo)
        {
            idPuntoVentaDelDispositivo =
                await PoliticaDeModoDePuntoVenta.ResolverIdPuntoVentaDelDispositivoAsync(db, idDispositivo, ct);
        }

        return PoliticaDeVisibilidadDeTurnos.Predicado(contexto.Rol, contexto.UsuarioId, idPuntoVentaDelDispositivo);
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
