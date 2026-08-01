using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Levanta un Postgres real en contenedor y la API completa contra él (ADR-17): las
/// pruebas de aislamiento tienen que probar RLS sobre una conexión que genuinamente no
/// puede saltarla, no sobre un doble en memoria.
///
/// Dos roles (ADR-17): <c>ways_owner</c> (dueño de las tablas, corre las migraciones
/// directamente contra el contenedor, antes de que arranque el host de la API) y
/// <c>ways_app</c> (<c>NOSUPERUSER NOBYPASSRLS</c>, con solo los GRANTs de datos) — la
/// API completa (<see cref="ConfigureWebHost"/>) y las aserciones de SQL crudo corren
/// bajo <c>ways_app</c>: es la única conexión bajo la cual una prueba de RLS prueba algo
/// real.
/// </summary>
public sealed class WaysApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string RolApp = "ways_app";
    private const string PasswordApp = "ways_app_password";

    private readonly PostgreSqlContainer _contenedor = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("ways_test")
        .WithUsername("ways_owner")
        .WithPassword("ways_owner")
        .Build();

    /// <summary>Rol dueño de las tablas — deliberadamente NO es la conexión que usa la
    /// API bajo prueba (ver <see cref="ConfigureWebHost"/>).</summary>
    public string OwnerConnectionString => _contenedor.GetConnectionString();

    /// <summary>Poblada en <see cref="InitializeAsync"/> tras crear <c>ways_app</c>.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _contenedor.StartAsync();
        await MigrarComoOwnerAsync();
        AppConnectionString = await CrearRolDeAplicacionAsync();
    }

    /// <summary>Corre las migraciones existentes directamente contra el contenedor, con el
    /// rol dueño — igual que <c>InicializadorDeBaseDeDatos</c> en producción, pero antes de
    /// que exista el host de la API (que va a conectarse como <c>ways_app</c>).
    ///
    /// <c>PendingModelChangesWarning</c> silenciado a propósito, SOLO ACÁ: mientras stage-1
    /// slice 2 está detenido en la DB CHANGE GATE #2 (`usuarios.id_tenant` ya existe en el
    /// modelo de <c>Usuario</c>/<c>UsuarioConfiguration</c>, la migración todavía no —
    /// `CLAUDE.md` prohíbe generarla sin aprobación explícita), el modelo en memoria y el
    /// snapshot de la última migración difieren de verdad, y EF Core 8+ lo trata como error
    /// fatal por default. Es exactamente el escenario documentado para este flag (ver el
    /// mensaje de la propia excepción). Ninguna prueba de este proyecto toca `usuarios`
    /// contra Postgres real todavía (las que sí lo hacen están con <c>Skip</c> hasta que la
    /// migración exista), así que aplicar las tres migraciones existentes con el esquema
    /// viejo de <c>usuarios</c> es seguro. Sacar este silenciado en el mismo batch que
    /// genere la migración 2 — a partir de ahí modelo y snapshot vuelven a coincidir y el
    /// warning no se dispara más.</summary>
    private async Task MigrarComoOwnerAsync()
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(OwnerConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
            })
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma);
        await db.Database.MigrateAsync();
    }

    /// <summary>ADR-5 / ADR-17: <c>ways_app</c> tiene los GRANTs de datos sobre lo que la
    /// migración creó, pero ni <c>SUPERUSER</c> ni <c>BYPASSRLS</c> — así <c>FORCE ROW
    /// LEVEL SECURITY</c> se prueba de verdad.</summary>
    private async Task<string> CrearRolDeAplicacionAsync()
    {
        await using (var conexion = new NpgsqlConnection(OwnerConnectionString))
        {
            await conexion.OpenAsync();

            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                $"""
                CREATE ROLE {RolApp} LOGIN PASSWORD '{PasswordApp}' NOSUPERUSER NOBYPASSRLS;
                GRANT USAGE ON SCHEMA public TO {RolApp};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {RolApp};
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {RolApp};
                """;
            await comando.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(OwnerConnectionString)
        {
            Username = RolApp,
            Password = PasswordApp
        };

        return builder.ConnectionString;
    }

    /// <summary>La API bajo prueba corre con <c>ways_app</c>, no con el dueño de las
    /// tablas — igual que en producción, donde un rol sin <c>BYPASSRLS</c> es la única
    /// conexión que la aplicación usa.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(
            [
                new("ConnectionStrings:Ways", AppConnectionString)
            ]);
        });
    }

    /// <summary>Un <see cref="WaysDbContext"/> nuevo contra <c>ways_app</c>, con el
    /// <see cref="ITenantActual"/> que pida la prueba — para ejercer la capa 1 (filtro de
    /// EF) igual que lo hace la API, sin pasar por HTTP.
    ///
    /// Tiene que registrar <see cref="InterceptorDeContextoDeTenant"/> a mano, igual que
    /// <c>DependencyInjection.AgregarInfrastructure</c> lo hace vía DI en producción: sin
    /// el interceptor nunca corre el <c>set_config</c> de conexión, el GUC queda sin
    /// setear y hasta un contexto en modo plataforma se topa con <c>WITH CHECK</c> — este
    /// helper es el único lugar donde ese cableado se arma a mano, así que es el único
    /// lugar donde se puede (y se pudo) olvidar.</summary>
    public WaysDbContext CrearContextoDeAplicacion(ITenantActual tenantActual)
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

    /// <summary>Conexión cruda como <c>ways_app</c> con el GUC de tenant seteado a mano
    /// —lo mismo que hace <c>InterceptorDeContextoDeTenant</c>, pero sin pasar por EF: es
    /// la prueba de que la capa 2 (RLS) no depende del ORM.</summary>
    public async Task<NpgsqlConnection> AbrirConexionCrudaAsync(string modo, int? idTenant)
    {
        var conexion = new NpgsqlConnection(AppConnectionString);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT set_config('app.acceso', $1, false), set_config('app.tenant_id', $2, false)";
        comando.Parameters.Add(new NpgsqlParameter { Value = modo });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant?.ToString() ?? string.Empty });
        await comando.ExecuteNonQueryAsync();

        return conexion;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _contenedor.DisposeAsync();
    }
}
