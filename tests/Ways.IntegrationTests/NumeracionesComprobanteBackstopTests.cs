using Npgsql;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 2 (task 2.6, db-error-backstops skill, design: Backstop Map):
/// <c>pk_numeraciones_comprobante</c> — exención documentada de prueba de carrera. El único
/// escritor legítimo (<c>AsignadorDeNumeroComprobante</c>) inserta con <c>ON CONFLICT DO
/// NOTHING</c>, así que nunca puede disparar 23505 por el camino normal; esto prueba solo la
/// traducción de esquema con un INSERT crudo que bypasea el asignador — mismo patrón que
/// <c>pk_ofertas_listas</c>/<c>PK_articulos_empresas</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class NumeracionesComprobanteBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    [Fact]
    public async Task UnaFilaConLaMismaClaveInsertadaPorFueraDelAsignadorViolaLaPk()
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant
        {
            Nombre = nameof(UnaFilaConLaMismaClaveInsertadaPorFueraDelAsignadorViolaLaPk),
            Estado = EstadoTenant.Activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa
        {
            IdTenant = tenant.Id, RazonSocial = tenant.Nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = tenant.Nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenant.Id);

        async Task InsertarAsync()
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO numeraciones_comprobante (id_tenant, id_punto_venta, tipo_comprobante, proximo_numero) " +
                "VALUES ($1, $2, 'TX', 1)";
            comando.Parameters.Add(new NpgsqlParameter { Value = tenant.Id });
            comando.Parameters.Add(new NpgsqlParameter { Value = puntoVenta.Id });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarAsync();

        var excepcion = await Assert.ThrowsAsync<PostgresException>(InsertarAsync);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("pk_numeraciones_comprobante", excepcion.ConstraintName);
    }
}
