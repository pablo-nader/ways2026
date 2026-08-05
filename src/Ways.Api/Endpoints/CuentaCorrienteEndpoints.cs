using Ways.Api.Seguridad;
using Ways.Application.CuentaCorriente;

namespace Ways.Api.Endpoints;

public static class CuentaCorrienteEndpoints
{
    public static IEndpointRouteBuilder MapearCuentaCorriente(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/clientes/{idCliente:int}/cuenta-corriente")
            .WithTags("CuentaCorriente")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-7-cuenta-corriente (Slice 2, task 2.7, design: API Surface): pago a cuenta — sin
        // GestionDeCatalogo apilado, mismo criterio que POST /api/ventas (un Vendedor tiene que
        // poder cobrar una cuenta corriente).
        grupo.MapPost("/pagos", async (
            ServicioDeCuentaCorriente servicio, int idCliente, SolicitudDePagoACuenta solicitud, CancellationToken ct) =>
        {
            var emitido = await servicio.RegistrarPagoAsync(idCliente, solicitud, ct);
            return Results.Created($"/api/ventas/{emitido.Id}", emitido);
        })
        .WithSummary("Registra un pago a cuenta (RC). Cero items, un único movimiento Pago negativo.");

        return app;
    }
}
