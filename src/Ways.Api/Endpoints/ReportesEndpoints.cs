using Ways.Api.Seguridad;
using Ways.Application.Reportes;
using Ways.Domain.Reportes;

namespace Ways.Api.Endpoints;

public static class ReportesEndpoints
{
    public static IEndpointRouteBuilder MapearReportes(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/reportes")
            .WithTags("Reportes")
            .RequireAuthorization(Politicas.LecturaDeReportes);

        grupo.MapGet("/ventas/resumen", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            Granularidad granularidad, CancellationToken ct) =>
            servicio.ObtenerResumenAsync(idEmpresa, idPuntoVenta, desde, hasta, granularidad, ct))
        .WithSummary(
            "Ventas netas bucketeadas por la zona horaria del punto de venta: ticket promedio " +
            "excluye NCX de numerador y denominador.");

        grupo.MapGet("/compras/por-proveedor", (
            ServicioDeReportesDeEgresos servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            CancellationToken ct) =>
            servicio.ObtenerComprasPorProveedorAsync(idEmpresa, idPuntoVenta, desde, hasta, ct))
        .WithSummary("Compras confirmadas dentro del rango, agrupadas por proveedor — borrador y anulada excluidas.");

        grupo.MapGet("/gastos/resumen", (
            ServicioDeReportesDeEgresos servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            Granularidad granularidad, CancellationToken ct) =>
            servicio.ObtenerGastosResumenAsync(idEmpresa, idPuntoVenta, desde, hasta, granularidad, ct))
        .WithSummary("Gastos bucketeados por la zona horaria del punto de venta, con desglose por categoría.");
        grupo.MapGet("/articulos/top", (
            ServicioDeReportesDeArticulos servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            int? limite, CancellationToken ct) =>
            servicio.ObtenerTopArticulosAsync(idEmpresa, idPuntoVenta, desde, hasta, limite, ct))
        .WithSummary(
            "Ranking de artículos por cantidad y monto neto vendido, ordenado por monto " +
            "descendente. Sin costo ni margen: ver /rentabilidad.");
        // stage-10-agregacion-dashboard, Slice 4 (design decisión 7): apila LecturaDeRentabilidad
        // sobre LecturaDeReportes — ASP.NET Core compone políticas con AND, mismo criterio que
        // StockEndpoints ("/ajustes" apilando GestionDeCatalogo sobre OperacionDePos).
        grupo.MapGet("/rentabilidad", (
            ServicioDeReportesDeRentabilidad servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            bool? incluirEstimados, CancellationToken ct) =>
            // bool? (mismo criterio que incluirEliminados/incluirInactivos en el resto de la API):
            // ausente en la query string ⇒ null ⇒ excluido por default (spec: Margin Excludes
            // Estimated Cost Lines By Default), nunca un 400 por parámetro faltante.
            servicio.ObtenerRentabilidadAsync(idEmpresa, idPuntoVenta, desde, hasta, incluirEstimados ?? false, ct))
        .RequireAuthorization(Politicas.LecturaDeRentabilidad)
        .WithSummary(
            "Margen del período: costo estimado excluido por defecto (incluirEstimados=true para " +
            "sumarlo), costo desconocido siempre salteado. Cobertura obligatoria en toda respuesta.");

        return app;
    }
}
