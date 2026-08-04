using Ways.Api.Seguridad;
using Ways.Application.Catalogos;
using Ways.Domain.Catalogos;

namespace Ways.Api.Endpoints;

public static class CatalogosEndpoints
{
    /// <summary>Mapea las 5 rutas de un catálogo (ADR-11, task 3.18): 1 línea por catálogo
    /// desde <see cref="MapearCatalogos"/>. <typeparamref name="TServicio"/> es el tipo
    /// concreto registrado en DI (p.ej. <see cref="ServicioDeAreas"/>), no
    /// <see cref="ServicioDeCatalogo{T,TListado,TAlta}"/> abierto — Minimal API resuelve
    /// parámetros de endpoint por tipo concreto.</summary>
    public static IEndpointRouteBuilder MapearCatalogo<T, TListado, TAlta, TServicio>(
        this IEndpointRouteBuilder app, string recurso)
        where T : CatalogoSimple
        where TListado : ListadoDeCatalogo
        where TAlta : AltaDeCatalogo
        where TServicio : ServicioDeCatalogo<T, TListado, TAlta>
    {
        var grupo = app.MapGroup($"/api/catalogos/{recurso}")
            .WithTags("Catálogos")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (TServicio servicio, bool? incluirInactivos, CancellationToken ct) =>
            servicio.ListarAsync(incluirInactivos ?? false, ct))
        .WithSummary($"Lista {recurso}.");

        grupo.MapGet("/{id:int}", (TServicio servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary($"Obtiene un elemento de {recurso}.");

        grupo.MapPost("/", async (TServicio servicio, TAlta datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/catalogos/{recurso}/{creado.Id}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary($"Crea un elemento de {recurso}.");

        grupo.MapPut("/{id:int}", (TServicio servicio, int id, TAlta datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary($"Actualiza un elemento de {recurso}.");

        grupo.MapDelete("/{id:int}", async (TServicio servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary($"Baja lógica de un elemento de {recurso}.");

        return app;
    }

    public static IEndpointRouteBuilder MapearCatalogos(this IEndpointRouteBuilder app)
    {
        app.MapearCatalogo<Area, AreaListado, AreaAlta, ServicioDeAreas>("areas");
        app.MapearCatalogo<Marca, MarcaListado, MarcaAlta, ServicioDeMarcas>("marcas");
        app.MapearCatalogo<Grupo, GrupoListado, GrupoAlta, ServicioDeGrupos>("grupos");
        app.MapearCatalogo<MedioPago, MedioPagoListado, MedioPagoAlta, ServicioDeMediosPago>("medios-pago");

        // Categorías usa la misma máquina de 5 operaciones (comparte ServicioDeCatalogo<T,..>)
        // pero es el escape hatch de ADR-11: ServicioDeCategorias agrega la validación de
        // profundidad/ciclo por encima antes de delegar en la base.
        app.MapearCatalogo<Categoria, CategoriaListado, CategoriaAlta, ServicioDeCategorias>("categorias");

        // stage-3-articulos-y-precios (Slice 4, task 4.3): ABM completo nuevo — la ruta
        // GET /api/listas-precio de solo lectura (ClientesEndpoints, stage 2, selector del
        // formulario de cliente) queda intacta, prefijo distinto, sin colisión.
        app.MapearCatalogo<ListaPrecio, ListaPrecioListado, ListaPrecioAlta, ServicioDeListasPrecio>("listas-precio");

        // Los 3 catálogos globales (ADR-11, gate #4) son de solo lectura en esta etapa — no
        // hay POST/PUT/DELETE mapeados a propósito, ni siquiera detrás de una policy: la
        // ausencia de ruta es la superficie de API, RLS (HabilitarRlsDeCatalogoGlobal) es la
        // segunda capa detrás. Cualquier sesión autenticada (tenant o plataforma) puede leer.
        var fiscales = app.MapGroup("/api/catalogos-fiscales").WithTags("Catálogos fiscales");

        fiscales.MapGet("/condiciones-fiscales", (
            ServicioDeCatalogosFiscales servicio, CancellationToken ct) =>
            servicio.ListarCondicionesFiscalesAsync(ct))
        .WithSummary("Lista las condiciones fiscales. Solo lectura (gate #4).");

        fiscales.MapGet("/alicuotas-iva", (ServicioDeCatalogosFiscales servicio, CancellationToken ct) =>
            servicio.ListarAlicuotasIvaAsync(ct))
        .WithSummary("Lista las alícuotas de IVA. Solo lectura (gate #4).");

        fiscales.MapGet("/tipos-comprobante", (ServicioDeCatalogosFiscales servicio, CancellationToken ct) =>
            servicio.ListarTiposComprobanteAsync(ct))
        .WithSummary("Lista los tipos de comprobante. Solo lectura (gate #4).");

        return app;
    }
}
