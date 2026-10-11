using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-4-ofertas, Slice 1 (task 1.10, db-error-backstops skill, design: Backstop Map
/// reachability note): raw-SQL INSERTs que bypasean por completo <c>ReglaDeOfertas</c> (no hay
/// <c>ServicioDeOfertas</c> todavía, Slice 2) para probar las cuatro CHECKs de esquema
/// directamente — SQLSTATE 23514 más el <c>ConstraintName</c> exacto que
/// <c>ManejadorDeErrores.ClasificarCheckDeOfertas</c> traduce (mismo patrón que
/// <c>BackstopClientesYProveedoresTests.UnUpdateDirectoPorSqlSobreElConsumidorFinalViolaLaCheckConstraint</c>:
/// sin endpoint/servicio en este lote, el <c>ConstraintName</c> asertado ES la prueba de que la
/// rama de <c>ManejadorDeErrores</c> matchea, porque el switch de acá es por nombre exacto).
///
/// Honesto sobre alcanzabilidad (design: Backstop Map): bajo operación normal (Slice 2 en
/// adelante) ninguna de las cuatro ramas es alcanzable — <c>ReglaDeOfertas</c> ya rechaza los
/// cuatro estados en el camino de servicio. Esto prueba la traducción de esquema, no un camino
/// de cliente real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OfertasCheckBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<(int IdTenant, int IdArticulo)> SembrarTenantConArticuloAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra alicuotas_iva)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = tenant.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = tenant.Id,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return (tenant.Id, articulo.Id);
    }

    [Fact]
    public async Task UnaOfertaSinNingunAlcanceSeteadoViolaLaCheckDeExclusividad()
    {
        var (idTenant, _) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaSinNingunAlcanceSeteadoViolaLaCheckDeExclusividad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, porcentaje, created_at, updated_at) " +
            "VALUES ($1, 'sin-alcance', 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_alcance_exclusivo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaOfertaConDosAlcancesSeteadosViolaLaCheckDeExclusividad()
    {
        var (idTenant, idArticulo) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConDosAlcancesSeteadosViolaLaCheckDeExclusividad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, id_articulo, id_grupo, porcentaje, created_at, updated_at) " +
            "VALUES ($1, 'doble-alcance', $2, 999999, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_alcance_exclusivo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaOfertaSinNingunBeneficioSeteadoViolaLaCheckDeExclusividad()
    {
        var (idTenant, idArticulo) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaSinNingunBeneficioSeteadoViolaLaCheckDeExclusividad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, id_articulo, created_at, updated_at) " +
            "VALUES ($1, 'sin-beneficio', $2, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_beneficio_exclusivo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaOfertaConDosBeneficiosSeteadosViolaLaCheckDeExclusividad()
    {
        var (idTenant, idArticulo) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConDosBeneficiosSeteadosViolaLaCheckDeExclusividad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, id_articulo, porcentaje, importe_fijo, created_at, updated_at) " +
            "VALUES ($1, 'doble-beneficio', $2, 10, 5, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_beneficio_exclusivo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaOfertaConFechaHastaAnteriorAFechaDesdeViolaLaCheckDeVentana()
    {
        var (idTenant, idArticulo) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConFechaHastaAnteriorAFechaDesdeViolaLaCheckDeVentana));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, id_articulo, porcentaje, fecha_desde, fecha_hasta, created_at, updated_at) " +
            "VALUES ($1, 'ventana-invertida', $2, 10, '2026-08-10', '2026-08-01', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_ventana_valida", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaOfertaConHoraHastaAnteriorAHoraDesdeViolaLaCheckDeVentana()
    {
        var (idTenant, idArticulo) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConHoraHastaAnteriorAHoraDesdeViolaLaCheckDeVentana));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, id_articulo, porcentaje, hora_desde, hora_hasta, created_at, updated_at) " +
            "VALUES ($1, 'hora-invertida', $2, 10, '14:00', '10:00', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_ventana_valida", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaOfertaConUnDiaDeSemanaFueraDeRangoViolaLaCheckDeDiasSemana()
    {
        var (idTenant, idArticulo) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConUnDiaDeSemanaFueraDeRangoViolaLaCheckDeDiasSemana));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, id_articulo, porcentaje, dias_semana, created_at, updated_at) " +
            "VALUES ($1, 'dia-invalido', $2, 10, ARRAY[8]::smallint[], now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_dias_semana", excepcion.ConstraintName);
    }

    private async Task<int> SembrarFamiliaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var familia = new Familia { IdTenant = idTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();

        return familia.Id;
    }

    /// <summary>La CHECK de cuatro columnas admite exactamente una: una oferta de familia sola entra. Sin esta prueba
    /// una CHECK que no aceptara la columna nueva pasaría las demás, que solo miran rechazos.</summary>
    [Fact]
    public async Task UnaOfertaConSoloFamiliaEsAceptadaPorLaCheckDeExclusividad()
    {
        var (idTenant, _) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConSoloFamiliaEsAceptadaPorLaCheckDeExclusividad));
        var idFamilia = await SembrarFamiliaAsync(idTenant, "Remeras");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas (id_tenant, nombre, id_familia, porcentaje, created_at, updated_at) " +
            "VALUES ($1, 'solo-familia', $2, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idFamilia });

        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("id_articulo")]
    [InlineData("id_grupo")]
    [InlineData("id_categoria")]
    public async Task UnaOfertaConFamiliaYOtroAlcanceSeteadoViolaLaCheckDeExclusividad(string otraColumna)
    {
        var (idTenant, _) = await SembrarTenantConArticuloAsync(
            nameof(UnaOfertaConFamiliaYOtroAlcanceSeteadoViolaLaCheckDeExclusividad) + otraColumna);
        var idFamilia = await SembrarFamiliaAsync(idTenant, "Remeras");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            $"INSERT INTO ofertas (id_tenant, nombre, id_familia, {otraColumna}, porcentaje, created_at, updated_at) " +
            "VALUES ($1, 'familia-y-otro', $2, 999999, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idFamilia });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ofertas_alcance_exclusivo", excepcion.ConstraintName);
    }

    /// <summary>La FK compuesta <c>(id_familia, id_tenant)</c>: un id que no existe y el id de una familia de OTRO
    /// tenant dan el mismo <c>23503</c> sobre <c>fk_ofertas_familia</c>, que <c>ManejadorDeErrores</c> traduce a
    /// 400 <c>referencia_invalida</c> con su mapeo genérico. El pre-chequeo del servicio los rechaza antes: esto
    /// prueba la red de la base.</summary>
    [Fact]
    public async Task UnaOfertaConFamiliaInexistenteOAjenaViolaLaFkCompuesta()
    {
        var (idTenant, _) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConFamiliaInexistenteOAjenaViolaLaFkCompuesta));
        var (idTenantAjeno, _) = await SembrarTenantConArticuloAsync(nameof(UnaOfertaConFamiliaInexistenteOAjenaViolaLaFkCompuesta) + "-ajeno");
        var idFamiliaAjena = await SembrarFamiliaAsync(idTenantAjeno, "Ajena");

        foreach (var idFamilia in new[] { 999_999_999, idFamiliaAjena })
        {
            await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO ofertas (id_tenant, nombre, id_familia, porcentaje, created_at, updated_at) " +
                "VALUES ($1, 'familia-colgante', $2, 10, now(), now())";
            comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = idFamilia });

            var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal("23503", excepcion.SqlState);
            Assert.Equal("fk_ofertas_familia", excepcion.ConstraintName);
        }
    }
}
