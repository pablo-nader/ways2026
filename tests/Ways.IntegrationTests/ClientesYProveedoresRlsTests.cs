using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Clientes;
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
/// INSERT que viola <c>WITH CHECK</c> (judgment-day ronda 1, item confirmado: agregado acá,
/// antes solo estaba SELECT/UPDATE); además un proof a nivel EF (LINQ, sin SQL crudo) de que
/// el filtro de tenant también bloquea la lectura por el ORM, mismo patrón que
/// <c>AislamientoDeTenantTests.ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant</c>.
///
/// DB CHANGE GATE aprobado 2026-08-02 y migración <c>ClientesYProveedoresEtapa2</c> aplicada:
/// pruebas activas contra Postgres real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ClientesYProveedoresRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
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

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Fact]
    public async Task NumeracionesClientesEsInvisibleParaOtroTenant()
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = nameof(NumeracionesClientesEsInvisibleParaOtroTenant) + "-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = nameof(NumeracionesClientesEsInvisibleParaOtroTenant) + "-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        // AsignadorDeNumeroCliente.AsegurarContadorAsync (SQL crudo), no db.NumeracionesClientes.Add:
        // el WaysDbContext.RechazarEscriturasDeNumeracionCliente rechaza cualquier Added/Modified
        // de NumeracionCliente que llegue por el ChangeTracker (judgment-day ronda 1).
        await AsignadorDeNumeroCliente.AsegurarContadorAsync(db, tenantA.Id);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM numeraciones_clientes WHERE id_tenant = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantA.Id });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }

    public static TheoryData<string> TablasParaInsertRechazado => new()
    {
        "clientes",
        "proveedores",
        "listas_precio",
        "numeraciones_clientes"
    };

    /// <summary>Judgment-day ronda 1 (item confirmado): faltaba el proof de INSERT (42501) para
    /// las 4 tablas nuevas — mismo mecanismo y mismo nombre de método que
    /// <c>CatalogosDeTenantRlsTests.UnInsertConIdTenantAjenoSeRechaza</c>: <c>WITH CHECK</c>
    /// rechaza el INSERT antes de que la fila exista, sin importar si el resto de columnas
    /// referencia algo inválido (por eso los ids de catálogo van con un valor cualquiera,
    /// <c>999999</c> — la FK ni llega a evaluarse).</summary>
    [Theory]
    [MemberData(nameof(TablasParaInsertRechazado))]
    public async Task UnInsertConIdTenantAjenoSeRechaza(string tabla)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var nombreBase = $"{nameof(UnInsertConIdTenantAjenoSeRechaza)}-{tabla}";

        var tenantA = new Tenant { Nombre = $"{nombreBase}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombreBase}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantA.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = tabla switch
        {
            "clientes" =>
                "INSERT INTO clientes (id_tenant, numero, nombre, id_condicion_fiscal, id_lista_precio, created_at, updated_at) " +
                "VALUES ($1, 2, 'intruso', 999999, 999999, now(), now())",
            "proveedores" =>
                "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, created_at, updated_at) " +
                "VALUES ($1, 'intruso', 999999, now(), now())",
            "listas_precio" =>
                "INSERT INTO listas_precio (id_tenant, nombre, es_default, modo, activo, created_at, updated_at) " +
                "VALUES ($1, 'intrusa', false, 'fija', true, now(), now())",
            "numeraciones_clientes" =>
                "INSERT INTO numeraciones_clientes (id_tenant, proximo_numero) VALUES ($1, 1)",
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    /// <summary>Judgment-day ronda 1 (item confirmado): proof a nivel EF (LINQ) de que el
    /// query filter de tenant (<c>WaysDbContext.AplicarFiltroDeTenant</c>) también bloquea a
    /// las 3 entidades nuevas que sí pasan por el ORM (<c>NumeracionCliente</c> queda afuera:
    /// design decision 3, solo se escribe/lee con SQL crudo) — mismo patrón que
    /// <c>AislamientoDeTenantTests.ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant</c>, acá para
    /// las 3 entidades de este slice en un solo método.</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas()
    {
        var (_, idClienteDeA, idTenantB1) = await SembrarFilaDeTenantAAsync(
            "clientes", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-clientes");
        var (_, idProveedorDeA, idTenantB2) = await SembrarFilaDeTenantAAsync(
            "proveedores", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-proveedores");
        var (_, idListaDeA, idTenantB3) = await SembrarFilaDeTenantAAsync(
            "listas_precio", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-listas");

        await using var sesionB1 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB1));
        var clientesVisibles = await sesionB1.Clientes.Where(c => c.Id == idClienteDeA).ToListAsync();
        Assert.Empty(clientesVisibles);

        await using var sesionB2 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB2));
        var proveedoresVisibles = await sesionB2.Proveedores.Where(p => p.Id == idProveedorDeA).ToListAsync();
        Assert.Empty(proveedoresVisibles);

        await using var sesionB3 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB3));
        var listasVisibles = await sesionB3.ListasPrecio.Where(l => l.Id == idListaDeA).ToListAsync();
        Assert.Empty(listasVisibles);
    }
}
