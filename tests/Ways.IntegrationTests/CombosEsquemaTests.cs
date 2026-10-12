using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Constraints de <c>combos</c> y <c>combos_componentes</c> (migración <c>Combos</c>) contra
/// Postgres real: SQL crudo que no pasa por <c>ReglaDeCombos</c>, asertando SQLSTATE y
/// <c>ConstraintName</c> exactos (skill db-error-backstops). Cubren el esquema y no la traducción a
/// HTTP, que están en <see cref="CombosRespaldosTests"/> y <c>ManejadorDeErroresCombosTests</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Semilla(
        int IdTenantA, int IdArticuloA1, int IdArticuloA2, int IdFamiliaA,
        int IdTenantB, int IdArticuloB, int IdFamiliaB, int IdEmpresaB);

    private async Task<Semilla> SembrarAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra alicuotas_iva)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var areaA = new Area { IdTenant = tenantA.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var areaB = new Area { IdTenant = tenantB.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.AddRange(areaA, areaB);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        Articulo NuevoArticulo(int idTenant, int idArea, string sufijo) => new()
        {
            IdTenant = idTenant,
            CodigoInterno = $"{nombre}-{sufijo}",
            Nombre = $"{nombre}-{sufijo}",
            IdArea = idArea,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        var articuloA1 = NuevoArticulo(tenantA.Id, areaA.Id, "a1");
        var articuloA2 = NuevoArticulo(tenantA.Id, areaA.Id, "a2");
        var articuloB = NuevoArticulo(tenantB.Id, areaB.Id, "b1");
        db.Articulos.AddRange(articuloA1, articuloA2, articuloB);

        var familiaA = new Familia { IdTenant = tenantA.Id, Nombre = $"{nombre} familia", CreatedAt = ahora, UpdatedAt = ahora };
        var familiaB = new Familia { IdTenant = tenantB.Id, Nombre = $"{nombre} familia", CreatedAt = ahora, UpdatedAt = ahora };
        db.Familias.AddRange(familiaA, familiaB);

        var empresaB = new Empresa { IdTenant = tenantB.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresaB);
        await db.SaveChangesAsync();

        return new Semilla(
            tenantA.Id, articuloA1.Id, articuloA2.Id, familiaA.Id,
            tenantB.Id, articuloB.Id, familiaB.Id, empresaB.Id);
    }

    private static NpgsqlParameter Entero(int? valor) =>
        new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = valor.HasValue ? valor.Value : DBNull.Value };

    private static NpgsqlParameter Numerico(decimal valor) =>
        new() { NpgsqlDbType = NpgsqlDbType.Numeric, Value = valor };

    private static async Task<int> InsertarComboAsync(
        NpgsqlConnection conexion, int idTenant, int codigo, decimal precio = 5000m, int? idEmpresa = null)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO combos (id_tenant, id_empresa, codigo, nombre, precio, created_at, updated_at) " +
            "VALUES ($1, $2, $3, 'combo de prueba', $4, now(), now()) RETURNING id_combo";
        comando.Parameters.Add(Entero(idTenant));
        comando.Parameters.Add(Entero(idEmpresa));
        comando.Parameters.Add(Entero(codigo));
        comando.Parameters.Add(Numerico(precio));
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task InsertarComponenteAsync(
        NpgsqlConnection conexion, int idTenant, int idCombo, int? idArticulo, int? idFamilia, decimal cantidad = 1m)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO combos_componentes (id_tenant, id_combo, id_articulo, id_familia, cantidad) " +
            "VALUES ($1, $2, $3, $4, $5)";
        comando.Parameters.Add(Entero(idTenant));
        comando.Parameters.Add(Entero(idCombo));
        comando.Parameters.Add(Entero(idArticulo));
        comando.Parameters.Add(Entero(idFamilia));
        comando.Parameters.Add(Numerico(cantidad));
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task AssertViolacionAsync(Func<Task> accion, string sqlState, string constraint)
    {
        var excepcion = await Assert.ThrowsAsync<PostgresException>(accion);
        Assert.Equal(sqlState, excepcion.SqlState);
        Assert.Equal(constraint, excepcion.ConstraintName);
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string sql, int parametro)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.Add(Entero(parametro));
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UnComboConPrecioNoPositivoViolaLaCheckDePrecio(int precio)
    {
        var s = await SembrarAsync(nameof(UnComboConPrecioNoPositivoViolaLaCheckDePrecio) + precio);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        await AssertViolacionAsync(
            () => InsertarComboAsync(cruda, s.IdTenantA, 1, precio),
            "23514", "ck_combos_precio_positivo");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UnComboConCodigoNoPositivoViolaLaCheckDeCodigo(int codigo)
    {
        var s = await SembrarAsync(nameof(UnComboConCodigoNoPositivoViolaLaCheckDeCodigo) + codigo);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        await AssertViolacionAsync(
            () => InsertarComboAsync(cruda, s.IdTenantA, codigo),
            "23514", "ck_combos_codigo_positivo");
    }

    [Fact]
    public async Task DosCombosVivosConElMismoCodigoDelTenantViolanElUnico()
    {
        var s = await SembrarAsync(nameof(DosCombosVivosConElMismoCodigoDelTenantViolanElUnico));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        await InsertarComboAsync(cruda, s.IdTenantA, 12);

        await AssertViolacionAsync(
            () => InsertarComboAsync(cruda, s.IdTenantA, 12),
            "23505", "ux_combos_codigo");
    }

    [Fact]
    public async Task ElCodigoDeUnComboDadoDeBajaQuedaLibreYElDeOtroTenantNuncaChoca()
    {
        var s = await SembrarAsync(nameof(ElCodigoDeUnComboDadoDeBajaQuedaLibreYElDeOtroTenantNuncaChoca));
        await using var crudaA = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idDadoDeBaja = await InsertarComboAsync(crudaA, s.IdTenantA, 12);

        await using (var baja = crudaA.CreateCommand())
        {
            baja.CommandText = "UPDATE combos SET deleted_at = now() WHERE id_combo = $1";
            baja.Parameters.Add(Entero(idDadoDeBaja));
            Assert.Equal(1, await baja.ExecuteNonQueryAsync());
        }

        var idNuevo = await InsertarComboAsync(crudaA, s.IdTenantA, 12);
        Assert.NotEqual(idDadoDeBaja, idNuevo);

        await using var crudaB = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantB);
        await InsertarComboAsync(crudaB, s.IdTenantB, 12);
    }

    [Fact]
    public async Task DosInsertsConcurrentesDelMismoCodigoDejanExactamenteUnoYRechazanAlOtroCon23505()
    {
        var s = await SembrarAsync(nameof(DosInsertsConcurrentesDelMismoCodigoDejanExactamenteUnoYRechazanAlOtroCon23505));
        await using var crudaUno = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        await using var crudaDos = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        async Task<string?> IntentarAsync(NpgsqlConnection conexion)
        {
            try
            {
                await InsertarComboAsync(conexion, s.IdTenantA, 77);
                return null;
            }
            catch (PostgresException ex)
            {
                return $"{ex.SqlState}/{ex.ConstraintName}";
            }
        }

        var resultados = await Task.WhenAll(IntentarAsync(crudaUno), IntentarAsync(crudaDos));

        Assert.Single(resultados, r => r is null);
        Assert.Single(resultados, r => r == "23505/ux_combos_codigo");
    }

    [Fact]
    public async Task UnComboConEmpresaDeOtroTenantViolaLaFkCompuestaDeEmpresa()
    {
        var s = await SembrarAsync(nameof(UnComboConEmpresaDeOtroTenantViolaLaFkCompuestaDeEmpresa));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        await AssertViolacionAsync(
            () => InsertarComboAsync(cruda, s.IdTenantA, 1, idEmpresa: s.IdEmpresaB),
            "23503", "fk_combos_empresa");
    }

    [Fact]
    public async Task UnComponenteSinArticuloNiFamiliaViolaLaCheckDeExclusividad()
    {
        var s = await SembrarAsync(nameof(UnComponenteSinArticuloNiFamiliaViolaLaCheckDeExclusividad));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, null, null),
            "23514", "ck_combos_componentes_alcance_exclusivo");
    }

    [Fact]
    public async Task UnComponenteConArticuloYFamiliaViolaLaCheckDeExclusividad()
    {
        var s = await SembrarAsync(nameof(UnComponenteConArticuloYFamiliaViolaLaCheckDeExclusividad));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloA1, s.IdFamiliaA),
            "23514", "ck_combos_componentes_alcance_exclusivo");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UnComponenteConCantidadNoPositivaViolaLaCheckDeCantidad(int cantidad)
    {
        var s = await SembrarAsync(nameof(UnComponenteConCantidadNoPositivaViolaLaCheckDeCantidad) + cantidad);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloA1, null, cantidad),
            "23514", "ck_combos_componentes_cantidad_positiva");
    }

    [Fact]
    public async Task ElMismoArticuloDosVecesEnUnComboViolaSuUnico()
    {
        var s = await SembrarAsync(nameof(ElMismoArticuloDosVecesEnUnComboViolaSuUnico));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloA1, null);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloA1, null),
            "23505", "ux_combos_componentes_articulo");
    }

    [Fact]
    public async Task LaMismaFamiliaDosVecesEnUnComboViolaSuUnico()
    {
        var s = await SembrarAsync(nameof(LaMismaFamiliaDosVecesEnUnComboViolaSuUnico));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, null, s.IdFamiliaA);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, null, s.IdFamiliaA),
            "23505", "ux_combos_componentes_familia");
    }

    [Fact]
    public async Task VariosArticulosYUnaFamiliaEnElMismoComboConviven()
    {
        var s = await SembrarAsync(nameof(VariosArticulosYUnaFamiliaEnElMismoComboConviven));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);

        await InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloA1, null);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloA2, null, 2m);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, null, s.IdFamiliaA);

        Assert.Equal(3, await ContarAsync(cruda, "SELECT count(*) FROM combos_componentes WHERE id_combo = $1", idCombo));
    }

    [Fact]
    public async Task UnArticuloQueNoExisteViolaLaFkDeArticulo()
    {
        var s = await SembrarAsync(nameof(UnArticuloQueNoExisteViolaLaFkDeArticulo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, 999999, null),
            "23503", "fk_combos_componentes_articulo");
    }

    [Fact]
    public async Task UnArticuloDeOtroTenantViolaLaFkCompuestaDeArticulo()
    {
        var s = await SembrarAsync(nameof(UnArticuloDeOtroTenantViolaLaFkCompuestaDeArticulo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloB, null),
            "23503", "fk_combos_componentes_articulo");
    }

    [Fact]
    public async Task UnaFamiliaQueNoExisteOEsDeOtroTenantViolaLaFkDeFamilia()
    {
        var s = await SembrarAsync(nameof(UnaFamiliaQueNoExisteOEsDeOtroTenantViolaLaFkDeFamilia));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);

        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, null, 999999),
            "23503", "fk_combos_componentes_familia");
        await AssertViolacionAsync(
            () => InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, null, s.IdFamiliaB),
            "23503", "fk_combos_componentes_familia");
    }

    [Fact]
    public async Task UnComponenteDeUnComboDeOtroTenantViolaLaFkCompuestaDeCombo()
    {
        var s = await SembrarAsync(nameof(UnComponenteDeUnComboDeOtroTenantViolaLaFkCompuestaDeCombo));
        await using var crudaB = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantB);
        var idComboDeB = await InsertarComboAsync(crudaB, s.IdTenantB, 1);

        await using var crudaA = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        await AssertViolacionAsync(
            () => InsertarComponenteAsync(crudaA, s.IdTenantA, idComboDeB, s.IdArticuloA1, null),
            "23503", "fk_combos_componentes_combo");
    }

    [Fact]
    public async Task NoSePuedeBorrarUnArticuloNiUnaFamiliaQueUnComboReferencia()
    {
        var s = await SembrarAsync(nameof(NoSePuedeBorrarUnArticuloNiUnaFamiliaQueUnComboReferencia));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idCombo = await InsertarComboAsync(cruda, s.IdTenantA, 1);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, s.IdArticuloA1, null);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idCombo, null, s.IdFamiliaA);

        async Task BorrarAsync(string tabla, string columna, int id)
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = $"DELETE FROM {tabla} WHERE {columna} = $1";
            comando.Parameters.Add(Entero(id));
            await comando.ExecuteNonQueryAsync();
        }

        await AssertViolacionAsync(
            () => BorrarAsync("articulos", "id_articulo", s.IdArticuloA1),
            "23503", "fk_combos_componentes_articulo");
        await AssertViolacionAsync(
            () => BorrarAsync("familias", "id_familia", s.IdFamiliaA),
            "23503", "fk_combos_componentes_familia");
    }

    [Fact]
    public async Task BorrarElComboBorraSusComponentesYNoTocaLosDeOtroCombo()
    {
        var s = await SembrarAsync(nameof(BorrarElComboBorraSusComponentesYNoTocaLosDeOtroCombo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var idComboBorrado = await InsertarComboAsync(cruda, s.IdTenantA, 1);
        var idComboHermano = await InsertarComboAsync(cruda, s.IdTenantA, 2);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idComboBorrado, s.IdArticuloA1, null);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idComboBorrado, s.IdArticuloA2, null);
        await InsertarComponenteAsync(cruda, s.IdTenantA, idComboHermano, s.IdArticuloA1, null);

        await using (var borrar = cruda.CreateCommand())
        {
            borrar.CommandText = "DELETE FROM combos WHERE id_combo = $1";
            borrar.Parameters.Add(Entero(idComboBorrado));
            Assert.Equal(1, await borrar.ExecuteNonQueryAsync());
        }

        Assert.Equal(0, await ContarAsync(cruda, "SELECT count(*) FROM combos_componentes WHERE id_combo = $1", idComboBorrado));
        Assert.Equal(1, await ContarAsync(cruda, "SELECT count(*) FROM combos_componentes WHERE id_combo = $1", idComboHermano));
    }
}
