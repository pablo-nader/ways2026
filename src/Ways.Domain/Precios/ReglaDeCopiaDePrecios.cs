namespace Ways.Domain.Precios;

/// <summary>Una fila de <c>precios</c> de un par (artículo, lista) sin su identidad: lo que se lee de un
/// artículo de referencia y lo que se escribe para uno nuevo (<see cref="ReglaDeCopiaDePrecios"/>).
/// <see cref="VigenteHasta"/> <c>null</c> ⇒ fila abierta, como en <see cref="Precio"/>.</summary>
public sealed record FilaDePrecio(decimal Monto, DateTimeOffset VigenteDesde, DateTimeOffset? VigenteHasta);

/// <summary>
/// Qué filas de <c>precios</c> necesita, en UNA lista fija, un artículo que tiene que quedar con un
/// <see cref="EstadoDePrecios"/> dado (doc 10 §3): el de un miembro de referencia, para un artículo recién creado
/// que entra a una familia. Regla pura, sin base de datos.
///
/// <para>El artículo no hereda la historia: su precio vigente arranca en <c>ahora</c>, no en la fecha en que el de
/// la referencia empezó, y se cierra donde empieza el pendiente. El pendiente lo hereda con su misma fecha.</para>
/// </summary>
public static class ReglaDeCopiaDePrecios
{
    /// <summary>
    /// Las filas para el artículo nuevo, en el orden en que se insertan (primero la vigente, después la
    /// pendiente): la vigente a <paramref name="ahora"/> con <c>vigente_desde = ahora</c> y
    /// <c>vigente_hasta</c> en el inicio del pendiente (nulo si no hay), y la pendiente con su fecha. Vacío si
    /// la referencia no tiene ni lo uno ni lo otro. <paramref name="filasDeLaReferencia"/> puede traer
    /// cualquier fila del par, historia incluida: la historia y las filas muertas (ventana vacía de un
    /// reemplazo con la misma fecha) no son ni vigentes ni pendientes y se ignoran
    /// (<see cref="EstadoDePrecios.De"/>).
    /// </summary>
    public static IReadOnlyList<FilaDePrecio> FilasParaElNuevoMiembro(
        IReadOnlyList<FilaDePrecio> filasDeLaReferencia, DateTimeOffset ahora) =>
        FilasDelEstado(EstadoDePrecios.De(filasDeLaReferencia, ahora), ahora);

    /// <summary>
    /// Las filas que, insertadas en un par sin ninguna fila abierta, lo dejan en <paramref name="estado"/> a
    /// <paramref name="ahora"/>, en el orden en que se insertan: la vigente con <c>vigente_desde = ahora</c> y
    /// <c>vigente_hasta</c> en el inicio del pendiente (nulo si no hay), y la pendiente con su fecha. Vacío si el
    /// estado no tiene ni lo uno ni lo otro.
    /// </summary>
    public static IReadOnlyList<FilaDePrecio> FilasDelEstado(EstadoDePrecios estado, DateTimeOffset ahora)
    {
        var filas = new List<FilaDePrecio>(2);

        if (estado.Vigente is { } vigente)
        {
            filas.Add(new FilaDePrecio(vigente, ahora, estado.Pendiente?.VigenteDesde));
        }

        if (estado.Pendiente is { } pendiente)
        {
            filas.Add(new FilaDePrecio(pendiente.Monto, pendiente.VigenteDesde, VigenteHasta: null));
        }

        return filas;
    }
}
