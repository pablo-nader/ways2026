using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
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

        // stage-11-exportacion-reportes, Slice 1b (design decisión 4; spec exportacion-de-
        // reportes: "Export Route Convention And Policy Inheritance By Co-Location"): sibling
        // declarado inmediatamente después de su ruta fuente, dentro del mismo MapGroup — hereda
        // LecturaDeReportes estructuralmente, sin política propia. El plomero de acá (formato,
        // contexto, tope) es el que reusa cada export sibling de las slices siguientes.
        grupo.MapGet("/ventas/resumen/export", async (
            ServicioDeReportesDeVentas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad, string formato,
            CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var resumen = await servicio.ObtenerResumenAsync(idEmpresa, idPuntoVenta, desde, hasta, granularidad, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, resumen.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(resumen, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("ventas_resumen", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de /ventas/resumen: mismos parámetros y figuras, gate LecturaDeReportes " +
            "heredado por co-locación.");

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
        grupo.MapGet("/ventas/por-punto-venta", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, DateOnly desde, DateOnly hasta, CancellationToken ct) =>
            servicio.ObtenerPorPuntoVentaAsync(idEmpresa, desde, hasta, ct))
        .WithSummary("Ventas netas del período agrupadas por punto de venta — una fila por PV, sin idPuntoVenta.");

        grupo.MapGet("/ventas/por-vendedor", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            CancellationToken ct) =>
            servicio.ObtenerPorVendedorAsync(idEmpresa, idPuntoVenta, desde, hasta, ct))
        .WithSummary("Ventas netas del período agrupadas por vendedor (id_empleado emisor).");

        grupo.MapGet("/ventas/por-medio-pago", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            CancellationToken ct) =>
            servicio.ObtenerPorMedioPagoAsync(idEmpresa, idPuntoVenta, desde, hasta, ct))
        .WithSummary("Ventas netas del período agrupadas por medio de pago (pagos_comprobante.id_medio_pago).");

        // stage-10-agregacion-dashboard, Slice 10 (PROVISIONAL — droppable en su totalidad): mismo
        // apilado de políticas que /rentabilidad (design decisión 7).
        grupo.MapGet("/comisiones", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            CancellationToken ct) =>
            servicio.ObtenerComisionesAsync(idEmpresa, idPuntoVenta, desde, hasta, ct))
        .RequireAuthorization(Politicas.LecturaDeRentabilidad)
        .WithSummary(
            "PROVISIONAL: comisión por vendedor = neto vendido × comision_porcentaje (default 0, " +
            "un Admin configura la tasa desde Parámetros). Nada se persiste — calculado on the fly.");

        return app;
    }
}
