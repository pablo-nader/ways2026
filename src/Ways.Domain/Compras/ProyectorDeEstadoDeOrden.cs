namespace Ways.Domain.Compras;

/// <summary>
/// Regla pura de decisión del design decisión 4: la proyección del <see cref="EstadoOrdenCompra"/>
/// vigente a partir del estado actual, la marca de cierre manual y la derivación del libro de
/// recepción (design: Domain — pure, no database; patrón <c>PoliticaDeRoles</c>, testeable sin
/// base de datos). Cinco brazos, en orden de prioridad:
///
/// 1. <see cref="EstadoOrdenCompra.Anulada"/> es terminal: nunca se abandona, sea cual sea el
///    resto del input (design decisión 9).
/// 2. Un cierre manual (<c>cierreManual</c> = <c>id_empleado_cierre IS NOT NULL</c>) tampoco se
///    revierte jamás — la proyección nunca deshace una decisión humana (design decisión 5).
/// 3. <c>completa</c> (todo artículo pedido recibido en cantidad suficiente) cierra
///    automáticamente.
/// 4. <c>algoRecibido</c> (alguna cantidad recibida, incompleta) marca recepción parcial.
/// 5. Ningún recibo todavía ⇒ se mantiene <see cref="EstadoOrdenCompra.Enviada"/>.
///
/// El caller — <c>EscriturasDeOrdenDeCompra.ProyectarEstadoAsync</c> — nunca invoca esta función
/// cuando <c>estadoActual</c> es <see cref="EstadoOrdenCompra.Borrador"/> (statement 1 corta ANTES
/// para <c>Anulada</c>/cierre manual, y un borrador nunca tiene recepciones ligadas); se acepta
/// como input general de todas formas para que la matriz de verdad del test unitario cubra el
/// dominio completo sin depender de esa invariante externa.
/// </summary>
public static class ProyectorDeEstadoDeOrden
{
    public static EstadoOrdenCompra Proyectar(
        EstadoOrdenCompra estadoActual, bool cierreManual, bool completa, bool algoRecibido) =>
        estadoActual is EstadoOrdenCompra.Anulada ? EstadoOrdenCompra.Anulada
        : cierreManual ? EstadoOrdenCompra.Cerrada
        : completa ? EstadoOrdenCompra.Cerrada
        : algoRecibido ? EstadoOrdenCompra.RecibidaParcial
        : EstadoOrdenCompra.Enviada;
}
