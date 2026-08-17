using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Parametros;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;

namespace Ways.Api.Endpoints;

public static class VentasEndpoints
{
    public static IEndpointRouteBuilder MapearVentas(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/ventas")
            .WithTags("Ventas")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-5-pos-ventas (Slice 4, task 4.6, design: API Surface): checkout — sin
        // GestionDeCatalogo apilado (design: Authorization Surface, "/api/ventas... solo
        // GestionDeCatalogo en POST /api/stock/ajustes"). Un Vendedor tiene que poder vender.
        grupo.MapPost("/", async (ServicioDeVentas servicio, SolicitudDeVenta solicitud, CancellationToken ct) =>
        {
            var emitido = await servicio.EmitirAsync(solicitud, ct);
            return Results.Created($"/api/ventas/{emitido.Id}", emitido);
        })
        .WithSummary("Emite un comprobante de venta (checkout). Todo el dinero se recalcula server-side.");

        grupo.MapGet("/{id:int}", (ServicioDeVentas servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Reimpresión: lee el snapshot del comprobante, nunca re-joinea el catálogo.");

        // stage-5-pos-ventas (Slice 5, task 5.2, design: API Surface): POST, no DELETE — produce
        // filas (movimientos inversos + contramovimiento CC), no elimina ninguna. Sin
        // GestionDeCatalogo apilado (spec: OperacionDePos Authorization For Emission and
        // Anulación — un Vendedor puede anular su propia venta, mismo criterio que emitir).
        grupo.MapPost("/{id:int}/anulacion", async (ServicioDeVentas servicio, int id, CancellationToken ct) =>
        {
            var anulado = await servicio.AnularAsync(id, ct);
            return Results.Ok(anulado);
        })
        .WithSummary("Anula un comprobante: revierte stock y cuenta corriente en la misma transacción. No existe restaurar.");

        grupo.MapGet("/", (
            ServicioDeVentas servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? idCliente,
            EstadoComprobante? estado,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, idCliente, estado, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista comprobantes de venta con filtros y paginado.");

        // stage-11-exportacion-reportes (Slice 3, design decisión 7): sibling declarado
        // inmediatamente después de su ruta fuente — hereda OperacionDePos por co-locación, sin
        // política propia. `desde`/`hasta` son OBLIGATORIOS acá (a diferencia del listado JSON):
        // un export es por definición un rango acotado, y el nombre de archivo determinístico
        // necesita ambas fechas (spec exportacion-de-reportes: XLSX Response Contract And
        // Deterministic Naming).
        grupo.MapGet("/export", async (
            ServicioDeVentas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            int? idPuntoVenta, DateTimeOffset desde, DateTimeOffset hasta, int? idCliente, EstadoComprobante? estado,
            string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var filas = await servicio.ListarParaExportacionAsync(
                idPuntoVenta, desde, hasta, idCliente, estado, opciones.Value.TopeDeFilas, ct);

            var (empresa, zonaId) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);
            var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);
            var (desdeFecha, hastaFecha) = FechaDelRango.De(desde, hasta);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, empresa, idPuntoVenta is { } id ? $"PV {id}" : null, desdeFecha, hastaFecha, zonaId);
            var tabla = ExportacionDeListados.De(filas, ctx, zona);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } pv ? $"pv{pv}" : "todos";
            var nombre = NombreDeArchivo.Construir("ventas_listado", alcance, desdeFecha, hastaFecha);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de GET /api/ventas: mismo filtro, sin paginado bajo el tope, gate " +
            "OperacionDePos heredado por co-locación.");

        return app;
    }
}
