using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Stage-2-clientes-proveedores, Slice 1 (task 1.14, spec: Tenant Isolation for
/// clientes/proveedores/listas_precio): mismo patrón que <c>CatalogosDeTenantRlsTests</c> —
/// SQL crudo, independiente de EF, 0 filas para SELECT/UPDATE cross-tenant, 42501 para el
/// INSERT que viola <c>WITH CHECK</c>.
///
/// GATED: <c>clientes</c>/<c>proveedores</c>/<c>listas_precio</c>/<c>numeraciones_clientes</c>
/// no existen todavía — la migración <c>ClientesYProveedoresEtapa2</c> está bloqueada por el
/// DB CHANGE GATE (task 1.7). <c>Skip</c> se saca en el mismo lote que genera y aplica esa
/// migración.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ClientesYProveedoresRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string RazonDeGate =
        "Gated: clientes/proveedores/listas_precio/numeraciones_clientes no existen hasta que " +
        "la migración ClientesYProveedoresEtapa2 se genere y apruebe (DB CHANGE GATE, task 1.7).";

    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "clientes", "id_cliente" },
        { "proveedores", "id_proveedor" },
        { "listas_precio", "id_lista_precio" }
    };

    private async Task<(int IdTenantA, int IdFila, int IdTenantB)> SembrarFilaDeTenantAAsync(string tabla, string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);

        var lista = new ListaPrecio
        {
            IdTenant = tenantA.Id, Nombre = nombre, EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        int idFila;
        switch (tabla)
        {
            case "clientes":
                var cliente = new Cliente
                {
                    IdTenant = tenantA.Id, Numero = 2, Nombre = nombre,
                    IdCondicionFiscal = condicionFiscal.Id, IdListaPrecio = lista.Id,
                    CreatedAt = ahora, UpdatedAt = ahora
                };
                db.Clientes.Add(cliente);
                await db.SaveChangesAsync();
                idFila = cliente.Id;
                break;

            case "proveedores":
                var proveedor = new Proveedor
                {
                    IdTenant = tenantA.Id, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
                    CreatedAt = ahora, UpdatedAt = ahora
                };
                db.Proveedores.Add(proveedor);
                await db.SaveChangesAsync();
                idFila = proveedor.Id;
                break;

            case "listas_precio":
                idFila = lista.Id;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.");
        }

        return (tenantA.Id, idFila, tenantB.Id);
    }

    [Theory(Skip = RazonDeGate)]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoVeLaFilaPorSelect(string tabla, string columnaId)
    {
        var (idTenantA, idFila, idTenantB) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
        Assert.NotEqual(idTenantA, idTenantB);
    }

    [Theory(Skip = RazonDeGate)]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila(string tabla, string columnaId)
    {
        var (_, idFila, idTenantB) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"UPDATE {tabla} SET updated_at = now() WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Fact(Skip = RazonDeGate)]
    public async Task NumeracionesClientesEsInvisibleParaOtroTenant()
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = nameof(NumeracionesClientesEsInvisibleParaOtroTenant) + "-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = nameof(NumeracionesClientesEsInvisibleParaOtroTenant) + "-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        db.NumeracionesClientes.Add(new NumeracionCliente { IdTenant = tenantA.Id, ProximoNumero = 1 });
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM numeraciones_clientes WHERE id_tenant = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantA.Id });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }
}
