using Ways.Application.Bajas;
using Ways.Application.Organizacion;

namespace Ways.Application.Tests.Bajas;

/// <summary>
/// fix/bajas-catalogos-guarda-de-uso: <see cref="GuardaDeReferencias.ComponerMensaje"/> es
/// pura (sin base) — la reglas de join en castellano, el override por etiqueta propia y el
/// dedupe corren enteros en memoria. El resto de <see cref="GuardaDeReferencias"/> (el lock de
/// fila y la llamada a <c>InspectorDeUso.TablasQueReferencianAsync</c>) necesita Postgres real y
/// se prueba en <c>Ways.IntegrationTests.BajasDeCatalogosTests</c>.
///
/// Qué cláusula prueba cada test está dicho en su propio doc-comment (<c>mutation-proof-tests</c>
/// regla 1).
/// </summary>
public class GuardaDeReferenciasTests
{
    /// <summary>Cláusula: una sola etiqueta no lleva ninguna conjunción — "tiene X.", nunca
    /// "tiene X y.".</summary>
    [Fact]
    public void UnaSolaEtiquetaNoLlevaConjuncion()
    {
        var mensaje = GuardaDeReferencias.ComponerMensaje("el área", ["articulos"], etiquetasPropias: null);

        Assert.Equal("No se puede dar de baja el área porque tiene artículos.", mensaje);
    }

    /// <summary>Cláusula: dos etiquetas se unen con " y ", sin coma.</summary>
    [Fact]
    public void DosEtiquetasSeUnenConYSinComa()
    {
        var mensaje = GuardaDeReferencias.ComponerMensaje(
            "el área", ["articulos", "gastos"], etiquetasPropias: null);

        Assert.Equal("No se puede dar de baja el área porque tiene artículos y gastos.", mensaje);
        Assert.DoesNotContain(", y", mensaje);
    }

    /// <summary>Cláusula: tres o más etiquetas se unen con comas y un solo " y " antes de la
    /// última — nunca una coma de Oxford.</summary>
    [Fact]
    public void TresEtiquetasSeUnenConComasYUnSoloYFinal()
    {
        var mensaje = GuardaDeReferencias.ComponerMensaje(
            "el proveedor", ["articulos", "gastos", "ordenes_compra"], etiquetasPropias: null);

        Assert.Equal(
            "No se puede dar de baja el proveedor porque tiene artículos, gastos y órdenes de compra.", mensaje);
    }

    /// <summary>Cláusula: una etiqueta propia del catálogo (<c>etiquetasPropias</c>) pisa a
    /// <see cref="EtiquetasDeTablas.DescribirBloqueo"/> para esa tabla puntual — "categorias" se
    /// redacta "subcategorías" para <c>ServicioDeCategorias</c>, no "categorías" a secas.</summary>
    [Fact]
    public void UnaEtiquetaPropiaPisaALaGenericaDeEtiquetasDeTablas()
    {
        var propias = new Dictionary<string, string>(StringComparer.Ordinal) { ["categorias"] = "subcategorías" };

        var mensaje = GuardaDeReferencias.ComponerMensaje("la categoría", ["categorias"], propias);

        Assert.Equal("No se puede dar de baja la categoría porque tiene subcategorías.", mensaje);
    }

    /// <summary>Cláusula: <c>etiquetasPropias</c> solo pisa la tabla que declara — el resto sigue
    /// resolviendo por <see cref="EtiquetasDeTablas.DescribirBloqueo"/>.</summary>
    [Fact]
    public void UnaEtiquetaPropiaNoAfectaAOtrasTablas()
    {
        var propias = new Dictionary<string, string>(StringComparer.Ordinal) { ["listas_precio"] = "listas derivadas" };

        var mensaje = GuardaDeReferencias.ComponerMensaje(
            "la lista de precios", ["clientes", "listas_precio"], propias);

        Assert.Equal(
            "No se puede dar de baja la lista de precios porque tiene clientes y listas derivadas.", mensaje);
    }

    /// <summary>Cláusula: dos etiquetas de RAMA distintas que describen igual (p.ej.
    /// <c>comprobantes_compra</c> e <c>items_comprobante_compra</c>, ambas "compras") colapsan a
    /// UNA sola mención — sin esto, un proveedor referenciado por el comprobante Y sus ítems diría
    /// "tiene compras y compras.".</summary>
    [Fact]
    public void DosEtiquetasQueDescribenIgualColapsanAUnaSolaMencion()
    {
        var mensaje = GuardaDeReferencias.ComponerMensaje(
            "el proveedor", ["comprobantes_compra", "items_comprobante_compra"], etiquetasPropias: null);

        Assert.Equal("No se puede dar de baja el proveedor porque tiene compras.", mensaje);
    }

    /// <summary>Cláusula: una rama PUENTEADA (etiqueta con <c>" via "</c>) sigue resolviendo por
    /// <see cref="EtiquetasDeTablas.DescribirBloqueo"/>, que nombra el puente — el guard de
    /// catálogos no reinventa ese parseo.</summary>
    [Fact]
    public void UnaEtiquetaPuenteadaNombraElPuente()
    {
        var mensaje = GuardaDeReferencias.ComponerMensaje(
            "la empresa", ["comprobantes_venta via puntos_venta"], etiquetasPropias: null);

        Assert.Equal("No se puede dar de baja la empresa porque tiene ventas en sus puntos de venta.", mensaje);
    }

    /// <summary>Cláusula: una tabla sin etiqueta propia y sin entrada en
    /// <see cref="EtiquetasDeTablas"/> degrada a <see cref="EtiquetasDeTablas.Generica"/>, nunca
    /// tira ni deja el nombre técnico de la tabla en el mensaje.</summary>
    [Fact]
    public void UnaTablaSinEtiquetaDegradaALaGenerica()
    {
        var mensaje = GuardaDeReferencias.ComponerMensaje(
            "el tenant", ["numeraciones_comprobante"], etiquetasPropias: null);

        Assert.Equal($"No se puede dar de baja el tenant porque tiene {EtiquetasDeTablas.Generica}.", mensaje);
    }
}
