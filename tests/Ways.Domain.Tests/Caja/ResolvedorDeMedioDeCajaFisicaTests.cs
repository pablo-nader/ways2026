using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// stage-6-turnos-caja, Slice 4 (tasks 4.2, 4.10, design decisión 3). Pura, sin base de datos —
/// 0 / 1 / 2 medios efectivo.
/// </summary>
public class ResolvedorDeMedioDeCajaFisicaTests
{
    private static ActividadDeMedio Medio(int id, ComportamientoMedioPago comportamiento) =>
        new(id, comportamiento, Pagos: 0m, Vueltos: 0m, Gastos: 0m, TuvoFilas: false);

    [Fact]
    public void UnUnicoMedioEfectivoSeResuelveSinError()
    {
        var medios = new[]
        {
            Medio(1, ComportamientoMedioPago.Efectivo),
            Medio(2, ComportamientoMedioPago.Electronico)
        };

        var idAncla = ResolvedorDeMedioDeCajaFisica.Resolver(medios);

        Assert.Equal(1, idAncla);
    }

    [Fact]
    public void SinNingunMedioEfectivoTira409()
    {
        var medios = new[] { Medio(2, ComportamientoMedioPago.Electronico) };

        var excepcion = Assert.Throws<ErrorDominio>(() => ResolvedorDeMedioDeCajaFisica.Resolver(medios));

        Assert.Equal("caja_sin_medio_efectivo_unico", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void ConDosMediosEfectivoTambienTira409()
    {
        var medios = new[]
        {
            Medio(1, ComportamientoMedioPago.Efectivo),
            Medio(2, ComportamientoMedioPago.Efectivo)
        };

        var excepcion = Assert.Throws<ErrorDominio>(() => ResolvedorDeMedioDeCajaFisica.Resolver(medios));

        Assert.Equal("caja_sin_medio_efectivo_unico", excepcion.Codigo);
    }

    [Fact]
    public void SinNingunMedioEnAbsolutoTambienTira409()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ResolvedorDeMedioDeCajaFisica.Resolver([]));

        Assert.Equal("caja_sin_medio_efectivo_unico", excepcion.Codigo);
    }
}
