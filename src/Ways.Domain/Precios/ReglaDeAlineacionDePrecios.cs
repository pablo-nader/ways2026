namespace Ways.Domain.Precios;

/// <summary>Lo que hay que hacer con el estado de precios de un artículo —el destino— en UNA lista fija para que
/// quede alineado con el de la referencia (<see cref="ReglaDeAlineacionDePrecios"/>). Los dos últimos valores son
/// los rechazos: un precio nunca se quita, así que hay estados que no se pueden alinear.</summary>
public enum ResolucionDeAlineacionDePrecios
{
    /// <summary>Los dos estados ya son iguales: no se escribe ninguna fila.</summary>
    SinCambios,

    /// <summary>El destino pasa a tener el estado de la referencia: se cierran sus filas abiertas y se insertan las
    /// del estado (<see cref="ReglaDeCopiaDePrecios.FilasDelEstado"/>).</summary>
    Alinear,

    /// <summary>La referencia no tiene ningún precio en la lista —ni vigente ni pendiente— y el destino sí: quitarle un
    /// precio al destino no es una alineación que se pueda escribir.</summary>
    InalineablePorReferenciaSinPrecios,

    /// <summary>La referencia solo tiene un precio programado, sin vigente, y el destino tiene un precio vigente: el
    /// destino no puede quedarse sin vigente hasta que llegue la fecha del programado.</summary>
    InalineablePorReferenciaSoloProgramada
}

/// <summary>
/// La tabla de decisión de la alineación de precios de una familia (doc 10 §3): qué hace falta para que el estado de
/// precios de un artículo en una lista fija quede igual al de la referencia. Regla pura, sin base de datos: compara
/// los <see cref="EstadoDePrecios"/> por valor —el vigente, y el monto y la fecha del pendiente— y decide.
///
/// <code>
/// referencia          | destino                              | resolución
/// X                   | X (el mismo estado)                  | SinCambios
/// (c, ninguno)        | cualquier otro                       | Alinear: c desde "ahora", sin pendiente
/// (c, (p, V))         | cualquier otro                       | Alinear: c desde "ahora" hasta V y p desde V
/// (ninguno, (p, V))   | sin vigente                          | Alinear: p desde V (reemplaza el pendiente propio)
/// (ninguno, (p, V))   | con vigente                          | InalineablePorReferenciaSoloProgramada
/// (ninguno, ninguno)  | cualquier otro (tiene algún precio)  | InalineablePorReferenciaSinPrecios
/// </code>
///
/// Alinear nunca cambia el monto de una fila existente: el destino cierra las filas que tenga abiertas y se insertan
/// las filas del estado de la referencia (<see cref="ReglaDeCopiaDePrecios.FilasDelEstado"/>), que es lo que ya hace
/// un cambio de precio.
/// </summary>
public static class ReglaDeAlineacionDePrecios
{
    public static ResolucionDeAlineacionDePrecios Resolver(EstadoDePrecios referencia, EstadoDePrecios destino)
    {
        if (referencia == destino)
        {
            return ResolucionDeAlineacionDePrecios.SinCambios;
        }

        if (referencia.Vigente is not null)
        {
            return ResolucionDeAlineacionDePrecios.Alinear;
        }

        if (referencia.Pendiente is not null)
        {
            return destino.Vigente is null
                ? ResolucionDeAlineacionDePrecios.Alinear
                : ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSoloProgramada;
        }

        return ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSinPrecios;
    }
}
