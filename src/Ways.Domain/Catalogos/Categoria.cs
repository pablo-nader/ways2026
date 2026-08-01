namespace Ways.Domain.Catalogos;

/// <summary>
/// La taxonomía comercial, jerárquica: Bebidas → Gaseosas → Cola (doc 10 §1). La
/// profundidad máxima (ADR-12) la valida <see cref="ReglaDeCategorias"/> en dominio, no una
/// constraint de SQL — el esquema deja <see cref="IdCategoriaPadre"/> sin restricción.
/// </summary>
public class Categoria : CatalogoSimple
{
    public int Orden { get; set; }

    /// <summary><c>NULL</c> ⇒ categoría raíz. FK compuesta a sí misma (ADR-9): una
    /// categoría de un tenant no puede colgar de la de otro tenant ni por bug.</summary>
    public int? IdCategoriaPadre { get; set; }
}
