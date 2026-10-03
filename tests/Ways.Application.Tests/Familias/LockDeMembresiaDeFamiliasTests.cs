using Ways.Application.Familias;

namespace Ways.Application.Tests.Familias;

/// <summary>
/// La clave y el contrato de <see cref="LockDeMembresiaDeFamilias"/> que se pueden probar sin base
/// de datos. Que el lock compartido espera al exclusivo (y no al revés), y en qué orden se toma
/// respecto de los demás locks de una escritura de precios, lo prueban las pruebas de integración
/// contra Postgres (<c>PreciosDeFamiliaTests</c>).
/// </summary>
public class LockDeMembresiaDeFamiliasTests
{
    private const long PrefijoFami = 0x46414D49L << 32;

    [Theory]
    [InlineData(1, 0x46414D4900000001L)]
    [InlineData(255, 0x46414D49000000FFL)]
    [InlineData(0x01020304, 0x46414D4901020304L)]
    [InlineData(int.MaxValue, 0x46414D497FFFFFFFL)]
    public void LaClaveLlevaFamiEnLosBitsAltosYElTenantEnLosBajos(int idTenant, long esperada)
    {
        Assert.Equal(esperada, LockDeMembresiaDeFamilias.ClaveDe(idTenant));
    }

    [Fact]
    public void LaClaveEsPositivaYDistintaParaCadaTenant()
    {
        var tenants = new[] { 1, 2, 3, 1000, 65535, 65536, int.MaxValue };
        var claves = tenants.Select(LockDeMembresiaDeFamilias.ClaveDe).ToList();

        Assert.All(claves, clave => Assert.True(clave > PrefijoFami));
        Assert.Equal(tenants.Length, claves.Distinct().Count());
    }

    /// <summary>Un <c>pg_advisory_xact_lock</c> tomado en autocommit se libera al terminar su propia
    /// sentencia: no protegería nada. Tiene que fallar fuerte antes de tocar la conexión (la prueba
    /// pasa <c>null</c> como conexión a propósito).</summary>
    [Fact]
    public async Task TomarElLockCompartidoFueraDeUnaTransaccionFallaFuerte()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LockDeMembresiaDeFamilias.TomarCompartidoAsync(null!, null, 1, CancellationToken.None));
    }

    [Fact]
    public async Task TomarElLockExclusivoFueraDeUnaTransaccionFallaFuerte()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LockDeMembresiaDeFamilias.TomarExclusivoAsync(null!, null, 1, CancellationToken.None));
    }
}
