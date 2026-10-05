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
    /// La decisión del ALTA de un artículo dentro de una familia (doc 10 §3), con la precedencia de los
    /// rechazos: familia inactiva, después familia sin miembros vivos y después campos compartidos distintos.
    /// <paramref name="referencia"/> son los trece valores compartidos del miembro vivo de menor id (<c>null</c>
    /// ⇒ la familia no tiene ninguno), y <paramref name="pedidos"/> los del artículo que entra; los dos tienen
    /// que salir de lecturas hechas bajo el lock de membresía exclusivo. Un artículo no entra a una familia con
    /// valores propios: tienen que ser idénticos a los del resto, porque la familia no guarda ninguno y sus
    /// miembros son la fuente de verdad.
    ///
    /// <code>
    /// familia   | miembros vivos | valores compartidos | resolución
    /// inactiva  | (cualquiera)   | (cualquiera)        | FamiliaInactiva
    /// activa    | ninguno        | (no se comparan)    | FamiliaSinArticulos
    /// activa    | alguno         | distintos           | ValoresDistintos
    /// activa    | alguno         | idénticos           | Permitido
    /// </code>
    /// </summary>
    public static ResolucionDeIngresoAFamilia ResolverIngreso(
        bool familiaActiva, ValoresCompartidosDeFamilia? referencia, ValoresCompartidosDeFamilia pedidos)
    {
        if (!familiaActiva)
        {
            return ResolucionDeIngresoAFamilia.FamiliaInactiva;
        }

        if (referencia is null)
        {
            return ResolucionDeIngresoAFamilia.FamiliaSinArticulos;
        }

        return referencia.CamposDistintos(pedidos).Count > 0
            ? ResolucionDeIngresoAFamilia.ValoresDistintos
            : ResolucionDeIngresoAFamilia.Permitido;
    }

    /// <summary>
    /// La decisión de AGREGAR artículos a una familia que ya existe (doc 10 §3): la regla de ingreso sin la comparación
    /// de los campos compartidos, porque lo que se agrega se ALINEA con la referencia en vez de tener que coincidir con
    /// ella. Mismos rechazos y misma precedencia que <see cref="ResolverIngreso"/>: familia inactiva y después familia
    /// sin miembros vivos, que no tiene referencia a la que alinear. <paramref name="familiaActiva"/> y
    /// <paramref name="tieneMiembrosVivos"/> tienen que salir de lecturas hechas bajo el lock de membresía exclusivo.
    ///
    /// <code>
    /// familia   | miembros vivos | resolución
    /// inactiva  | (cualquiera)   | FamiliaInactiva
    /// activa    | ninguno        | FamiliaSinArticulos
    /// activa    | alguno         | Permitido
    /// </code>
    ///
    /// Nunca devuelve <see cref="ResolucionDeIngresoAFamilia.ValoresDistintos"/>.
    /// </summary>
    public static ResolucionDeIngresoAFamilia ResolverAgregado(bool familiaActiva, bool tieneMiembrosVivos)
    {
        if (!familiaActiva)
        {
            return ResolucionDeIngresoAFamilia.FamiliaInactiva;
        }

        return tieneMiembrosVivos
            ? ResolucionDeIngresoAFamilia.Permitido
            : ResolucionDeIngresoAFamilia.FamiliaSinArticulos;
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
