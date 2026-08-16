using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Exportacion;
using Ways.Application.Parametros;
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

        // stage-11-exportacion-reportes, Slice 9 (proposal decisión 10, droppable a Etapa 13;
        // spec reportes-de-gestion: Existencias Report Joins Stock To Artículos Under The Same
        // Gate): gate heredado del grupo, sin política propia. Sin idArticulo (a diferencia de
        // GET /api/stock) y sin idEmpresa (mismo criterio que /tesoreria): la ruta solo pide el
        // punto de venta.
        grupo.MapGet("/stock/existencias", (
            ServicioDeReportesDeStock servicio, int idPuntoVenta, CancellationToken ct) =>
            servicio.ObtenerExistenciasAsync(idPuntoVenta, ct))
        .WithSummary(
            "Existencias de un punto de venta: stock ⋈ articulos, sin idArticulo requerido — a " +
            "diferencia de GET /api/stock, que exige el par completo.");

        // Sibling declarado inmediatamente después de su ruta fuente — hereda LecturaDeReportes
        // por co-locación. AGREGADO (design decisión 6): la guarda corre sobre
        // TablaExportable.Filas.Count ya mapeada, sin COUNT(*) propio. Sin desde/hasta: el stock
        // no tiene dimensión temporal, así que el encabezado y el nombre de archivo usan la
        // fecha del servidor (hoy) para ambos extremos del rango — mismo criterio "ids, no
        // nombres" que el resto de NombreDeArchivo, adaptado a un reporte sin rango real.
        grupo.MapGet("/stock/existencias/export", async (
            ServicioDeReportesDeStock servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            int idPuntoVenta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var existencias = await servicio.ObtenerExistenciasAsync(idPuntoVenta, ct);

            var (empresa, zonaId) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);
            var hoy = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(reloj.Ahora, TimeZoneInfo.FindSystemTimeZoneById(zonaId)).Date);

            var ctx = ContextoDeExportacionHttp.Construir(usuario, reloj, empresa, $"PV {idPuntoVenta}", hoy, hoy, zonaId);
            var tabla = ExportacionDeReportes.De(existencias, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var nombre = NombreDeArchivo.Construir("existencias", $"pv{idPuntoVenta}", hoy, hoy);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de /stock/existencias: mismas filas, encabezado fechado a hoy (reporte " +
            "de estado actual, sin rango).");

        // stage-12-lotes-vencimientos, Slice 13 (design decisión 15/16, spec lotes-y-vencimientos:
        // "Vencimientos Report Resolves 'Hoy' In The Punto De Venta's Own Zona Horaria, With An
        // Export Sibling"): gate heredado del grupo (LecturaDeReportes). "hoy" y
        // dias_alerta_vencimiento se resuelven DENTRO del servicio (ResolverContextoAsync) —
        // gobiernan tanto este JSON como /resumen y /export, nunca solo un encabezado HTTP.
        grupo.MapGet("/stock/vencimientos", (
            ServicioDeReportesDeStock servicio, int idPuntoVenta, int? dias, CancellationToken ct) =>
            servicio.ObtenerVencimientosAsync(idPuntoVenta, dias, ct))
        .WithSummary(
            "Lotes con saldo positivo de un punto de venta, clasificados vencido/por_vencer/" +
            "vigente/sin_fecha en la zona horaria del punto de venta — dias por defecto: " +
            "dias_alerta_vencimiento.");

        // Tile de Tablero (design: API Surface) — reusa la misma clasificación que el JSON de
        // arriba, nunca una segunda query de agregación (ObtenerResumenDeVencimientosAsync).
        grupo.MapGet("/stock/vencimientos/resumen", (
            ServicioDeReportesDeStock servicio, int idPuntoVenta, CancellationToken ct) =>
            servicio.ObtenerResumenDeVencimientosAsync(idPuntoVenta, ct))
        .WithSummary("Tile de Tablero: conteos de vencido/por_vencer/sin_fecha del punto de venta.");

        // Sibling declarado inmediatamente después de su ruta fuente — hereda LecturaDeReportes
        // por co-locación. LISTADO (design decisión 17): el tope de filas ya lo exige el servicio
        // (Contar → rechazar → Take(tope + 1)) antes de volver acá, mismo shape que /cajas/export
        // — sin un GuardaDeTope adicional en este nivel, el servicio ya la corrió dos veces.
        // AlcanceDeListadoHttp resuelve solo la ETIQUETA de empresa del encabezado: la zona
        // efectivamente usada para clasificar es la que devuelve el propio servicio
        // (vencimientos.ZonaHoraria), nunca una segunda resolución que pudiera divergir.
        grupo.MapGet("/stock/vencimientos/export", async (
            ServicioDeReportesDeStock servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            int idPuntoVenta, int? dias, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var vencimientos = await servicio.ObtenerVencimientosParaExportacionAsync(
                idPuntoVenta, dias, opciones.Value.TopeDeFilas, ct);

            var (empresa, _) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, empresa, $"PV {idPuntoVenta}", vencimientos.Hoy, vencimientos.Hoy, vencimientos.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(vencimientos, ctx);

            var bytes = exportador.Generar(tabla);
            var nombre = NombreDeArchivo.Construir("vencimientos", $"pv{idPuntoVenta}", vencimientos.Hoy, vencimientos.Hoy);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de /stock/vencimientos: mismas filas, tope de filas exigido antes de " +
            "generar el archivo (design decisión 17).");

        // stage-13-stock-inteligente, Slice 4 (design decisión 1/2/3, spec reposicion-de-stock:
        // "Reposición Report Is The Alert And The Purchase Suggestion..."): gate heredado del
        // grupo. Sin campos de rotación todavía (slice 5) — dias es opcional, mismo criterio que
        // vencimientos?dias=.
        grupo.MapGet("/stock/reposicion", (
            ServicioDeReportesDeStock servicio, int idPuntoVenta, int? dias, CancellationToken ct) =>
            servicio.ObtenerReposicionAsync(idPuntoVenta, dias, ct))
        .WithSummary(
            "Alerta y sugerencia de compra: stock ⋈ articulos ⟕ proveedores donde minimo IS NOT " +
            "NULL AND cantidad <= minimo, agrupable por proveedor habitual (Sin proveedor incluido, " +
            "nunca omitido). Sin campos de rotación en esta slice — llegan en la etapa siguiente.");

        // Sibling declarado inmediatamente después de su ruta fuente — hereda LecturaDeReportes
        // por co-locación. AGREGADO acotado por catálogo (design decisión 13, mismo shape que
        // /stock/existencias/export): la guarda corre sobre TablaExportable.Filas.Count ya
        // mapeada, sin COUNT(*) propio, y sin ObtenerReposicionParaExportacionAsync separado — el
        // mismo ObtenerReposicionAsync respalda ambas rutas.
        grupo.MapGet("/stock/reposicion/export", async (
            ServicioDeReportesDeStock servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            int idPuntoVenta, int? dias, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var reposicion = await servicio.ObtenerReposicionAsync(idPuntoVenta, dias, ct);

            var (empresa, _) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, empresa, $"PV {idPuntoVenta}", reposicion.Hoy, reposicion.Hoy, reposicion.ZonaHoraria);
            var tabla = ExportacionDeReportes.De(reposicion, ctx);

            GuardaDeTope.Exigir(tabla.Filas.Count, opciones.Value.TopeDeFilas);

            var bytes = exportador.Generar(tabla);
            var nombre = NombreDeArchivo.Construir("reposicion", $"pv{idPuntoVenta}", reposicion.Hoy, reposicion.Hoy);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de /stock/reposicion: mismas filas y figuras (design decisión 13), tope " +
            "de filas exigido tras mapear — agregado acotado por catálogo, mismo criterio que " +
            "/stock/existencias/export.");

        // Tile de Tablero (stage-13-stock-inteligente, Slice 7; design decisión 8/9, spec
        // reposicion-de-stock: "The Tablero Tile Reuses The Report Method, Never A Second
        // Aggregation Query"): gate heredado del grupo. Reusa la misma clasificación que el JSON
        // de arriba, nunca una segunda query de agregación (ObtenerResumenDeReposicionAsync).
        grupo.MapGet("/stock/reposicion/resumen", (
            ServicioDeReportesDeStock servicio, int idPuntoVenta, CancellationToken ct) =>
            servicio.ObtenerResumenDeReposicionAsync(idPuntoVenta, ct))
        .WithSummary(
            "Tile de Tablero: conteos de bajoMinimo/sinStock/sinProveedor del punto de venta — " +
            "sinProveedor cuenta el grupo Sin proveedor, nunca conflado con sugerido ausente.");

        // stage-13-stock-inteligente, Slice 5 (design decisión 14, spec reposicion-de-stock:
        // "GET /api/reportes/stock/rotacion Feeds The Suggested-Minimo Column..."): gate heredado
        // del grupo. Feed INDEPENDIENTE de minimoSugerido — no depende de minimo, agrega sobre
        // TODO el catálogo del PV (design decisión 12); un artículo sin movimiento calificado en
        // la ventana está AUSENTE, nunca una fila en cero. dias opcional, mismo criterio que
        // /stock/reposicion?dias=.
        grupo.MapGet("/stock/rotacion", (
            ServicioDeReportesDeStock servicio, int idPuntoVenta, int? dias, CancellationToken ct) =>
            servicio.ObtenerRotacionAsync(idPuntoVenta, dias, ct))
        .WithSummary(
            "Feed de minimoSugerido para el editor: una fila por artículo con al menos un " +
            "movimiento calificado (venta o su anulación, nunca la anulación de una compra) en " +
            "la ventana de rotación — ausente, nunca en cero, cuando no rota.");

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

        // stage-11-exportacion-reportes, Slice 7 (design: G2/G3 — minimal aggregation, "G3:
        // MovimientosTesoreria by PV, OrderBy(m => m.Id), paginated. Zero derivation."; spec
        // tesoreria: Tesorería Book Has A Read/Listing Endpoint): gate heredado del grupo, sin
        // política propia. idPuntoVenta es OBLIGATORIO (a diferencia de /cajas): mezclar puntos de
        // venta rompería el significado de la cadena Inicio/Final (design decisión 11).
        grupo.MapGet("/tesoreria", (
            ServicioDeTesoreria servicio, int idPuntoVenta, DateTimeOffset? desde, DateTimeOffset? hasta,
            int? pagina, int? tamanio, CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary(
            "Libro de tesorería encadenado de un punto de venta, ordenado por id (nunca por " +
            "fecha): cero derivación, cada fila ya trae su inicio/final persistidos al cierre.");

        // Sibling declarado inmediatamente después de su ruta fuente (design: Data Flow) — hereda
        // LecturaDeReportes por co-locación. `desde`/`hasta` son OBLIGATORIOS acá (mismo criterio
        // que /api/ventas/export): un export es por definición un rango acotado, y el nombre de
        // archivo determinístico necesita ambas fechas.
        grupo.MapGet("/tesoreria/export", async (
            ServicioDeTesoreria servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            int idPuntoVenta, DateTimeOffset desde, DateTimeOffset hasta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var filas = await servicio.ListarParaExportacionAsync(
                idPuntoVenta, desde, hasta, opciones.Value.TopeDeFilas, ct);

            var (empresa, zonaId) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);
            var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);
            var desdeFecha = DateOnly.FromDateTime(desde.UtcDateTime);
            var hastaFecha = DateOnly.FromDateTime(hasta.UtcDateTime);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, empresa, $"PV {idPuntoVenta}", desdeFecha, hastaFecha, zonaId);
            var tabla = ExportacionDeCaja.De(filas, ctx, zona);

            var bytes = exportador.Generar(tabla);
            var nombre = NombreDeArchivo.Construir("tesoreria", $"pv{idPuntoVenta}", desdeFecha, hastaFecha);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary("Export XLSX de /tesoreria: mismos parámetros y figuras, mismo orden de cadena.");

        // stage-11-exportacion-reportes, Slice 5b (spec historico-de-cajas: G2 And G3 Endpoints
        // Have Export Siblings Equal To Their JSON): a diferencia del resto de exports de esta
        // etapa, un turno NO es un catálogo acotado — ServicioDeHistoricoDeCajas.
        // ListarCierresParaExportacionAsync corre Contar → rechazar → Take(tope + 1) en vez de
        // reusar el paginado (tope duro de 200) de /cajas, para que GuardaDeTope pueda dispararse
        // de verdad más allá de esa página. `desde`/`hasta` OBLIGATORIOS, mismo criterio que los
        // exports de listado de Slice 3: un nombre de archivo determinístico necesita un rango
        // acotado. AlcanceDeListadoHttp resuelve Empresa/zona porque /cajas nunca tuvo idEmpresa.
        grupo.MapGet("/cajas/export", async (
            ServicioDeHistoricoDeCajas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            int? idPuntoVenta, DateTimeOffset desde, DateTimeOffset hasta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var filas = await servicio.ListarCierresParaExportacionAsync(
                idPuntoVenta, desde, hasta, opciones.Value.TopeDeFilas, ct);

            var (empresa, zonaId) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);
            var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);
            var desdeFecha = DateOnly.FromDateTime(desde.UtcDateTime);
            var hastaFecha = DateOnly.FromDateTime(hasta.UtcDateTime);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, empresa, idPuntoVenta is { } id ? $"PV {id}" : null, desdeFecha, hastaFecha, zonaId);
            var tabla = ExportacionDeCaja.De(filas, ctx, zona);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } pv ? $"pv{pv}" : "todos";
            var nombre = NombreDeArchivo.Construir("cajas_historico", alcance, desdeFecha, hastaFecha);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de /cajas: mismos filtros y figuras, gate LecturaDeReportes heredado por " +
            "co-locación. desde/hasta obligatorios (a diferencia de /cajas), rango acotado por diseño.");

        return app;
    }
}
