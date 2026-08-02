using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Approved hardening de judgment-day (slice 3, ronda 1): la prueba de aislamiento de RLS por
/// SQL crudo, independiente de EF, para los 6 catálogos de tenant que <c>AislamientoDeTenantTests</c>
/// y <c>CatalogosGlobalesRlsTests</c> todavía no cubrían fila por fila — mismas convenciones que
/// ese último (0 filas para SELECT/UPDATE cross-tenant, 42501 para el INSERT que viola
/// <c>WITH CHECK</c>), parametrizada por tabla en vez de repetir 6 clases casi idénticas.
/// <c>LaCoberturaDePoliciesEsCompleta</c> ya prueba que las 6 tienen policy — esto prueba que la
/// policy realmente aísla filas, no solo que existe.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogosDeTenantRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "areas", "id_area" },
        { "categorias", "id_categoria" },
        { "marcas", "id_marca" },
        { "grupos", "id_grupo" },
        { "medios_pago", "id_medio_pago" },
        { "parametros", "id_parametro" }
    };

    private async Task<(int IdTenantA, int IdFila, int IdTenantB)> SembrarFilaDeTenantAAsync(string tabla, string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        int idFila;
        switch (tabla)
        {
            case "areas":
                var area = new Area { IdTenant = tenantA.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
                db.Areas.Add(area);
                await db.SaveChangesAsync();
                idFila = area.Id;
                break;

            case "categorias":
                var categoria = new Categoria { IdTenant = tenantA.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
                db.Categorias.Add(categoria);
                await db.SaveChangesAsync();
                idFila = categoria.Id;
                break;

            case "marcas":
                var marca = new Marca { IdTenant = tenantA.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
                db.Marcas.Add(marca);
                await db.SaveChangesAsync();
                idFila = marca.Id;
                break;

            case "grupos":
                var grupo = new Grupo { IdTenant = tenantA.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
                db.Grupos.Add(grupo);
                await db.SaveChangesAsync();
                idFila = grupo.Id;
                break;

            case "medios_pago":
                var medioPago = new MedioPago
                {
                    IdTenant = tenantA.Id,
                    Nombre = nombre,
                    Orden = 1,
                    Comportamiento = ComportamientoMedioPago.Efectivo,
                    AdmiteVuelto = true,
                    RequiereReferencia = false,
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                };
                db.MediosPago.Add(medioPago);
                await db.SaveChangesAsync();
                idFila = medioPago.Id;
                break;

            case "parametros":
                var empresa = new Empresa { IdTenant = tenantA.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
                db.Empresas.Add(empresa);
                await db.SaveChangesAsync();

                var parametro = new Parametro
                {
                    IdTenant = tenantA.Id,
                    IdEmpresa = empresa.Id,
                    Clave = "tolerancia_pago",
                    Valor = "\"1\"",
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                };
                db.Parametros.Add(parametro);
                await db.SaveChangesAsync();
                idFila = parametro.Id;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.");
        }

        return (tenantA.Id, idFila, tenantB.Id);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoVeLaFilaPorSelect(string tabla, string columnaId)
    {
        var (idTenantA, idFila, idTenantB) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        // 0 filas, no una excepción: USING oculta la fila de otro tenant antes de que el
        // SELECT la evalúe — la misma mecánica que AislamientoDeTenantTests, acá para las 6
        // tablas de catálogos de tenant en vez de solo empresas/tenants.
        Assert.Equal(0, total);
        Assert.NotEqual(idTenantA, idTenantB);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila(string tabla, string columnaId)
    {
        var (_, idFila, idTenantB) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"UPDATE {tabla} SET updated_at = now() WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        // 0 filas afectadas, no una excepción — mismo mecanismo y misma garantía de
        // seguridad que CatalogosGlobalesRlsTests: USING gobierna la visibilidad de fila
        // para UPDATE, WITH CHECK no participa cuando la fila ya es invisible.
        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnInsertConIdTenantAjenoSeRechaza(string tabla, string columnaId)
    {
        _ = columnaId;
        var (idTenantA, _, idTenantB) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnInsertConIdTenantAjenoSeRechaza) + tabla);

        // Sesión de tenant A intentando insertar una fila marcada como de tenant B: WITH
        // CHECK rechaza antes de que la fila exista, sin importar si el resto de columnas
        // referencia algo válido (una FK compuesta inválida nunca llega a evaluarse — el
        // INSERT ya abortó).
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = tabla switch
        {
            "areas" or "categorias" =>
                $"INSERT INTO {tabla} (id_tenant, nombre, orden, created_at, updated_at) " +
                "VALUES ($1, 'intrusa', 1, now(), now())",
            "marcas" or "grupos" =>
                $"INSERT INTO {tabla} (id_tenant, nombre, created_at, updated_at) " +
                "VALUES ($1, 'intrusa', now(), now())",
            "medios_pago" =>
                "INSERT INTO medios_pago " +
                "(id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, created_at, updated_at) " +
                "VALUES ($1, 'intrusa', 1, 'efectivo', true, false, now(), now())",
            "parametros" =>
                "INSERT INTO parametros (id_tenant, id_empresa, clave, valor, created_at, updated_at) " +
                "VALUES ($1, 999999, 'tolerancia_pago', '\"1\"', now(), now())",
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        comando.Parameters.Add(new NpgsqlParameter { Value = idTenantB });

        // 42501 = insufficient_privilege (violación de WITH CHECK).
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }
}
