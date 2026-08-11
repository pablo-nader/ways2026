using Ways.Application.Exportacion;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11, Slice 1a (design decisión 5) — el valor por defecto de producción es 25.000; el
/// fixture de integración de la slice 1b lo pisa con un valor bajo para ejercitar el rechazo por
/// tope sin sembrar 25.001 filas.
/// </summary>
public class OpcionesDeExportacionTests
{
    [Fact]
    public void ElTopeDeFilasPorDefectoEs25000()
    {
        var opciones = new OpcionesDeExportacion();

        Assert.Equal(25_000, opciones.TopeDeFilas);
    }
}
