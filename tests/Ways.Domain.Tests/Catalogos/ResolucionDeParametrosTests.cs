using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Catalogos;

public class ResolucionDeParametrosTests
{
    private const string Clave = "tolerancia_pago";

    private static Parametro Fila(int? idPuntoVenta, string valor) => new()
    {
        Id = 1,
        IdTenant = 1,
        IdEmpresa = 1,
        IdPuntoVenta = idPuntoVenta,
        Clave = Clave,
        Valor = valor
    };

    [Fact]
    public void ElValorDePuntoDeVentaGanaSobreElDeEmpresa()
    {
        Parametro[] candidatos = [Fila(idPuntoVenta: null, valor: "15"), Fila(idPuntoVenta: 3, valor: "25")];

        var resuelto = ResolucionDeParametros.Resolver(Clave, candidatos, idPuntoVenta: 3);

        Assert.Equal("25", resuelto);
    }

    [Fact]
    public void UsaElValorDeEmpresaCuandoNoHayFilaDePuntoDeVenta()
    {
        Parametro[] candidatos = [Fila(idPuntoVenta: null, valor: "15")];

        var resuelto = ResolucionDeParametros.Resolver(Clave, candidatos, idPuntoVenta: 3);

        Assert.Equal("15", resuelto);
    }

    [Fact]
    public void UsaElDefaultDeclaradoCuandoNoHayNingunaFila()
    {
        var resuelto = ResolucionDeParametros.Resolver(Clave, candidatos: [], idPuntoVenta: 3);

        Assert.Equal(ParametroConocido.ToleranciaPago.ValorPorDefecto, resuelto);
    }

    [Fact]
    public void UsaElDefaultDeclaradoCuandoNoHayPuntoDeVentaEnContexto()
    {
        var resuelto = ResolucionDeParametros.Resolver(Clave, candidatos: [], idPuntoVenta: null);

        Assert.Equal(ParametroConocido.ToleranciaPago.ValorPorDefecto, resuelto);
    }

    [Fact]
    public void UnaClaveDesconocidaEsRechazada()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ResolucionDeParametros.Resolver("clave_inventada", candidatos: [], idPuntoVenta: null));

        Assert.Equal("parametro_desconocido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
