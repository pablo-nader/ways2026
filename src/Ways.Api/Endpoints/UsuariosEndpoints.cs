using Ways.Api.Seguridad;
using Ways.Application.Usuarios;

namespace Ways.Api.Endpoints;

public static class UsuariosEndpoints
{
    public static IEndpointRouteBuilder MapearUsuarios(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/usuarios")
            .WithTags("Usuarios")
            .RequireAuthorization(Politicas.GestionDeUsuarios);

        grupo.MapGet("/", (
            ServicioDeUsuarios servicio,
            string? busqueda,
            bool? incluirEliminados,
            int? pagina,
            int? tamanio,
            int? id,
            string? usuario,
            string? mail,
            string? tenant,
            int? rolId,
            string? estado,
            bool? conectado,
            string? orden,
            string? direccion,
            CancellationToken ct) =>
            servicio.ListarAsync(
                busqueda,
                incluirEliminados ?? false,
                pagina ?? 1,
                tamanio ?? 25,
                new FiltrosDeUsuarios(id, usuario, mail, tenant, rolId, estado, conectado),
                orden,
                direccion,
                ct))
        .WithSummary(
            "Lista usuarios con búsqueda, filtros por columna y paginado. Filtros: usuario y mail contienen el " +
            "texto; id, rolId y estado (Activo, Inactivo, Bloqueado) son exactos; conectado separa a quien alguna " +
            "vez inició sesión; tenant (id o sin-tenant) es solo del actor de plataforma. orden (id, usuario, mail, " +
            "tenant, rol, estado, ultimaConexion) y direccion (ascendente, descendente) ordenan todo el resultado " +
            "antes de paginar. Respuestas 400: orden_invalido, direccion_invalida, direccion_sin_orden, " +
            "orden_no_permitido (orden=tenant sin ser de plataforma), estado_invalido, tenant_invalido y " +
            "filtro_tenant_no_permitido (tenant sin ser de plataforma).");

        grupo.MapGet("/{id:int}", (ServicioDeUsuarios servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un usuario.");

        grupo.MapPost("/", async (
            ServicioDeUsuarios servicio, CrearUsuario datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/usuarios/{creado.Id}", creado);
        })
        .WithSummary("Crea un usuario. Root puede asignar admin; admin no.");

        grupo.MapPut("/{id:int}", (
            ServicioDeUsuarios servicio, int id, ActualizarUsuario datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary("Actualiza un usuario.");

        grupo.MapPost("/{id:int}/password", async (
            ServicioDeUsuarios servicio, int id, CambiarPassword datos, CancellationToken ct) =>
        {
            await servicio.CambiarPasswordAsync(id, datos, ct);
            return Results.NoContent();
        })
        .WithSummary("Cambia la contraseña de un usuario.");

        grupo.MapPost("/{id:int}/desbloquear", async (
            ServicioDeUsuarios servicio, int id, CancellationToken ct) =>
        {
            await servicio.DesbloquearAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Desbloquea una cuenta y reinicia los intentos fallidos.");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeUsuarios servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Baja lógica del usuario.");

        app.MapGet("/api/roles", (ServicioDeUsuarios servicio, CancellationToken ct) =>
            servicio.RolesAsignablesAsync(ct))
        .WithTags("Usuarios")
        .RequireAuthorization(Politicas.GestionDeUsuarios)
        .WithSummary("Roles que el usuario autenticado puede asignar.");

        return app;
    }
}
