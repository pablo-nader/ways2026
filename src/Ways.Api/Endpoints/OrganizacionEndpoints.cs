using Ways.Api.Seguridad;
using Ways.Application.Organizacion;

namespace Ways.Api.Endpoints;

/// <summary>
/// Lectura y edición de organización (doc 09): tenants (plataforma-only, mismo grupo de ruta
/// que <see cref="AprovisionamientoEndpoints"/> pero mapeado desde acá — ASP.NET Core admite
/// varios <c>MapGroup</c> sobre el mismo prefijo), empresas y puntos de venta (plataforma
/// ve/edita cualquiera, un admin de tenant ve/edita solo los propios —
/// <see cref="Politicas.GestionDeOrganizacion"/>). Alta y baja siguen siendo plataforma-only
/// vía <see cref="ServicioDeAprovisionamiento"/> (ADR-16) — acá no hay <c>POST</c> ni
/// <c>DELETE</c> a propósito.
/// </summary>
public static class OrganizacionEndpoints
{
    public static IEndpointRouteBuilder MapearOrganizacion(this IEndpointRouteBuilder app)
    {
        var tenants = app.MapGroup("/api/plataforma/tenants")
            .WithTags("Organización")
            .RequireAuthorization(Politicas.SoloPlataforma);

        tenants.MapGet("/", (ServicioDeOrganizacion servicio, CancellationToken ct) =>
            servicio.ListarTenantsAsync(ct))
        .WithSummary("Lista todos los tenants.");

        tenants.MapGet("/{id:int}", (ServicioDeOrganizacion servicio, int id, CancellationToken ct) =>
            servicio.ObtenerTenantAsync(id, ct))
        .WithSummary("Obtiene un tenant.");

        tenants.MapPut("/{id:int}", (
            ServicioDeOrganizacion servicio, int id, TenantEdicion datos, CancellationToken ct) =>
            servicio.ActualizarTenantAsync(id, datos, ct))
        .WithSummary("Actualiza el nombre de un tenant.");

        tenants.MapPost("/{id:int}/suspender", (ServicioDeOrganizacion servicio, int id, CancellationToken ct) =>
            servicio.SuspenderTenantAsync(id, ct))
        .WithSummary("Suspende un tenant: sus usuarios pierden la sesión en el próximo request.");

        tenants.MapPost("/{id:int}/reactivar", (ServicioDeOrganizacion servicio, int id, CancellationToken ct) =>
            servicio.ReactivarTenantAsync(id, ct))
        .WithSummary("Reactiva un tenant suspendido.");

        var empresas = app.MapGroup("/api/empresas")
            .WithTags("Organización")
            .RequireAuthorization(Politicas.GestionDeOrganizacion);

        empresas.MapGet("/", (ServicioDeOrganizacion servicio, CancellationToken ct) =>
            servicio.ListarEmpresasAsync(ct))
        .WithSummary("Lista empresas: plataforma ve todas, un admin de tenant ve las propias.");

        empresas.MapGet("/{id:int}", (ServicioDeOrganizacion servicio, int id, CancellationToken ct) =>
            servicio.ObtenerEmpresaAsync(id, ct))
        .WithSummary("Obtiene una empresa.");

        empresas.MapPut("/{id:int}", (
            ServicioDeOrganizacion servicio, int id, EmpresaEdicion datos, CancellationToken ct) =>
            servicio.ActualizarEmpresaAsync(id, datos, ct))
        .WithSummary("Actualiza los datos descriptivos de una empresa.");

        var puntosVenta = app.MapGroup("/api/puntos-venta")
            .WithTags("Organización")
            .RequireAuthorization(Politicas.GestionDeOrganizacion);

        puntosVenta.MapGet("/", (ServicioDeOrganizacion servicio, CancellationToken ct) =>
            servicio.ListarPuntosVentaAsync(ct))
        .WithSummary("Lista puntos de venta: plataforma ve todos, un admin de tenant ve los propios.");

        puntosVenta.MapGet("/{id:int}", (ServicioDeOrganizacion servicio, int id, CancellationToken ct) =>
            servicio.ObtenerPuntoVentaAsync(id, ct))
        .WithSummary("Obtiene un punto de venta.");

        puntosVenta.MapPut("/{id:int}", (
            ServicioDeOrganizacion servicio, int id, PuntoVentaEdicion datos, CancellationToken ct) =>
            servicio.ActualizarPuntoVentaAsync(id, datos, ct))
        .WithSummary("Actualiza los datos descriptivos de un punto de venta.");

        return app;
    }
}
