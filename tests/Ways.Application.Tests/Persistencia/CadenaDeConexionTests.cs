using Npgsql;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

public class CadenaDeConexionTests
{
    [Fact]
    public void ParseaLaUriQueEntregaEasyPanel()
    {
        var resultado = CadenaDeConexion.Normalizar(
            "postgres://aiposroot:clave-secreta@aipos_aipos-postgres:5432/aipos?sslmode=disable");

        var b = new NpgsqlConnectionStringBuilder(resultado);

        Assert.Equal("aipos_aipos-postgres", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("aipos", b.Database);
        Assert.Equal("aiposroot", b.Username);
        Assert.Equal("clave-secreta", b.Password);
        Assert.Equal(SslMode.Disable, b.SslMode);
    }

    [Fact]
    public void AceptaElEsquemaPostgresql()
    {
        var b = new NpgsqlConnectionStringBuilder(
            CadenaDeConexion.Normalizar("postgresql://u:p@host/basededatos"));

        Assert.Equal("host", b.Host);
        Assert.Equal("basededatos", b.Database);
        // Sin puerto explícito en la URI se usa el 5432.
        Assert.Equal(5432, b.Port);
    }

    [Fact]
    public void DesescapaCredencialesConCaracteresEspeciales()
    {
        var b = new NpgsqlConnectionStringBuilder(
            CadenaDeConexion.Normalizar("postgres://us%40er:cla%2Fve%3A1@host:5432/db"));

        Assert.Equal("us@er", b.Username);
        Assert.Equal("cla/ve:1", b.Password);
    }

    [Theory]
    [InlineData("require", SslMode.Require)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    public void TraduceElSslmodeDeLaUri(string valor, SslMode esperado)
    {
        var b = new NpgsqlConnectionStringBuilder(
            CadenaDeConexion.Normalizar($"postgres://u:p@host:5432/db?sslmode={valor}"));

        Assert.Equal(esperado, b.SslMode);
    }

    [Fact]
    public void DejaIntactaUnaCadenaQueYaEstaEnFormatoClaveValor()
    {
        const string cadena = "Host=localhost;Port=5432;Database=ways;Username=ways;Password=ways";

        Assert.Equal(cadena, CadenaDeConexion.Normalizar(cadena));
    }

    [Fact]
    public void RechazaUnaCadenaVacia()
    {
        Assert.Throws<ArgumentException>(() => CadenaDeConexion.Normalizar("   "));
    }
}
