namespace Ways.Domain.Catalogos;

/// <summary>
/// Modo de una lista de precios (doc 10 §3). Enum nativo de Postgres (<c>modo_lista</c>).
/// Esta etapa solo crea listas <see cref="Fija"/> (la General, <c>es_default = true</c>);
/// <see cref="Derivada"/> y las columnas que la acompañan (<c>id_lista_base</c>,
/// <c>porcentaje</c>) quedan declaradas para la etapa 3 (spec: listas-precio-minimal).
/// </summary>
public enum ModoLista
{
    Fija,
    Derivada
}
