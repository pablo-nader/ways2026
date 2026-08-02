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
/// DB CHANGE GATE aprobado 2026-08-02 y migración <c>ClientesYProveedoresEtapa2</c> aplicada:
/// pruebas activas contra Postgres real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class BackstopClientesYProveedoresTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
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
    [Fact]
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

    /// <summary>Judgment-day ronda 1 (item 1, db-error-backstops skill): prueba de backstop
    /// propiamente dicha para <c>ux_clientes_numero</c> — dos INSERTs crudos por SQL con el
    /// mismo <c>(id_tenant, numero)</c>, bypasseando <c>AsignadorDeNumeroCliente</c> por
    /// completo (nunca tocan <c>numeraciones_clientes</c>). Complementa, no reemplaza, a
    /// <c>ClientesEndpointsTests.LaCreacionConcurrenteAsignaNumerosSecuencialesSinExponerElBackstop</c>
    /// (Slice 2, task 2.5), que prueba la AUSENCIA de esta rama bajo concurrencia real vía el
    /// contador atómico — dos pruebas distintas para dos afirmaciones distintas (ver el
    /// comentario corregido en <c>ManejadorDeErrores.ClasificarUnicidad</c>).
    ///
    /// Nivel HTTP: no hay forma de ejercer este 409 a través de <c>POST /api/clientes</c> —
    /// <c>ServicioDeClientes.CrearAsync</c> siempre asigna <c>numero</c> vía el contador
    /// atómico, nunca acepta un <c>numero</c> de request; el bypass solo es alcanzable con SQL
    /// directo, como hace esta prueba.</summary>
    [Fact]
    public async Task UnaFilaConNumeroDuplicadoInsertadaPorFueraDelContadorViolaLaUnicidad()
    {
        var (idTenant, idCondicionFiscal, idListaPrecio) =
            await SembrarTenantConCatalogosAsync(nameof(UnaFilaConNumeroDuplicadoInsertadaPorFueraDelContadorViolaLaUnicidad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using (var primero = cruda.CreateCommand())
        {
            primero.CommandText =
                "INSERT INTO clientes (id_tenant, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
                "VALUES ($1, 2, 'primero', $2, $3, now(), now())";
            primero.Parameters.Add(new NpgsqlParameter { Value = idTenant });
            primero.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscal });
            primero.Parameters.Add(new NpgsqlParameter { Value = idListaPrecio });
            await primero.ExecuteNonQueryAsync();
        }

        await using var segundo = cruda.CreateCommand();
        segundo.CommandText =
            "INSERT INTO clientes (id_tenant, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
            "VALUES ($1, 2, 'duplicado', $2, $3, now(), now())";
        segundo.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        segundo.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscal });
        segundo.Parameters.Add(new NpgsqlParameter { Value = idListaPrecio });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => segundo.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_clientes_numero", excepcion.ConstraintName);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    /// <summary>Judgment-day ronda 1 (item 2, GATE-APPROVED 2026-08-02): prueba de humo de la
    /// FK compuesta <c>fk_clientes_lista_precio</c> — antes del hardening, un <c>id_lista_precio</c>
    /// de OTRO tenant era una fila EXISTENTE (pasaba la FK simple, que solo exige que el id
    /// exista en algún lado) y únicamente RLS lo frenaba en runtime. Con la FK compuesta
    /// <c>(id_lista_precio, id_tenant) → listas_precio(id_lista_precio, id_tenant)</c>, la
    /// propia constraint la rechaza.</summary>
    [Fact]
    public async Task UnClienteConIdListaPrecioDeOtroTenantViolaLaFkCompuesta()
    {
        var (idTenantA, idCondicionFiscalA, _) =
            await SembrarTenantConCatalogosAsync(nameof(UnClienteConIdListaPrecioDeOtroTenantViolaLaFkCompuesta) + "-A");
        var (_, _, idListaPrecioB) =
            await SembrarTenantConCatalogosAsync(nameof(UnClienteConIdListaPrecioDeOtroTenantViolaLaFkCompuesta) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO clientes (id_tenant, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
            "VALUES ($1, 2, 'intruso', $2, $3, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenantA });
        comando.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscalA });
        comando.Parameters.Add(new NpgsqlParameter { Value = idListaPrecioB });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_clientes_lista_precio", excepcion.ConstraintName);
    }

    /// <summary>Judgment-day ronda 1 (item 4): prueba de humo faltante para
    /// <c>fk_clientes_empresa</c> — <c>id_empresa</c> es nullable (MATCH SIMPLE), así que la
    /// prueba tiene que mandar un valor no nulo pero inexistente para disparar la FK.</summary>
    [Fact]
    public async Task UnClienteConIdEmpresaInexistenteViolaLaFkCompuesta()
    {
        var (idTenant, idCondicionFiscal, idListaPrecio) =
            await SembrarTenantConCatalogosAsync(nameof(UnClienteConIdEmpresaInexistenteViolaLaFkCompuesta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO clientes (id_tenant, id_empresa, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
            "VALUES ($1, 999999, 2, 'intruso', $2, $3, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscal });
        comando.Parameters.Add(new NpgsqlParameter { Value = idListaPrecio });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_clientes_empresa", excepcion.ConstraintName);
    }

    /// <summary>Judgment-day ronda 1 (item 4): prueba de humo faltante para
    /// <c>fk_proveedores_empresa</c>.</summary>
    [Fact]
    public async Task UnProveedorConIdEmpresaInexistenteViolaLaFkCompuesta()
    {
        var (idTenant, idCondicionFiscal, _) =
            await SembrarTenantConCatalogosAsync(nameof(UnProveedorConIdEmpresaInexistenteViolaLaFkCompuesta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO proveedores (id_tenant, id_empresa, razon_social, id_condicion_fiscal, created_at, updated_at) " +
            "VALUES ($1, 999999, 'intruso', $2, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscal });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_proveedores_empresa", excepcion.ConstraintName);
    }

    /// <summary>Judgment-day ronda 1 (item 4): prueba de humo faltante para
    /// <c>fk_listas_precio_empresa</c>.</summary>
    [Fact]
    public async Task UnaListaDePrecioConIdEmpresaInexistenteViolaLaFkCompuesta()
    {
        var (idTenant, _, _) =
            await SembrarTenantConCatalogosAsync(nameof(UnaListaDePrecioConIdEmpresaInexistenteViolaLaFkCompuesta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO listas_precio (id_tenant, id_empresa, nombre, es_default, modo, activo, created_at, updated_at) " +
            "VALUES ($1, 999999, 'intrusa', false, 'fija', true, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_listas_precio_empresa", excepcion.ConstraintName);
    }

    /// <summary>Judgment-day ronda 1 (item 4): prueba de humo faltante para
    /// <c>fk_listas_precio_lista_base</c> — FK simple (no compuesta, self-referencing por
    /// <c>id_lista_precio</c>): un <c>id_lista_base</c> inexistente la viola sin importar el
    /// tenant.</summary>
    [Fact]
    public async Task UnaListaDePrecioConIdListaBaseInexistenteViolaLaFk()
    {
        var (idTenant, _, _) =
            await SembrarTenantConCatalogosAsync(nameof(UnaListaDePrecioConIdListaBaseInexistenteViolaLaFk));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO listas_precio (id_tenant, id_lista_base, nombre, es_default, modo, activo, created_at, updated_at) " +
            "VALUES ($1, 999999, 'intrusa', false, 'fija', true, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_listas_precio_lista_base", excepcion.ConstraintName);
    }

    /// <summary>db-error-backstops (judgment-day ronda 1, Slice 3): prueba de humo cruda para
    /// el mapeo genérico 22003 → 400 <c>valor_fuera_de_rango</c> — un INSERT directo por SQL
    /// bypasea por completo <c>ServicioDeProveedores.ExigirMargenValido</c> (el pre-chequeo de
    /// aplicación) y fuerza a Postgres a rechazar el valor por desbordar
    /// <c>numeric(5,2)</c>.</summary>
    [Fact]
    public async Task UnProveedorConMargenQueDesbordaNumericViolaElRangoEnPostgres()
    {
        var (idTenant, idCondicionFiscal, _) =
            await SembrarTenantConCatalogosAsync(nameof(UnProveedorConMargenQueDesbordaNumericViolaElRangoEnPostgres));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, margen, created_at, updated_at) " +
            "VALUES ($1, 'intruso', $2, 9999.99, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscal });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("22003", excepcion.SqlState);
    }

    [Theory]
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
