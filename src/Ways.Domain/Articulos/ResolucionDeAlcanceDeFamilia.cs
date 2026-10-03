namespace Ways.Domain.Articulos;

/// <summary>
/// Qué tiene que hacer un escritor, dado el <see cref="ModoDeAlcanceDeFamilia"/> pedido y si el
/// artículo es miembro de una familia en este momento (resultado de
/// <see cref="ReglaDeFamilias.ResolverAlcance"/>). Las dos últimas son rechazos: el escritor las
/// convierte en un 409 sin escribir nada.
/// </summary>
public enum ResolucionDeAlcanceDeFamilia
{
    /// <summary>Escribir solo el artículo. No cambia la pertenencia.</summary>
    SoloElArticulo,

    /// <summary>Escribir a todos los miembros vivos de la familia.</summary>
    TodaLaFamilia,

    /// <summary>El artículo sale de la familia (<see cref="Articulo.IdFamilia"/> en <c>null</c>) y se
    /// escribe solo él.</summary>
    SalirDeLaFamilia,

    /// <summary>Miembro sin decisión del llamador: rechazar con <c>alcance_requerido</c>.</summary>
    AlcanceRequerido,

    /// <summary>Decisión explícita sobre un artículo que no es miembro: rechazar con
    /// <c>familia_cambio</c>.</summary>
    FamiliaCambio
}
