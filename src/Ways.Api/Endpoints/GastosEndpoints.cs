using Ways.Api.Seguridad;
using Ways.Application.Gastos;
using Ways.Domain.Gastos;

namespace Ways.Api.Endpoints;

public static class GastosEndpoints
{
    public static IEndpointRouteBuilder MapearGastos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/gastos")
            .WithTags("Gastos")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-6-turnos-caja (Slice 3, task 3.2, design: API Surface): captura de gasto contra
        // el turno abierto — sin GestionDeCatalogo apilado (spec: Gasto Authorization, un
        // Vendedor tiene que poder registrar un gasto), mismo criterio que
        // "/api/caja/turnos/{id}/movimientos".
        grupo.MapPost("/", async (ServicioDeGastos servicio, SolicitudDeGasto solicitud, CancellationToken ct) =>
        {
            var gasto = await servicio.RegistrarAsync(solicitud, ct);
            return Results.Created($"/api/gastos/{gasto.Id}", gasto);
        })
        .WithSummary("Registra un gasto contra el turno abierto del punto de venta.");

        grupo.MapGet("/", (
            ServicioDeGastos servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Historial de gastos, paginado.");

        // Edición desde el POS: sin GestionDeCatalogo apilado, un Vendedor corrige los gastos de su
        // turno mientras siga abierto (el servicio lo exige bajo el lock del turno).
        grupo.MapPut("/{id:int}", async (
            ServicioDeGastos servicio, int id, SolicitudDeEdicionDeGasto solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.EditarAsync(id, solicitud, ct)))
        .WithSummary("Edita un gasto de un turno que sigue abierto.");

        // stage-gasto-a-compra (PR4), owner requirement: cualquier gasto (POS o admin) se liga a
        // una compra DESPUÉS de creado — GestionDeCatalogo propio (apila sobre el
        // Politicas.OperacionDePos del grupo), mismo criterio que las escrituras de
        // "/api/compras" (ComprasEndpoints): la vinculación mueve el saldo de un proveedor.
        grupo.MapPost("/{id:int}/vincular-compra", async (
            ServicioDeGastos servicio, int id, SolicitudDeVincularCompra solicitud, CancellationToken ct) =>
        {
            var gasto = await servicio.VincularCompraAsync(id, solicitud, ct);
            return Results.Ok(gasto);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Vincula (o convierte a proveedor) un gasto existente a una compra confirmada.");

        // stage-gastos-admin-retroactivos (PR3, owner's use case 2): grupo propio bajo
        // GestionDeCatalogo (Admin-only) — mismo gate que toda escritura de /api/compras: el
        // gasto administrativo mueve dinero de la tesorería de la empresa sin pasar por un turno,
        // así que ni siquiera la LECTURA de este historial de gestión es Politicas.OperacionDePos
        // (a diferencia de "/api/gastos" de arriba, que un Vendedor necesita para cargar contra su
        // turno). Sub-grupo propio para no tocar el gate del historial del turno de arriba.
        var grupoAdministracion = app.MapGroup("/api/gastos/administracion")
            .WithTags("Gastos")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupoAdministracion.MapPost("/", async (
            ServicioDeGastos servicio, SolicitudDeGastoDeAdministracion solicitud, CancellationToken ct) =>
        {
            var gasto = await servicio.RegistrarDeAdministracionAsync(solicitud, ct);
            return Results.Created($"/api/gastos/administracion/{gasto.Id}", gasto);
        })
        .WithSummary("Registra un gasto administrativo sin turno, pagado de la tesorería de la empresa.");

        grupoAdministracion.MapPut("/{id:int}", async (
            ServicioDeGastos servicio, int id, SolicitudDeEdicionDeGasto solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.EditarDeAdministracionAsync(id, solicitud, ct)))
        .WithSummary(
            "Edita cualquier gasto; si es de caja de un turno cerrado, recalcula el arqueo de ese turno.");

        grupoAdministracion.MapDelete("/{id:int}", async (ServicioDeGastos servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarDeAdministracionAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary(
            "Da de baja cualquier gasto, revirtiendo sus efectos; si es de caja de un turno cerrado, recalcula el arqueo.");

        grupoAdministracion.MapGet("/{id:int}", (ServicioDeGastos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerDeAdministracionAsync(id, ct))
        .WithSummary("Detalle de un gasto de gestión (mismo shape que el listado).");

        grupoAdministracion.MapGet("/", (
            ServicioDeGastos servicio,
            int? idEmpresa,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            CategoriaGasto? categoria,
            OrigenFondosGasto? origenFondos,
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
