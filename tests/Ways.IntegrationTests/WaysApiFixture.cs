using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Ways.IntegrationTests;

/// <summary>
/// Levanta un Postgres real en contenedor y la API completa contra él (ADR-17): las
/// pruebas de aislamiento tienen que probar RLS sobre una conexión que genuinamente no
/// puede saltarla, no sobre un doble en memoria.
///
/// Scaffold del task 1.16. Los tests que dependen de esta fixture están marcados
/// <c>Skip</c> hasta que exista la migración 1 (Organización) — sin ella
/// <c>MigrateAsync</c> no crea ninguna tabla y todo lo que siga falla con
/// "relation does not exist", no con una aserción útil. Ver el resumen del DB CHANGE
/// GATE en el mensaje final del apply para el modelo pendiente de aprobación.
///
/// Dos roles se provisionan en el contenedor (ADR-17): <c>OwnerConnectionString</c>
/// (dueño de las tablas, corre las migraciones) y <c>AppConnectionString</c>
/// (<c>ways_app</c>, <c>NOSUPERUSER NOBYPASSRLS</c> — la única conexión bajo la cual una
/// prueba de RLS prueba algo real).
/// </summary>
public sealed class WaysApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _contenedor = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("ways_test")
        .WithUsername("ways_owner")
        .WithPassword("ways_owner")
        .Build();

    public string OwnerConnectionString => _contenedor.GetConnectionString();

    /// <summary>Poblada en <see cref="InitializeAsync"/>, después de crear el rol
    /// <c>ways_app</c> sin <c>BYPASSRLS</c> en el contenedor recién levantado.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _contenedor.StartAsync();

        // TODO (post-gate): CREATE ROLE ways_app LOGIN NOSUPERUSER NOBYPASSRLS ...
        // y GRANT sobre las tablas creadas por la migración 1, una vez que exista.
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(
            [
                new("ConnectionStrings:Ways", OwnerConnectionString)
            ]);
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _contenedor.DisposeAsync();
    }
}
