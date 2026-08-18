using Ways.Api.Seguridad;
using Ways.Application.CuentaCorriente;

namespace Ways.Api.Endpoints;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 4 (design: API Surface, task 4.6): el estado de cuenta
/// paginado del proveedor. Grupo bajo <c>OperacionDePos</c> — sin policy apilada, mismo criterio
/// que <c>CuentaCorrienteEndpoints</c> (cliente): un Vendedor tiene que poder consultar la cuenta
/// corriente de un proveedor. <c>POST /ajustes</c> (Slice 5, design decisión 12) se mapea
/// TOP-LEVEL sobre <c>app</c>, nunca dentro de este grupo — la razón por la que ese endpoint NO
/// vive acá.
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

        return app;
    }
}
