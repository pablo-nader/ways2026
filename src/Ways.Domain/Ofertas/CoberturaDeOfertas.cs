namespace Ways.Domain.Ofertas;

/// <summary>Una línea de un documento ya resuelta por <see cref="ResolvedorDeOfertas"/>. <see cref="Unidades"/> son las
/// que cobra la oferta, sin signo: la cantidad de la línea menos las que consumió un combo.</summary>
public readonly record struct LineaConOfertas(
    decimal Unidades, decimal PrecioOriginal, IReadOnlyList<OfertaAplicada> Aplicadas);

/// <summary>
/// Descuento de oferta de cada línea de un documento como IMPORTE DE LÍNEA. Hace falta por las ofertas "solo múltiplos"
/// (<see cref="OfertaAplicada.MultiploDe"/>): su descuento unitario alcanza solo a <c>N × floor(q / N)</c> unidades y el
/// resto paga el precio original, así que el descuento de la línea deja de ser <c>q × DescuentoUnitario</c>.
///
/// <para>Una oferta común cubre todas las <see cref="LineaConOfertas.Unidades"/>. Una "solo múltiplos" de artículo,
/// grupo o categoría cuenta los grupos sobre las unidades de su propia línea. Una de familia los cuenta sobre la suma de
/// las unidades de todas las líneas del documento que la llevan, y reparte las unidades cubiertas en el orden de las
/// líneas: las que quedan fuera de un grupo completo caen en las últimas.</para>
///
/// <para>Cada unidad suma los descuentos unitarios de las ofertas que la cubren, con el mismo tope del motor
/// (<see cref="PrecioConOfertas.DescuentoUnitario"/>: nunca más que el precio original). Las unidades cubiertas por una
/// oferta "solo múltiplos" se cuentan como las primeras de la línea, así que las coberturas quedan anidadas. Sin ninguna
/// oferta "solo múltiplos" el resultado es exactamente <c>Unidades × DescuentoUnitario</c>.</para>
/// </summary>
public static class CoberturaDeOfertas
{
    /// <summary><c>true</c> si alguna oferta aplicada es "solo múltiplos": solo entonces el descuento de la línea
    /// necesita <see cref="DescuentosDeLinea"/>.</summary>
    public static bool TieneSoloMultiplos(IReadOnlyList<OfertaAplicada> aplicadas) =>
        aplicadas.Any(a => a.MultiploDe is not null);

    /// <summary>El descuento de oferta de una línea sola: una oferta de familia cuenta sus grupos sobre esta línea.</summary>
    public static decimal DescuentoDeLinea(in PrecioConOfertas resuelto, decimal unidades) =>
        DescuentosDeLinea([new LineaConOfertas(unidades, resuelto.PrecioOriginal, resuelto.Aplicadas)])[0];

    /// <summary>El descuento de oferta de cada línea de <paramref name="lineas"/>, en el mismo orden, sin signo.</summary>
    public static IReadOnlyList<decimal> DescuentosDeLinea(IReadOnlyList<LineaConOfertas> lineas)
    {
        var cubiertasRestantesPorFamilia = UnidadesCubiertasPorOfertaDeFamilia(lineas);
        var descuentos = new List<decimal>(lineas.Count);

        foreach (var linea in lineas)
        {
            var unidades = Math.Max(linea.Unidades, 0m);
            var cubiertas = new decimal[linea.Aplicadas.Count];

            for (var i = 0; i < cubiertas.Length; i++)
            {
                var aplicada = linea.Aplicadas[i];
                if (aplicada.MultiploDe is not { } multiplo)
                {
                    cubiertas[i] = unidades;
                }
                else if (aplicada.PorFamilia)
                {
                    var restantes = cubiertasRestantesPorFamilia[aplicada.IdOferta];
                    cubiertas[i] = Math.Min(restantes, unidades);
                    cubiertasRestantesPorFamilia[aplicada.IdOferta] = restantes - cubiertas[i];
                }
                else
                {
                    cubiertas[i] = EnGruposCompletos(unidades, multiplo);
                }
            }

            descuentos.Add(DescuentoEscalonado(linea, cubiertas));
        }

        return descuentos;
    }

    /// <summary>Unidades cubiertas de cada oferta "solo múltiplos" de familia: los grupos completos que entran en la
    /// suma de las unidades de las líneas que la llevan.</summary>
    private static Dictionary<int, decimal> UnidadesCubiertasPorOfertaDeFamilia(IReadOnlyList<LineaConOfertas> lineas)
    {
        var unidadesPorOferta = new Dictionary<int, (decimal Multiplo, decimal Unidades)>();

        foreach (var linea in lineas)
        {
            foreach (var aplicada in linea.Aplicadas)
            {
                if (aplicada.MultiploDe is { } multiplo && aplicada.PorFamilia)
                {
                    var acumulado = unidadesPorOferta.GetValueOrDefault(aplicada.IdOferta, (Multiplo: multiplo, Unidades: 0m));
                    unidadesPorOferta[aplicada.IdOferta] = (multiplo, acumulado.Unidades + Math.Max(linea.Unidades, 0m));
                }
            }
        }

        return unidadesPorOferta.ToDictionary(
            par => par.Key, par => EnGruposCompletos(par.Value.Unidades, par.Value.Multiplo));
    }

    private static decimal EnGruposCompletos(decimal unidades, decimal multiplo) =>
        multiplo <= 0m ? unidades : multiplo * Math.Floor(unidades / multiplo);

    /// <summary>Recorre las coberturas de menor a mayor: cada tramo de unidades lleva la suma de los descuentos de las
    /// ofertas que lo cubren, con el tope del precio original por unidad.</summary>
    private static decimal DescuentoEscalonado(in LineaConOfertas linea, decimal[] cubiertas)
    {
        var descuento = 0m;
        var anterior = 0m;

        foreach (var nivel in cubiertas.Where(c => c > 0m).Distinct().Order())
        {
            var unitario = 0m;
            for (var i = 0; i < cubiertas.Length; i++)
            {
                if (cubiertas[i] >= nivel)
                {
                    unitario += linea.Aplicadas[i].DescuentoUnitario;
                }
            }

            descuento += (nivel - anterior) * Math.Clamp(unitario, 0m, linea.PrecioOriginal);
            anterior = nivel;
        }

        return descuento;
    }
}
