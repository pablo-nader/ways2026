using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Application.Caja;

/// <summary>
/// Resumen parcial (design: API Surface, <c>GET /api/caja/turnos/{id}/resumen</c>; spec: Resumen
/// Parcial Uses The Same Derivation As Cierre) — llama al MISMO par
/// <see cref="LectorDeMovimientosDelTurno"/> + <see cref="CalculadorDeArqueo"/> que
/// <c>ServicioDeTurnos.CerrarAsync</c>, de solo lectura, sin ninguna escritura: es la única
/// garantía estructural de que el número que el cajero ve a mitad de turno es el que el cierre va
/// a comparar (task 4.13, "byte-identical"). El contenido D6 (tickets, áreas, egresos) lo trae
/// <see cref="LectorDeContenidoDeResumen"/>, un lector HERMANO deliberadamente separado (follow-up
/// "Resumen parcial D6-content enrichment") — nunca toca <see cref="InsumosDeArqueo"/> ni
/// <see cref="CalculadorDeArqueo"/>, así que la derivación del arqueo queda intacta.
/// </summary>
public class ServicioDeResumenDeTurno(
    IWaysDbContext db, LectorDeMovimientosDelTurno lector, LectorDeContenidoDeResumen lectorDeContenido)
{
    public async Task<ResumenDeTurno> ObtenerAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        await ExigirTurnoExisteAsync(idTurnoCaja, ct);

        var insumos = await lector.LeerAsync(idTurnoCaja, ct);
        var idAncla = ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad);
        var lineas = CalculadorDeArqueo.Calcular(insumos, idAncla);

        var contenido = await lectorDeContenido.LeerAsync(idTurnoCaja, ct);

        return new ResumenDeTurno(
            idTurnoCaja, idAncla,
            lineas.Select(l => new LineaDeResumen(l.IdMedioPago, l.ImporteEsperado)).ToList(),
            contenido.CantidadTickets, contenido.PrimerTicket, contenido.UltimoTicket, contenido.IngresosPorArea,
            new EgresosDeTurno(contenido.EgresosPorCategoria, insumos.Retiros));
    }

    private async Task ExigirTurnoExisteAsync(int idTurnoCaja, CancellationToken ct)
    {
        var existe = await db.TurnosCaja.AnyAsync(t => t.Id == idTurnoCaja, ct);
        if (!existe)
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF/RLS ya
            // deja invisible un turno ajeno, mismo criterio que ServicioDeTurnos.ObtenerAsync.
            throw ErrorDominio.NoEncontrado($"No existe el turno {idTurnoCaja}.");
        }
    }
}
