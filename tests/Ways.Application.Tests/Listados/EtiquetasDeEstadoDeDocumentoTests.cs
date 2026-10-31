using Ways.Application.Listados;
using Ways.Domain.Compras;
using Ways.Domain.Ventas;

namespace Ways.Application.Tests.Listados;

/// <summary>Guarda contra el desfase entre las etiquetas de estado del servidor (con las que ordenan los listados)
/// y las de la web (las que se ven). Fija la lista literal de pares valor-etiqueta; su espejo es
/// <c>etiquetasDeEstadoDeDocumento.test.ts</c> en <c>Ways.Web/src/api</c>, que fija la MISMA lista.</summary>
public class EtiquetasDeEstadoDeDocumentoTests
{
    [Fact]
    public void LasEtiquetasDeCompraSonExactamenteLaListaFijada()
    {
        Assert.Equal(
            [("Borrador", "Borrador"), ("Confirmada", "Confirmada"), ("Anulada", "Anulada")],
            EtiquetasDeEstadoDeDocumento.Compra.Select(p => (p.Key.ToString(), p.Value)).ToList());
    }

    [Fact]
    public void LasEtiquetasDePresupuestoSonExactamenteLaListaFijada()
    {
        Assert.Equal(
            [("Borrador", "Borrador"), ("Enviado", "Enviado"), ("Convertido", "Convertido"), ("Anulado", "Anulado")],
            EtiquetasDeEstadoDeDocumento.Presupuesto.Select(p => (p.Key.ToString(), p.Value)).ToList());
    }

    [Fact]
    public void LasEtiquetasDeRemitoSonExactamenteLaListaFijada()
    {
        Assert.Equal(
            [("Borrador", "Borrador"), ("Emitido", "Emitido"), ("Facturado", "Facturado"), ("Anulado", "Anulado")],
            EtiquetasDeEstadoDeDocumento.Remito.Select(p => (p.Key.ToString(), p.Value)).ToList());
    }

    [Fact]
    public void LasEtiquetasDeOrdenDeCompraSonExactamenteLaListaFijada()
    {
        Assert.Equal(
            [
                ("Borrador", "Borrador"),
                ("Enviada", "Enviada"),
                ("RecibidaParcial", "Recibida parcial"),
                ("Cerrada", "Cerrada"),
                ("Anulada", "Anulada"),
            ],
            EtiquetasDeEstadoDeDocumento.OrdenCompra.Select(p => (p.Key.ToString(), p.Value)).ToList());
    }

    [Fact]
    public void TodoValorDeCadaEnumTieneEtiqueta()
    {
        Assert.All(Enum.GetValues<EstadoCompra>(), e => Assert.True(EtiquetasDeEstadoDeDocumento.Compra.ContainsKey(e), $"Falta {e}."));
        Assert.All(Enum.GetValues<EstadoPresupuesto>(), e => Assert.True(EtiquetasDeEstadoDeDocumento.Presupuesto.ContainsKey(e), $"Falta {e}."));
        Assert.All(Enum.GetValues<EstadoRemito>(), e => Assert.True(EtiquetasDeEstadoDeDocumento.Remito.ContainsKey(e), $"Falta {e}."));
        Assert.All(Enum.GetValues<EstadoOrdenCompra>(), e => Assert.True(EtiquetasDeEstadoDeDocumento.OrdenCompra.ContainsKey(e), $"Falta {e}."));
    }
}
