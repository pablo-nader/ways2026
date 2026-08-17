using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Exportacion;
using Ways.Application.Parametros;

namespace Ways.Api.Endpoints;

public static class AuditoriaEndpoints
{
    public static IEndpointRouteBuilder MapearAuditoria(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/auditoria")
            .WithTags("Auditoria")
            .RequireAuthorization(Politicas.LecturaDeAuditoria);

        grupo.MapGet("/", (
            ServicioDeConsultaDeAuditoria servicio,
            DateTimeOffset? desde, DateTimeOffset? hasta, string? accion, int? idActor,
            string? entidad, int? idEntidad, int? idPuntoVenta, int? pagina, int? tamanio,
            CancellationToken ct) =>
        {
            var filtros = new FiltrosDeAuditoria(desde, hasta, accion, idActor, entidad, idEntidad, idPuntoVenta);
            return servicio.ConsultarAsync(filtros, pagina ?? 1, tamanio ?? 25, ct);
        })
        .WithSummary(
            "Log de auditoría filtrado y paginado — Admin-only (LecturaDeAuditoria), sin apilar " +
            "sobre LecturaDeReportes: dentro de un tenant, un Admin ve filas de TODOS los puntos " +
            "de venta (idPuntoVenta es un filtro, no un alcance). idEntidad exige entidad (400 " +
            "entidad_requerida) — accion/entidad desconocidas no rechazan, devuelven 0 filas " +
            "(design decisión 15: una acción retirada deja rastro consultable).");

        // Slice 6 (design decisión 13): sibling declarado inmediatamente después de su ruta
        // fuente, sin `.RequireAuthorization` propio — hereda `LecturaDeAuditoria` por
        // co-locación bajo el mismo `grupo` (mutation target 6.9: borrar el `.RequireAuthorization`
        // de ARRIBA es lo único que puede hacer fallar el 403 de Supervisor acá, no hay una
        // segunda línea que borrar). `desde`/`hasta` OBLIGATORIOS (regla de la casa del export +
        // nombre de archivo determinístico), a diferencia del listado JSON.
        grupo.MapGet("/export", async (
            ServicioDeConsultaDeAuditoria servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            DateTimeOffset desde, DateTimeOffset hasta, string? accion, int? idActor, string? entidad,
            int? idEntidad, int? idPuntoVenta, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var filtros = new FiltrosDeAuditoria(desde, hasta, accion, idActor, entidad, idEntidad, idPuntoVenta);
            var filas = await servicio.ConsultarParaExportacionAsync(filtros, opciones.Value.TopeDeFilas, ct);

            var (empresa, zonaId) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);
            var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);
            var desdeFecha = DateOnly.FromDateTime(desde.UtcDateTime);
            var hastaFecha = DateOnly.FromDateTime(hasta.UtcDateTime);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, empresa, idPuntoVenta is { } id ? $"PV {id}" : null, desdeFecha, hastaFecha, zonaId);
            var tabla = ExportacionDeAuditoria.De(filas, ctx, zona);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } pv ? $"pv{pv}" : "todos";
            var nombre = NombreDeArchivo.Construir("auditoria", alcance, desdeFecha, hastaFecha);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de GET /api/auditoria: mismos filtros, desde/hasta obligatorios (regla " +
            "de la casa del export), política LecturaDeAuditoria heredada por co-locación. " +
            "Rechaza (400 exportacion_demasiado_grande) en vez de truncar al superar el tope.");

        return app;
    }
}
