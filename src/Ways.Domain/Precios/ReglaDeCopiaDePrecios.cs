namespace Ways.Domain.Precios;

/// <summary>Una fila de <c>precios</c> de un par (artículo, lista) sin su identidad: lo que se lee de un
/// artículo de referencia y lo que se escribe para uno nuevo (<see cref="ReglaDeCopiaDePrecios"/>).
/// <see cref="VigenteHasta"/> <c>null</c> ⇒ fila abierta, como en <see cref="Precio"/>.</summary>
public sealed record FilaDePrecio(decimal Monto, DateTimeOffset VigenteDesde, DateTimeOffset? VigenteHasta);

/// <summary>
/// Qué filas de <c>precios</c> necesita, en UNA lista fija, un artículo recién creado que entra a una familia
/// para tener el mismo estado de precios que sus miembros (doc 10 §3). Regla pura, sin base de datos: parte
/// de las filas del miembro de referencia en esa lista y del instante en que nace el artículo.
///
/// <para>El estado de precios de un par son dos cosas: el precio <b>vigente</b> a un instante
/// (<c>vigente_desde &lt;= ahora</c> y <c>vigente_hasta</c> nulo o posterior a <c>ahora</c>) y, si lo hay, el
/// <b>pendiente</b> (la fila abierta con <c>vigente_desde</c> a futuro, que dejó programar un precio). El
/// artículo nuevo no tiene historia: su precio vigente arranca en <c>ahora</c>, no en la fecha en que el de la
/// referencia empezó, y se cierra donde empieza el pendiente. El pendiente lo hereda con su misma fecha.</para>
/// </summary>
public static class ReglaDeCopiaDePrecios
{
    /// <summary>
    /// Las filas para el artículo nuevo, en el orden en que se insertan (primero la vigente, después la
    /// pendiente): la vigente a <paramref name="ahora"/> con <c>vigente_desde = ahora</c> y
    /// <c>vigente_hasta</c> en el inicio del pendiente (nulo si no hay), y la pendiente con su fecha. Vacío si
    /// la referencia no tiene ni lo uno ni lo otro. <paramref name="filasDeLaReferencia"/> puede traer
    /// cualquier fila del par, historia incluida: la historia y las filas muertas (ventana vacía de un
    /// reemplazo con la misma fecha) no son ni vigentes ni pendientes y se ignoran.
    /// </summary>
    public static IReadOnlyList<FilaDePrecio> FilasParaElNuevoMiembro(
        IReadOnlyList<FilaDePrecio> filasDeLaReferencia, DateTimeOffset ahora)
    {
        var vigente = filasDeLaReferencia
            .Where(f => f.VigenteDesde <= ahora && (f.VigenteHasta is null || f.VigenteHasta > ahora))
            .OrderByDescending(f => f.VigenteDesde)
            .FirstOrDefault();

        var pendiente = filasDeLaReferencia
            .Where(f => f.VigenteDesde > ahora && f.VigenteHasta is null)
            .OrderBy(f => f.VigenteDesde)
            .FirstOrDefault();

        var filas = new List<FilaDePrecio>(2);

        if (vigente is not null)
        {
            filas.Add(new FilaDePrecio(vigente.Monto, ahora, pendiente?.VigenteDesde));
        }

        if (pendiente is not null)
        {
            filas.Add(new FilaDePrecio(pendiente.Monto, pendiente.VigenteDesde, VigenteHasta: null));
        }

        return filas;
    }
}
