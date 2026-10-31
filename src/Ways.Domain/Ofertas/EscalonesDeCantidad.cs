namespace Ways.Domain.Ofertas;

/// <summary>Un escalón de cantidad ya resuelto: desde <see cref="CantidadDesde"/> unidades (y
/// hasta el siguiente escalón, si hay), la línea resuelve a <see cref="Resolucion"/>. Solo el
/// resultado viaja, no la oferta que lo disparó: un escalón puede nacer de varias ofertas con el
/// mismo umbral (el umbral es el eje, no la oferta).</summary>
public readonly record struct EscalonResuelto(decimal CantidadDesde, PrecioConOfertas Resolucion);

/// <summary>La curva completa de una línea: la resolución a SU PROPIA cantidad
/// (<see cref="Base"/>) más los escalones que la siguen. Las dos cosas viajan juntas porque el
/// colapso de <see cref="EscalonesDeCantidad.Colapsar"/> necesita la base como primer punto de
/// comparación y el llamador la necesita igual para el resultado plano: devolverlas de a una
/// obligaba a resolverla dos veces (judgment-day ronda 1, acordado por ambos jueces), y pedirla
/// por parámetro habría abierto la puerta a pasar la base de OTRA línea sin que nada lo note.</summary>
public readonly record struct CurvaDeCantidad(PrecioConOfertas Base, IReadOnlyList<EscalonResuelto> Escalones);

/// <summary>Una celda de la grilla de familia: con <see cref="CantidadDeFamiliaDesde"/> unidades
/// de la familia en el carrito y <see cref="CantidadDesde"/> del propio artículo, la línea resuelve
/// a <see cref="Resolucion"/>. La grilla existe porque la curva de
/// <see cref="EscalonesDeCantidad.Construir"/> solo describe la diagonal "la familia crece junto
/// con el artículo", y no el caso en que la cantidad de familia viene de OTROS artículos.</summary>
public readonly record struct CeldaDeFamilia(
    decimal CantidadDeFamiliaDesde, decimal CantidadDesde, PrecioConOfertas Resolucion);

/// <summary>
/// Tabla de escalones de cantidad de UNA línea (stage-pos-venta-offline: ofertas por volumen en la
/// instantánea offline) — pura, sin base de datos, mismo criterio que
/// <see cref="ResolvedorDeOfertas"/>, del que es un consumidor: acá NO se reimplementa ni el
/// matching (<see cref="ResolvedorDeOfertas.Coincide"/>) ni la aritmética
/// (<see cref="ResolvedorDeOfertas.Resolver"/>).
///
/// <para>Existe porque el dispositivo offline no vuelve a evaluar ofertas nunca (decisión del
/// dueño, rechazada explícitamente la idea de reimplementar el motor en TypeScript): en vez de un
/// único precio congelado a cantidad unitaria — que dejaba afuera toda oferta con
/// <c>cantidad_minima &gt; 1</c> — el servidor le manda la CURVA de precio por cantidad, ya
/// resuelta por el motor real. El dispositivo solo elige el último escalón cuyo umbral es &lt;= la
/// cantidad del carrito.</para>
///
/// <para><see cref="DescubrirUmbrales"/> y <see cref="Colapsar"/> son públicos (y no detalles
/// privados de <see cref="Construir"/>) porque son las dos unidades que los tests tienen que poder
/// matar por separado: pasando por <see cref="Construir"/>, el colapso ABSORBE cualquier umbral
/// espurio que el descubrimiento deje entrar, así que un test de punta a punta no puede distinguir
/// un descubrimiento correcto de uno roto (mutation-proof-tests regla 3: rutear por debajo del
/// confound). El repo no usa <c>InternalsVisibleTo</c> en ningún lado (mismo criterio que
/// <c>InspectorDeUso.Renderizar</c>/<c>IEsperador</c>).</para>
/// </summary>
public static class EscalonesDeCantidad
{
    /// <summary>Tope de puntos de quiebre por "solo múltiplos" (<see cref="DescubrirPuntosDeQuiebre"/>) que puede agregar
    /// una curva, o una grilla de familia sumando sus dos ejes. Una "solo múltiplos" de descuento unitario <c>dS</c>
    /// contra una común de <c>dO</c> compite durante <c>dS / (dS − dO)</c> grupos y cada grupo agrega a lo sumo tres
    /// puntos (el múltiplo y un cruce con la cantidad siguiente): 250 contra 200 en grupos de 2 agrega 12, y con una común
    /// a un 5 % o más por debajo nunca pasa de 60. Más cerca, la competencia dura tantos grupos que la instantánea
    /// crecería decenas de escalones por artículo; pasado el tope rige <see cref="SinLaPrioridadEnCompetencia"/>.</summary>
    public const int MaximoDePuntosDeQuiebre = 64;

    /// <summary>La menor diferencia entre dos cantidades del punto de venta: tres decimales, como <c>cantidad</c>.</summary>
    public const decimal PasoDeCantidad = 0.001m;

    /// <summary>Punto de entrada único de producción: resuelve la línea a su propia cantidad,
    /// descubre los umbrales y los puntos de quiebre, resuelve en cada uno con el motor real y colapsa los que no cambian
    /// nada. <see cref="CurvaDeCantidad.Escalones"/> sale ascendente por
    /// <see cref="EscalonResuelto.CantidadDesde"/> y NUNCA incluye la cantidad de la propia línea:
    /// esa es <see cref="CurvaDeCantidad.Base"/> (para la instantánea, los campos planos de
    /// cantidad 1). La base se resuelve UNA sola vez y se devuelve junto con los escalones, así el
    /// llamador no la vuelve a calcular para el resultado plano.
    ///
    /// <para>Si los puntos de quiebre superan <see cref="MaximoDePuntosDeQuiebre"/> (o no terminan nunca), los escalones
    /// por encima de la cantidad de la línea se resuelven con <see cref="SinLaPrioridadEnCompetencia"/> desde la cantidad
    /// siguiente: el dispositivo cobra igual o más que el servidor, nunca menos, y la auditoría de diferencias lo
    /// registra.</para></summary>
    public static CurvaDeCantidad Construir(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas)
    {
        var resolucionDeLaLinea = ResolvedorDeOfertas.Resolver(linea, candidatas);

        var resolverCon = candidatas;
        var umbrales = new SortedSet<decimal>();
        if (DescubrirPuntosDeQuiebre(linea, candidatas) is { } puntos)
        {
            umbrales.UnionWith(DescubrirUmbrales(linea, candidatas));
            umbrales.UnionWith(puntos);
        }
        else
        {
            resolverCon = SinLaPrioridadEnCompetencia(candidatas, ParesDeLaCurva(linea, candidatas));
            umbrales.UnionWith(DescubrirUmbrales(linea, resolverCon));
            umbrales.Add(linea.Cantidad + PasoDeCantidad);
        }

        if (umbrales.Count == 0)
        {
            return new CurvaDeCantidad(resolucionDeLaLinea, []);
        }

        var candidatos = new List<EscalonResuelto>(umbrales.Count);
        foreach (var umbral in umbrales)
        {
            candidatos.Add(new EscalonResuelto(
                umbral, ResolvedorDeOfertas.Resolver(ConCantidad(linea, umbral), resolverCon)));
        }

        return new CurvaDeCantidad(resolucionDeLaLinea, Colapsar(resolucionDeLaLinea, candidatos));
    }

    /// <summary>
    /// Grilla (cantidad de familia × cantidad propia) de <paramref name="linea"/>, vacía si ninguna
    /// oferta de familia con <c>cantidad_minima</c> llega a la línea.
    ///
    /// <para>Los umbrales de familia son las <c>cantidad_minima</c> de las candidatas de alcance
    /// familia por encima de <see cref="LineaAResolver.CantidadDeFamilia"/> que coinciden con la
    /// línea en todos los demás ejes (se decide con <see cref="ResolvedorDeOfertas.Coincide"/> en el
    /// umbral, igual que <see cref="DescubrirUmbrales"/>). El eje propio es la cantidad de la línea
    /// más los umbrales de las demás candidatas con <c>cantidad_minima</c>. Cada celda es el
    /// producto cartesiano, resuelto por el motor real con ambas cantidades fijadas: así el
    /// dispositivo elige una celda en vez de evaluar ofertas.</para>
    ///
    /// <para>Con ofertas "solo múltiplos" en competencia cada eje suma sus puntos de quiebre: los de una "solo múltiplos"
    /// de familia dependen solo de la cantidad de la familia, los de una de artículo, grupo o categoría solo de la propia.
    /// Una "solo múltiplos" de familia contra una de otro alcance con la misma prioridad depende de las dos cantidades a la
    /// vez y no entra en una grilla: ahí, igual que pasado <see cref="MaximoDePuntosDeQuiebre"/>, las celdas se resuelven
    /// con <see cref="SinLaPrioridadEnCompetencia"/> y una fila más en la cantidad de familia siguiente a la de la línea
    /// hace que la grilla rija apenas la familia crece.</para>
    ///
    /// <para>Las celdas no se colapsan: son pocas (los umbrales de un artículo) y una celda
    /// repetida no puede dar un precio distinto del que el motor da.</para>
    /// </summary>
    public static IReadOnlyList<CeldaDeFamilia> ConstruirGrillaDeFamilia(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas)
    {
        var umbralesDeFamilia = new SortedSet<decimal>();
        var umbralesPropios = new SortedSet<decimal> { linea.Cantidad };

        foreach (var candidata in candidatas)
        {
            if (candidata.CantidadMinima is not { } umbral)
            {
                continue;
            }

            var esDeFamilia = candidata.Alcance.IdFamilia is not null;
            if (umbral <= (esDeFamilia ? linea.CantidadDeFamilia : linea.Cantidad))
            {
                continue;
            }

            if (ResolvedorDeOfertas.Coincide(linea with { Cantidad = umbral, CantidadDeFamilia = umbral }, candidata))
            {
                (esDeFamilia ? umbralesDeFamilia : umbralesPropios).Add(umbral);
            }
        }

        var resolverCon = candidatas;
        var pares = Pares(Competidoras(linea, candidatas, desplazamientoDeFamilia: 0m));
        if (umbralesDeFamilia.Count == 0 && !pares.Any(p => EsDeFamilia(p.Estricta) || EsSoloMultiplosDeFamilia(p.Otra)))
        {
            return [];
        }

        var deFamilia = pares.Where(p => EsDeFamilia(p.Estricta) && !EsSoloMultiplosPropia(p.Otra)).ToList();
        var propios = pares.Where(p => !EsDeFamilia(p.Estricta) && !EsSoloMultiplosDeFamilia(p.Otra)).ToList();
        var separables = deFamilia.Count + propios.Count == pares.Count;

        var puntosDeFamilia = separables ? PuntosDelEje(deFamilia, linea.CantidadDeFamilia) : null;
        var puntosPropios = separables ? PuntosDelEje(propios, linea.Cantidad) : null;
        if (puntosDeFamilia is not null && puntosPropios is not null &&
            puntosDeFamilia.Count + puntosPropios.Count <= MaximoDePuntosDeQuiebre)
        {
            umbralesDeFamilia.UnionWith(puntosDeFamilia);
            umbralesPropios.UnionWith(puntosPropios);
        }
        else
        {
            resolverCon = SinLaPrioridadEnCompetencia(candidatas, pares);
            umbralesDeFamilia.Add(linea.CantidadDeFamilia + PasoDeCantidad);
        }

        var celdas = new List<CeldaDeFamilia>(umbralesDeFamilia.Count * umbralesPropios.Count);
        foreach (var deFamiliaDesde in umbralesDeFamilia)
        {
            foreach (var propio in umbralesPropios)
            {
                celdas.Add(new CeldaDeFamilia(
                    deFamiliaDesde, propio,
                    ResolvedorDeOfertas.Resolver(
                        linea with { Cantidad = propio, CantidadDeFamilia = deFamiliaDesde }, resolverCon)));
            }
        }

        return celdas;
    }

    /// <summary>Mueve la cantidad de la línea y el agregado de su familia en el mismo delta: la
    /// curva es de UN artículo, así que sondear un umbral es "este artículo llega a esa cantidad",
    /// y el resto de la familia queda donde estaba.</summary>
    private static LineaAResolver ConCantidad(in LineaAResolver linea, decimal cantidad) =>
        linea with { Cantidad = cantidad, CantidadDeFamilia = linea.CantidadDeFamilia + (cantidad - linea.Cantidad) };

    /// <summary>
    /// Las cantidades en las que el resultado de <paramref name="linea"/> PUEDE cambiar, distintas
    /// y ascendentes.
    ///
    /// <para>Umbral candidato = <c>cantidad_minima</c> ESTRICTAMENTE mayor a
    /// <see cref="LineaAResolver.Cantidad"/>. Un umbral menor o igual ya está contemplado en la
    /// resolución de la propia línea, y emitirlo sería peor que redundante: el dispositivo elige el
    /// ÚLTIMO escalón cuyo umbral entra en la cantidad, así que un escalón por debajo de la
    /// cantidad ya resuelta le haría cobrar el precio de MENOS unidades. Para la instantánea (que
    /// resuelve a cantidad 1) esto es exactamente "todo umbral &gt; 1".</para>
    ///
    /// <para>Que un umbral <c>T</c> califique se decide con <see cref="ResolvedorDeOfertas.Coincide"/>
    /// evaluado EN <c>T</c>, nunca con una copia del matching: a cantidad <c>T</c> el eje de
    /// cantidad pasa por definición (<c>T &gt;= T</c>), así que un <c>true</c> prueba que TODOS los
    /// otros ejes (alcance, lista, día de semana, ventana de fecha, ventana de hora) matchean en el
    /// momento de esta línea. Por eso una oferta por volumen fuera de su ventana, o de otro
    /// artículo, no aporta umbral. Ese filtro NO es la red de correctitud del resultado — de eso se
    /// ocupa <see cref="Colapsar"/>, que descartaría igual un umbral espurio porque no cambia nada
    /// — sino el que evita resolver, artículo por artículo, los umbrales de TODO el tenant: el lote
    /// de la instantánea trae las candidatas de todo el catálogo juntas.</para>
    /// </summary>
    public static IReadOnlyList<decimal> DescubrirUmbrales(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas)
    {
        var umbrales = new HashSet<decimal>();

        foreach (var candidata in candidatas)
        {
            if (candidata.CantidadMinima is not { } umbral || umbral <= linea.Cantidad)
            {
                continue;
            }

            if (ResolvedorDeOfertas.Coincide(ConCantidad(linea, umbral), candidata))
            {
                umbrales.Add(umbral);
            }
        }

        return umbrales.OrderBy(u => u).ToList();
    }

    /// <summary>
    /// Las cantidades por encima de <see cref="LineaAResolver.Cantidad"/> en las que la base de <paramref name="linea"/>
    /// puede cambiar por la competencia entre una oferta "solo múltiplos" y otra no acumulable de la misma prioridad
    /// (<see cref="ResolvedorDeOfertas"/> las ordena por descuento de línea), ascendentes; <c>null</c> si son más de
    /// <see cref="MaximoDePuntosDeQuiebre"/> o no terminan nunca. Complementa a <see cref="DescubrirUmbrales"/>, que da las
    /// de <c>cantidad_minima</c>.
    ///
    /// <para>Por unidad de línea, una común vale su descuento <c>d</c> y una "solo múltiplos" de N vale
    /// <c>d × N × k / Q</c> entre <c>Q = kN</c> y <c>(k + 1)N</c>, con Q la cantidad de su umbral. Los quiebres son los
    /// múltiplos <c>kN</c> (la clave salta) y los cruces de dos claves dentro de un tramo, que se resuelven en forma
    /// cerrada. Un cruce que cae entre dos cantidades del punto de venta se lleva a la siguiente
    /// (<see cref="PasoDeCantidad"/>); uno que cae justo en una empata ahí y se desempata por descuento unitario e id, así
    /// que se agregan esa y la siguiente. La competencia termina: si la "solo múltiplos" tiene más descuento unitario
    /// <c>dS</c> que la otra <c>dO</c>, gana siempre a partir de <c>Q &gt; dS × N / (dS − dO)</c>; con menos, no gana
    /// nunca; con el mismo, contra una común gana justo en los múltiplos si su id es menor (infinitos quiebres,
    /// <c>null</c>).</para>
    ///
    /// <para>Una "solo múltiplos" de familia se mide con la cantidad de la familia, que en la curva se mueve junto con la
    /// de la línea (<see cref="ConCantidad"/>).</para>
    /// </summary>
    public static IReadOnlyList<decimal>? DescubrirPuntosDeQuiebre(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas) =>
        PuntosDelEje(ParesDeLaCurva(linea, candidatas), linea.Cantidad);

    /// <summary>
    /// <paramref name="candidatas"/> sin las no acumulables de prioridad menor o igual a la mayor de
    /// <paramref name="pares"/>: lo que el dispositivo cobra cuando los puntos de quiebre no entran en
    /// <see cref="MaximoDePuntosDeQuiebre"/>. Nunca cobra menos que el servidor: si la base del servidor tiene mayor
    /// prioridad, es la misma; si no, el dispositivo se queda sin base y solo suma las acumulables. Sacar solo la "solo
    /// múltiplos" no alcanzaría: subiría una base de menor prioridad que puede dar más descuento.
    /// </summary>
    private static IReadOnlyList<OfertaCandidata> SinLaPrioridadEnCompetencia(
        IReadOnlyList<OfertaCandidata> candidatas, IReadOnlyList<(Competidora Estricta, Competidora Otra)> pares)
    {
        if (pares.Count == 0)
        {
            return candidatas;
        }

        var prioridad = pares.Max(p => p.Estricta.Candidata.Prioridad);
        return candidatas.Where(c => c.Acumulable || c.Prioridad > prioridad).ToList();
    }

    /// <summary>Una no acumulable que puede ser base de la línea, con lo que su clave de línea necesita sobre un eje de
    /// cantidad <c>u</c>: <see cref="Descuento"/> unitario, <see cref="Multiplo"/> (solo una "solo múltiplos" con
    /// descuento) y <see cref="Desplazamiento"/>, la diferencia entre la cantidad de su umbral y <c>u</c>.</summary>
    private readonly record struct Competidora(
        OfertaCandidata Candidata, decimal Descuento, decimal? Multiplo, decimal Desplazamiento);

    private static List<(Competidora Estricta, Competidora Otra)> ParesDeLaCurva(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas) =>
        Pares(Competidoras(linea, candidatas, linea.CantidadDeFamilia - linea.Cantidad));

    private static List<Competidora> Competidoras(
        in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas, decimal desplazamientoDeFamilia)
    {
        var sinTopeDeCantidad = linea with { Cantidad = decimal.MaxValue, CantidadDeFamilia = decimal.MaxValue };
        var competidoras = new List<Competidora>();

        foreach (var candidata in candidatas)
        {
            if (candidata.Acumulable || !ResolvedorDeOfertas.Coincide(sinTopeDeCantidad, candidata))
            {
                continue;
            }

            var descuento = ResolvedorDeOfertas.DescuentoUnitario(candidata, linea.PrecioOriginal);
            var multiplo = descuento > 0m ? ResolvedorDeOfertas.MultiploDe(candidata) : null;
            competidoras.Add(new Competidora(
                candidata, descuento, multiplo,
                candidata.Alcance.IdFamilia is not null ? desplazamientoDeFamilia : 0m));
        }

        return competidoras;
    }

    private static List<(Competidora Estricta, Competidora Otra)> Pares(List<Competidora> competidoras)
    {
        var pares = new List<(Competidora, Competidora)>();
        foreach (var estricta in competidoras.Where(c => c.Multiplo is not null))
        {
            foreach (var otra in competidoras)
            {
                if (otra.Candidata.Id != estricta.Candidata.Id &&
                    otra.Candidata.Prioridad == estricta.Candidata.Prioridad)
                {
                    pares.Add((estricta, otra));
                }
            }
        }

        return pares;
    }

    private static bool EsDeFamilia(Competidora competidora) => competidora.Candidata.Alcance.IdFamilia is not null;

    private static bool EsSoloMultiplosDeFamilia(Competidora competidora) =>
        competidora.Multiplo is not null && EsDeFamilia(competidora);

    private static bool EsSoloMultiplosPropia(Competidora competidora) =>
        competidora.Multiplo is not null && !EsDeFamilia(competidora);

    /// <summary>Los puntos de quiebre de <paramref name="pares"/> sobre un eje, mayores a <paramref name="desde"/>;
    /// <c>null</c> si no terminan o pasan de <see cref="MaximoDePuntosDeQuiebre"/>.</summary>
    private static List<decimal>? PuntosDelEje(
        IReadOnlyList<(Competidora Estricta, Competidora Otra)> pares, decimal desde)
    {
        decimal? hasta = null;
        foreach (var (estricta, otra) in pares)
        {
            if (!Termina(estricta, otra, out var cota))
            {
                return null;
            }

            if (cota is { } valor && (hasta is null || valor > hasta))
            {
                hasta = valor;
            }
        }

        if (hasta is not { } fin || fin <= desde)
        {
            return [];
        }

        var particion = new SortedSet<decimal> { desde };
        foreach (var estricta in pares.Select(p => p.Estricta).DistinctBy(e => e.Candidata.Id))
        {
            var multiplo = estricta.Multiplo!.Value;
            var primero = Math.Floor((desde + estricta.Desplazamiento) / multiplo) + 1m;
            var ultimo = Math.Floor((fin + estricta.Desplazamiento) / multiplo);
            if (ultimo - primero + 1m > MaximoDePuntosDeQuiebre)
            {
                return null;
            }

            for (var k = primero; k <= ultimo; k++)
            {
                particion.Add((k * multiplo) - estricta.Desplazamiento);
            }
        }

        var puntos = new SortedSet<decimal>(particion.Where(p => p > desde));
        if (puntos.Count > MaximoDePuntosDeQuiebre)
        {
            return null;
        }

        var tramos = particion.ToList();
        tramos.Add(fin);

        for (var i = 0; i < tramos.Count - 1; i++)
        {
            foreach (var (estricta, otra) in pares)
            {
                if (Cruce(estricta, otra, tramos[i]) is { } cruce && cruce >= tramos[i] && cruce <= tramos[i + 1])
                {
                    var siguiente = Math.Ceiling(cruce / PasoDeCantidad) * PasoDeCantidad;
                    puntos.Add(siguiente);
                    if (siguiente == cruce)
                    {
                        puntos.Add(siguiente + PasoDeCantidad);
                    }
                }
            }
        }

        var resultado = puntos.Where(p => p > desde).ToList();
        return resultado.Count > MaximoDePuntosDeQuiebre ? null : resultado;
    }

    /// <summary><c>false</c> si el orden entre <paramref name="estricta"/> y <paramref name="otra"/> cambia infinitas
    /// veces; si no, <paramref name="cota"/> es la cantidad del eje a partir de la que ya no cambia (<c>null</c>: la
    /// clave de línea no lo cambia nunca).</summary>
    private static bool Termina(Competidora estricta, Competidora otra, out decimal? cota)
    {
        cota = null;
        var multiplo = estricta.Multiplo!.Value;

        if (estricta.Descuento > otra.Descuento)
        {
            cota = (estricta.Descuento * multiplo / (estricta.Descuento - otra.Descuento)) - estricta.Desplazamiento;
            return true;
        }

        if (estricta.Descuento < otra.Descuento)
        {
            return true;
        }

        return otra.Multiplo is null
            ? estricta.Candidata.Id > otra.Candidata.Id
            : otra.Multiplo == multiplo && otra.Desplazamiento == estricta.Desplazamiento;
    }

    /// <summary>La cantidad del eje en la que las claves de <paramref name="estricta"/> y <paramref name="otra"/> se
    /// igualan dentro del tramo que empieza en <paramref name="desde"/> (sin múltiplos adentro), si la hay.</summary>
    private static decimal? Cruce(Competidora estricta, Competidora otra, decimal desde)
    {
        var deLaEstricta = Coeficiente(estricta, desde);
        if (deLaEstricta == 0m)
        {
            return null;
        }

        if (otra.Multiplo is null)
        {
            return otra.Descuento == 0m ? null : (deLaEstricta / otra.Descuento) - estricta.Desplazamiento;
        }

        var deLaOtra = Coeficiente(otra, desde);
        if (deLaOtra == 0m || deLaOtra == deLaEstricta)
        {
            return null;
        }

        return ((deLaOtra * estricta.Desplazamiento) - (deLaEstricta * otra.Desplazamiento)) /
            (deLaEstricta - deLaOtra);
    }

    /// <summary>El numerador de la clave de una "solo múltiplos" en el tramo de <paramref name="cantidad"/>:
    /// <c>d × N × floor((u + desplazamiento) / N)</c>.</summary>
    private static decimal Coeficiente(Competidora competidora, decimal cantidad)
    {
        var multiplo = competidora.Multiplo!.Value;
        return competidora.Descuento * multiplo * Math.Floor((cantidad + competidora.Desplazamiento) / multiplo);
    }

    /// <summary>
    /// Descarta los escalones que no cambian nada: un umbral cuyo resultado es igual al del escalón
    /// VIGENTE (el anterior CONSERVADO, o <paramref name="resolucionDeLaLinea"/> para el primero) es
    /// puro ruido de payload. Llegando desde <see cref="Construir"/> los casos reales son dos: la
    /// oferta del umbral PIERDE contra la base vigente (la directa que ya aplicaba, o un escalón
    /// anterior de mayor prioridad), o un punto de quiebre de <see cref="DescubrirPuntosDeQuiebre"/>
    /// en el que la ganadora no cambia (un cruce empatado que sigue ganando la misma). El otro caso clásico — la oferta del umbral no matchea por
    /// fecha/hora/alcance — no llega hasta acá, lo filtra antes
    /// <see cref="DescubrirUmbrales"/>; este colapso lo descartaría igual, y eso es justamente lo
    /// que lo vuelve la red que hace que un umbral espurio nunca altere la curva.
    ///
    /// <para><paramref name="candidatos"/> tiene que venir ascendente por
    /// <see cref="EscalonResuelto.CantidadDesde"/> (lo garantiza
    /// <see cref="DescubrirUmbrales"/>): el colapso es contra el anterior conservado, que es
    /// justo el escalón que el dispositivo estaría usando al llegar a esta cantidad.</para>
    ///
    /// <para>La comparación es <see cref="PrecioConOfertas.DescuentoUnitario"/> + los ids aplicados
    /// EN ORDEN. <see cref="PrecioConOfertas.PrecioOriginal"/> es constante entre cantidades (la
    /// cantidad no entra en la resolución del precio de lista) y
    /// <see cref="PrecioConOfertas.PrecioFinal"/> es <c>original - descuento</c>, así que ninguno de
    /// los dos aporta información que el descuento no tenga ya. Los ids sí: dos escalones pueden
    /// llegar al mismo descuento con ofertas distintas, y cuáles aplicaron es dato visible del
    /// ticket.</para>
    /// </summary>
    public static IReadOnlyList<EscalonResuelto> Colapsar(
        in PrecioConOfertas resolucionDeLaLinea, IReadOnlyList<EscalonResuelto> candidatos)
    {
        var escalones = new List<EscalonResuelto>(candidatos.Count);
        var vigente = resolucionDeLaLinea;

        foreach (var candidato in candidatos)
        {
            if (MismoResultado(vigente, candidato.Resolucion))
            {
                continue;
            }

            escalones.Add(candidato);
            vigente = candidato.Resolucion;
        }

        return escalones;
    }

    private static bool MismoResultado(in PrecioConOfertas vigente, in PrecioConOfertas candidato) =>
        vigente.DescuentoUnitario == candidato.DescuentoUnitario &&
        vigente.Aplicadas.Select(a => a.IdOferta).SequenceEqual(candidato.Aplicadas.Select(a => a.IdOferta));
}
