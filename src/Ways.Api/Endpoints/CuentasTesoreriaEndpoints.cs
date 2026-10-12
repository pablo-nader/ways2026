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

        return app;
    }
}
