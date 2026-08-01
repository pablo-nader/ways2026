using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Verificación de apply-time del guard de ADR-3 (design.md, stage-1-organization-and-catalogs):
/// <c>Multiplexing</c> y <c>No Reset On Close</c> tienen que quedar deshabilitados.
/// </summary>
public class InvariantesDeConexionTests
{
    [Fact]
    public void NoViolaNadaConLaConfiguracionPorDefecto()
    {
        const string cadena = "Host=localhost;Port=5432;Database=ways;Username=ways;Password=ways";

        Assert.False(InvariantesDeConexion.ViolaMultiplexingOResetOnClose(cadena));
    }

    [Fact]
    public void DetectaMultiplexingActivado()
    {
        const string cadena =
            "Host=localhost;Port=5432;Database=ways;Username=ways;Password=ways;Multiplexing=true";

        Assert.True(InvariantesDeConexion.ViolaMultiplexingOResetOnClose(cadena));
    }

    [Fact]
    public void DetectaNoResetOnCloseActivado()
    {
        const string cadena =
            "Host=localhost;Port=5432;Database=ways;Username=ways;Password=ways;No Reset On Close=true";

        Assert.True(InvariantesDeConexion.ViolaMultiplexingOResetOnClose(cadena));
    }
}
