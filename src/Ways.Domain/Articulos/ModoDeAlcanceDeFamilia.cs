namespace Ways.Domain.Articulos;

/// <summary>
/// Qué decidió quien escribe un campo compartido o un precio sobre la familia del artículo que
/// escribe (doc 10 §3, "Familias de artículos"). Es la entrada de
/// <see cref="ReglaDeFamilias.ResolverAlcance"/>; la API solo expone dos de los cuatro valores
/// (<c>Familia</c> y <c>SoloEste</c>, más su ausencia = <see cref="ExigirDecision"/>) — el cuarto
/// es de los llamadores internos.
/// </summary>
public enum ModoDeAlcanceDeFamilia
{
    /// <summary>El llamador no decidió (la solicitud de la API sin <c>alcance</c>). Un artículo
    /// que no es miembro se escribe solo; uno que SÍ es miembro obliga a decidir: el escritor
    /// rechaza con <c>alcance_requerido</c> sin escribir nada.</summary>
    ExigirDecision,

    /// <summary>El llamador pidió explícitamente toda la familia. Solo vale para un miembro: si el
    /// artículo ya no lo es, quien decidió miraba una pantalla vieja y el escritor rechaza con
    /// <c>familia_cambio</c>.</summary>
    Familia,

    /// <summary>El llamador pidió explícitamente "solo este": el artículo SALE de la familia con el
    /// valor nuevo. Solo vale para un miembro (mismo <c>familia_cambio</c> que
    /// <see cref="Familia"/>) y es el único modo que cambia la pertenencia.</summary>
    SoloEste,

    /// <summary>Para llamadores internos que no tienen a nadie a quien preguntar (p.ej. aplicar el
    /// precio sugerido de una compra): un miembro se escribe junto con toda su familia y quien no
    /// lo es se escribe solo, sin error en ningún caso.</summary>
    FamiliaSiCorresponde
}
