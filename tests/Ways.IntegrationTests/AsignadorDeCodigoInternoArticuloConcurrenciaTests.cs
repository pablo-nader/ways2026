using Ways.Application.Articulos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Stage-3-articulos-y-precios, Slice 1 (task 1.13, design: Backstop Map — "numeraciones_articulos
/// counter race"): mismo mecanismo y misma estructura que
/// <c>AsignadorDeNumeroClienteConcurrenciaTests</c> — <c>AsignarSiguienteAsync</c> es un
/// <c>UPDATE ... RETURNING</c> incondicional, el propio row lock de Postgres sobre la fila del
/// contador serializa las dos transacciones concurrentes sin ayuda externa (design decision 6,
/// mismo shape que <see cref="AsignadorDeNumeroCliente"/>'s counter).
///
/// Corre 3 rondas (2 asignaciones concurrentes por ronda, 6 en total) para la estabilidad
/// pedida — junta los 6 números y confirma que son exactamente consecutivos, sin huecos ni
/// duplicados.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AsignadorDeCodigoInternoArticuloConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const int CantidadDeRondas = 3;

    [Fact]
    public async Task DosAsignacionesConcurrentesDelMismoTenantDanCodigosDistintosYConsecutivos()
    {
        using var _ = fixture.CreateClient();

        int idTenant;
        await using (var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var tenant = new Tenant
            {
                Nombre = nameof(DosAsignacionesConcurrentesDelMismoTenantDanCodigosDistintosYConsecutivos),
                Estado = EstadoTenant.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            siembra.Tenants.Add(tenant);
            await siembra.SaveChangesAsync();
            idTenant = tenant.Id;

            await AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync(siembra, idTenant);
        }

        var todosLosCodigos = new List<int>();

        for (var ronda = 0; ronda < CantidadDeRondas; ronda++)
        {
            var tareaA = AsignarUnoAsync(idTenant);
            var tareaB = AsignarUnoAsync(idTenant);

            var codigos = await Task.WhenAll(tareaA, tareaB);

            Assert.NotEqual(codigos[0], codigos[1]);
            todosLosCodigos.AddRange(codigos);
        }

        var minimo = todosLosCodigos.Min();
        var esperados = Enumerable.Range(minimo, todosLosCodigos.Count);

        Assert.Equal(esperados, todosLosCodigos.OrderBy(n => n));
        Assert.Equal(todosLosCodigos.Count, todosLosCodigos.Distinct().Count());
    }

    private async Task<int> AsignarUnoAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await using var transaccion = await db.Database.BeginTransactionAsync();

        var codigo = await AsignadorDeCodigoInternoArticulo.AsignarSiguienteAsync(db, idTenant);

        await transaccion.CommitAsync();
        return codigo;
    }
}
