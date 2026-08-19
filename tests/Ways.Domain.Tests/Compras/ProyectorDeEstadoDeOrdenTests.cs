using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 1 (task 1.20, design decisión 4): la matriz de verdad
/// completa de <see cref="ProyectorDeEstadoDeOrden.Proyectar"/> — pura, sin base de datos, patrón
/// <c>PoliticaDeRoles</c>. Cubre los cinco brazos de prioridad: <c>Anulada</c> terminal desde
/// cualquier input, cierre manual nunca revertido, <c>completa</c> le gana a <c>algoRecibido</c>,
/// y el caso base (nada recibido) se mantiene <c>Enviada</c>.
/// </summary>
public class ProyectorDeEstadoDeOrdenTests
{
    // estadoActual × cierreManual × completa × algoRecibido — 2 estados no terminales
    // (Enviada, RecibidaParcial) cruzados con los 3 booleanos: 2 × 2 × 2 × 2 = 16 casos.
    [Theory]
    [InlineData(EstadoOrdenCompra.Enviada, false, false, false, EstadoOrdenCompra.Enviada)]
    [InlineData(EstadoOrdenCompra.Enviada, false, false, true, EstadoOrdenCompra.RecibidaParcial)]
    [InlineData(EstadoOrdenCompra.Enviada, false, true, false, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.Enviada, false, true, true, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.Enviada, true, false, false, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.Enviada, true, false, true, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.Enviada, true, true, false, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.Enviada, true, true, true, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, false, false, false, EstadoOrdenCompra.Enviada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, false, false, true, EstadoOrdenCompra.RecibidaParcial)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, false, true, false, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, false, true, true, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, true, false, false, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, true, false, true, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, true, true, false, EstadoOrdenCompra.Cerrada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, true, true, true, EstadoOrdenCompra.Cerrada)]
    public void LaMatrizDeVerdadCompletaProyectaElEstadoEsperado(
        EstadoOrdenCompra estadoActual, bool cierreManual, bool completa, bool algoRecibido,
        EstadoOrdenCompra esperado)
    {
        var resultado = ProyectorDeEstadoDeOrden.Proyectar(estadoActual, cierreManual, completa, algoRecibido);

        Assert.Equal(esperado, resultado);
    }

    /// <summary><c>Anulada</c> es terminal: ningún input (cierre manual, derivación completa o
    /// parcial) la abandona — design decisión 9.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void AnuladaEsTerminalDesdeCualquierInput(bool cierreManual, bool completa, bool algoRecibido)
    {
        var resultado = ProyectorDeEstadoDeOrden.Proyectar(
            EstadoOrdenCompra.Anulada, cierreManual, completa, algoRecibido);

        Assert.Equal(EstadoOrdenCompra.Anulada, resultado);
    }

    /// <summary>Un cierre manual (<c>id_empleado_cierre IS NOT NULL</c>) nunca se revierte, sea
    /// cual sea la derivación — design decisión 5.</summary>
    [Theory]
    [InlineData(EstadoOrdenCompra.Enviada, false, false)]
    [InlineData(EstadoOrdenCompra.Enviada, false, true)]
    [InlineData(EstadoOrdenCompra.Enviada, true, false)]
    [InlineData(EstadoOrdenCompra.Enviada, true, true)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, false, false)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, false, true)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, true, false)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial, true, true)]
    public void ElCierreManualNuncaSeRevierte(EstadoOrdenCompra estadoActual, bool completa, bool algoRecibido)
    {
        var resultado = ProyectorDeEstadoDeOrden.Proyectar(
            estadoActual, cierreManual: true, completa, algoRecibido);

        Assert.Equal(EstadoOrdenCompra.Cerrada, resultado);
    }

    /// <summary><c>completa</c> le gana a <c>algoRecibido</c>: con las dos en <c>true</c> el
    /// resultado es <c>Cerrada</c>, nunca <c>RecibidaParcial</c> (design decisión 4).</summary>
    [Theory]
    [InlineData(EstadoOrdenCompra.Enviada)]
    [InlineData(EstadoOrdenCompra.RecibidaParcial)]
    public void CompletaLeGanaAAlgoRecibido(EstadoOrdenCompra estadoActual)
    {
        var resultado = ProyectorDeEstadoDeOrden.Proyectar(
            estadoActual, cierreManual: false, completa: true, algoRecibido: true);

        Assert.Equal(EstadoOrdenCompra.Cerrada, resultado);
    }
}
