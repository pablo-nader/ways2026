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

    // ---- judgment-day, slice 1 ronda 2, finding 1 (juez B): la denylist/snake_case es
    // recursiva, no solo top-level ------------------------------------------------------------

    [Fact]
    public void UnaClaveProhibidaDentroDeUnDiccionarioAnidadoLanza()
    {
        var nuevo = new Dictionary<string, object?>
        {
            ["datos"] = new Dictionary<string, object?> { ["hash_password"] = "x" }
        };

        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo));
    }

    [Fact]
    public void UnDiccionarioAnidadoSinClavesProhibidasEsValido()
    {
        var nuevo = new Dictionary<string, object?>
        {
            ["datos"] = new Dictionary<string, object?> { ["monto"] = 100m, ["moneda"] = "ars" }
        };

        var registro = new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo);

        Assert.Equal(nuevo, registro.ValorNuevo);
    }

    [Fact]
    public void UnaClaveProhibidaDentroDeUnaListaDeDiccionariosLanza()
    {
        var nuevo = new Dictionary<string, object?>
        {
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["monto"] = 10m },
                new() { ["token"] = "x" }
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo));
    }

    // ---- judgment-day, slice 1 ronda 2, residual R2-B-1 (juez B): por invarianza de TValue, un
    // Dictionary<string,string> anidado o un Hashtable no genérico caían al case IEnumerable sin
    // validar sus claves ------------------------------------------------------------------------

    [Fact]
    public void UnaClaveProhibidaDentroDeUnDiccionarioDeStringAStringAnidadoLanza()
    {
        var nuevo = new Dictionary<string, object?>
        {
            ["datos"] = new Dictionary<string, string> { ["password_hash"] = "x" }
        };

        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo));
    }

    [Fact]
    public void UnaClaveProhibidaDentroDeUnHashtableAnidadoLanza()
    {
        var nuevo = new Dictionary<string, object?>
        {
            ["datos"] = new System.Collections.Hashtable { ["secret_key"] = "x" }
        };

        Assert.Throws<InvalidOperationException>(() =>
            new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo));
    }

    [Fact]
    public void UnDiccionarioDeStringAStringAnidadoSinClavesProhibidasEsValido()
    {
        var nuevo = new Dictionary<string, object?>
        {
            ["datos"] = new Dictionary<string, string> { ["moneda"] = "ars" }
        };

        var registro = new RegistroDeAuditoria(1, null, Accion, 41, null, nuevo);

        Assert.Equal(nuevo, registro.ValorNuevo);
    }
}
