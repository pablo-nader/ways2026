using Ways.Domain.Common;

namespace Ways.Domain.Combos;

/// <summary>
/// Reglas de negocio puras de un combo (sin base de datos, mismo criterio que
/// <see cref="Ofertas.ReglaDeOfertas"/>). Todo camino de escritura de combos debe pasar por
/// <see cref="Validar"/> antes de tocar filas (todavía no existe ninguno; lo crea T2); las CHECKs <c>ck_combos_precio_positivo</c>,
/// <c>ck_combos_componentes_cantidad_positiva</c> y <c>ck_combos_componentes_alcance_exclusivo</c>
/// y los índices únicos de componente respaldan una parte de estas reglas en el esquema. La
/// regla de al menos dos unidades en total no tiene respaldo de esquema.
/// </summary>
public static class ReglaDeCombos
{
    public const int UnidadesMinimas = 2;

    /// <summary>Tope de cantidad por componente: mantiene la cantidad dentro de un entero y de la columna.</summary>
    public const decimal CantidadMaxima = 999_999m;

    /// <summary>Tope de precio: el máximo de <c>numeric(14,2)</c>.</summary>
    public const decimal PrecioMaximo = 999_999_999_999.99m;

    public static void Validar(decimal precio, IReadOnlyCollection<ComponenteDeCombo> componentes)
    {
        if (precio <= 0 || precio > PrecioMaximo)
        {
            throw new ErrorDominio(
                "combo_precio_invalido",
                "El precio del combo tiene que ser mayor a cero y no superar 999999999999,99.",
                400);
        }

        var articulos = new HashSet<int>();
        var familias = new HashSet<int>();
        decimal unidades = 0;

        foreach (var componente in componentes)
        {
            if ((componente.IdArticulo is null) == (componente.IdFamilia is null))
            {
                throw new ErrorDominio(
                    "combo_componente_invalido",
                    "Cada componente del combo tiene que ser exactamente un artículo o una familia.",
                    400);
            }

            if (componente.Cantidad <= 0
                || componente.Cantidad > CantidadMaxima
                || componente.Cantidad != decimal.Truncate(componente.Cantidad))
            {
                throw new ErrorDominio(
                    "combo_cantidad_invalida",
                    "La cantidad de cada componente del combo tiene que ser un entero entre 1 y 999999.",
                    400);
            }

            var esNuevo = componente.IdArticulo is { } idArticulo
                ? articulos.Add(idArticulo)
                : familias.Add(componente.IdFamilia!.Value);

            if (!esNuevo)
            {
                throw new ErrorDominio(
                    "combo_componente_duplicado",
                    "Un artículo o una familia puede figurar una sola vez en el combo.",
                    400);
            }

            unidades += componente.Cantidad;
        }

        if (unidades < UnidadesMinimas)
        {
            throw new ErrorDominio(
                "combo_unidades_insuficientes",
                "El combo tiene que sumar al menos dos unidades entre todos sus componentes.",
                400);
        }
    }
}
