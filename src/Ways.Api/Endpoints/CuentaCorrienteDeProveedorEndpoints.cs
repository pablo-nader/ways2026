using Ways.Api.Seguridad;
using Ways.Application.CuentaCorriente;

namespace Ways.Api.Endpoints;

/// <summary>
/// stage-15-cc-proveedores-ledger. Slice 4 (design: API Surface, task 4.6): el estado de cuenta
/// paginado del proveedor. Grupo bajo <c>OperacionDePos</c> — sin policy apilada, mismo criterio
/// que <c>CuentaCorrienteEndpoints</c> (cliente): un Vendedor tiene que poder consultar la cuenta
/// corriente de un proveedor. Slice 5 (design decisión 12, task 5.5): <c>POST /ajustes</c> se
/// mapea TOP-LEVEL sobre <c>app</c>, nunca sobre <c>grupo</c> — apilarlo ahí compondría
/// <c>SupervisionDeCuentaDeProveedor</c> (AND) sobre <c>OperacionDePos</c> y dejaría el efecto neto
/// idéntico (Supervisor+Admin ⊆ OperacionDePos), pero como una composición implícita en vez de una
/// policy propia — exactamente el trap que <c>ProveedoresEndpoints.cs:50-61</c> (<c>/saldo</c>)
/// documenta y que el proposal (decisión 8) rechaza explícitamente para este endpoint.
/// </summary>
public static class CuentaCorrienteDeProveedorEndpoints
{
    public static IEndpointRouteBuilder MapearCuentaCorrienteDeProveedor(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/proveedores/{idProveedor:int}/cuenta-corriente")
            .WithTags("CuentaCorrienteDeProveedor")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", async (
            ServicioDeCuentaCorrienteDeProveedor servicio, int idProveedor, DateTimeOffset? desde,
            DateTimeOffset? hasta, bool? historico, int? pagina, int? tamanio, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerEstadoDeCuentaAsync(
                idProveedor, desde, hasta, historico ?? false, pagina ?? 1, tamanio ?? 25, ct)))
        .WithSummary("Estado de cuenta del proveedor: header + movimientos paginados con saldo corrido.");

        // Slice 5 (task 5.5, mutation target #27): TOP-LEVEL sobre `app`, bajo
        // SupervisionDeCuentaDeProveedor SOLA — una policy, un gate, sin composición que razonar.
        app.MapPost("/api/proveedores/{idProveedor:int}/cuenta-corriente/ajustes", async (
            ServicioDeCuentaCorrienteDeProveedor servicio, int idProveedor, SolicitudDeAjusteDeProveedor solicitud,
            CancellationToken ct) =>
        {
            var movimiento = await servicio.RegistrarAjusteAsync(idProveedor, solicitud, ct);
            return Results.Created($"/api/proveedores/{idProveedor}/cuenta-corriente", movimiento);
        })
        .WithTags("CuentaCorrienteDeProveedor")
        .RequireAuthorization(Politicas.SupervisionDeCuentaDeProveedor)
        .WithSummary("Registra un ajuste manual de la cuenta corriente del proveedor — importe con signo, detalle obligatorio.");

        return app;
    }
}
