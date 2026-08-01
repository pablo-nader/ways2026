using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Regla de negocio pura, sin dependencias (ADR-12): la profundidad de <c>categorias</c> se
/// valida en dominio con dos hechos que Infrastructure calcula con una única CTE recursiva
/// por escritura — <c>nivelDelPadre</c> (cantidad de ancestros del padre elegido) y
/// <c>alturaDelSubarbol</c> (altura del subárbol que cuelga del nodo insertado o movido).
/// No se guarda un nivel denormalizado: reescribirlo en cada re-parent y el riesgo de que
/// alguien lo desincronice escribiendo una categoría por SQL no valen la pena para catálogos
/// de unas pocas docenas de filas.
/// </summary>
public static class ReglaDeCategorias
{
    public const int ProfundidadMaxima = 3;

    /// <summary>Valida que insertar o mover un nodo no haga que ninguna hoja del subárbol
    /// que cuelga de él supere <see cref="ProfundidadMaxima"/> niveles.
    /// <paramref name="nivelDelPadre"/>: 0 si el nodo va a ser raíz (sin padre), o la
    /// cantidad de ancestros del padre elegido. <paramref name="alturaDelSubarbol"/>: 0 si
    /// el nodo no tiene hijos, o la altura del subárbol que cuelga de él.</summary>
    public static void ValidarProfundidad(int nivelDelPadre, int alturaDelSubarbol)
    {
        var profundidadResultante = nivelDelPadre + 1 + alturaDelSubarbol;

        if (profundidadResultante > ProfundidadMaxima)
        {
            throw new ErrorDominio(
                "categoria_profundidad_excedida",
                $"La categoría superaría la profundidad máxima de {ProfundidadMaxima} niveles.",
                400);
        }
    }

    /// <summary>Valida que mover una categoría bajo <paramref name="idDestino"/> no cree un
    /// ciclo: el destino no puede ser ni la propia categoría ni ninguno de sus
    /// descendientes.</summary>
    public static void ValidarSinCiclo(int idDestino, IReadOnlyCollection<int> descendientes)
    {
        if (descendientes.Contains(idDestino))
        {
            throw new ErrorDominio(
                "categoria_ciclo",
                "No se puede mover una categoría dentro de su propio subárbol.",
                400);
        }
    }
}
