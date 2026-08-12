using Ways.Application.Exportacion;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11, Slice 1b (design decisión 5-6) — <c>Exigir</c> toma la cantidad de filas ya contada
/// por el caller, para que un reporte de listado (slice 3) pueda pasar un <c>COUNT(*)</c> sin
/// materializar filas de más.
/// </summary>
public class GuardaDeTopeTests
{
    [Fact]
    public void UnaCantidadPorEncimaDelTopeRechazaConElCodigoDeDominio()
    {
        var error = Assert.Throws<ErrorDominio>(() => GuardaDeTope.Exigir(cantidadDeFilas: 4, topeDeFilas: 3));

        Assert.Equal("exportacion_demasiado_grande", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.Contains("4", error.Message);
        Assert.Contains("3", error.Message);
    }

    [Fact]
    public void UnaCantidadExactamenteEnElTopeNoLanza()
    {
        var excepcion = Record.Exception(() => GuardaDeTope.Exigir(cantidadDeFilas: 3, topeDeFilas: 3));

        Assert.Null(excepcion);
    }

    [Fact]
    public void UnaCantidadPorDebajoDelTopeNoLanza()
    {
        var excepcion = Record.Exception(() => GuardaDeTope.Exigir(cantidadDeFilas: 1, topeDeFilas: 3));

        Assert.Null(excepcion);
    }
}
