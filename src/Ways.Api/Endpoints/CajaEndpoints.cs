using Ways.Api.Seguridad;
using Ways.Application.Caja;

namespace Ways.Api.Endpoints;

public static class CajaEndpoints
{
    public static IEndpointRouteBuilder MapearCaja(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/caja/turnos")
            .WithTags("Caja")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-6-turnos-caja (Slice 2, task 2.6, design: API Surface): apertura — INSERT llano
        // detrás de ux_turnos_caja_abierto, 409 turno_ya_abierto si el punto de venta ya tiene
        // uno abierto. Sin GestionDeCatalogo apilado (spec: Apertura And Cierre Authorization —
        // un Vendedor tiene que poder abrir turno).
        grupo.MapPost("/", async (ServicioDeTurnos servicio, SolicitudDeApertura solicitud, CancellationToken ct) =>
        {
            var turno = await servicio.AbrirAsync(solicitud, ct);
            return Results.Created($"/api/caja/turnos/{turno.Id}", turno);
        })
        .WithSummary("Apertura de turno de caja.");

        grupo.MapGet("/abierto", async (ServicioDeTurnos servicio, int idPuntoVenta, CancellationToken ct) =>
        {
            var turno = await servicio.ObtenerAbiertoAsync(idPuntoVenta, ct);
            // Con turno null hay que emitir el literal JSON "null" a mano: tanto Ok(null) como
            // Json(null) producen body vacío, y eso rompe el response.json() del cliente.
            return turno is null
                ? Results.Content("null", "application/json; charset=utf-8")
                : Results.Json(turno);
        })
        .WithSummary("Fuente de verdad del gate seam de Pos.tsx: 200 con el turno abierto o 200 con null.");

        grupo.MapGet("/{id:int}", async (ServicioDeTurnos servicio, int id, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerAsync(id, ct)))
        .WithSummary("Turno por id (payload del Z-report).");

        grupo.MapGet("/", (
            ServicioDeTurnos servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Historial de turnos, paginado.");

        // task 2.6, design: API Surface — retiro / refuerzo / apertura de cajón contra el turno
        // que identifica la ruta (nunca contra un idTurnoCaja del cuerpo).
        grupo.MapPost("/{id:int}/movimientos", async (
            ServicioDeTurnos servicio, int id, SolicitudDeMovimiento solicitud, CancellationToken ct) =>
        {
            var movimiento = await servicio.RegistrarMovimientoAsync(id, solicitud, ct);
            return Results.Created($"/api/caja/turnos/{id}/movimientos/{movimiento.Id}", movimiento);
        })
        .WithSummary("Retiro / refuerzo / apertura de cajón contra el turno abierto.");

        // stage-6-turnos-caja (Slice 4, task 4.7, design: API Surface): resumen parcial — misma
        // derivación que el cierre (spec: Resumen Parcial Uses The Same Derivation As Cierre),
        // de solo lectura.
        grupo.MapGet("/{id:int}/resumen", async (
            ServicioDeResumenDeTurno servicio, int id, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerAsync(id, ct)))
        .WithSummary("Resumen parcial del turno — misma derivación que el cierre.");

        // stage-6-turnos-caja (Slice 4, task 4.7, design: The Cierre Transaction): cierre —
        // irreversible, una sola transacción atómica. El cuerpo SOLO trae los conteos declarados
        // (spec: Cierre Payload Carries Only Declared Counts) — el importe esperado siempre lo
        // deriva el servidor.
        grupo.MapPost("/{id:int}/cierre", async (
            ServicioDeTurnos servicio, int id, SolicitudDeCierre solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.CerrarAsync(id, solicitud, ct)))
        .WithSummary("Cierre de turno: deriva el arqueo, lo persiste y encadena la tesorería — irreversible.");

        // stage-11-exportacion-reportes, Slice 5a (design "The load-bearing refinement of the
        // proposal is where the caja detail lives": la ruta MOVIÓ acá desde
        // /api/reportes/cajas/{id} para que OperacionDePos se herede por co-locación en vez de
        // pelearse con LecturaDeReportes; spec historico-de-cajas: G2 Detail Reuses ResumenDeTurno
        // Plus Ticket And Gasto Listings). ServicioDeResumenDeTurno.ObtenerAsync corre TAL CUAL
        // (misma derivación que /resumen, invariante intacto) + LectorDeLineasDelTurno, dos
        // lecturas indexadas llanas — sin escritura, sin agregado nuevo.
        grupo.MapGet("/{id:int}/detalle", async (
            ServicioDeResumenDeTurno servicioDeResumen, LectorDeLineasDelTurno lectorDeLineas, int id, CancellationToken ct) =>
        {
            var resumen = await servicioDeResumen.ObtenerAsync(id, ct);
            var tickets = await lectorDeLineas.LeerTicketsAsync(id, ct);
            var gastos = await lectorDeLineas.LeerGastosAsync(id, ct);

            return Results.Ok(new DetalleDeTurno(resumen, tickets, gastos));
        })
        .WithSummary(
            "Detalle del turno (Z-report): el mismo resumen de /resumen más los tickets y gastos " +
            "del turno — el cajero puede leer su propio cierre, mismo gate que /resumen.");

        return app;
    }
}
