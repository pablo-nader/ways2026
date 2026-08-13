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

    [Fact]
    public void ZonaHorariaResuelveASuDefaultSinFilasConfiguradas()
    {
        var resuelto = ResolucionDeParametros.Resolver("zona_horaria", [], idPuntoVenta: 3);

        Assert.Equal("\"America/Argentina/Buenos_Aires\"", resuelto);
    }

    [Fact]
    public void ComisionPorcentajeResuelveACeroSinFilasConfiguradas()
    {
        var resuelto = ResolucionDeParametros.Resolver("comision_porcentaje", [], idPuntoVenta: 3);

        Assert.Equal("0", resuelto);
    }

    // ---- stage-12 slice 2 (task 2.8, spec parametros-operativos: "lotes_habilitado And
    // dias_alerta_vencimiento Are Known Parametro Keys") ------------------------------------

    private static Parametro FilaDe(string clave, int? idPuntoVenta, string valor) => new()
    {
        Id = 1, IdTenant = 1, IdEmpresa = 1, IdPuntoVenta = idPuntoVenta, Clave = clave, Valor = valor
    };

    [Fact]
    public void LotesHabilitadoResuelveAFalseSinFilaConfigurada()
    {
        var resuelto = ResolucionDeParametros.Resolver("lotes_habilitado", candidatos: [], idPuntoVenta: 3);

        Assert.Equal("false", resuelto);
    }

    [Fact]
    public void UnaFilaDeEmpresaPrendeElModuloParaTodosLosPuntosDeVentaDeEsaEmpresa()
    {
        Parametro[] candidatos = [FilaDe("lotes_habilitado", idPuntoVenta: null, valor: "true")];

        var resueltoParaPv3 = ResolucionDeParametros.Resolver("lotes_habilitado", candidatos, idPuntoVenta: 3);
        var resueltoParaPv9 = ResolucionDeParametros.Resolver("lotes_habilitado", candidatos, idPuntoVenta: 9);

        Assert.Equal("true", resueltoParaPv3);
        Assert.Equal("true", resueltoParaPv9);
    }

    [Fact]
    public void DiasAlertaVencimientoResuelveA30SinFilaConfigurada()
    {
        var resuelto = ResolucionDeParametros.Resolver("dias_alerta_vencimiento", candidatos: [], idPuntoVenta: 3);

        Assert.Equal("30", resuelto);
    }

    [Fact]
    public void UnaFilaDePuntoDeVentaGanaSobreElDefaultDeDiasAlertaVencimiento()
    {
        Parametro[] candidatos =
            [FilaDe("dias_alerta_vencimiento", idPuntoVenta: null, valor: "30"),
             FilaDe("dias_alerta_vencimiento", idPuntoVenta: 3, valor: "15")];

        var resuelto = ResolucionDeParametros.Resolver("dias_alerta_vencimiento", candidatos, idPuntoVenta: 3);

        Assert.Equal("15", resuelto);
    }
}
