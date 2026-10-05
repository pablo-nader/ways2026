using Microsoft.EntityFrameworkCore;

namespace Ways.Api.ConectorMcp;

/// <summary>Almacén de OpenIddict (cliente, autorizaciones y tokens) en el proveedor InMemory de
/// EF Core: no toca el esquema de Postgres de Ways y se pierde al reiniciar el proceso.</summary>
public sealed class ContextoOAuthDelConector(DbContextOptions<ContextoOAuthDelConector> opciones) : DbContext(opciones);
