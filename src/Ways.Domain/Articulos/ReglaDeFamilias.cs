namespace Ways.Domain.Articulos;

/// <summary>
/// Reglas puras de familias de artículos (doc 10 §3), sin base de datos — mismo criterio que
/// <see cref="ReglaDeArticulos"/>. Hoy las usa el escritor de precios
/// (<c>ServicioDePrecios.AbrirNuevoPrecioAsync</c>).
/// </summary>
public static class ReglaDeFamilias
{
    /// <summary>
    /// La tabla de decisión modo × pertenencia:
    ///
    /// <code>
    /// modo                  | no es miembro   | es miembro
    /// ExigirDecision        | SoloElArticulo  | AlcanceRequerido
    /// Familia               | FamiliaCambio   | TodaLaFamilia
    /// SoloEste              | FamiliaCambio   | SalirDeLaFamilia
    /// FamiliaSiCorresponde  | SoloElArticulo  | TodaLaFamilia
    /// </code>
    ///
    /// <paramref name="esMiembro"/> tiene que ser la pertenencia leída BAJO el lock de membresía
    /// (<c>LockDeMembresiaDeFamilias</c>): leída antes, la decisión se tomaría sobre un estado que
    /// otro escritor puede estar cambiando.
    /// </summary>
    public static ResolucionDeAlcanceDeFamilia ResolverAlcance(ModoDeAlcanceDeFamilia modo, bool esMiembro) =>
        (modo, esMiembro) switch
        {
            (ModoDeAlcanceDeFamilia.ExigirDecision, false) => ResolucionDeAlcanceDeFamilia.SoloElArticulo,
            (ModoDeAlcanceDeFamilia.ExigirDecision, true) => ResolucionDeAlcanceDeFamilia.AlcanceRequerido,
            (ModoDeAlcanceDeFamilia.Familia, false) => ResolucionDeAlcanceDeFamilia.FamiliaCambio,
            (ModoDeAlcanceDeFamilia.Familia, true) => ResolucionDeAlcanceDeFamilia.TodaLaFamilia,
            (ModoDeAlcanceDeFamilia.SoloEste, false) => ResolucionDeAlcanceDeFamilia.FamiliaCambio,
            (ModoDeAlcanceDeFamilia.SoloEste, true) => ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia,
            (ModoDeAlcanceDeFamilia.FamiliaSiCorresponde, false) => ResolucionDeAlcanceDeFamilia.SoloElArticulo,
            (ModoDeAlcanceDeFamilia.FamiliaSiCorresponde, true) => ResolucionDeAlcanceDeFamilia.TodaLaFamilia,
            _ => throw new ArgumentOutOfRangeException(nameof(modo), modo, "Modo de alcance de familia desconocido.")
        };

    /// <summary>
    /// <c>true</c> si el escritor puede cambiar la PERTENENCIA y por eso toma el lock de membresía
    /// en modo exclusivo; <c>false</c> si solo necesita que la pertenencia no cambie mientras
    /// escribe (modo compartido). Se decide por el modo pedido, antes de leer nada: un lock
    /// compartido no se puede promover a exclusivo sin riesgo de deadlock entre dos escritores que
    /// lo intenten a la vez.
    /// </summary>
    public static bool RequiereLockExclusivo(ModoDeAlcanceDeFamilia modo) =>
        modo == ModoDeAlcanceDeFamilia.SoloEste;
}
