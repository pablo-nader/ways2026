namespace Ways.Domain.Catalogos;

/// <summary>
/// El agrupador de ofertas y márgenes: "todas las latas 473cc a 3x2" (doc 10 §1). Destino
/// de las ofertas de grupo.
/// </summary>
public class Grupo : CatalogoSimple
{
    /// <summary>Margen sugerido del grupo. Opcional, editable por fila.</summary>
    public decimal? Margen { get; set; }
}
