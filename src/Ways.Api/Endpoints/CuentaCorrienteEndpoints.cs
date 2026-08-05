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

        // stage-7-cuenta-corriente (Slice 3, task 3.5, design: API Surface): preview — la MISMA
        // ReliquidadorDeConsumos que el commit, sin lock, nunca autoritativo.
        // SupervisionDeCuentaCorriente apilado sobre OperacionDePos (mismo patrón que
        // GestionDeCatalogo en ArticulosEndpoints): con SupervisionDeCuentaCorriente ⊆
        // OperacionDePos, el AND de las dos policies deja el efecto neto en Supervisor+Admin.
        grupo.MapGet("/reliquidacion", async (
            ServicioDeReliquidacion servicio, int idCliente, CancellationToken ct) =>
            Results.Ok(await servicio.PreviewAsync(idCliente, ct)))
        .RequireAuthorization(Politicas.SupervisionDeCuentaCorriente)
        .WithSummary("Preview de reliquidación a precio del día — mismo cálculo que el commit, sin lock, nunca autoritativo.");

        // stage-7-cuenta-corriente (Slice 3, task 3.5): commit — irreversible, sin turno (design
        // decisión 4).
        grupo.MapPost("/reliquidacion", async (
            ServicioDeReliquidacion servicio, int idCliente, SolicitudDeReliquidacion solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.EjecutarAsync(idCliente, solicitud, ct)))
        .RequireAuthorization(Politicas.SupervisionDeCuentaCorriente)
        .WithSummary("Ejecuta la reliquidación a precio del día — irreversible, sin turno.");

        return app;
    }
}
