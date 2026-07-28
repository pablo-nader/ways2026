using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ways.Application.Abstracciones;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia;

/// <summary>
/// Aplica migraciones pendientes y siembra los datos que el sistema necesita para arrancar.
/// Es idempotente: se puede correr en cada arranque del contenedor.
/// </summary>
public class InicializadorDeBaseDeDatos(
    WaysDbContext db,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj,
    ILogger<InicializadorDeBaseDeDatos> log)
{
    private static readonly (RolConocido Rol, string Nombre, string Descripcion)[] RolesBase =
    [
        (RolConocido.Root,       "root",       "Acceso total. No se puede crear ni eliminar desde la aplicación."),
        (RolConocido.Admin,      "admin",      "Administra usuarios, catálogo y configuración."),
        (RolConocido.Supervisor, "supervisor", "Supervisa la operación y cierra caja."),
        (RolConocido.Vendedor,   "vendedor",   "Opera el punto de venta.")
    ];

    public async Task EjecutarAsync(SemillaRoot semilla, CancellationToken ct = default)
    {
        log.LogInformation("Aplicando migraciones pendientes.");
        await db.Database.MigrateAsync(ct);

        await SembrarRolesAsync(ct);
        await SembrarRootAsync(semilla, ct);
    }

    private async Task SembrarRolesAsync(CancellationToken ct)
    {
        var existentes = await db.Roles
            .IgnoreQueryFilters()
            .Select(r => r.Id)
            .ToListAsync(ct);

        var ahora = reloj.Ahora;
        var nuevos = RolesBase
            .Where(r => !existentes.Contains((int)r.Rol))
            .Select(r => new Rol
            {
                Id = (int)r.Rol,
                Nombre = r.Nombre,
                Descripcion = r.Descripcion,
                CreatedAt = ahora,
                UpdatedAt = ahora
            })
            .ToList();

        if (nuevos.Count == 0)
        {
            return;
        }

        db.Roles.AddRange(nuevos);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Sembrados {Cantidad} roles.", nuevos.Count);
    }

    /// <summary>
    /// Crea la cuenta root si no existe ninguna. Nunca pisa una cuenta root existente:
    /// si ya hay una, se respeta la contraseña que tenga.
    /// </summary>
    private async Task SembrarRootAsync(SemillaRoot semilla, CancellationToken ct)
    {
        var hayRoot = await db.Usuarios
            .IgnoreQueryFilters()
            .AnyAsync(u => u.RolId == (int)RolConocido.Root, ct);

        if (hayRoot)
        {
            return;
        }

        var ahora = reloj.Ahora;
        db.Usuarios.Add(new Usuario
        {
            NombreUsuario = semilla.Usuario,
            Mail = semilla.Mail,
            RolId = (int)RolConocido.Root,
            Estado = EstadoUsuario.Activo,
            PasswordHash = hasheador.Hashear(semilla.Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });

        await db.SaveChangesAsync(ct);

        log.LogWarning(
            "Se creó la cuenta root '{Usuario}' con la contraseña de arranque. " +
            "Cambiala antes de poner el sistema en producción.",
            semilla.Usuario);
    }
}

/// <summary>Credenciales de la cuenta root inicial. Se configuran por variables de entorno.</summary>
public class SemillaRoot
{
    public const string Seccion = "Semilla:Root";

    public string Usuario { get; set; } = "root";
    public string Mail { get; set; } = "test@test.com";
    public string Password { get; set; } = "root";
}
