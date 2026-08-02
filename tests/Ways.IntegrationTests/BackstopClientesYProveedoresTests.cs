using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Stage-2-clientes-proveedores, Slice 1 (tasks 1.16-1.17, db-error-backstops skill):
/// backstop map de <c>ManejadorDeErrores</c> — la constraint <c>ck_clientes_cf_protegido</c>
/// (23514) y una prueba de humo por cada FK nueva (23503).
///
/// GATED: mismo motivo que <c>ClientesYProveedoresRlsTests</c> — bloqueado hasta que la
/// migración <c>ClientesYProveedoresEtapa2</c> se genere y apruebe (DB CHANGE GATE, task 1.7).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class BackstopClientesYProveedoresTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string RazonDeGate =
        "Gated: clientes/proveedores no existen hasta que la migración " +
        "ClientesYProveedoresEtapa2 se genere y apruebe (DB CHANGE GATE, task 1.7).";

    private async Task<(int IdTenant, int IdCondicionFiscal, int IdListaPrecio)> SembrarTenantConCatalogosAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);

        var lista = new ListaPrecio
        {
            IdTenant = tenant.Id, Nombre = nombre, EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return (tenant.Id, condicionFiscal.Id, lista.Id);
    }

    /// <summary>Spec: Consumidor Final Protected Row — bypass directo del servicio (raw SQL,
    /// no <c>ServicioDeClientes</c>) sobre la fila Consumidor Final asserts 23514.</summary>
    [Fact(Skip = RazonDeGate)]
    public async Task UnUpdateDirectoPorSqlSobreElConsumidorFinalViolaLaCheckConstraint()
    {
        var (idTenant, idCondicionFiscal, idListaPrecio) =
            await SembrarTenantConCatalogosAsync(nameof(UnUpdateDirectoPorSqlSobreElConsumidorFinalViolaLaCheckConstraint));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        db.Clientes.Add(new Cliente
        {
            IdTenant = idTenant,
            Numero = ReglaDeClientes.NumeroConsumidorFinal,
            Nombre = "Consumidor Final",
            IdCondicionFiscal = idCondicionFiscal,
            IdListaPrecio = idListaPrecio,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE clientes SET deleted_at = now() WHERE numero = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = ReglaDeClientes.NumeroConsumidorFinal });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_clientes_cf_protegido", excepcion.ConstraintName);
    }

    [Fact(Skip = RazonDeGate)]
    public async Task UnClienteConIdListaPrecioInexistenteViolaLaFk()
    {
        var (idTenant, idCondicionFiscal, _) =
            await SembrarTenantConCatalogosAsync(nameof(UnClienteConIdListaPrecioInexistenteViolaLaFk));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO clientes (id_tenant, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
            "VALUES ($1, 2, 'intruso', $2, 999999, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscal });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_clientes_lista_precio", excepcion.ConstraintName);
    }

    [Fact(Skip = RazonDeGate)]
    public async Task UnClienteConIdCondicionFiscalInexistenteViolaLaFk()
    {
        var (idTenant, _, idListaPrecio) =
            await SembrarTenantConCatalogosAsync(nameof(UnClienteConIdCondicionFiscalInexistenteViolaLaFk));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO clientes (id_tenant, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
            "VALUES ($1, 2, 'intruso', 999999, $2, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idListaPrecio });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_clientes_condicion_fiscal", excepcion.ConstraintName);
    }

    [Fact(Skip = RazonDeGate)]
    public async Task UnProveedorConIdCondicionFiscalInexistenteViolaLaFk()
    {
        var (idTenant, _, _) =
            await SembrarTenantConCatalogosAsync(nameof(UnProveedorConIdCondicionFiscalInexistenteViolaLaFk));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, created_at, updated_at) " +
            "VALUES ($1, 'intruso', 999999, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_proveedores_condicion_fiscal", excepcion.ConstraintName);
    }

    [Theory(Skip = RazonDeGate)]
    [InlineData("clientes", "fk_clientes_tenant")]
    [InlineData("proveedores", "fk_proveedores_tenant")]
    [InlineData("listas_precio", "fk_listas_precio_tenant")]
    [InlineData("numeraciones_clientes", "fk_numeraciones_clientes_tenant")]
    public async Task UnIdTenantInexistenteEnCadaTablaNuevaViolaSuFk(string tabla, string nombreDeFk)
    {
        _ = nombreDeFk;
        using var host = fixture.CreateClient();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = tabla switch
        {
            "clientes" =>
                "INSERT INTO clientes (id_tenant, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
                "VALUES (999999, 2, 'intruso', 1, 1, now(), now())",
            "proveedores" =>
                "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, created_at, updated_at) " +
                "VALUES (999999, 'intruso', 1, now(), now())",
            "listas_precio" =>
                "INSERT INTO listas_precio (id_tenant, nombre, es_default, modo, activo, created_at, updated_at) " +
                "VALUES (999999, 'intrusa', false, 'fija', true, now(), now())",
            "numeraciones_clientes" =>
                "INSERT INTO numeraciones_clientes (id_tenant, proximo_numero) VALUES (999999, 1)",
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
    }
}
