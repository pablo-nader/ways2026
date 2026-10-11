using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Combos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Aislamiento por tenant de <c>combos</c> y <c>combos_componentes</c>, mismo patrón que
/// <c>OfertasRlsTests</c>: SQL crudo sobre la conexión NOBYPASSRLS de la app (0 filas para
/// SELECT/UPDATE cross-tenant, 42501 para el INSERT que viola <c>WITH CHECK</c>) más una prueba
/// a nivel EF: la lectura por el ORM no cruza tenants (RLS y filtro juntos) y, aparte, el SQL de
/// cada DbSet lleva el predicado de tenant del filtro de EF (RLS por sí sola no lo probaría).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "combos", "id_combo" },
        { "combos_componentes", "id_combo_componente" }
    };

    private async Task<(int IdTenantA, int IdFilaDeA, int IdTenantB, int IdComboDeA)> SembrarFilaDeTenantAAsync(
        string tabla, string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra alicuotas_iva)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = tenantA.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = tenantA.Id,
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

        var combo = new Combo
        {
            IdTenant = tenantA.Id,
            Codigo = 1,
            Nombre = nombre,
            Precio = 5000m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Combos.Add(combo);
        await db.SaveChangesAsync();

        var componente = new ComboComponente
        {
            IdTenant = tenantA.Id, IdCombo = combo.Id, IdArticulo = articulo.Id, Cantidad = 2m
        };
        db.CombosComponentes.Add(componente);
        await db.SaveChangesAsync();

        var idFila = tabla switch
        {
            "combos" => combo.Id,
            "combos_componentes" => componente.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        return (tenantA.Id, idFila, tenantB.Id, combo.Id);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoVeLaFilaPorSelect(string tabla, string columnaId)
    {
        var (idTenantA, idFila, idTenantB, _) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        Assert.Equal(0L, (long)(await comando.ExecuteScalarAsync())!);
        Assert.NotEqual(idTenantA, idTenantB);

        await using var propia = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);
        await using var control = propia.CreateCommand();
        control.CommandText = $"SELECT count(*) FROM {tabla} WHERE {columnaId} = $1";
        control.Parameters.Add(new NpgsqlParameter { Value = idFila });
        Assert.Equal(1L, (long)(await control.ExecuteScalarAsync())!);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila(string tabla, string columnaId)
    {
        var (_, idFila, idTenantB, _) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        // combos_componentes no tiene updated_at: el UPDATE toca cantidad. Da lo mismo qué
        // columna; lo que se prueba es que USING oculta la fila antes de que el UPDATE la alcance.
        comando.CommandText = tabla == "combos_componentes"
            ? $"UPDATE {tabla} SET cantidad = cantidad WHERE {columnaId} = $1"
            : $"UPDATE {tabla} SET updated_at = now() WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        Assert.Equal(0, await comando.ExecuteNonQueryAsync());
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnInsertConIdTenantAjenoSeRechazaConWithCheck(string tabla, string columnaId)
    {
        _ = columnaId;
        var (idTenantA, _, idTenantB, idComboDeA) = await SembrarFilaDeTenantAAsync(
            tabla, nameof(UnInsertConIdTenantAjenoSeRechazaConWithCheck) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = tabla switch
        {
            "combos" =>
                "INSERT INTO combos (id_tenant, codigo, nombre, precio, created_at, updated_at) " +
                "VALUES ($1, 2, 'intruso', 100, now(), now())",
            "combos_componentes" =>
                "INSERT INTO combos_componentes (id_tenant, id_combo, id_articulo, cantidad) " +
                "VALUES ($1, $2, 999999, 1)",
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        comando.Parameters.Add(new NpgsqlParameter { Value = idTenantB });
        if (tabla == "combos_componentes")
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = idComboDeA });
        }

        // 42501 = violación de WITH CHECK; se dispara antes que cualquier FK (999999 no existe).
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public void ElSqlDeEfDeCombosYComponentesLlevaElPredicadoDeTenantDelFiltro()
    {
        using var _ = fixture.CreateClient();
        using var sesion = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, 1));

        foreach (var sql in new[]
        {
            sesion.Combos.Where(c => c.Id == 1).ToQueryString(),
            sesion.CombosComponentes.Where(c => c.IdCombo == 1).ToQueryString()
        })
        {
            var donde = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
            Assert.Contains("id_tenant", donde, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LaLecturaPorEfNoDevuelveCombosNiComponentesDeOtroTenant()
    {
        var (idTenantA, idComboDeA, idTenantB, _) = await SembrarFilaDeTenantAAsync(
            "combos", nameof(LaLecturaPorEfNoDevuelveCombosNiComponentesDeOtroTenant));

        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantA));
        Assert.Single(await sesionA.Combos.Where(c => c.Id == idComboDeA).ToListAsync());
        Assert.Single(await sesionA.CombosComponentes.Where(c => c.IdCombo == idComboDeA).ToListAsync());

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB));
        Assert.Empty(await sesionB.Combos.Where(c => c.Id == idComboDeA).ToListAsync());
        Assert.Empty(await sesionB.CombosComponentes.Where(c => c.IdCombo == idComboDeA).ToListAsync());
    }
}
