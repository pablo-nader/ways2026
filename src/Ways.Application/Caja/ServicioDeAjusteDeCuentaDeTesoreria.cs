using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Application.Caja;

/// <summary>
/// Ajuste manual del saldo de una cuenta de tesorería (doc 10 §7): escribe UNA fila <c>ajuste</c> en el libro de la cuenta,
/// sin turno ni medio, para conciliar con lo que informa la plataforma. La autorización (Admin) se aplica en la capa de API.
///
/// Sin reintento: la transacción toma <c>FOR NO KEY UPDATE</c> sobre la fila de la cuenta, el mismo lock con el que el cierre
/// de turno serializa su cadena (<see cref="EscriturasDeCuentaDeTesoreria.BloquearYLeerColaAsync"/>), y lee la cola de la
/// cadena DESPUÉS del lock. En el modo "saldo objetivo" la diferencia se calcula con ese saldo bajo lock, no con el que la
/// pantalla mostraba. El lock compartido de conciliación es el primero de la transacción y, bajo él, un ajuste fechado en o antes
/// de la marca de la empresa responde 409 <c>periodo_conciliado</c> (solo alcanzable si la marca se confirma entre el reloj y el
/// commit). Como el precedente del ajuste manual de cuenta corriente, no hay clave de idempotencia: un reenvío
/// del mismo importe escribiría dos filas (un reenvío del mismo saldo objetivo no, porque la segunda diferencia da cero y
/// responde 409 <c>ajuste_sin_diferencia</c>); la protección es la guarda de re-entrada de la pantalla.
/// </summary>
public class ServicioDeAjusteDeCuentaDeTesoreria(IWaysDbContext db, IContextoDeUsuario contexto, IRelojDelSistema reloj)
{
    /// <summary>Orden: 404 de la cuenta, validación del cuerpo y, bajo lock, la diferencia contra el saldo vigente.</summary>
    public async Task<ResultadoDeAjusteDeCuenta> AjustarAsync(
        int idCuenta, SolicitudDeAjusteDeCuenta solicitud, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeAjusteDeCuentaDeTesoreria requiere un actor de tenant; la política de la ruta no admite plataforma.");
        var idEmpleado = contexto.UsuarioId;

        var idEmpresa = await db.CuentasTesoreria
            .Where(c => c.Id == idCuenta)
            .Select(c => (int?)c.IdEmpresa)
            .FirstOrDefaultAsync(ct) ?? throw SinCuenta(idCuenta);

        var concepto = ReglasDeAjusteDeCuentaTesoreria.NormalizarConcepto(solicitud.Concepto);
        ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(solicitud.Importe, solicitud.SaldoObjetivo);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        var movimiento = await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            // Primer lock de la transacción (ver GuardaDeConciliacion): una marca que se confirma entre el reloj y el commit
            // no puede dejar un ajuste fechado en o antes de ella.
            await GuardaDeConciliacion.TomarCompartidoAsync(db, idTenant, idEmpresa, ct);

            var conexion = db.Database.GetDbConnection();
            var transaccionCruda = transaccion.GetDbTransaction();

            var cola = await EscriturasDeCuentaDeTesoreria.BloquearYLeerColaAsync(
                db, conexion, transaccionCruda, idTenant, idCuenta, ct) ?? throw SinCuenta(idCuenta);

            var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(cola.Saldo, solicitud.Importe, solicitud.SaldoObjetivo);

            var fecha = EscriturasDeCuentaDeTesoreria.FechaMonotona(reloj.Ahora, cola.Fecha);
            await GuardaDeConciliacion.ExigirNoConciliadoAsync(db, idTenant, idEmpresa, fecha, ct);

            var fila = new MovimientoCuenta
            {
                IdTenant = idTenant,
                IdCuentaTesoreria = idCuenta,
                Fecha = fecha,
                Tipo = TipoMovimientoCuenta.Ajuste,
                Concepto = concepto,
                Inicio = ajuste.Inicio,
                Ingreso = ajuste.Ingreso,
                Egreso = ajuste.Egreso,
                Final = ajuste.Final,
                IdEmpleado = idEmpleado
            };

            db.MovimientosCuenta.Add(fila);
            await db.SaveChangesAsync(ct);

            // Todo lo que arma la respuesta se resuelve antes del commit: un fallo acá deshace el ajuste en vez de
            // responder 5xx por una escritura que ya quedó confirmada.
            var nombreEmpleado = await db.Usuarios
                .Where(u => u.Id == idEmpleado)
                .Select(u => u.NombreUsuario)
                .FirstOrDefaultAsync(ct);

            var resultado = new ResultadoDeAjusteDeCuenta(
                new MovimientoDeCuentaListado(
                    fila.Id, fila.Fecha, fila.Tipo, fila.Concepto, fila.Ingreso, fila.Egreso, fila.Final,
                    IdTurnoCaja: null, IdMedioPago: null, NombreMedioPago: null, fila.IdEmpleado, nombreEmpleado,
                    BaseBruta: null, PorcentajeAplicado: null),
                fila.Final);

            await transaccion.CommitAsync(ct);

            return resultado;
        });

        return movimiento;
    }

    private static ErrorDominio SinCuenta(int idCuenta) =>
        ErrorDominio.NoEncontrado($"No existe la cuenta de tesorería {idCuenta}.");
}
