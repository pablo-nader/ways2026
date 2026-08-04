using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Reglas de negocio puras de <see cref="TurnoCaja"/> (design decisión 10), sin dependencias —
/// mismo criterio que <see cref="Ventas.ReglaDeComprobantes"/>. <c>ServicioDeTurnos.CerrarAsync</c>
/// (Slice 4, cierre — la única transición real de este stage) pasa por acá antes de tocar la fila.
/// </summary>
public static class ReglaDeTurnos
{
    /// <summary>Única transición válida: <see cref="EstadoTurno.Abierto"/> → <see
    /// cref="EstadoTurno.Cerrado"/> (design decisión 10) — nunca al revés, no hay reapertura.
    /// Un turno ya cerrado que intenta cerrarse de nuevo es <c>409 turno_ya_cerrado</c> (design:
    /// The Cierre Transaction, statement 1 — el mismo código que produce el <c>UPDATE … WHERE
    /// estado = 'abierto'</c> de 0 filas cuando el turno existe pero ya está cerrado, para que el
    /// resultado sea idéntico sin importar si la doble transición se detecta acá o en la carrera
    /// de la base).</summary>
    public static void ValidarTransicionAEstado(EstadoTurno actual, EstadoTurno nuevo)
    {
        if (actual == EstadoTurno.Cerrado)
        {
            throw new ErrorDominio("turno_ya_cerrado", "El turno ya está cerrado.", 409);
        }

        if (actual == EstadoTurno.Abierto && nuevo == EstadoTurno.Cerrado)
        {
            return;
        }

        throw new ErrorDominio("transicion_de_estado_invalida", "Esa transición de estado no es válida.", 400);
    }
}
