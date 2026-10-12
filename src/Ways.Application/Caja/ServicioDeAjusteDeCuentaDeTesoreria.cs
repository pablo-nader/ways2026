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
/// de turno serializa su cadena (<see cref="EscriturasDeCuentaDeTesoreria.BloquearYLeerSaldoAsync"/>), y lee la cola de la
/// cadena DESPUÉS del lock. En el modo "saldo objetivo" la diferencia se calcula con ese saldo bajo lock, no con el que la
/// pantalla mostraba. Como el precedente del ajuste manual de cuenta corriente, no hay clave de idempotencia: un reenvío
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

        if (!await db.CuentasTesoreria.AnyAsync(c => c.Id == idCuenta, ct))
        {
            throw SinCuenta(idCuenta);
        }

        var concepto = ReglasDeAjusteDeCuentaTesoreria.NormalizarConcepto(solicitud.Concepto);
        ReglasDeAjusteDeCuentaTesoreria.ValidarSolicitud(solicitud.Importe, solicitud.SaldoObjetivo);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        var movimiento = await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var conexion = db.Database.GetDbConnection();
            var transaccionCruda = transaccion.GetDbTransaction();

            var saldoActual = await EscriturasDeCuentaDeTesoreria.BloquearYLeerSaldoAsync(
                db, conexion, transaccionCruda, idTenant, idCuenta, ct) ?? throw SinCuenta(idCuenta);

            var ajuste = ReglasDeAjusteDeCuentaTesoreria.Resolver(saldoActual, solicitud.Importe, solicitud.SaldoObjetivo);

            var fila = new MovimientoCuenta
            {
                IdTenant = idTenant,
                IdCuentaTesoreria = idCuenta,
                Fecha = reloj.Ahora,
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
            await transaccion.CommitAsync(ct);

            return fila;
        });

        var nombreEmpleado = await db.Usuarios
            .Where(u => u.Id == idEmpleado)
            .Select(u => u.NombreUsuario)
            .FirstOrDefaultAsync(ct);

        return new ResultadoDeAjusteDeCuenta(
            new MovimientoDeCuentaListado(
                movimiento.Id, movimiento.Fecha, movimiento.Tipo, movimiento.Concepto, movimiento.Ingreso,
                movimiento.Egreso, movimiento.Final, IdTurnoCaja: null, IdMedioPago: null, NombreMedioPago: null,
                movimiento.IdEmpleado, nombreEmpleado, BaseBruta: null, PorcentajeAplicado: null),
            movimiento.Final);
    }

    private static ErrorDominio SinCuenta(int idCuenta) =>
        ErrorDominio.NoEncontrado($"No existe la cuenta de tesorería {idCuenta}.");
}
