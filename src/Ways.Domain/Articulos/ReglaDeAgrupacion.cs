using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Las reglas de forma del pedido de agrupar artículos en una familia (doc 10 §3), sin base de datos: qué ids se
/// agrupan de verdad y cuántos se admiten por pedido. Las usan la previsualización, el alta de una familia con sus
/// artículos y el agregado de artículos a una familia que ya existe.
/// </summary>
public static class ReglaDeAgrupacion
{
    /// <summary>
    /// Cuántos artículos, además del de referencia, admite un pedido. Cada uno aporta un lock advisory por lista fija
    /// que la transacción sostiene hasta el commit, y la tabla de locks de Postgres es compartida por todas las
    /// conexiones: un pedido sin tope podría agotarla y rechazar a los demás con <c>53200</c>. Sobre cien artículos se
    /// agrupa en varios pedidos.
    /// </summary>
    public const int MaximoDeArticulosPorPedido = 100;

    /// <summary>
    /// Los artículos que hay que alinear con la referencia: los ids pedidos sin repetir, sin el de la referencia —que
    /// ya es el modelo, y puede venir repetido en el pedido— y ascendentes. <c>null</c> es una lista vacía. Los ids que
    /// no son de ningún artículo no se descartan acá: son un rechazo del servicio, que es quien los busca.
    /// <c>400 demasiados_articulos</c> si son más que <see cref="MaximoDeArticulosPorPedido"/>.
    /// </summary>
    /// <param name="idArticuloReferencia">El id de la referencia, o <c>null</c> cuando todavía no se conoce (el
    /// agregado a una familia existente la resuelve bajo el lock de membresía).</param>
    public static IReadOnlyList<int> Destinos(IReadOnlyList<int>? idsPedidos, int? idArticuloReferencia)
    {
        var destinos = (idsPedidos ?? [])
            .Where(id => id != idArticuloReferencia)
            .Distinct()
            .Order()
            .ToList();

        if (destinos.Count > MaximoDeArticulosPorPedido)
        {
            throw new ErrorDominio(
                "demasiados_articulos",
                $"Un pedido agrupa como máximo {MaximoDeArticulosPorPedido} artículos además del de referencia.",
                400);
        }

        return destinos;
    }

    /// <summary><c>400 id_articulo_referencia_requerido</c> si el id no es el de un artículo posible.</summary>
    public static void ExigirReferencia(int idArticuloReferencia)
    {
        if (idArticuloReferencia <= 0)
        {
            throw new ErrorDominio(
                "id_articulo_referencia_requerido", "El campo idArticuloReferencia es obligatorio.", 400);
        }
    }
}
