using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia;

/// <summary>
/// Aplica migraciones pendientes y siembra los datos que el sistema necesita para arrancar.
/// Es idempotente: se puede correr en cada arranque del contenedor.
/// </summary>
public class InicializadorDeBaseDeDatos(
    [FromKeyedServices(DependencyInjection.ClaveContextoPlataforma)] WaysDbContext db,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj,
    IHostEnvironment entorno,
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
        // Todo lo que sigue en este scope corre en modo plataforma: migraciones, RLS y
        // semilla no tienen un tenant "actual", siembran para el tenant que corresponda
        // de forma explícita (ADR-14). El `db` inyectado ya está atado a la instancia
        // inmutable `TenantActualFijo.Plataforma` (ADR-2): no hace falta, y no se puede,
        // mutarlo.
        await VerificarRolSinBypassAsync(ct);
        VerificarInvariantesDeConexion();

        log.LogInformation("Aplicando migraciones pendientes.");
        await db.Database.MigrateAsync(ct);

        await SembrarRolesAsync(ct);
        await SembrarRootAsync(semilla, ct);
        await SembrarOrganizacionAsync(ct);
        await BackfillDeUsuariosAsync(ct);
    }

    /// <summary>
    /// ADR-5: <c>FORCE ROW LEVEL SECURITY</c> no alcanza si el rol conectado es
    /// superusuario o tiene <c>BYPASSRLS</c> — Postgres ignora las policies igual, y el
    /// aislamiento entre tenants quedaría solo en manos del filtro de EF. Frena el
    /// arranque en Production; en el resto de los entornos solo avisa.
    ///
    /// ADO.NET crudo sobre <c>db.Database.GetDbConnection()</c> en vez de
    /// <c>Database.SqlQuery&lt;T&gt;()</c>: encontrado en batch 7 (slice 2) — con el modelo
    /// completo de este proyecto (varios tipos con query filters "this"-scoped, ADR-1/ADR-6),
    /// <c>SqlQuery&lt;T&gt;()</c> hace explotar a
    /// <c>NavigationExpandingExpressionVisitor.CreateNavigationExpansionExpression</c> con
    /// <c>IndexOutOfRangeException</c> — confirmado que el bug es previo a este slice
    /// (reproduce igual en `main`, no algo que el retrofit de <c>usuarios</c> haya
    /// introducido) y que ninguna prueba lo había disparado nunca porque, hasta este batch,
    /// ningún test arrancaba el host real (<c>WebApplicationFactory.CreateClient()</c>) —
    /// que es lo único que ejecuta este método. Una consulta ADO.NET simple no pasa por esa
    /// canalización de LINQ en absoluto.
    /// </summary>
    private async Task VerificarRolSinBypassAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();
        var laAbrimosAca = conexion.State != System.Data.ConnectionState.Open;

        if (laAbrimosAca)
        {
            await conexion.OpenAsync(ct);
        }

        bool rolSuper;
        bool rolBypassRls;

        try
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";

            await using var lector = await comando.ExecuteReaderAsync(ct);

            if (!await lector.ReadAsync(ct))
            {
                return;
            }

            rolSuper = lector.GetBoolean(0);
            rolBypassRls = lector.GetBoolean(1);
        }
        finally
        {
            if (laAbrimosAca)
            {
                await conexion.CloseAsync();
            }
        }

        if (!rolSuper && !rolBypassRls)
        {
            return;
        }

        const string mensaje =
            "El rol de conexión tiene rolsuper o rolbypassrls: Postgres ignora " +
            "FORCE ROW LEVEL SECURITY y el aislamiento entre tenants queda solo en " +
            "manos del filtro de EF Core.";

        if (entorno.IsProduction())
        {
            throw new InvalidOperationException(mensaje);
        }

        log.LogWarning("{Mensaje}", mensaje);
    }

    /// <summary>
    /// ADR-3: <c>Multiplexing</c> y <c>No Reset On Close</c> tienen que quedar
    /// deshabilitados (default de Npgsql) — cualquiera de los dos activado rompe el
    /// aislamiento de <c>set_config(..., false)</c> entre conexiones reutilizadas del
    /// pool. Misma política de entorno que <see cref="VerificarRolSinBypassAsync"/>.
    /// </summary>
    private void VerificarInvariantesDeConexion()
    {
        var cadena = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException(
                "No se pudo resolver la cadena de conexión para verificar sus invariantes.");

        if (!InvariantesDeConexion.ViolaMultiplexingOResetOnClose(cadena))
        {
            return;
        }

        const string mensaje =
            "La cadena de conexión tiene Multiplexing o No Reset On Close activados: " +
            "los GUC de tenant pueden filtrarse entre conexiones reutilizadas del pool " +
            "(ADR-3).";

        if (entorno.IsProduction())
        {
            throw new InvalidOperationException(mensaje);
        }

        log.LogWarning("{Mensaje}", mensaje);
    }

    private async Task SembrarRolesAsync(CancellationToken ct)
    {
        var existentes = await db.Roles
            .IgnoreQueryFilters(["BajaLogica"])
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
            .IgnoreQueryFilters(["BajaLogica"])
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

    /// <summary>
    /// Siembra el tenant 1 / empresa 1 / los 2 puntos de venta actuales (doc 09: "el
    /// negocio actual es el tenant 1, una empresa, dos puntos de venta"). Solo corre una
    /// vez: si ya existe algún tenant, no vuelve a tocar nada.
    /// </summary>
    private async Task SembrarOrganizacionAsync(CancellationToken ct)
    {
        var hayTenants = await db.Tenants.IgnoreQueryFilters(["BajaLogica"]).AnyAsync(ct);
        if (hayTenants)
        {
            return;
        }

        var ahora = reloj.Ahora;

        var tenant = new Tenant
        {
            Nombre = "Ways",
            Estado = EstadoTenant.Activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        var empresa = new Empresa
        {
            IdTenant = tenant.Id,
            RazonSocial = "Ways",
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync(ct);

        db.PuntosVenta.AddRange(
            new PuntoVenta
            {
                IdTenant = tenant.Id,
                IdEmpresa = empresa.Id,
                Nombre = "Local 1",
                CreatedAt = ahora,
                UpdatedAt = ahora
            },
            new PuntoVenta
            {
                IdTenant = tenant.Id,
                IdEmpresa = empresa.Id,
                Nombre = "Local 2",
                CreatedAt = ahora,
                UpdatedAt = ahora
            });

        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Sembrado el tenant inicial '{Tenant}' con su empresa y 2 puntos de venta.",
            tenant.Nombre);
    }

    /// <summary>
    /// Backfill de <c>usuarios.id_tenant</c> (task 2.3, ADR-14): la cuenta <c>root</c>
    /// existente se queda con <c>id_tenant NULL</c> (plataforma, sin tocar), y cualquier
    /// otra cuenta preexistente se asigna al tenant 1. Corre después de
    /// <see cref="SembrarOrganizacionAsync"/> — necesita que el tenant 1 ya exista.
    ///
    /// Idempotente: en una instalación nueva no hay ninguna cuenta huérfana (solo existe
    /// <c>root</c>, que el filtro de rol excluye) y esto es un no-op; en un redeploy sobre
    /// una base con el backfill ya corrido, tampoco encuentra nada para tocar.
    ///
    /// Encontrado en judgment-day (batch 9, ronda 2): esta es la ÚNICA mutación NULL→valor
    /// legítima de <c>Usuario.IdTenant</c> en todo el sistema, y el guard de tamper de
    /// <c>WaysDbContext.EstamparTenant</c> rechaza CUALQUIER <c>Modified</c> con
    /// <c>IdTenant</c> tocado, sin distinguir esta asignación legítima de una reasignación
    /// real. Por eso <c>ExecuteUpdateAsync</c> en vez de cargar las entidades y asignarles
    /// la propiedad: es un UPDATE set-based que nunca pasa por el <c>ChangeTracker</c>, así
    /// que nunca entra al guard — evita la excepción sin abrir un agujero en esa defensa
    /// (mantenerla estricta para cualquier <c>Modified</c> real es justamente el punto de
    /// defense-in-depth del comentario de <c>EstamparTenant</c>). Sigue pasando RLS: corre
    /// en modo plataforma, y <c>WITH CHECK (app_es_plataforma() OR ...)</c> deja pasar
    /// cualquier valor de <c>id_tenant</c> bajo ese modo.
    /// </summary>
    private async Task BackfillDeUsuariosAsync(CancellationToken ct)
    {
        var idTenantPorDefecto = await db.Tenants
            .IgnoreQueryFilters(["BajaLogica"])
            .OrderBy(t => t.Id)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (idTenantPorDefecto is null)
        {
            return;
        }

        var actualizados = await db.Usuarios
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(u => u.IdTenant == null && u.RolId != (int)RolConocido.Root)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IdTenant, idTenantPorDefecto), ct);

        if (actualizados == 0)
        {
            return;
        }

        log.LogInformation(
            "Backfill: {Cantidad} usuarios existentes asignados al tenant {Tenant}.",
            actualizados, idTenantPorDefecto);
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
