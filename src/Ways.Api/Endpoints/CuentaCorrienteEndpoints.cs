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

        // stage-7-cuenta-corriente (Slice 4, task 4.4, design: API Surface): ajuste manual — sin
        // turno, mismo criterio que la reliquidación. SupervisionDeCuentaCorriente apilado sobre
        // OperacionDePos (mismo patrón que las dos rutas de reliquidación de arriba).
        grupo.MapPost("/ajustes", async (
            ServicioDeCuentaCorriente servicio, int idCliente, SolicitudDeAjuste solicitud, CancellationToken ct) =>
        {
            var movimiento = await servicio.RegistrarAjusteAsync(idCliente, solicitud, ct);
            return Results.Created($"/api/clientes/{idCliente}/cuenta-corriente", movimiento);
        })
        .RequireAuthorization(Politicas.SupervisionDeCuentaCorriente)
        .WithSummary("Registra un ajuste manual de cuenta corriente — importe con signo, detalle obligatorio.");

        // stage-7-cuenta-corriente (Slice 4, task 4.4, design: API Surface): estado de cuenta —
        // header + movimientos en un único GET, bajo OperacionDePos (el grupo entero, sin policy
        // apilada — un Vendedor tiene que poder consultar la cuenta corriente de un cliente).
        grupo.MapGet("/", async (
            ServicioDeCuentaCorriente servicio, int idCliente, DateTimeOffset? desde, DateTimeOffset? hasta,
            bool? historico, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerEstadoDeCuentaAsync(idCliente, desde, hasta, historico ?? false, ct)))
        .WithSummary("Estado de cuenta: header (saldo/acuerdo/disponibilidad) + movimientos con saldo corrido.");

        return app;
    }
}
