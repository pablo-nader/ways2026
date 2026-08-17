using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Exportacion;
using Ways.Domain.Compras;

namespace Ways.Api.Endpoints;

/// <summary>
/// Comprobantes de compra (design: API Surface) — lecturas bajo <c>OperacionDePos</c>, toda
/// escritura apila <c>GestionDeCatalogo</c> (Admin-only): el ciclo de vida entero de una compra
/// mueve dinero y stock, mismo criterio que <c>POST /api/stock/ajustes</c>.
/// </summary>
public static class ComprasEndpoints
{
    public static IEndpointRouteBuilder MapearCompras(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/compras")
            .WithTags("Compras")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDeCompras servicio,
            int? idProveedor,
            EstadoCompra? estado,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idProveedor, estado, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista comprobantes de compra con filtros y paginado.");

        // stage-11-exportacion-reportes (Slice 3, design decisión 7): sibling declarado
        // inmediatamente después de su ruta fuente — hereda OperacionDePos por co-locación.
        // Sin idPuntoVenta (el listado JSON tampoco lo tiene): Empresa/zona usan el default de
        // AlcanceDeListadoHttp, no una consulta nueva.
        grupo.MapGet("/export", async (
            ServicioDeCompras servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int? idProveedor, EstadoCompra? estado, DateTimeOffset desde, DateTimeOffset hasta, string formato,
            CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var filas = await servicio.ListarParaExportacionAsync(
                idProveedor, estado, desde, hasta, opciones.Value.TopeDeFilas, ct);

            var zona = TimeZoneInfo.FindSystemTimeZoneById(AlcanceDeListadoHttp.ZonaPorDefecto);
            var (desdeFecha, hastaFecha) = FechaDelRango.De(desde, hasta);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, "Todas", puntoVenta: null, desdeFecha, hastaFecha, AlcanceDeListadoHttp.ZonaPorDefecto);
            var tabla = ExportacionDeListados.De(filas, ctx, zona);

            var bytes = exportador.Generar(tabla);
            var nombre = NombreDeArchivo.Construir("compras_listado", "todas", desdeFecha, hastaFecha);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de GET /api/compras: mismo filtro, sin paginado bajo el tope, gate " +
            "OperacionDePos heredado por co-locación.");

        grupo.MapGet("/{id:int}", (ServicioDeCompras servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Detalle de una compra: header + items + precioSugerido por item.");

        grupo.MapPost("/", async (ServicioDeCompras servicio, SolicitudDeCompra solicitud, CancellationToken ct) =>
        {
            var creada = await servicio.CrearBorradorAsync(solicitud, ct);
            return Results.Created($"/api/compras/{creada.Id}", creada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea un borrador de compra (header + items opcionales).");

        grupo.MapPut("/{id:int}", async (ServicioDeCompras servicio, int id, SolicitudDeCompra solicitud, CancellationToken ct) =>
        {
            var actualizada = await servicio.ActualizarBorradorAsync(id, solicitud, ct);
            return Results.Ok(actualizada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Reemplaza el header y el set completo de items de un borrador (borrador únicamente).");

        grupo.MapPost("/{id:int}/confirmar", async (ServicioDeCompras servicio, int id, CancellationToken ct) =>
        {
            var confirmada = await servicio.ConfirmarAsync(id, ct);
            return Results.Ok(confirmada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Confirma un borrador: entra el stock, se actualiza costo_nominal, se congela precio_sugerido.");

        grupo.MapPost("/{id:int}/anular", async (ServicioDeCompras servicio, int id, CancellationToken ct) =>
        {
            var resultado = await servicio.AnularAsync(id, ct);
            return Results.Ok(resultado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Anula una compra confirmada: contramovimientos de stock, nunca revierte costo_nominal.");

        grupo.MapPost("/{id:int}/precios", async (
            ServicioDeCompras servicio, int id, SolicitudDeAplicarPrecios solicitud, CancellationToken ct) =>
        {
            var resultados = await servicio.AplicarPrecioSugeridoAsync(id, solicitud, ct);
            return Results.Ok(resultados);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Aplica precio_sugerido por item vía ServicioDePrecios, per-line results.");

        return app;
    }
}
