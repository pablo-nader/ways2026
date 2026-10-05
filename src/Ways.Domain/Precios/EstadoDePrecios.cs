namespace Ways.Domain.Precios;

/// <summary>Un precio programado: su monto y la fecha desde la que rige. Es la fila abierta de un par
/// (artículo, lista) cuyo <c>vigente_desde</c> todavía no llegó (<see cref="EstadoDePrecios.Pendiente"/>).</summary>
public sealed record PrecioPendiente(decimal Monto, DateTimeOffset VigenteDesde);

/// <summary>
/// El estado de precios de UN par (artículo, lista fija) a un instante (doc 10 §3, "Familias de artículos"): lo
/// que los miembros de una familia tienen que tener idéntico en cada lista fija. Son dos cosas y nada más: el
/// precio <b>vigente</b> —el de la fila con <c>vigente_desde &lt;= ahora</c> y <c>vigente_hasta</c> nulo o
/// posterior a <c>ahora</c>— y, si lo hay, el <b>pendiente</b> —la fila abierta con <c>vigente_desde</c> a
/// futuro, que dejó programar un precio—. La historia cerrada y las filas muertas (ventana vacía de un
/// reemplazo con la misma fecha) no forman parte del estado. Regla pura, sin base de datos: dos artículos están
/// alineados en una lista cuando sus estados son iguales, comparados por valor
/// (<see cref="ReglaDeAlineacionDePrecios"/>).
/// </summary>
public sealed record EstadoDePrecios(decimal? Vigente, PrecioPendiente? Pendiente)
{
    /// <summary>El par sin ningún precio vigente ni pendiente.</summary>
    public static readonly EstadoDePrecios Vacio = new(null, null);

    /// <summary>El estado a <paramref name="ahora"/> que resulta de <paramref name="filas"/>, que pueden ser
    /// cualquier fila del par, historia incluida y en cualquier orden. Con más de un candidato, el vigente es el de
    /// <c>vigente_desde</c> más reciente y el pendiente el más próximo.</summary>
    public static EstadoDePrecios De(IReadOnlyList<FilaDePrecio> filas, DateTimeOffset ahora)
    {
        var vigente = filas
            .Where(f => f.VigenteDesde <= ahora && (f.VigenteHasta is null || f.VigenteHasta > ahora))
            .OrderByDescending(f => f.VigenteDesde)
            .FirstOrDefault();

        var pendiente = filas
            .Where(f => f.VigenteDesde > ahora && f.VigenteHasta is null)
            .OrderBy(f => f.VigenteDesde)
            .FirstOrDefault();

        return new EstadoDePrecios(
            vigente?.Monto,
            pendiente is null ? null : new PrecioPendiente(pendiente.Monto, pendiente.VigenteDesde));
    }
}
