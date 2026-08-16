using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure;
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
        // Ver el comentario de ConfigureWebHost: el mismo trámite de "modelo adelantado a
        // las migraciones" del gate #3 pendiente aplica acá, así que reusa el mismo helper.
        var opciones = new DbContextOptionsBuilder<WaysDbContext>();
        ConfigurarNpgsqlDePrueba(opciones, OwnerConnectionString);

        await using var db = new WaysDbContext(opciones.Options, TenantActualFijo.Plataforma);
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

        // Slice 3 (stage-1-organization-and-catalogs) agregó el modelo de catálogos
        // (Ways.Domain.Catalogos) antes del gate #3: el modelo en C# ya conoce esas tablas,
        // la migración 3 (CatalogosDeTenant) todavía no existe — es el estado "modelo
        // adelantado a las migraciones" que el DB CHANGE GATE deja a propósito a mitad de
        // desarrollo (mismo trámite documentado en el batch 6 de slice 2, ver
        // MigrarComoOwnerAsync). InicializadorDeBaseDeDatos llama Database.MigrateAsync()
        // en cada arranque del host — también con este DbContext, no solo con el de
        // MigrarComoOwnerAsync — y EF Core 8+ tira PendingModelChangesWarning en ese caso.
        //
        // En vez de suprimirlo en DependencyInjection.ConfigurarNpgsql (código de
        // producción, que NO se toca), acá se reemplazan los dos registros de DI que arma
        // AgregarInfrastructure para este host de prueba únicamente — mismas dos piezas,
        // con la supresión agregada. Se saca solo cuando la migración 3 aterrice y el
        // snapshot vuelva a coincidir con el modelo.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WaysDbContext>>();
            services.RemoveAll<WaysDbContext>();

            // AddDbContext no reemplaza la configuración anterior: desde EF Core 9 registra
            // el delegate de opciones como IDbContextOptionsConfiguration<TContext>, un
            // servicio ACUMULATIVO (pensado para composición modular). Sin sacar también
            // esto, el MapEnum de AgregarInfrastructure (producción) y el de acá se aplican
            // los dos sobre el mismo DbContextOptions final — cada enum queda mapeado dos
            // veces y Npgsql tira "Sequence contains more than one matching element" al
            // buscar la mapping. RemoveAll<DbContextOptions<WaysDbContext>>() por sí solo no
            // alcanza porque ese es un servicio distinto.
            services.RemoveAll<IDbContextOptionsConfiguration<WaysDbContext>>();

            services.AddDbContext<WaysDbContext>((sp, options) =>
            {
                ConfigurarNpgsqlDePrueba(options, AppConnectionString);
                options.AddInterceptors(sp.GetRequiredService<InterceptorDeContextoDeTenant>());
            });

            services.AddKeyedScoped<WaysDbContext>(
                DependencyInjection.ClaveContextoPlataforma,
                (_, _) =>
                {
                    var options = new DbContextOptionsBuilder<WaysDbContext>();
                    ConfigurarNpgsqlDePrueba(options, AppConnectionString);
                    options.AddInterceptors(new InterceptorDeContextoDeTenant(TenantActualFijo.Plataforma));
                    return new WaysDbContext(options.Options, TenantActualFijo.Plataforma);
                });
        });
    }

    private static void ConfigurarNpgsqlDePrueba(DbContextOptionsBuilder options, string cadena) =>
        options
            .UseNpgsql(cadena, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
                npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
                npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(3), null);
            })
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

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
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
                npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

    /// <summary>Un <see cref="WaysDbContext"/> sobre <c>ways_owner</c> (bypassea RLS) — el único
    /// mecanismo que puede aislar sobre esta conexión es el query filter de EF, nunca RLS. Usado
    /// por las pruebas que necesitan probar genuinamente el filtro de EF de tenant, no la
    /// política RLS (ya cubierta por las pruebas sobre <c>ways_app</c>).
    ///
    /// Judgment-day (juez B, slice 5, sugerencia): hoisteado desde <c>LotesRlsTests</c> —
    /// duplicaba 15 líneas de <c>MapEnum</c> con <c>AuditoriaConsultaTests</c>, y crecía con cada
    /// enum nuevo. El slice 6 lo va a reusar también.</summary>
    public WaysDbContext CrearContextoDeOwner(ITenantActual tenantActual)
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(OwnerConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
                npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
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
