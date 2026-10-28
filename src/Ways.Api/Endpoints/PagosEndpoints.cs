using Ways.Api.Seguridad;
using Ways.Application.Pagos;
using Ways.Domain.Pagos;

namespace Ways.Api.Endpoints;

public static class PagosEndpoints
{
    public static IEndpointRouteBuilder MapearPagos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/gastos")
            .WithTags("Gastos")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-6-turnos-caja (Slice 3, task 3.2, design: API Surface): captura de pago contra
        // el turno abierto — sin GestionDeCatalogo apilado (spec: Pago Authorization, un
        // Vendedor tiene que poder registrar un pago), mismo criterio que
        // "/api/caja/turnos/{id}/movimientos".
        grupo.MapPost("/", async (ServicioDePagos servicio, SolicitudDePago solicitud, CancellationToken ct) =>
        {
            var pago = await servicio.RegistrarAsync(solicitud, ct);
            return Results.Created($"/api/gastos/{pago.Id}", pago);
        })
        .WithSummary("Registra un gasto contra el turno abierto del punto de venta.");

        grupo.MapGet("/", (
            ServicioDePagos servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Historial de gastos, paginado.");

        // Edición desde el POS: sin GestionDeCatalogo apilado, un Vendedor corrige los pagos de su
        // turno mientras siga abierto (el servicio lo exige bajo el lock del turno).
        grupo.MapPut("/{id:int}", async (
            ServicioDePagos servicio, int id, SolicitudDeEdicionDePago solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.EditarAsync(id, solicitud, ct)))
        .WithSummary("Edita un gasto de un turno que sigue abierto.");

        // stage-gasto-a-compra (PR4), owner requirement: cualquier pago (POS o admin) se liga a
        // una compra DESPUÉS de creado — GestionDeCatalogo propio (apila sobre el
        // Politicas.OperacionDePos del grupo), mismo criterio que las escrituras de
        // "/api/compras" (ComprasEndpoints): la vinculación mueve el saldo de un proveedor.
        grupo.MapPost("/{id:int}/vincular-compra", async (
            ServicioDePagos servicio, int id, SolicitudDeVincularCompra solicitud, CancellationToken ct) =>
        {
            var pago = await servicio.VincularCompraAsync(id, solicitud, ct);
            return Results.Ok(pago);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Vincula (o convierte a proveedor) un gasto existente a una compra confirmada.");

        // stage-gastos-admin-retroactivos (PR3, owner's use case 2): grupo propio bajo
        // GestionDeCatalogo (Admin-only) — mismo gate que toda escritura de /api/compras: el
        // pago administrativo mueve dinero de la tesorería de la empresa sin pasar por un turno,
        // así que ni siquiera la LECTURA de este historial de gestión es Politicas.OperacionDePos
        // (a diferencia de "/api/gastos" de arriba, que un Vendedor necesita para cargar contra su
        // turno). Sub-grupo propio para no tocar el gate del historial del turno de arriba.
        var grupoAdministracion = app.MapGroup("/api/gastos/administracion")
            .WithTags("Gastos")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupoAdministracion.MapPost("/", async (
            ServicioDePagos servicio, SolicitudDePagoDeAdministracion solicitud, CancellationToken ct) =>
        {
            var pago = await servicio.RegistrarDeAdministracionAsync(solicitud, ct);
            return Results.Created($"/api/gastos/administracion/{pago.Id}", pago);
        })
        .WithSummary("Registra un gasto administrativo sin turno, pagado de la tesorería de la empresa.");

        grupoAdministracion.MapPut("/{id:int}", async (
            ServicioDePagos servicio, int id, SolicitudDeEdicionDePago solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.EditarDeAdministracionAsync(id, solicitud, ct)))
        .WithSummary(
            "Edita cualquier pago; si es de caja de un turno cerrado, recalcula el arqueo de ese turno.");

        grupoAdministracion.MapDelete("/{id:int}", async (ServicioDePagos servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarDeAdministracionAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary(
            "Da de baja cualquier pago, revirtiendo sus efectos; si es de caja de un turno cerrado, recalcula el arqueo.");

        grupoAdministracion.MapGet("/{id:int}", (ServicioDePagos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerDeAdministracionAsync(id, ct))
        .WithSummary("Detalle de un gasto de gestión (mismo shape que el listado).");

        grupoAdministracion.MapGet("/", (
            ServicioDePagos servicio,
            int? idEmpresa,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            CategoriaPago? categoria,
            OrigenFondosPago? origenFondos,
            int? idProveedor,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarDeAdministracionAsync(
                idEmpresa, idPuntoVenta, desde, hasta, categoria, origenFondos, idProveedor,
                pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Historial de gestión de gastos, con filtros amplios y nombres resueltos.");

        return app;
    }
}
