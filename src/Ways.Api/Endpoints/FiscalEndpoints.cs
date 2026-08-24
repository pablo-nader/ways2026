using Ways.Api.Seguridad;
using Ways.Application.Fiscal;

namespace Ways.Api.Endpoints;

/// <summary>
/// El ABM de certificados fiscales + la carga de condición fiscal de empresa / número fiscal de
/// punto de venta (stage-19a, Slice 4; proposal.md API surface 705-710) — las TRES rutas van bajo
/// <see cref="Politicas.AdministracionFiscal"/> (solo Admin, target 63). La emisión fiscal en sí
/// (<c>POST /api/fiscal/comprobantes</c>, <c>.../reintentar</c>) llega en Slice 5, bajo
/// <see cref="Politicas.OperacionDePos"/> — un grupo de ruta distinto, esta clase solo mapea la
/// mitad de configuración de esta slice.
/// </summary>
public static class FiscalEndpoints
{
    public static IEndpointRouteBuilder MapearFiscal(this IEndpointRouteBuilder app)
    {
        var certificados = app.MapGroup("/api/fiscal/certificados")
            .WithTags("Fiscal")
            .RequireAuthorization(Politicas.AdministracionFiscal);

        certificados.MapPost("/", async (
            ServicioDeCertificados servicio, RegistroDeCertificadoFiscal datos, CancellationToken ct) =>
        {
            var creado = await servicio.RegistrarAsync(datos, ct);
            return Results.Created($"/api/fiscal/certificados/{creado.Id}", creado);
        })
        .WithSummary("Registra (o rota, si ya hay uno activo) un certificado fiscal.");

        certificados.MapGet("/", (ServicioDeCertificados servicio, CancellationToken ct) =>
            servicio.ListarAsync(ct))
        .WithSummary("Lista los certificados fiscales — nunca expone material de clave.");

        certificados.MapDelete("/{id:int}", async (
            ServicioDeCertificados servicio, int id, CancellationToken ct) =>
        {
            await servicio.DesactivarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Desactiva un certificado fiscal.");

        var fiscal = app.MapGroup("/api/fiscal")
            .WithTags("Fiscal")
            .RequireAuthorization(Politicas.AdministracionFiscal);

        fiscal.MapPut("/empresas/{id:int}/condicion-fiscal", async (
            ServicioDeCertificados servicio, int id, CondicionFiscalDeEmpresaEdicion datos, CancellationToken ct) =>
        {
            await servicio.ActualizarCondicionFiscalDeEmpresaAsync(id, datos.IdCondicionFiscal, ct);
            return Results.NoContent();
        })
        .WithSummary("Carga la condición fiscal (ARCA) del emisor sobre una empresa.");

        fiscal.MapPut("/puntos-venta/{id:int}/numero-fiscal", async (
            ServicioDeCertificados servicio, int id, NumeroFiscalDePuntoVentaEdicion datos, CancellationToken ct) =>
        {
            await servicio.ActualizarNumeroFiscalDePuntoVentaAsync(id, datos.NumeroFiscal, ct);
            return Results.NoContent();
        })
        .WithSummary("Carga el punto de venta ARCA de un punto de venta interno.");

        return app;
    }
}
