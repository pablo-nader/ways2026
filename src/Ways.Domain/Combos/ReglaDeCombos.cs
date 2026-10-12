using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Domain.Combos;

/// <summary>
/// Reglas de negocio puras de un combo (sin base de datos, mismo criterio que
/// <see cref="Ofertas.ReglaDeOfertas"/>). Todo camino de escritura de combos debe pasar por
/// <see cref="Validar"/> antes de tocar filas (lo hace <c>ServicioDeCombos</c>); las CHECKs <c>ck_combos_precio_positivo</c>,
/// <c>ck_combos_componentes_cantidad_positiva</c> y <c>ck_combos_componentes_alcance_exclusivo</c>
/// y los índices únicos de componente respaldan una parte de estas reglas en el esquema. El
/// mínimo de un combo (<see cref="AlcanzaElMinimo"/>) no tiene respaldo de esquema. La cantidad de un componente
/// admite hasta tres decimales porque el componente puede ser un artículo que se vende por peso; que la cantidad sea
/// la que admite la unidad de venta de ese artículo (entera si se vende por unidad) no se decide acá, sino donde se
/// conoce el artículo (<c>ServicioDeCombos</c>).
/// </summary>
public static class ReglaDeCombos
{
    /// <summary>Cantidad mínima del componente de un combo que tiene uno solo.</summary>
    public const decimal CantidadMinimaDeUnComponenteUnico = 2m;

    /// <summary>Tope de cantidad por componente, dentro de lo que admite la columna <c>numeric(12,3)</c>.</summary>
    public const decimal CantidadMaxima = 999_999m;

    /// <summary>Tope de precio: el máximo de <c>numeric(14,2)</c>.</summary>
    public const decimal PrecioMaximo = 999_999_999_999.99m;

    /// <summary>Tope de componentes de un combo: cada uno cuesta un <c>FOR KEY SHARE</c> bajo el lock por tenant.</summary>
    public const int ComponentesMaximos = 50;

    /// <summary>Tope del código que se tipea tras la <c>C</c> en el POS: seis dígitos.</summary>
    public const int CodigoMaximo = 999_999;

    /// <summary>El código de un combo es un entero entre 1 y <see cref="CodigoMaximo"/>. La unicidad entre los
    /// combos vivos del tenant no se decide acá: la comprueba el servicio y la respalda <c>ux_combos_codigo</c>.</summary>
    public static void ValidarCodigo(int codigo)
    {
        if (codigo is < 1 or > CodigoMaximo)
        {
            throw new ErrorDominio(
                "combo_codigo_invalido",
                "El código del combo tiene que ser un entero entre 1 y 999999.",
                400);
        }
    }

    public static void Validar(decimal precio, IReadOnlyCollection<ComponenteDeCombo> componentes)
    {
        if (precio <= 0 || precio > PrecioMaximo || precio != decimal.Round(precio, 2))
        {
            throw new ErrorDominio(
                "combo_precio_invalido",
                "El precio del combo tiene que ser mayor a cero, tener hasta dos decimales y no superar 999999999999,99.",
                400);
        }

        if (componentes.Count > ComponentesMaximos)
        {
            throw new ErrorDominio(
                "combo_componentes_excedidos",
                $"Un combo admite hasta {ComponentesMaximos} componentes.",
                400);
        }

        var articulos = new HashSet<int>();
        var familias = new HashSet<int>();
        foreach (var componente in componentes)
        {
            if ((componente.IdArticulo is null) == (componente.IdFamilia is null))
            {
                throw new ErrorDominio(
                    "combo_componente_invalido",
                    "Cada componente del combo tiene que ser exactamente un artículo o una familia.",
                    400);
            }

            if (!EsCantidadDeComponenteValida(componente.Cantidad))
            {
                throw new ErrorDominio(
                    "combo_cantidad_invalida",
                    "La cantidad de cada componente del combo tiene que ser mayor a cero, tener hasta tres decimales " +
                    "y no superar 999999.",
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
        }

        if (!AlcanzaElMinimo(componentes))
        {
            throw new ErrorDominio(
                "combo_unidades_insuficientes",
                "El combo tiene que tener al menos dos componentes, o uno solo con cantidad dos o más.",
                400);
        }
    }

    /// <summary>Mayor a cero, hasta <see cref="CantidadMaxima"/> y con a lo sumo los decimales que admite una cantidad
    /// por peso (<see cref="ReglaDeCantidadDeVenta.DecimalesMaximosDePeso"/>).</summary>
    public static bool EsCantidadDeComponenteValida(decimal cantidad) =>
        cantidad > 0
        && cantidad <= CantidadMaxima
        && ReglaDeCantidadDeVenta.EsCantidadAdmitida(UnidadVenta.Peso, cantidad);

    /// <summary>Un combo es un conjunto: tiene al menos dos componentes, o uno solo con cantidad
    /// <see cref="CantidadMinimaDeUnComponenteUnico"/> o más. No mira la suma de las cantidades: dos componentes de
    /// 0,200 kg son un combo.</summary>
    public static bool AlcanzaElMinimo(IReadOnlyCollection<ComponenteDeCombo> componentes) =>
        componentes.Count >= 2
        || (componentes.Count == 1 && componentes.First().Cantidad >= CantidadMinimaDeUnComponenteUnico);
}
