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

        MapearPagosConLineas(app);

        return app;
    }

    /// <summary>E2-S4: contrato nuevo con líneas por medio, a la par de "/api/gastos" (que sigue igual para el POS
    /// instalado). Mismas políticas que su gemelo de gastos. Los listados reutilizan <see cref="PagoListado"/> y
    /// <see cref="PagoDeAdministracionListado"/>, que ya traen <c>lineas</c> y conservan además el <c>idMedioPago</c> de
    /// compatibilidad del primer ítem; depurar ese campo queda para la etapa de limpieza.</summary>
    private static void MapearPagosConLineas(IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/pagos")
            .WithTags("Pagos")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapPost("/", async (ServicioDePagos servicio, SolicitudDePagoConLineas solicitud, CancellationToken ct) =>
        {
            var pago = await servicio.RegistrarAsync(solicitud.AComando(), ct);
            return Results.Created($"/api/pagos/{pago.Id}", await servicio.ConLineasAsync(pago, ct));
        })
        .WithSummary("Registra un pago con líneas por medio contra el turno abierto del punto de venta.");

        grupo.MapGet("/", (
            ServicioDePagos servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Historial de pagos con sus líneas por medio, paginado.");

        grupo.MapPut("/{id:int}", async (
            ServicioDePagos servicio, int id, SolicitudDeEdicionDePagoConLineas solicitud, CancellationToken ct) =>
        {
            var pago = await servicio.EditarAsync(id, solicitud.AComando(), ct);
            return Results.Ok(await servicio.ConLineasAsync(pago, ct));
        })
        .WithSummary("Edita un pago de un turno que sigue abierto, reemplazando sus líneas por medio.");

        grupo.MapPost("/{id:int}/vincular-compra", async (
            ServicioDePagos servicio, int id, SolicitudDeVincularCompra solicitud, CancellationToken ct) =>
        {
            var pago = await servicio.VincularCompraAsync(id, solicitud, ct);
            return Results.Ok(await servicio.ConLineasAsync(pago, ct));
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Vincula (o convierte a proveedor) un pago existente a una compra confirmada.");

        grupo.MapPost("/de-compra/{idCompra:int}", async (
            ServicioDePagos servicio, int idCompra, SolicitudDePagoDeCompraConLineas solicitud, CancellationToken ct) =>
        {
            var resultado = await servicio.PagarCompraAsync(idCompra, solicitud.AComando(), ct);
            return Results.Created(
                $"/api/pagos/administracion/{resultado.Pago.Id}",
                new ResultadoDePagoDeCompraConLineas(
                    await servicio.ConLineasAsync(resultado.Pago, ct), resultado.Pagado, resultado.SaldoPendiente));
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Paga una compra confirmada con líneas por medio: crea el pago de tesorería imputado a la compra. " +
            "El total de las líneas no puede superar el saldo pendiente.");

        var administracion = app.MapGroup("/api/pagos/administracion")
            .WithTags("Pagos")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        administracion.MapPost("/", async (
            ServicioDePagos servicio, SolicitudDePagoDeAdministracionConLineas solicitud, CancellationToken ct) =>
        {
            var pago = await servicio.RegistrarDeAdministracionAsync(solicitud.AComando(), ct);
            return Results.Created($"/api/pagos/administracion/{pago.Id}", await servicio.ConLineasAsync(pago, ct));
        })
        .WithSummary("Registra un pago administrativo con líneas por medio, sin turno, pagado de la tesorería.");

        administracion.MapPut("/{id:int}", async (
            ServicioDePagos servicio, int id, SolicitudDeEdicionDePagoConLineas solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.EditarDeAdministracionAsync(id, solicitud.AComando(), ct)))
        .WithSummary("Edita cualquier pago reemplazando sus líneas; recalcula el arqueo si es de un turno cerrado.");

        administracion.MapDelete("/{id:int}", async (ServicioDePagos servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarDeAdministracionAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Da de baja cualquier pago, revirtiendo sus efectos; recalcula el arqueo si es de un turno cerrado.");

        administracion.MapGet("/{id:int}", (ServicioDePagos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerDeAdministracionAsync(id, ct))
        .WithSummary("Detalle de un pago de gestión con sus líneas.");

        administracion.MapGet("/", (
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
        .WithSummary("Historial de gestión de pagos, con filtros amplios, nombres resueltos y líneas por medio.");
    }
}
