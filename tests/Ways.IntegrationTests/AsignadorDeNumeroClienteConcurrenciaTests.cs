using Ways.Application.Clientes;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Judgment-day ronda 1 (item de hardening): <see cref="AsignadorDeNumeroCliente.AsignarSiguienteAsync"/>
/// bajo concurrencia real contra Postgres — dos asignaciones simultáneas sobre el MISMO
/// contador de tenant no pueden repetir ni saltear un número.
///
/// A diferencia de <c>ParametrosTests.DosEstablecimientosConcurrentesConLaMismaClaveYElMismoAlcanceDisparanElBackstopDelSaveChanges</c>,
/// acá no hace falta un <c>InterceptorDeRendezVous</c> que fuerce el timing a mano: la carrera
/// de <c>ParametrosTests</c> es sobre un INSERT condicional (busca "existente" antes de
/// insertar, así que dos requests pueden ver "no existe" los dos si no se los sincroniza), pero
/// <c>AsignadorDeNumeroCliente.AsignarSiguienteAsync</c> es un <c>UPDATE ... RETURNING</c>
/// incondicional — el propio row lock de Postgres sobre la fila del contador serializa las dos
/// transacciones sin ayuda externa: la segunda espera a que la primera confirme (o revierta)
/// antes de tomar el lock, así que el <c>Task.WhenAll</c> real ya alcanza para forzar la carrera
/// genuina (design decisions 2/3, doc del propio <see cref="AsignadorDeNumeroCliente"/>).
///
/// Corre 3 rondas (2 asignaciones concurrentes por ronda, 6 en total) para la estabilidad
/// pedida — junta los 6 números y confirma que son exactamente consecutivos, sin huecos ni
/// duplicados, no solo que cada par de una ronda lo sea.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AsignadorDeNumeroClienteConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const int CantidadDeRondas = 3;

    [Fact]
    public async Task DosAsignacionesConcurrentesDelMismoTenantDanNumerosDistintosYConsecutivos()
    {
        using var _ = fixture.CreateClient();

        int idTenant;
        await using (var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var tenant = new Tenant
            {
                Nombre = nameof(DosAsignacionesConcurrentesDelMismoTenantDanNumerosDistintosYConsecutivos),
                Estado = EstadoTenant.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            siembra.Tenants.Add(tenant);
            await siembra.SaveChangesAsync();
            idTenant = tenant.Id;

            await AsignadorDeNumeroCliente.AsegurarContadorAsync(siembra, idTenant);
        }

        var todosLosNumeros = new List<int>();

        for (var ronda = 0; ronda < CantidadDeRondas; ronda++)
        {
            var tareaA = AsignarUnoAsync(idTenant);
            var tareaB = AsignarUnoAsync(idTenant);

            var numeros = await Task.WhenAll(tareaA, tareaB);

            Assert.NotEqual(numeros[0], numeros[1]);
            todosLosNumeros.AddRange(numeros);
        }

        var minimo = todosLosNumeros.Min();
        var esperados = Enumerable.Range(minimo, todosLosNumeros.Count);

        Assert.Equal(esperados, todosLosNumeros.OrderBy(n => n));
        Assert.Equal(todosLosNumeros.Count, todosLosNumeros.Distinct().Count());
    }

    private async Task<int> AsignarUnoAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await using var transaccion = await db.Database.BeginTransactionAsync();

        var numero = await AsignadorDeNumeroCliente.AsignarSiguienteAsync(db, idTenant);

        await transaccion.CommitAsync();
        return numero;
    }
}
