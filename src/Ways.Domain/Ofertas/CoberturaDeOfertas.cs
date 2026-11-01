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
/// grupo o categoría cuenta los grupos sobre las unidades de su propia línea. Una de familia los cuenta sobre la suma
/// <c>U</c> de las unidades de todas las líneas del documento que la llevan y reparte su descuento, <c>C × d</c> con
/// <c>C</c> las unidades de los grupos completos y <c>d</c> su descuento unitario en la primera de esas líneas, en
/// proporción a las unidades de cada línea: <c>d × C / U</c> por unidad, la clave con la que la oferta compitió por la
/// base (<see cref="ResolvedorDeOfertas.ClaveDeLinea"/>) cuando todas las líneas de la familia la llevan y ningún combo
/// consumió unidades. Cada línea lleva su parte truncada al peso y la última el resto, así el neto de las líneas (que se
/// redondea al peso) no corre el total del documento; si el resto cubriera más unidades de las que tiene la última,
/// sigue hacia las anteriores.</para>
///
/// <para>Cada unidad suma los descuentos unitarios de las ofertas que la cubren, con el mismo tope del motor
/// (<see cref="PrecioConOfertas.DescuentoUnitario"/>: nunca más que el precio original). Las unidades cubiertas por una
/// oferta "solo múltiplos" se cuentan como las primeras de la línea, así que las coberturas quedan anidadas; la de una
/// de familia es su parte dividida por <c>d</c>, una fracción, y el descuento de una línea que la lleva sale redondeado a
/// seis decimales (lejos de cero). Sin ninguna oferta "solo múltiplos" el resultado es exactamente
/// <c>Unidades × DescuentoUnitario</c>.</para>
/// </summary>
public static class CoberturaDeOfertas
{
    private const int DecimalesConFamilia = 6;

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
        var partesDeFamilia = PartesDeFamilia(lineas);
        var descuentos = new List<decimal>(lineas.Count);

        for (var l = 0; l < lineas.Count; l++)
        {
            var linea = lineas[l];
            var unidades = Math.Max(linea.Unidades, 0m);
            var coberturas = new Cobertura[linea.Aplicadas.Count];

            for (var i = 0; i < coberturas.Length; i++)
            {
                var aplicada = linea.Aplicadas[i];
                coberturas[i] = aplicada.MultiploDe is not { } multiplo ? new Cobertura(unidades, 1m)
                    : aplicada.PorFamilia ? partesDeFamilia[(l, aplicada.IdOferta)]
                    : new Cobertura(EnGruposCompletos(unidades, multiplo), 1m);
            }

            descuentos.Add(DescuentoEscalonado(linea, coberturas));
        }

        return descuentos;
    }

    /// <summary>Unidades cubiertas como fracción <c>Numerador / Denominador</c>: el denominador es 1 salvo en la parte
    /// de una oferta de familia, que es su importe dividido por el descuento unitario de la oferta.</summary>
    private readonly record struct Cobertura(decimal Numerador, decimal Denominador);

    /// <summary>La cobertura de cada oferta "solo múltiplos" de familia en cada línea que la lleva, por (índice de
    /// línea, id de oferta).</summary>
    private static Dictionary<(int Linea, int IdOferta), Cobertura> PartesDeFamilia(IReadOnlyList<LineaConOfertas> lineas)
    {
        var porOferta = new Dictionary<int, (decimal Multiplo, decimal Unitario, List<(int Linea, decimal Unidades)> Lineas)>();

        for (var l = 0; l < lineas.Count; l++)
        {
            foreach (var aplicada in lineas[l].Aplicadas)
            {
                if (aplicada.MultiploDe is not { } multiplo || !aplicada.PorFamilia)
                {
                    continue;
                }

                if (!porOferta.TryGetValue(aplicada.IdOferta, out var oferta))
                {
                    oferta = (multiplo, aplicada.DescuentoUnitario, []);
                    porOferta[aplicada.IdOferta] = oferta;
                }

                oferta.Lineas.Add((l, Math.Max(lineas[l].Unidades, 0m)));
            }
        }

        var partes = new Dictionary<(int Linea, int IdOferta), Cobertura>();
        foreach (var (idOferta, (multiplo, unitario, deLaOferta)) in porOferta)
        {
            var importes = Repartir(deLaOferta.Select(x => x.Unidades).ToArray(), multiplo, unitario);
            for (var k = 0; k < importes.Length; k++)
            {
                partes[(deLaOferta[k].Linea, idOferta)] =
                    unitario > 0m ? new Cobertura(importes[k], unitario) : new Cobertura(0m, 1m);
            }
        }

        return partes;
    }

    /// <summary>El descuento de los grupos completos de una oferta de familia repartido por unidades: cada línea su parte
    /// truncada al peso, el resto a la última (y hacia atrás, sin pasar de lo que cubren sus unidades).</summary>
    private static decimal[] Repartir(decimal[] unidades, decimal multiplo, decimal unitario)
    {
        var total = unidades.Sum();
        var importe = unitario > 0m && total > 0m ? EnGruposCompletos(total, multiplo) * unitario : 0m;
        var importes = unidades.Select(u => importe == 0m ? 0m : Math.Floor(importe * u / total)).ToArray();

        var resto = importe - importes.Sum();
        for (var k = importes.Length - 1; k >= 0 && resto > 0m; k--)
        {
            var agregado = Math.Min(resto, (unidades[k] * unitario) - importes[k]);
            importes[k] += agregado;
            resto -= agregado;
        }

        return importes;
    }

    private static decimal EnGruposCompletos(decimal unidades, decimal multiplo) =>
        multiplo <= 0m ? unidades : multiplo * Math.Floor(unidades / multiplo);

    /// <summary>Recorre las coberturas de menor a mayor: cada tramo de unidades lleva la suma de los descuentos de las
    /// ofertas que lo cubren, con el tope del precio original por unidad. Las coberturas se llevan al producto de sus
    /// denominadores para ordenarlas y restarlas sin dividir.</summary>
    private static decimal DescuentoEscalonado(in LineaConOfertas linea, Cobertura[] coberturas)
    {
        var denominador = 1m;
        var niveles = new decimal[coberturas.Length];
        for (var i = 0; i < coberturas.Length; i++)
        {
            denominador *= coberturas[i].Denominador;
            niveles[i] = coberturas[i].Numerador;
            for (var j = 0; j < coberturas.Length; j++)
            {
                if (j != i)
                {
                    niveles[i] *= coberturas[j].Denominador;
                }
            }
        }

        var descuento = 0m;
        var anterior = 0m;

        foreach (var nivel in niveles.Where(c => c > 0m).Distinct().Order())
        {
            var unitario = 0m;
            for (var i = 0; i < niveles.Length; i++)
            {
                if (niveles[i] >= nivel)
                {
                    unitario += linea.Aplicadas[i].DescuentoUnitario;
                }
            }

            descuento += (nivel - anterior) * Math.Clamp(unitario, 0m, linea.PrecioOriginal);
            anterior = nivel;
        }

        return linea.Aplicadas.Any(a => a.MultiploDe is not null && a.PorFamilia)
            ? Math.Round(descuento / denominador, DecimalesConFamilia, MidpointRounding.AwayFromZero)
            : descuento;
    }
}
