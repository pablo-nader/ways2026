namespace Ways.Domain.Articulos;

/// <summary>
/// Reglas puras de familias de artículos (doc 10 §3), sin base de datos — mismo criterio que
/// <see cref="ReglaDeArticulos"/>. Las usan los escritores que respetan la invariante de la familia: el de
/// precios (<c>ServicioDePrecios.AbrirNuevoPrecioAsync</c>) y el de artículos
/// (<c>ServicioDeArticulos.ActualizarAsync</c> y <c>ServicioDeArticulos.CrearAsync</c>).
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
    /// La decisión de la EDICIÓN de un artículo que ES miembro (<see cref="ResolverAlcance"/> con
    /// <c>esMiembro: true</c>, afinada por lo que la edición toca). Quien edita un artículo no siempre
    /// cambia un campo compartido: <paramref name="cambiaCamposCompartidos"/> es el resultado de comparar
    /// los trece valores del pedido con los ACTUALES del artículo (<see cref="ValoresCompartidosDeFamilia"/>),
    /// y tiene que salir de la lectura hecha bajo el lock de membresía y las filas de los miembros. Cuando el
    /// pedido deja <c>acumula_en_venta</c> en <c>null</c> para conservar el valor guardado, ese campo entra a la
    /// comparación con el valor guardado del artículo —leído en esa misma lectura— como valor del pedido, así
    /// que no cuenta como cambio.
    ///
    /// <code>
    /// modo                 | cambia compartidos | no cambia compartidos
    /// ExigirDecision       | AlcanceRequerido   | SoloElArticulo
    /// Familia              | TodaLaFamilia      | SoloElArticulo
    /// SoloEste             | SalirDeLaFamilia   | SalirDeLaFamilia
    /// FamiliaSiCorresponde | TodaLaFamilia      | SoloElArticulo
    /// </code>
    ///
    /// Sin cambio de campos compartidos no hay nada que replicar —los demás miembros ya son idénticos al
    /// artículo— ni nada que decidir, así que no se exige alcance y se escriben solo los campos propios del
    /// artículo. "Solo este" es la excepción: es una decisión explícita de salir de la familia y se respeta
    /// aunque el cambio no toque ningún campo compartido. <see cref="ResolucionDeAlcanceDeFamilia.FamiliaCambio"/>
    /// no sale nunca de acá: es el rechazo de quien NO es miembro, que se decide antes de leer ningún valor
    /// con <see cref="ResolverAlcance"/>.
    /// </summary>
    public static ResolucionDeAlcanceDeFamilia ResolverEdicionDeMiembro(
        ModoDeAlcanceDeFamilia modo, bool cambiaCamposCompartidos)
    {
        var resolucion = ResolverAlcance(modo, esMiembro: true);

        return !cambiaCamposCompartidos
            && (resolucion is ResolucionDeAlcanceDeFamilia.AlcanceRequerido or ResolucionDeAlcanceDeFamilia.TodaLaFamilia)
            ? ResolucionDeAlcanceDeFamilia.SoloElArticulo
            : resolucion;
    }

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
