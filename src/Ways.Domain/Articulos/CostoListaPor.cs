namespace Ways.Domain.Articulos;

/// <summary>
/// Base sobre la que se cargó <see cref="Articulo.CostoLista"/> (doc 10 §3): el importe de una unidad o el
/// de un bulto completo. Se guarda como texto (<c>costo_lista_por</c>) con el nombre del miembro, acotado por un
/// CHECK de la tabla.
/// </summary>
public enum CostoListaPor
{
    Unidad,
    Bulto
}
