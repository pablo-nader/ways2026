namespace Ways.Domain.Articulos;

/// <summary>
/// Qué resuelve <see cref="ReglaDeFamilias.ResolverIngreso"/> sobre el alta de un artículo dentro de una
/// familia (doc 10 §3). Las tres últimas son rechazos: el escritor las convierte en un 409 sin escribir
/// nada. El orden de la declaración es el de precedencia de la regla: una familia inactiva se informa antes
/// que una familia sin artículos, y esta antes que valores distintos.
/// </summary>
public enum ResolucionDeIngresoAFamilia
{
    /// <summary>La familia está activa, tiene un miembro vivo de referencia y los trece campos compartidos del
    /// pedido son idénticos a los suyos: el artículo entra.</summary>
    Permitido,

    /// <summary>La familia está inactiva: no se le agregan artículos (<c>familia_inactiva</c>).</summary>
    FamiliaInactiva,

    /// <summary>La familia no tiene ningún miembro vivo: no hay valores compartidos contra los que comparar ni
    /// estado de precios que copiar (<c>familia_sin_articulos</c>).</summary>
    FamiliaSinArticulos,

    /// <summary>Algún campo compartido del pedido difiere del miembro de referencia
    /// (<c>familia_valores_distintos</c>): un artículo no entra a una familia con valores propios.</summary>
    ValoresDistintos
}
