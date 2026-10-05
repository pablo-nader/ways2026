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
    /// Cuántos artículos, además del de referencia, admite un pedido: acota los destinos, que son los que se piden alinear
    /// con la referencia. No acota todo lo que un pedido bloquea y lee: al agregar a una familia que ya existe se bloquean
    /// y se leen, además de los destinos, todos los miembros vivos de la familia, que este tope no acota. Tampoco acota por
    /// sí solo los locks advisory de par artículo-lista que el pedido sostiene: cada destino aporta uno por lista fija, así
    /// que con muchas listas el tope de artículos deja pasar más pares de los que <see cref="MaximoDeParesPorPedido"/>
    /// admite, que es el que los acota. Sobre cien artículos se agrupa en varios pedidos.
    /// </summary>
    public const int MaximoDeArticulosPorPedido = 100;

    /// <summary>
    /// Cuántos pares (artículo destino, lista fija) admite un pedido. Cada par es un lock advisory que la transacción
    /// sostiene hasta el commit, y la tabla de locks de Postgres es compartida por todas las conexiones: un pedido que
    /// tomara demasiados la agotaría y rechazaría a los demás con <c>53200</c>. Con <see cref="MaximoDeArticulosPorPedido"/>
    /// destinos admite hasta diez listas fijas; con más listas, los destinos por pedido bajan en proporción.
    /// </summary>
    public const int MaximoDeParesPorPedido = 1000;

    /// <summary>
    /// Los artículos que hay que alinear con la referencia: los ids pedidos sin repetir, sin el de la referencia —que
    /// ya es el modelo, y puede venir repetido en el pedido— y ascendentes. <c>null</c> es una lista vacía. Los ids que
    /// no son de ningún artículo no se descartan acá: son un rechazo del servicio, que es quien los busca.
    /// <c>400 demasiados_articulos</c> si son más que <see cref="MaximoDeArticulosPorPedido"/>.
    /// </summary>
    /// <param name="idArticuloReferencia">El id de la referencia, o <c>null</c> si el pedido no trae ninguna.</param>
    public static IReadOnlyList<int> Destinos(IReadOnlyList<int>? idsPedidos, int? idArticuloReferencia)
    {
        var destinos = Normalizar(idsPedidos, idArticuloReferencia);

        ExigirTope(destinos);

        return destinos;
    }

    /// <summary>
    /// Los ids pedidos al AGREGAR artículos a una familia que ya existe, sin repetir y ascendentes. La referencia es la
    /// de la familia —el miembro vivo de menor id—, que el servicio resuelve bajo el lock de membresía: hasta entonces
    /// puede ser uno de los ids pedidos, y la referencia no es un destino. Por eso admite UNO más que
    /// <see cref="MaximoDeArticulosPorPedido"/> (<c>400 demasiados_articulos</c> si son más que eso): el tope exacto lo exige
    /// <see cref="ExigirTope"/> sobre los destinos, una vez que la referencia salió de la lista.
    /// </summary>
    public static IReadOnlyList<int> DestinosAlAgregar(IReadOnlyList<int>? idsPedidos)
    {
        var ids = Normalizar(idsPedidos, idArticuloReferencia: null);

        if (ids.Count > MaximoDeArticulosPorPedido + 1)
        {
            throw ErrorDeTopeDeArticulos();
        }

        return ids;
    }

    /// <summary><c>400 demasiados_articulos</c> si hay más destinos que <see cref="MaximoDeArticulosPorPedido"/>.</summary>
    public static void ExigirTope(IReadOnlyCollection<int> destinos)
    {
        if (destinos.Count > MaximoDeArticulosPorPedido)
        {
            throw ErrorDeTopeDeArticulos();
        }
    }

    /// <summary>
    /// <c>400 demasiados_articulos</c> si los <paramref name="destinos"/> por las <paramref name="listasFijas"/> del tenant
    /// son más pares que <see cref="MaximoDeParesPorPedido"/>. El mensaje de ese rechazo es el de
    /// <see cref="MensajeSiExcedeLosPares"/>.
    /// </summary>
    public static void ExigirParesAcotados(int destinos, int listasFijas)
    {
        if (MensajeSiExcedeLosPares(destinos, listasFijas) is { } mensaje)
        {
            throw new ErrorDominio("demasiados_articulos", mensaje, 400);
        }
    }

    /// <summary>El mensaje del rechazo por pares, o <c>null</c> si los <paramref name="destinos"/> por las
    /// <paramref name="listasFijas"/> no pasan de <see cref="MaximoDeParesPorPedido"/>. La previsualización lo informa como
    /// problema con el mismo texto que el pedido real da como error.</summary>
    public static string? MensajeSiExcedeLosPares(int destinos, int listasFijas)
    {
        var pares = (long)destinos * listasFijas;

        return pares > MaximoDeParesPorPedido
            ? $"Un pedido admite como máximo {MaximoDeParesPorPedido} pares de artículo y lista de precios fija, y este tiene " +
                $"{pares} ({destinos} artículos × {listasFijas} listas fijas). Hay que agrupar los artículos en varios pedidos."
            : null;
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

    private static List<int> Normalizar(IReadOnlyList<int>? idsPedidos, int? idArticuloReferencia) =>
        (idsPedidos ?? [])
            .Where(id => id != idArticuloReferencia)
            .Distinct()
            .Order()
            .ToList();

    private static ErrorDominio ErrorDeTopeDeArticulos() =>
        new(
            "demasiados_articulos",
            $"Un pedido agrupa como máximo {MaximoDeArticulosPorPedido} artículos además del de referencia.",
            400);
}
