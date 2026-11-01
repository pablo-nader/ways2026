using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Application.Caja;

/// <summary>Cuerpo de la conciliación: la empresa, el instante hasta el cual se revisó la tesorería y una nota opcional.</summary>
public sealed record SolicitudDeConciliacion(int IdEmpresa, DateTimeOffset? Hasta, string? Nota);

public sealed record ResultadoDeConciliacion(
    int IdConciliacion, int IdEmpresa, DateTimeOffset Hasta, string? Nota, DateTimeOffset CreadoEl, int IdUsuario);

/// <summary>
/// Marca de conciliación de la tesorería de una empresa. La autorización (Admin) se aplica en la capa de API. Orden: 404 de la
/// empresa, 400 del cuerpo y recién después, bajo el lock exclusivo de la empresa (<see cref="GuardaDeConciliacion"/>), los 409:
/// futura, no posterior a la vigente y con un turno abierto que empezó antes del instante.
///
/// Sin reintento: la marca no tiene clave de idempotencia y una repetición exacta la rechaza como <c>conciliacion_anterior</c>. El
/// único lock que toma esta transacción es el exclusivo de la marca; cada escritor protegido toma el compartido antes que cualquier
/// otro lock, así que no hay ciclo posible (ver <see cref="GuardaDeConciliacion"/>).
/// </summary>
public class ServicioDeConciliacionDeTesoreria(IWaysDbContext db, IContextoDeUsuario contexto, IRelojDelSistema reloj)
{
    public async Task<ResultadoDeConciliacion> ConciliarAsync(SolicitudDeConciliacion solicitud, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeConciliacionDeTesoreria requiere un actor de tenant; la política de la ruta no admite plataforma.");

        if (!await db.Empresas.AnyAsync(e => e.Id == solicitud.IdEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {solicitud.IdEmpresa}.");
        }

        var hasta = ReglasDeConciliacion.ExigirHasta(solicitud.Hasta);
        var nota = ReglasDeConciliacion.NormalizarNota(solicitud.Nota);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await GuardaDeConciliacion.TomarExclusivoAsync(db, idTenant, solicitud.IdEmpresa, ct);

            var ahora = reloj.Ahora;
            var vigente = await GuardaDeConciliacion.LeerMarcaAsync(db, solicitud.IdEmpresa, ct);
            ReglasDeConciliacion.ExigirMarcaValida(hasta, ahora, vigente);

            var idsDePuntosVenta = await db.PuntosVenta
                .IgnoreQueryFilters(["BajaLogica"])
                .Where(pv => pv.IdEmpresa == solicitud.IdEmpresa)
                .Select(pv => pv.Id)
                .ToListAsync(ct);

            var conTurnoAbierto = await db.TurnosCaja.AnyAsync(
                t => t.Estado == EstadoTurno.Abierto && t.FechaApertura <= hasta && idsDePuntosVenta.Contains(t.IdPuntoVenta), ct);
            if (conTurnoAbierto)
            {
                throw ReglasDeConciliacion.ConTurnoAbierto();
            }

            var marca = new ConciliacionTesoreria
            {
                IdTenant = idTenant,
                IdEmpresa = solicitud.IdEmpresa,
                Hasta = hasta,
                IdUsuario = contexto.UsuarioId,
                CreadoEl = ahora,
                Nota = nota
            };
            db.ConciliacionesTesoreria.Add(marca);
            await db.SaveChangesAsync(ct);

            await transaccion.CommitAsync(ct);

            return new ResultadoDeConciliacion(marca.Id, marca.IdEmpresa, marca.Hasta, marca.Nota, marca.CreadoEl, marca.IdUsuario);
        });
    }
}
