using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// stage-6-turnos-caja, Slice 2 (task 2.1, task 2.7, design decisión 10) — pura, sin base de
/// datos. Única transición válida: Abierto → Cerrado.
/// </summary>
public class ReglaDeTurnosTests
{
    [Fact]
    public void AbiertoACerradoEsUnaTransicionValida() =>
        ReglaDeTurnos.ValidarTransicionAEstado(EstadoTurno.Abierto, EstadoTurno.Cerrado);

    [Fact]
    public void CerradoNoPuedeVolverAAbrirse()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeTurnos.ValidarTransicionAEstado(EstadoTurno.Cerrado, EstadoTurno.Abierto));
        Assert.Equal("turno_ya_cerrado", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void CerradoNoPuedeVolverACerrarse()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeTurnos.ValidarTransicionAEstado(EstadoTurno.Cerrado, EstadoTurno.Cerrado));
        Assert.Equal("turno_ya_cerrado", excepcion.Codigo);
    }

    [Fact]
    public void AbiertoNoPuedeQuedarseEnAbiertoComoTransicion()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeTurnos.ValidarTransicionAEstado(EstadoTurno.Abierto, EstadoTurno.Abierto));
        Assert.Equal("transicion_de_estado_invalida", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }
}
