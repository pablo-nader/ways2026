using Ways.Domain.Auditoria;

namespace Ways.Domain.Tests.Auditoria;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 1 (task 1.12, design decisión 3): las cuatro
/// invariantes del constructor de <see cref="RegistroDeAuditoria"/>, patrón <c>PoliticaDeRoles</c>
/// — sin DB, sin fixture.
/// </summary>
public class RegistroDeAuditoriaTests
{
    private static readonly AccionAuditada Accion = AccionAuditada.PrecioCambio;

    private static Dictionary<string, object?> Nuevo() => new() { ["monto"] = 100m };

    [Fact]
    public void UnSubconjuntoDeClavesEsValido()
    {
        var anterior = new Dictionary<string, object?> { ["monto"] = 90m };
        var nuevo = new Dictionary<string, object?> { ["monto"] = 100m, ["vigente_desde"] = DateTimeOffset.UtcNow };

        var registro = new RegistroDeAuditoria(1, null, Accion, 41, anterior, nuevo);

        Assert.Equal(anterior, registro.ValorAnterior);
        Assert.Equal(nuevo, registro.ValorNuevo);
    }

    [Fact]
    public void UnaClaveExtraEnValorAnteriorLanza()
    {
        var anterior = new Dictionary<string, object?> { ["monto"] = 90m, ["descuento"] = 10m };
        var nuevo = Nuevo();

        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, anterior, nuevo));
    }

    [Fact]
    public void ValorAnteriorNuloEsLegal()
    {
        var registro = new RegistroDeAuditoria(1, null, Accion, 41, null, Nuevo());

        Assert.Null(registro.ValorAnterior);
    }

    [Fact]
    public void ValorNuevoVacioLanza()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, null, new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData("hash_password")]
    [InlineData("password")]
    [InlineData("contrasena")]
    [InlineData("token")]
    [InlineData("secret")]
    [InlineData("PASSWORD")]
    public void UnaClaveDeLaDenylistLanza(string claveProhibida)
    {
        var nuevo = new Dictionary<string, object?> { [claveProhibida] = "x" };

        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo));
    }

    [Theory]
    [InlineData("Monto")]
    [InlineData("montoNuevo")]
    [InlineData("monto-nuevo")]
    [InlineData("1monto")]
    public void UnaClaveFueraDeSnakeCaseLanza(string clavePascalCase)
    {
        var nuevo = new Dictionary<string, object?> { [clavePascalCase] = "x" };

        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo));
    }
}
