using Npgsql;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 1 (task 1.18): prueba que la migración
/// <c>LotesYVencimientosEtapa12</c> aplica limpiamente sobre un Postgres 17 fresco —
/// <see cref="WaysApiFixture.InitializeAsync"/> ya corre <c>Database.MigrateAsync()</c> como
/// <c>ways_owner</c> antes de que exista ningún cliente HTTP (todo test de esta suite ya ejerce
/// esa aplicación); acá se afirma explícitamente el post-estado: las dos tablas nuevas, las seis
/// columnas aditivas y los dos valores nuevos de <c>motivo_stock</c> existen post-<c>Up()</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class LotesMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<NpgsqlConnection> AbrirAsync()
    {
        using var _ = fixture.CreateClient(); // fuerza el arranque del host (siembra + migración ya corrida en InitializeAsync)
        return await fixture.AbrirConexionCrudaAsync("plataforma", null);
    }

    [Theory]
    [InlineData("lotes")]
    [InlineData("stock_lotes")]
    public async Task LaTablaExistePostMigracion(string tabla)
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = tabla });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(1, total);
    }

    public static TheoryData<string, string> ColumnasAditivas => new()
    {
        { "movimientos_stock", "id_lote" },
        { "articulos", "controla_lote" },
        { "items_comprobante_venta", "id_lote" },
        { "items_comprobante_compra", "codigo_lote" },
        { "items_comprobante_compra", "fecha_vencimiento" },
        { "items_comprobante_compra", "id_lote" }
    };

    [Theory]
    [MemberData(nameof(ColumnasAditivas))]
    public async Task LaColumnaAditivaExistePostMigracion(string tabla, string columna)
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = $1 AND column_name = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = tabla });
        comando.Parameters.Add(new NpgsqlParameter { Value = columna });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(1, total);
    }

    [Theory]
    [InlineData("decomiso")]
    [InlineData("reclasificacion")]
    public async Task ElValorDeEnumExistePostMigracion(string valor)
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_enum e JOIN pg_type t ON e.enumtypid = t.oid " +
            "WHERE t.typname = 'motivo_stock' AND e.enumlabel = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = valor });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(1, total);
    }

    /// <summary>Ocho motivos en total (seis previos + los dos de esta etapa) — asegura que la
    /// migración no perdió ninguno de los preexistentes al reescribir el enum.</summary>
    [Fact]
    public async Task ElEnumMotivoStockTieneLosOchoValores()
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_enum e JOIN pg_type t ON e.enumtypid = t.oid " +
            "WHERE t.typname = 'motivo_stock'";

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(8, total);
    }
}
