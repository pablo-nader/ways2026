using Ways.Api.Seguridad;
using Ways.Application.Clientes;

namespace Ways.Api.Endpoints;

public static class ClientesEndpoints
{
    public static IEndpointRouteBuilder MapearClientes(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/clientes")
            .WithTags("Clientes")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDeClientes servicio,
            string? busqueda,
            bool? incluirEliminados,
            int? pagina,
            int? tamanio,
            int? numero,
            string? nombre,
            string? documento,
            string? telefono,
            string? email,
            bool? activo,
            string? orden,
            string? direccion,
            CancellationToken ct) =>
            servicio.ListarAsync(
                busqueda,
                incluirEliminados ?? false,
                pagina ?? 1,
                tamanio ?? 25,
                new FiltrosDeClientes(numero, nombre, documento, telefono, email, activo),
                orden,
                direccion,
                ct))
        .WithSummary(
            "Lista clientes con búsqueda, filtros por columna (numero y activo exactos; nombre, documento, " +
            "telefono y email contienen el texto) y paginado. orden (numero, nombre, documento, telefono, " +
            "email, estado) y direccion (ascendente, descendente) ordenan todo el resultado antes de paginar; " +
            "un valor desconocido responde 400 orden_invalido o direccion_invalida.");

        grupo.MapGet("/{id:int}", (ServicioDeClientes servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un cliente.");

        grupo.MapPost("/", async (
            ServicioDeClientes servicio, AltaCliente datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/clientes/{creado.Id}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea un cliente. El numero lo asigna el servidor (contador atómico por tenant).");

        grupo.MapPost("/rapido", async (
            ServicioDeClientes servicio, AltaRapidaDeCliente datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearRapidoAsync(datos, ct);
            return Results.Created($"/api/clientes/{creado.Id}", creado);
        })
        .WithSummary(
            "Alta rápida desde el POS (Vendedor, Supervisor, Admin): nombre, telefono y domicilio. " +
            "Siempre Consumidor Final, lista por defecto, sin cuenta corriente.");

        grupo.MapPut("/{id:int}", (
            ServicioDeClientes servicio, int id, EdicionCliente datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Actualiza un cliente. Rechazado sobre el Consumidor Final (numero = 1).");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeClientes servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Baja lógica del cliente. Rechazada sobre el Consumidor Final (numero = 1).");

        // Referencia de solo lectura para el selector de lista de precios del formulario —
        // no un ABM de listas_precio (design decision 1, spec: listas_precio ABM Is Out of
        // Scope This Stage). Mismo criterio que /api/roles en UsuariosEndpoints. Etapa 5
        // (design decisión 6): pasa a OperacionDePos, la lectura la necesita el POS.
        app.MapGet("/api/listas-precio", (ServicioDeClientes servicio, CancellationToken ct) =>
            servicio.ListasDePrecioAsignablesAsync(ct))
        .WithTags("Clientes")
        .RequireAuthorization(Politicas.OperacionDePos)
        .WithSummary("Listas de precio asignables a un cliente. Sin ABM propio esta etapa.");

        return app;
    }
}
