using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Task 1.17 (tasks.md): la prueba de aislamiento de dos capas (ADR-4, ADR-15, ADR-17)
/// contra un Postgres real en contenedor, con el rol restringido <c>ways_app</c> — nunca
/// como el dueño de las tablas.
///
/// Requiere un daemon de Docker alcanzable (Testcontainers). Si no hay uno disponible,
/// <see cref="WaysApiFixture.InitializeAsync"/> falla al arrancar el contenedor y toda la
/// clase falla en el fixture, no en cada aserción individual — esa es la gate real
/// (el daemon), no algo que se resuelva con un <c>Skip</c> en el código.
/// </summary>
public class AislamientoDeTenantTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<(int TenantId, int EmpresaId)> CrearTenantConEmpresaAsync(string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Nombre = nombre,
            Estado = EstadoTenant.Activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa
        {
            IdTenant = tenant.Id,
            RazonSocial = $"{nombre}-empresa",
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return (tenant.Id, empresa.Id);
    }

    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant()
    {
        var (idA, _) = await CrearTenantConEmpresaAsync(nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + "-A");
        var (idB, _) = await CrearTenantConEmpresaAsync(nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + "-B");

        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idA));

        var visibles = await sesionA.Empresas
            .Where(e => e.IdTenant == idA || e.IdTenant == idB)
            .Select(e => e.IdTenant)
            .ToListAsync();

        Assert.All(visibles, id => Assert.Equal(idA, id));
        Assert.NotEmpty(visibles);
    }

    [Fact]
    public async Task RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf()
    {
        var (idA, _) = await CrearTenantConEmpresaAsync(nameof(RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf) + "-A");
        var (idB, _) = await CrearTenantConEmpresaAsync(nameof(RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf) + "-B");

        // Capa 2, sin EF: conexión cruda como ways_app, contexto de tenant A.
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idA))
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "SELECT id_tenant FROM empresas WHERE id_tenant IN ($1, $2)";
            comando.Parameters.Add(new NpgsqlParameter { Value = idA });
            comando.Parameters.Add(new NpgsqlParameter { Value = idB });

            await using var lector = await comando.ExecuteReaderAsync();
            var vistos = new List<int>();
            while (await lector.ReadAsync())
            {
                vistos.Add(lector.GetInt32(0));
            }

            Assert.All(vistos, id => Assert.Equal(idA, id));
            Assert.NotEmpty(vistos);
        }

        // Mismo resultado con EF pero IgnoreQueryFilters(["Tenant"]): RLS es quien
        // realmente lo impide, no el filtro que se está ignorando a propósito.
        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idA));
        var conFiltroIgnorado = await sesionA.Empresas
            .IgnoreQueryFilters(["Tenant"])
            .Where(e => e.IdTenant == idA || e.IdTenant == idB)
            .Select(e => e.IdTenant)
            .ToListAsync();

        Assert.All(conFiltroIgnorado, id => Assert.Equal(idA, id));

        // Cobertura de tenants (pedida explícita en la aprobación del gate): la propia
        // tabla tenants también tiene RLS — un tenant no ve la fila de otro tenant ahí.
        await using (var crudaTenants = await fixture.AbrirConexionCrudaAsync("tenant", idA))
        {
            await using var comando = crudaTenants.CreateCommand();
            comando.CommandText = "SELECT id_tenant FROM tenants WHERE id_tenant IN ($1, $2)";
            comando.Parameters.Add(new NpgsqlParameter { Value = idA });
            comando.Parameters.Add(new NpgsqlParameter { Value = idB });

            await using var lector = await comando.ExecuteReaderAsync();
            var vistos = new List<int>();
            while (await lector.ReadAsync())
            {
                vistos.Add(lector.GetInt32(0));
            }

            Assert.Equal([idA], vistos);
        }

        // Plataforma sí ve las dos.
        await using (var crudaPlataforma = await fixture.AbrirConexionCrudaAsync("plataforma", null))
        {
            await using var comando = crudaPlataforma.CreateCommand();
            comando.CommandText = "SELECT id_tenant FROM tenants WHERE id_tenant IN ($1, $2)";
            comando.Parameters.Add(new NpgsqlParameter { Value = idA });
            comando.Parameters.Add(new NpgsqlParameter { Value = idB });

            await using var lector = await comando.ExecuteReaderAsync();
            var vistos = new List<int>();
            while (await lector.ReadAsync())
            {
                vistos.Add(lector.GetInt32(0));
            }

            Assert.Equal([idA, idB], vistos.OrderBy(x => x));
        }
    }

    [Fact]
    public async Task WithCheckRechazaUnInsertConIdTenantAjeno()
    {
        var (idA, _) = await CrearTenantConEmpresaAsync(nameof(WithCheckRechazaUnInsertConIdTenantAjeno) + "-A");
        var (idB, _) = await CrearTenantConEmpresaAsync(nameof(WithCheckRechazaUnInsertConIdTenantAjeno) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'intrusa', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idB });

        // 42501 = insufficient_privilege (violación de RLS/WITH CHECK): así distinguimos
        // el rechazo genuino de RLS de cualquier otro PostgresException incidental.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task WithCheckRechazaUnUpdateQueReasignaIdTenant()
    {
        var (idA, idEmpresaA) = await CrearTenantConEmpresaAsync(
            nameof(WithCheckRechazaUnUpdateQueReasignaIdTenant) + "-A");
        var (idB, _) = await CrearTenantConEmpresaAsync(
            nameof(WithCheckRechazaUnUpdateQueReasignaIdTenant) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE empresas SET id_tenant = $1 WHERE id_empresa = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = idB });
        comando.Parameters.Add(new NpgsqlParameter { Value = idEmpresaA });

        // 42501 = insufficient_privilege (violación de RLS/WITH CHECK): confirma que es
        // RLS quien rechaza el UPDATE, no un error de columna previo a la evaluación de RLS.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task SinGucElResultadoEsCeroFilasNoUnError()
    {
        await CrearTenantConEmpresaAsync(nameof(SinGucElResultadoEsCeroFilasNoUnError));

        // Contexto explícitamente vacío (no "ausente por casualidad" de la reutilización
        // del pool): mismo efecto que un GUC nunca seteado, por diseño de app_tenant_actual().
        await using var cruda = await fixture.AbrirConexionCrudaAsync(string.Empty, null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM empresas";
        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }

    [Fact]
    public async Task NoHayFugaDeGucEntreConexionesDelPool()
    {
        var (idA, _) = await CrearTenantConEmpresaAsync(nameof(NoHayFugaDeGucEntreConexionesDelPool));

        await using (await fixture.AbrirConexionCrudaAsync("tenant", idA))
        {
            // se cierra al salir del using: Npgsql manda DISCARD ALL al devolverla al pool.
        }

        await using var segunda = new NpgsqlConnection(fixture.AppConnectionString);
        await segunda.OpenAsync();

        await using var comando = segunda.CreateCommand();
        comando.CommandText = "SELECT current_setting('app.tenant_id', true)";
        var valor = await comando.ExecuteScalarAsync();

        Assert.True(valor is null or DBNull || (string)valor == string.Empty);
    }

    [Fact]
    public async Task LaCoberturaDePoliciesEsCompleta()
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            """
            SELECT c.relname
            FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE a.attname = 'id_tenant' AND c.relkind = 'r'
              AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity
                   OR NOT EXISTS (SELECT 1 FROM pg_policies p WHERE p.tablename = c.relname));
            """;

        await using var lector = await comando.ExecuteReaderAsync();
        var sinCobertura = new List<string>();
        while (await lector.ReadAsync())
        {
            sinCobertura.Add(lector.GetString(0));
        }

        Assert.Empty(sinCobertura);
    }

    [Fact]
    public async Task WaysAppNoTieneRolsuperNiRolbypassrls()
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = 'ways_app'";

        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());

        Assert.False(lector.GetBoolean(0));
        Assert.False(lector.GetBoolean(1));
    }
}
