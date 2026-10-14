using Ways.Api.Seguridad;
using Ways.Application.Articulos;
using Ways.Application.CatalogoOnline;
using Ways.Application.Precios;
using Ways.Application.Ventas;

namespace Ways.Api.Endpoints;

public static class ArticulosEndpoints
{
    public static IEndpointRouteBuilder MapearArticulos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/articulos")
            .WithTags("Articulos")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDeArticulos servicio,
            string? busqueda,
            int? idEmpresa,
            bool? incluirEliminados,
            int? pagina,
            int? tamanio,
            // stage-18-etiquetas-y-consulta, Slice 2 (task 2.6; design.md:291): tres query params
            // aditivos, opcionales — con los tres ausentes el listado queda byte-idéntico al de
            // antes de esta slice (verify criterio 6).
            int? idArea,
            int? idCategoria,
            int? idMarca,
            // Con busqueda, suma (y lista primero) los artículos cuyo código de ESE proveedor
            // coincide exacto con el término; sin busqueda no tiene efecto.
            int? idProveedor,
            // Búsqueda de la caja: deja afuera los artículos que no están disponibles a la venta.
            bool? soloVendibles,
            // Anula soloVendibles: la búsqueda de una devolución también ve el artículo de uso interno.
            bool? incluirNoVendibles,
            CancellationToken ct) =>
            servicio.ListarAsync(
                busqueda, idEmpresa, incluirEliminados ?? false, pagina ?? 1, tamanio ?? 25,
                idArea, idCategoria, idMarca, idProveedor,
                (soloVendibles ?? false) && !(incluirNoVendibles ?? false), ct))
        .WithSummary(
            "Lista artículos con búsqueda, filtros de disponibilidad/área/categoría/marca y paginado. Con soloVendibles "
            + "deja afuera los artículos no disponibles a la venta (la búsqueda de la caja), salvo que se pida "
            + "incluirNoVendibles=true (devoluciones).");

        // stage-5-pos-ventas (Slice 2, task 2.9, design: API Surface): resolución de escaneo
        // del POS — segmento literal, no colisiona con "/{id:int}" de abajo. Hereda
        // Politicas.OperacionDePos del grupo (Vendedor + Admin, design decisión 6).
        grupo.MapGet("/escaneo", (ServicioDeEscaneo servicio, string entrada, bool? incluirNoVendibles, CancellationToken ct) =>
            servicio.ResolverAsync(entrada, incluirNoVendibles ?? false, ct))
        .WithSummary(
            "Resuelve un código escaneado (codigo_interno o codigos_barra) a su artículo vendible. Con "
            + "incluirNoVendibles=true también resuelve uno de uso interno (devoluciones).");

        grupo.MapGet("/atajos-de-importe", (ServicioDeEscaneo servicio, CancellationToken ct) =>
            servicio.ListarAtajosDeImporteAsync(ct))
        .WithSummary(
            "Lista los artículos por importe vivos y disponibles a la venta que tienen tecla de atajo (F5 a F8), "
            + "ordenados por tecla. Es la fuente de los atajos del POS cuando no hay instantánea del punto de venta.");

        // stage-articulos-grilla-api: grilla del back-office con filtros por columna (Código,
        // Nombre, Precio, Proveedor, Estado) — segmento literal, no colisiona con "/{id:int}" de
        // abajo. Mismo read policy que "/" (hereda Politicas.OperacionDePos del grupo): esta
        // grilla es Admin-only en el front, pero el contrato de la API no lo restringe más que
        // el listado existente.
        grupo.MapGet("/grilla", (
            ServicioDeGrillaDeArticulos servicio,
            string? codigo,
            string? nombre,
            decimal? precioDesde,
            decimal? precioHasta,
            int? idProveedor,
            bool? sinProveedor,
            bool? activo,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(
                codigo, nombre, precioDesde, precioHasta, idProveedor, sinProveedor ?? false, activo,
                pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary(
            "Lista artículos para la grilla del back-office: precio de la lista default del tenant, "
                + "proveedor habitual y estado, con filtros por columna.");

        grupo.MapGet("/{id:int}", (ServicioDeArticulos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un artículo.");

        grupo.MapPost("/", async (
            ServicioDeArticulos servicio, AltaArticulo datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/articulos/{creado.Id}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Crea un artículo. El código interno se autogenera si se omite. Con idFamilia, el artículo entra "
                + "a esa familia: sus campos compartidos tienen que coincidir con los de la familia y copia sus precios. "
                + "Con nombreFamiliaNueva (excluyente con idFamilia), crea esa familia en la misma transacción y el artículo "
                + "es su único miembro; 409 familia_nombre_duplicado si el nombre está en uso.");

        grupo.MapPut("/{id:int}", (
            ServicioDeArticulos servicio, int id, EdicionArticulo datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Actualiza un artículo, incluida su disponibilidad por empresa. En un miembro de una familia, "
                + "un cambio de campos compartidos exige el alcance (Familia o SoloEste).");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeArticulos servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Baja lógica del artículo.");

        // Disponibilidad del artículo (online y a la venta) y su imagen. Mismo grupo y misma policy que editar el
        // artículo.
        grupo.MapPut("/{id:int}/disponibilidad", (
            ServicioDeCatalogoWeb servicio, int id, SolicitudDeDisponibilidad datos, CancellationToken ct) =>
            servicio.CambiarDisponibilidadAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Fija si el artículo está disponible online y a la venta (los dos son obligatorios). Idempotente. Es por "
            + "artículo: no es un campo de la familia.");

        // El cuerpo es la imagen cruda y no JSON: el servicio lo lee acotado y valida el tipo por contenido.
        grupo.MapPut("/{id:int}/imagen", (
            ServicioDeCatalogoWeb servicio, int id, HttpRequest peticion, CancellationToken ct) =>
            servicio.GuardarImagenAsync(id, peticion.ContentType, peticion.Body, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Sube la imagen del artículo (image/png, image/jpeg o image/webp, hasta 1 MB) como cuerpo de la request; "
            + "reemplaza la anterior.");

        // Vista previa del admin: sirve la imagen aunque el artículo no esté publicado, sin caché compartida ni del
        // navegador (se ve lo último que se subió), pero con ETag para que una condicional no reenvíe los bytes.
        grupo.MapGet("/{id:int}/imagen", (
            ServicioDeCatalogoWeb servicio, int id, HttpContext contexto, CancellationToken ct) =>
            PublicoEndpoints.ServirImagenAsync(
                contexto,
                "private, no-store",
                () => PublicoEndpoints.SinImagen("El artículo no tiene una imagen cargada."),
                () => servicio.ObtenerVersionDeImagenAsync(id, ct),
                () => servicio.ObtenerImagenAsync(id, ct)))
        .WithSummary("Los bytes de la imagen del artículo, publicado o no, con ETag; 404 imagen_no_encontrada si no tiene.");

        grupo.MapDelete("/{id:int}/imagen", async (ServicioDeCatalogoWeb servicio, int id, CancellationToken ct) =>
        {
            await servicio.QuitarImagenAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Quita la imagen del artículo. Idempotente: sin imagen también contesta 204.");

        grupo.MapPost("/{id:int}/codigos-barra", async (
            ServicioDeArticulos servicio, int id, AltaCodigoBarra datos, CancellationToken ct) =>
        {
            var creado = await servicio.AgregarCodigoBarraAsync(id, datos, ct);
            return Results.Created($"/api/articulos/{id}/codigos-barra/{creado.Id}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Agrega un código de barras al artículo.");

        grupo.MapPost("/{id:int}/codigos-proveedor", async (
            ServicioDeArticulos servicio, int id, AltaCodigoProveedor datos, CancellationToken ct) =>
        {
            var resultado = await servicio.AgregarCodigoProveedorAsync(id, datos, ct);
            return Results.Json(
                resultado.Codigo,
                statusCode: resultado.Creado ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Asocia al artículo el código que un proveedor imprime en su factura (idempotente).");

        grupo.MapDelete("/{id:int}/codigos-barra/{idCodigoBarra:int}", async (
            ServicioDeArticulos servicio, int id, int idCodigoBarra, CancellationToken ct) =>
        {
            await servicio.EliminarCodigoBarraAsync(id, idCodigoBarra, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Quita un código de barras del artículo.");

        grupo.MapGet("/{id:int}/codigos-barra", (
            ServicioDeArticulos servicio, int id, CancellationToken ct) =>
            servicio.ListarCodigosBarraAsync(id, ct))
        .WithSummary("Lista los códigos de barra activos del artículo.");

        grupo.MapGet("/{id:int}/sugerencia-precio", (
            ServicioDeArticulos servicio, int id, int? idEmpresa, CancellationToken ct) =>
            servicio.SugerirPrecioAsync(id, idEmpresa, ct))
        .RequireAuthorization(Politicas.LecturaDeCostos)
        .WithSummary(
            "Sugiere un precio a partir del costo y margen del artículo (nunca se aplica solo), redondeado al "
            + "múltiplo de redondeo_sugerencia_precio de la empresa indicada en idEmpresa (opcional; sin ella, el "
            + "default del parámetro; una empresa inexistente es 404).");

        // Slice 3 (stage-3-articulos-y-precios, task 3.5): precios nidificados bajo
        // /api/articulos/{id}/precios, no un recurso top-level propio (proposal's Affected
        // Areas note) — mismo grupo/policy que el resto de ArticulosEndpoints.

        grupo.MapPost("/{id:int}/precios", async (
            ServicioDePrecios servicio, int id, AltaPrecio datos, CancellationToken ct) =>
        {
            var creado = await servicio.EstablecerPrecioAsync(id, datos, ct);
            return Results.Created($"/api/articulos/{id}/precios/{datos.IdListaPrecio}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Establece el precio vigente de un artículo en una lista fija, efectivo ahora.");

        grupo.MapPost("/{id:int}/precios/programados", async (
            ServicioDePrecios servicio, int id, ProgramarPrecio datos, CancellationToken ct) =>
        {
            var creado = await servicio.ProgramarPrecioAsync(id, datos, ct);
            return Results.Created($"/api/articulos/{id}/precios/{datos.IdListaPrecio}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Programa un precio a futuro; reemplaza el pendiente existente solo con confirmarReemplazo.");

        grupo.MapGet("/{id:int}/precios", (
            ServicioDePrecios servicio, int id, DateTimeOffset? fecha, CancellationToken ct) =>
            servicio.PreciosVigentesAsync(id, fecha, ct))
        .WithSummary("Precio vigente del artículo en todas las listas activas, a una fecha dada (default: ahora).");

        grupo.MapGet("/{id:int}/precios/{idListaPrecio:int}", (
            ServicioDePrecios servicio, int id, int idListaPrecio, DateTimeOffset? fecha, CancellationToken ct) =>
            servicio.PrecioVigenteAsync(id, idListaPrecio, fecha, ct))
        .WithSummary("Precio vigente del artículo en una lista puntual, a una fecha dada (default: ahora).");

        grupo.MapGet("/{id:int}/precios/{idListaPrecio:int}/historial", (
            ServicioDePrecios servicio, int id, int idListaPrecio, CancellationToken ct) =>
            servicio.HistorialDePrecioAsync(id, idListaPrecio, ct))
        .WithSummary("Historial completo de precios del artículo en una lista fija.");

        return app;
    }
}
