using Ways.Api.Seguridad;
using Ways.Application.Fiscal;

namespace Ways.Api.Endpoints;

/// <summary>
/// El ABM de certificados fiscales + la carga de condición fiscal de empresa / número fiscal de
/// punto de venta (stage-19a, Slice 4; proposal.md API surface 705-710) bajo
/// <see cref="Politicas.AdministracionFiscal"/> (solo Admin, target 63), MÁS la emisión fiscal en sí
/// (Slice 5: <c>POST /api/fiscal/comprobantes</c> / <c>.../reintentar</c>) bajo
/// <see cref="Politicas.OperacionDePos"/> — grupo de ruta DISTINTO a propósito (spec
/// operacion-de-pos: "Fiscal Emission Stays Under OperacionDePos, Not AdministracionFiscal" — la
/// letra, los totales y el CAE los decide el servidor, el riesgo gateado no es quién aprieta el
/// botón).
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

        // Slice 5: la emisión fiscal en sí — OperacionDePos, NUNCA AdministracionFiscal (spec
        // operacion-de-pos: la letra/totales/CAE los decide el servidor, target 5.24).
        var emision = app.MapGroup("/api/fiscal/comprobantes")
            .WithTags("Fiscal")
            .RequireAuthorization(Politicas.OperacionDePos);

        emision.MapPost("/", async (
            ServicioDeFacturacionFiscal servicio, SolicitudDeEmisionFiscal solicitud, CancellationToken ct) =>
        {
            var emitido = await servicio.EmitirAsync(solicitud, ct);
            return Results.Created($"/api/fiscal/comprobantes/{emitido.Id}", emitido);
        })
        .WithSummary("Emite un comprobante fiscal end-to-end contra WSAA/WSFE (I2/I3/I4).");

        emision.MapPost("/{id:int}/reintentar", async (
            ServicioDeFacturacionFiscal servicio, int id, CancellationToken ct) =>
            Results.Ok(await servicio.ReintentarAsync(id, ct)))
        .WithSummary(
            "Reintenta un comprobante fiscal 'pendiente' — FECompConsultar primero (I2), adopta el " +
            "CAE si ARCA ya lo autorizó.");

        return app;
    }
}
