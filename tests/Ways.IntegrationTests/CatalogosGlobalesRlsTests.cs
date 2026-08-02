using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// DB CHANGE GATE #4 (2026-08-01): el usuario restauró la defensa en profundidad de RLS sobre
/// los 3 catálogos globales, override de ADR-11 (design.md) — lectura para todos (incluido un
/// modo <c>tenant</c>), escritura restringida a la plataforma
/// (<see cref="RlsMigrationBuilderExtensions.HabilitarRlsDeCatalogoGlobal"/>). La superficie de
/// API sigue siendo de solo lectura para un tenant, sin cambios — esto prueba la segunda capa
/// independiente detrás. Todo con conexión cruda como <c>ways_app</c>, sin pasar por EF.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogosGlobalesRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task SembrarCondicionFiscalAsync(string codigo)
    {
        using var _ = fixture.CreateClient(); // arranca el host

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        db.CondicionesFiscales.Add(new CondicionFiscal
        {
            Codigo = codigo,
            Nombre = codigo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UnaSesionDeTenantPuedeLeerUnCatalogoGlobal()
    {
        await SembrarCondicionFiscalAsync(nameof(UnaSesionDeTenantPuedeLeerUnCatalogoGlobal));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", 1);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM condiciones_fiscales WHERE codigo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = nameof(UnaSesionDeTenantPuedeLeerUnCatalogoGlobal) });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(1, total);
    }

    [Fact]
    public async Task UnaSesionDeTenantNoPuedeInsertarEnUnCatalogoGlobal()
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", 1);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO condiciones_fiscales (codigo, nombre, created_at, updated_at) " +
            "VALUES ($1, $2, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = "INTRUSA" });
        comando.Parameters.Add(new NpgsqlParameter { Value = "Intrusa" });

        // 42501 = insufficient_privilege: WITH CHECK de la policy de escritura rechaza el
        // INSERT porque app_es_plataforma() es falso en modo tenant.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task UnaSesionDeTenantNoPuedeActualizarUnCatalogoGlobal()
    {
        await SembrarCondicionFiscalAsync(nameof(UnaSesionDeTenantNoPuedeActualizarUnCatalogoGlobal));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", 1);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE condiciones_fiscales SET nombre = 'hackeado' WHERE codigo = $1";
        comando.Parameters.Add(
            new NpgsqlParameter { Value = nameof(UnaSesionDeTenantNoPuedeActualizarUnCatalogoGlobal) });

        // 0 filas, no una excepción — mecánica real de RLS de Postgres, no lo que se pidió
        // originalmente (ver nota en apply-progress.md): la policy de lectura es FOR SELECT
        // únicamente, no participa del filtro de visibilidad de un UPDATE (eso lo gobierna
        // solo el USING de la policy FOR ALL de escritura). Con app_es_plataforma() en falso,
        // la fila queda invisible para el UPDATE antes de llegar a evaluar WITH CHECK — mismo
        // mecanismo, mismo resultado en el código, que el caso cross-tenant de
        // AislamientoDeTenantTests. La garantía de seguridad es idéntica: la fila no se toca.
        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);

        await using var verificacion = cruda.CreateCommand();
        verificacion.CommandText = "SELECT nombre FROM condiciones_fiscales WHERE codigo = $1";
        verificacion.Parameters.Add(
            new NpgsqlParameter { Value = nameof(UnaSesionDeTenantNoPuedeActualizarUnCatalogoGlobal) });
        var nombreActual = (string)(await verificacion.ExecuteScalarAsync())!;
        Assert.Equal(nameof(UnaSesionDeTenantNoPuedeActualizarUnCatalogoGlobal), nombreActual);
    }

    [Fact]
    public async Task UnaSesionDeTenantNoPuedeBorrarDeUnCatalogoGlobal()
    {
        await SembrarCondicionFiscalAsync(nameof(UnaSesionDeTenantNoPuedeBorrarDeUnCatalogoGlobal));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", 1);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "DELETE FROM condiciones_fiscales WHERE codigo = $1";
        comando.Parameters.Add(
            new NpgsqlParameter { Value = nameof(UnaSesionDeTenantNoPuedeBorrarDeUnCatalogoGlobal) });

        // DELETE no tiene WITH CHECK en Postgres — solo USING gobierna qué filas puede tocar.
        // Mismo mecanismo que el UPDATE de arriba: 0 filas borradas, no una excepción.
        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Fact]
    public async Task LaPlataformaPuedeEscribirEnUnCatalogoGlobal()
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO condiciones_fiscales (codigo, nombre, created_at, updated_at) " +
            "VALUES ($1, $2, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = nameof(LaPlataformaPuedeEscribirEnUnCatalogoGlobal) });
        comando.Parameters.Add(new NpgsqlParameter { Value = "Desde plataforma" });

        var filas = await comando.ExecuteNonQueryAsync();

        Assert.Equal(1, filas);
    }

    [Fact]
    public async Task SinContextoResueltoNoSePuedeEscribirEnUnCatalogoGlobal()
    {
        // Falla cerrado (ADR-4): un GUC sin setear hace que app_es_plataforma() sea falso, lo
        // mismo que en modo tenant — la lectura sigue abierta (USING (true) no depende del
        // GUC), pero la escritura se rechaza igual.
        await using var cruda = await fixture.AbrirConexionCrudaAsync(string.Empty, null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO condiciones_fiscales (codigo, nombre, created_at, updated_at) " +
            "VALUES ($1, $2, now(), now())";
        comando.Parameters.Add(
            new NpgsqlParameter { Value = nameof(SinContextoResueltoNoSePuedeEscribirEnUnCatalogoGlobal) });
        comando.Parameters.Add(new NpgsqlParameter { Value = "Sin contexto" });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }
}
