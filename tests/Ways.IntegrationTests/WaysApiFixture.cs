using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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

        // Ver el comentario de ConfigureWebHost: esto (no el override de configuración de
        // ahí) es lo que de verdad hace que Program.cs se conecte al contenedor.
        Environment.SetEnvironmentVariable("ConnectionStrings__Ways", AppConnectionString);
    }

    /// <summary>Corre las migraciones existentes directamente contra el contenedor, con el
    /// rol dueño — igual que <c>InicializadorDeBaseDeDatos</c> en producción, pero antes de
    /// que exista el host de la API (que va a conectarse como <c>ways_app</c>).</summary>
    private async Task MigrarComoOwnerAsync()
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(OwnerConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
            })
            .Options;

        await using var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma);
        await db.Database.MigrateAsync();
    }

    /// <summary>ADR-5 / ADR-17: <c>ways_app</c> tiene los GRANTs de datos sobre lo que la
    /// migración creó, pero ni <c>SUPERUSER</c> ni <c>BYPASSRLS</c> — así <c>FORCE ROW
    /// LEVEL SECURITY</c> se prueba de verdad.
    ///
    /// El <c>CREATE</c> sobre el schema (agregado en batch 7, slice 2) hace falta porque
    /// <c>InicializadorDeBaseDeDatos.EjecutarAsync</c> llama a <c>Database.MigrateAsync()</c>
    /// en CADA arranque del host — también cuando arranca como <c>ways_app</c>, más allá de
    /// que <see cref="MigrarComoOwnerAsync"/> ya haya dejado el esquema al día como dueño.
    /// Postgres exige <c>CREATE</c> en el schema para siquiera intentar el
    /// <c>CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"</c> con el que EF verifica si
    /// hay migraciones pendientes, sin importar que la tabla ya exista. Esto no debilita la
    /// prueba de RLS (que depende de <c>NOSUPERUSER</c>/<c>NOBYPASSRLS</c>, no de los
    /// permisos de schema) y en los hechos representa mejor a producción: ADR-5 documenta
    /// que ahí el rol de la aplicación ES el dueño de las tablas.</summary>
    private async Task<string> CrearRolDeAplicacionAsync()
    {
        await using (var conexion = new NpgsqlConnection(OwnerConnectionString))
        {
            await conexion.OpenAsync();

            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                $"""
                CREATE ROLE {RolApp} LOGIN PASSWORD '{PasswordApp}' NOSUPERUSER NOBYPASSRLS;
                GRANT USAGE, CREATE ON SCHEMA public TO {RolApp};
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
    /// conexión que la aplicación usa.
    ///
    /// El <c>ConfigureAppConfiguration</c> de acá solo, encontrado en batch 7 (slice 2), NO
    /// alcanza: <c>Program.cs</c> es hosting mínimo (<c>WebApplication.CreateBuilder</c>) y
    /// lee <c>builder.Configuration</c> de forma síncrona dentro de sus propios
    /// top-level statements (<c>AgregarInfrastructure</c>), antes de que
    /// <c>WebApplicationFactory</c> tenga la oportunidad de inyectar este override — se
    /// confirmó agregando un log temporal, que mostró la cadena de conexión de
    /// <c>appsettings.json</c> (<c>localhost:5432</c>), no la del contenedor. La variable de
    /// entorno que fija <see cref="InitializeAsync"/> SÍ funciona: la lee
    /// <c>WebApplication.CreateBuilder</c> recién cuando <c>Program.Main</c> corre de verdad
    /// (al primer <c>CreateClient()</c>/acceso a <c>Server</c>), momento en el que la
    /// variable ya está seteada en el proceso. Se deja este override igual, como red
    /// adicional sin costo — no hace daño, y documenta la intención.</summary>
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
