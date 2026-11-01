using Ways.Api.Seguridad;
using Ways.Application.Caja;

namespace Ways.Api.Endpoints;

public static class CuentasTesoreriaEndpoints
{
    public static IEndpointRouteBuilder MapearCuentasTesoreria(this IEndpointRouteBuilder app)
    {
        // Un solo grupo bajo GestionDeCatalogo, lectura incluida: la pantalla es de configuración
        // y expone saldos, y el Supervisor consulta la tesorería por los reportes.
        var grupo = app.MapGroup("/api/cuentas-tesoreria")
            .WithTags("Cuentas de tesorería")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupo.MapGet("/", (
            ServicioDeCuentasDeTesoreria servicio, int? idEmpresa, CancellationToken ct) =>
            servicio.ListarAsync(idEmpresa, ct))
        .WithSummary("Lista las cuentas de tesorería, activas e inactivas, con su saldo actual; filtro opcional por empresa.");

        grupo.MapGet("/{id:int}", (ServicioDeCuentasDeTesoreria servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene una cuenta de tesorería.");

        grupo.MapPost("/", async (
            ServicioDeCuentasDeTesoreria servicio, AltaDeCuentaTesoreria datos, CancellationToken ct) =>
        {
            var creada = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/cuentas-tesoreria/{creada.Id}", creada);
        })
        .WithSummary("Crea una cuenta de tesorería en una empresa; el nombre no se repite entre las cuentas vivas de la empresa.");

        grupo.MapPut("/{id:int}", (
            ServicioDeCuentasDeTesoreria servicio, int id, EdicionDeCuentaTesoreria datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary("Actualiza nombre, estado y saldo inicial; el saldo inicial no cambia una vez que la cuenta tiene movimientos.");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeCuentasDeTesoreria servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Baja lógica de la cuenta, solo si ningún medio de pago ni movimiento la referencia.");

        // Solo Admin, igual que el resto del grupo: el saldo de una cuenta de tesorería es un dato del dueño. Si más
        // adelante lo ajusta un Supervisor, se cambia acá con una política propia.
        grupo.MapPost("/{id:int}/ajustes", async (
            ServicioDeAjusteDeCuentaDeTesoreria servicio, int id, SolicitudDeAjusteDeCuenta solicitud, CancellationToken ct) =>
        {
            var resultado = await servicio.AjustarAsync(id, solicitud, ct);
            return Results.Created($"/api/reportes/tesoreria/cuentas/{id}/libro", resultado);
        })
        .WithSummary(
            "Ajuste manual del saldo de una cuenta: concepto obligatorio y exactamente uno de importe con signo o saldo " +
            "objetivo (la diferencia se calcula bajo el lock de la cuenta).");

        // Solo Admin, por la política del grupo. La vista previa es un POST porque lleva cuerpo, pero no escribe nada.
        grupo.MapPost("/reconstruccion/vista-previa", (
            ServicioDeReconstruccionDeCuentas servicio, SolicitudDeReconstruccionDeCuentas solicitud, CancellationToken ct) =>
            servicio.VistaPreviaAsync(solicitud, ct))
        .WithSummary(
            "Vista previa de la reconstrucción del historial: los cobros de turnos cerrados en el rango (fecha de cierre en la zona " +
            "de la empresa, hasta 366 días) que se acreditarían en las cuentas actuales de sus medios. No escribe.");

        grupo.MapPost("/reconstruccion", (
            ServicioDeReconstruccionDeCuentas servicio, SolicitudDeReconstruccionDeCuentas solicitud, CancellationToken ct) =>
            servicio.AplicarAsync(solicitud, ct))
        .WithSummary(
            "Reconstruye el historial: acredita con la fecha de cierre del turno lo que no se acreditó porque el medio no tenía " +
            "cuenta, y recalcula inicio y final de las filas posteriores de cada cuenta. Una segunda corrida no escribe nada.");

        return app;
    }
}
