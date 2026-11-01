using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Application.Caja;

/// <summary>Cuerpo del ajuste manual del efectivo: la empresa dueña del fondo, el concepto y el importe con signo.</summary>
public sealed record SolicitudDeAjusteDeEfectivo(int IdEmpresa, string? Concepto, decimal? Importe);

public sealed record ResultadoDeAjusteDeEfectivo(int IdMovimiento, DateTimeOffset Fecha, string Concepto, decimal Importe);

/// <summary>
/// Ajuste manual del fondo de efectivo de tesorería de una empresa: escribe UNA fila <see cref="TipoMovimientoTesoreria.AjusteManual"/>
/// en la cadena de la empresa, sin punto de venta, turno ni pago. La autorización (Admin) se aplica en la capa de API.
///
/// Sin reintento: toma el advisory lock de la empresa (el mismo con el que serializan el cierre y los pagos de origen
/// tesorería) y recién después agrega la fila encadenada con <see cref="EscriturasDeTesoreria.ApendearAsync"/>, que lee la cola
/// de la cadena bajo ese lock. No hay clave de idempotencia: un reenvío escribiría dos filas; la protección es la guarda de
/// re-entrada de la pantalla. Orden: 404 de la empresa y recién después la validación del cuerpo.
/// </summary>
public class ServicioDeAjusteDeEfectivoDeTesoreria(IWaysDbContext db, IContextoDeUsuario contexto, IRelojDelSistema reloj)
{
    public async Task<ResultadoDeAjusteDeEfectivo> AjustarAsync(SolicitudDeAjusteDeEfectivo solicitud, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeAjusteDeEfectivoDeTesoreria requiere un actor de tenant; la política de la ruta no admite plataforma.");
        var idEmpleado = contexto.UsuarioId;

        if (!await db.Empresas.AnyAsync(e => e.Id == solicitud.IdEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {solicitud.IdEmpresa}.");
        }

        var concepto = ReglasDeAjusteDeCuentaTesoreria.NormalizarConcepto(solicitud.Concepto);
        var importe = ReglasDeAjusteDeEfectivo.ValidarImporte(solicitud.Importe);
        var (ingreso, egreso) = ReglasDeAjusteDeEfectivo.Dividir(importe);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            // Primer lock de la transacción (ver GuardaDeConciliacion).
            await GuardaDeConciliacion.TomarCompartidoAsync(db, idTenant, solicitud.IdEmpresa, ct);

            await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(
                db.Database.GetDbConnection(), transaccion.GetDbTransaction(), idTenant, solicitud.IdEmpresa, ct);

            var fila = await EscriturasDeTesoreria.ApendearAsync(
                db, idTenant, solicitud.IdEmpresa, idPuntoVenta: null, reloj.Ahora, TipoMovimientoTesoreria.AjusteManual,
                idTurnoCaja: null, idPago: null, concepto, ingreso, egreso, idEmpleado, ct);

            // Un ajuste fechado en o antes de la marca (solo si se confirmó entre el reloj y el commit) se deshace.
            await GuardaDeConciliacion.ExigirNoConciliadoAsync(db, idTenant, solicitud.IdEmpresa, fila.Fecha, ct);

            await transaccion.CommitAsync(ct);

            return new ResultadoDeAjusteDeEfectivo(fila.Id, fila.Fecha, fila.Concepto, importe);
        });
    }
}
