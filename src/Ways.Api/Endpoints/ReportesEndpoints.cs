using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
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

        // stage-11-exportacion-reportes, Slice 2: mismo plomero de /ventas/resumen/export
        // (formato, contexto, tope), sin política propia — hereda LecturaDeReportes por co-locación.
        grupo.MapGet("/compras/por-proveedor/export", async (
            ServicioDeReportesDeEgresos servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var reporte = await servicio.ObtenerComprasPorProveedorAsync(idEmpresa, idPuntoVenta, desde, hasta, ct);

            // ComprasPorProveedor no trae ZonaHoraria en su contrato (a diferencia del resto de
            // los reportes de esta slice) — el rango bucketea por fecha_recepcion sin exponerla,
            // así que el encabezado no puede repetir un valor que la respuesta nunca trae.
            var ctx = ContextoDeExportacionHttp.Construir(usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, "N/A");
            var tabla = ExportacionDeReportes.De(reporte, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("compras_por_proveedor", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary("Export XLSX de /compras/por-proveedor: mismos parámetros y figuras.");

        grupo.MapGet("/gastos/resumen", (
            ServicioDeReportesDeEgresos servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            Granularidad granularidad, CancellationToken ct) =>
            servicio.ObtenerGastosResumenAsync(idEmpresa, idPuntoVenta, desde, hasta, granularidad, ct))
        .WithSummary("Gastos bucketeados por la zona horaria del punto de venta, con desglose por categoría.");

        grupo.MapGet("/gastos/resumen/export", async (
            ServicioDeReportesDeEgresos servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad, string formato,
            CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var resumen = await servicio.ObtenerGastosResumenAsync(idEmpresa, idPuntoVenta, desde, hasta, granularidad, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, resumen.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(resumen, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("gastos_resumen", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary("Export XLSX de /gastos/resumen: mismos parámetros y figuras.");

        grupo.MapGet("/articulos/top", (
            ServicioDeReportesDeArticulos servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            int? limite, CancellationToken ct) =>
            servicio.ObtenerTopArticulosAsync(idEmpresa, idPuntoVenta, desde, hasta, limite, ct))
        .WithSummary(
            "Ranking de artículos por cantidad y monto neto vendido, ordenado por monto " +
            "descendente. Sin costo ni margen: ver /rentabilidad.");

        grupo.MapGet("/articulos/top/export", async (
            ServicioDeReportesDeArticulos servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, int? limite, string formato,
            CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var top = await servicio.ObtenerTopArticulosAsync(idEmpresa, idPuntoVenta, desde, hasta, limite, ct);

            var ctx = ContextoDeExportacionHttp.Construir(usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, top.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(top, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("articulos_top", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary("Export XLSX de /articulos/top: mismos parámetros y figuras.");

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

        // stage-11-exportacion-reportes, Slice 2 (spec rentabilidad-y-comisiones: Rentabilidad And
        // Comisiones Exports Stack LecturaDeRentabilidad And Carry Coverage): re-apila
        // LecturaDeRentabilidad EXACTAMENTE como su ruta fuente — la co-locación por sí sola
        // heredaría solo LecturaDeReportes del MapGroup, así que acá SÍ hace falta declarar la
        // política otra vez (mismo patrón que /rentabilidad, no "sin política propia").
        grupo.MapGet("/rentabilidad/export", async (
            ServicioDeReportesDeRentabilidad servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, bool? incluirEstimados, string formato,
            CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var rentabilidad = await servicio.ObtenerRentabilidadAsync(
                idEmpresa, idPuntoVenta, desde, hasta, incluirEstimados ?? false, ct);

            var textoDeCobertura = ExportacionDeReportes.ArmarTextoDeCobertura(rentabilidad.Cobertura);
            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, rentabilidad.ZonaHoraria, textoDeCobertura);
            var tabla = ExportacionDeReportes.De(rentabilidad, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("rentabilidad", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .RequireAuthorization(Politicas.LecturaDeRentabilidad)
        .WithSummary(
            "Export XLSX de /rentabilidad: mismos parámetros y figuras, con el bloque de cobertura " +
            "de costo repetido en el encabezado.");

        grupo.MapGet("/ventas/por-punto-venta", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, DateOnly desde, DateOnly hasta, CancellationToken ct) =>
            servicio.ObtenerPorPuntoVentaAsync(idEmpresa, desde, hasta, ct))
        .WithSummary("Ventas netas del período agrupadas por punto de venta — una fila por PV, sin idPuntoVenta.");

        // Sin idPuntoVenta (mismo criterio que su ruta fuente): el alcance del nombre de archivo es
        // siempre "todos", nunca un PV puntual — filtrar por el mismo eje que se agrupa sería una
        // contradicción (dto-contract-honesty, igual que en /ventas/por-punto-venta).
        grupo.MapGet("/ventas/por-punto-venta/export", async (
            ServicioDeReportesDeVentas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, DateOnly desde, DateOnly hasta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var reporte = await servicio.ObtenerPorPuntoVentaAsync(idEmpresa, desde, hasta, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, idEmpresa, null, desde, hasta, reporte.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(reporte, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var nombre = NombreDeArchivo.Construir("ventas_por_punto_venta", "todos", desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary("Export XLSX de /ventas/por-punto-venta: mismos parámetros y figuras.");

        grupo.MapGet("/ventas/por-vendedor", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            CancellationToken ct) =>
            servicio.ObtenerPorVendedorAsync(idEmpresa, idPuntoVenta, desde, hasta, ct))
        .WithSummary("Ventas netas del período agrupadas por vendedor (id_empleado emisor).");

        grupo.MapGet("/ventas/por-vendedor/export", async (
            ServicioDeReportesDeVentas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var reporte = await servicio.ObtenerPorVendedorAsync(idEmpresa, idPuntoVenta, desde, hasta, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, reporte.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(reporte, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("ventas_por_vendedor", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary("Export XLSX de /ventas/por-vendedor: mismos parámetros y figuras.");

        grupo.MapGet("/ventas/por-medio-pago", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            CancellationToken ct) =>
            servicio.ObtenerPorMedioPagoAsync(idEmpresa, idPuntoVenta, desde, hasta, ct))
        .WithSummary("Ventas netas del período agrupadas por medio de pago (pagos_comprobante.id_medio_pago).");

        grupo.MapGet("/ventas/por-medio-pago/export", async (
            ServicioDeReportesDeVentas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var reporte = await servicio.ObtenerPorMedioPagoAsync(idEmpresa, idPuntoVenta, desde, hasta, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, reporte.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(reporte, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("ventas_por_medio_pago", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary("Export XLSX de /ventas/por-medio-pago: mismos parámetros y figuras.");

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

        // stage-11-exportacion-reportes, Slice 2: mismo re-apilado de LecturaDeRentabilidad que
        // /rentabilidad/export (spec: Rentabilidad And Comisiones Exports Stack LecturaDeRentabilidad
        // And Carry Coverage) — la etiqueta PROVISIONAL viaja en el encabezado, verbatim con
        // Comisiones.Provisional (siempre true).
        grupo.MapGet("/comisiones/export", async (
            ServicioDeReportesDeVentas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var comisiones = await servicio.ObtenerComisionesAsync(idEmpresa, idPuntoVenta, desde, hasta, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, idEmpresa, idPuntoVenta, desde, hasta, comisiones.ZonaHoraria,
                ExportacionDeReportes.EtiquetaProvisionalComisiones);
            var tabla = ExportacionDeReportes.De(comisiones, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } id ? $"pv{id}" : "todos";
            var nombre = NombreDeArchivo.Construir("comisiones", alcance, desde, hasta);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .RequireAuthorization(Politicas.LecturaDeRentabilidad)
        .WithSummary("Export XLSX de /comisiones: mismos parámetros y figuras, etiquetado PROVISIONAL.");
        // stage-11-exportacion-reportes, Slice 5a (design: G2/G3 — minimal aggregation; spec
        // historico-de-cajas: G2 Histórico Lists Closed Turnos Only, Role Split — Turno Detail
        // Under OperacionDePos, Cross-Turno Views Under LecturaDeReportes): gate heredado del
        // grupo, sin política propia — vista de gestión sobre turnos ajenos, nunca la del cajero.
        grupo.MapGet("/cajas", (
            ServicioDeHistoricoDeCajas servicio, int? idPuntoVenta, DateTimeOffset? desde, DateTimeOffset? hasta,
            int? pagina, int? tamanio, CancellationToken ct) =>
            servicio.ListarCierresAsync(idPuntoVenta, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary(
            "Histórico de cierres (turnos cerrados únicamente): totales sumados de los arqueos ya " +
            "persistidos, nunca re-derivados. Un turno abierto nunca aparece acá.");

        return app;
    }
}
