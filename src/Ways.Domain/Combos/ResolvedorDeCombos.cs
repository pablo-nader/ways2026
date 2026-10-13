using Ways.Domain.Articulos;
using Ways.Domain.Dinero;

namespace Ways.Domain.Combos;

/// <summary>
/// Motor de combos puro y determinista (D1-D6 del feature). Detecta conjuntos completos en las
/// líneas de un documento y reparte el ahorro de cada aplicación entre las líneas consumidas.
/// Es voraz y se define así: en cada vuelta se elige, entre los combos que caben con las cantidades
/// restantes, el de mayor ganancia real por aplicación (costo de esas cantidades con sus ofertas
/// menos lo que el combo cobra; empate: menor
/// id). Con la asignación de líneas de esa vuelta, el combo se aplica de una vez k veces, con
/// k = mínimo, entre las líneas asignadas, de (cantidad restante / cantidad tomada por
/// aplicación) redondeado hacia abajo, y recién entonces se reevalúa. El reparto por línea se
/// calcula para UNA aplicación y se multiplica por k. Lo que cobra una aplicación es el precio del combo redondeado
/// al peso entero, y ese cobro se reparte según el importe de lista de cada línea en pesos enteros que suman
/// exactamente el cobro, cada parte entre cero y el importe de lista de la cantidad que aporta
/// (hacia abajo al peso). El importe de lista de la cantidad que aporta una línea se redondea a dos decimales antes
/// del reparto, así que el ahorro de cada línea (ese importe menos su parte del cobro) lleva a lo sumo dos decimales.
/// El cobro es exacto por aplicación; como el redondeo del importe de lista se repite en cada una de las k
/// aplicaciones, el ahorro acumulado de una línea se acota al final por el importe de lista, redondeado a dos
/// decimales, de todo lo que consumió. Así nunca supera el bruto de sus unidades consumidas (lo que exige el servidor
/// a una venta offline) y la línea no puede quedar cobrada en negativo.
/// Un combo que tras ese redondeo no cobra
/// menos que las ofertas, o cuyos topes no alcanzan a contener el cobro, no se aplica. Un componente
/// familia toma líneas en orden de documento (el orden de entrada es el orden del documento), así
/// que un combo puede descartarse aunque otro emparejamiento del documento lo completaría. No
/// busca el óptimo global entre combos que se pisan.
///
/// <para>Cantidades: una línea aporta toda su cantidad y el combo consume exactamente la cantidad de cada componente;
/// lo que sobra queda fuera del combo y la línea nunca se parte. Qué cantidades valen lo decide la unidad de venta de
/// la línea (<see cref="ReglaDeCantidadDeVenta"/>), nunca la forma del número: una línea cuya cantidad su unidad no
/// admite no se detecta, una línea solo es candidata de un componente cuya cantidad su unidad admite, y de una línea
/// por unidad solo se toman unidades enteras. Solo entran las líneas por unidad y por peso
/// (<see cref="ReglaDeCombos.ParticipaEnCombos"/>): una línea de un artículo por importe no se detecta. Las cuentas
/// de cantidad se hacen en milésimas enteras.</para>
/// </summary>
public static class ResolvedorDeCombos
{
    private const long MilesimasPorUnidad = 1000;

    private readonly record struct Aplicacion(
        ComboCandidato Combo, decimal Ganancia, decimal Ahorro, IReadOnlyList<(int Posicion, long Milesimas, decimal Ahorro)> Reparto);

    public static ResolucionDeCombos Resolver(
        IReadOnlyList<LineaParaCombos> lineas, IReadOnlyList<ComboCandidato> combos)
    {
        var restantes = new long[lineas.Count];
        for (var i = 0; i < lineas.Count; i++)
        {
            restantes[i] = MilesimasDetectables(lineas[i]);
        }

        var aplicables = combos.Where(EsAplicable).ToList();
        var consumidas = new long[lineas.Count];
        var ahorroPorLinea = new decimal[lineas.Count];
        var combosPorLinea = new SortedSet<int>[lineas.Count];
        var vecesPorCombo = new Dictionary<int, (int Veces, decimal Ahorro)>();

        while (true)
        {
            Aplicacion? mejor = null;
            foreach (var combo in aplicables)
            {
                if (Intentar(combo, lineas, restantes) is not { } candidata)
                {
                    continue;
                }

                if (mejor is null
                    || candidata.Ganancia > mejor.Value.Ganancia
                    || (candidata.Ganancia == mejor.Value.Ganancia && combo.Id < mejor.Value.Combo.Id))
                {
                    mejor = candidata;
                }
            }

            if (mejor is not { } elegida)
            {
                break;
            }

            // Cabe en un entero de 32 bits: MilesimasDetectables acota lo que aporta cada línea.
            var veces = (long)int.MaxValue;
            foreach (var (posicion, milesimas, _) in elegida.Reparto)
            {
                veces = Math.Min(veces, restantes[posicion] / milesimas);
            }

            foreach (var (posicion, milesimas, ahorro) in elegida.Reparto)
            {
                restantes[posicion] -= milesimas * veces;
                consumidas[posicion] += milesimas * veces;
                ahorroPorLinea[posicion] += ahorro * veces;
                (combosPorLinea[posicion] ??= []).Add(elegida.Combo.Id);
            }

            vecesPorCombo.TryGetValue(elegida.Combo.Id, out var acumulado);
            vecesPorCombo[elegida.Combo.Id] = (acumulado.Veces + (int)veces, acumulado.Ahorro + (elegida.Ahorro * veces));
        }

        if (vecesPorCombo.Count == 0)
        {
            return ResolucionDeCombos.Vacia;
        }

        var resultadoLineas = new List<ConsumoDeLinea>();
        for (var i = 0; i < lineas.Count; i++)
        {
            if (consumidas[i] > 0)
            {
                var tope = Math.Round(ACantidad(consumidas[i]) * lineas[i].PrecioLista, 2, MidpointRounding.AwayFromZero);
                resultadoLineas.Add(new ConsumoDeLinea(
                    lineas[i].Indice, ACantidad(consumidas[i]), Math.Min(ahorroPorLinea[i], tope), [.. combosPorLinea[i]]));
            }
        }

        var resultadoCombos = aplicables
            .Where(c => vecesPorCombo.ContainsKey(c.Id))
            .DistinctBy(c => c.Id)
            .Select(c => new ComboAplicado(c.Id, c.Nombre, vecesPorCombo[c.Id].Veces, vecesPorCombo[c.Id].Ahorro))
            .OrderBy(c => c.IdCombo)
            .ToList();

        return new ResolucionDeCombos(resultadoLineas, resultadoCombos);
    }

    private static decimal ACantidad(long milesimas) => (decimal)milesimas / MilesimasPorUnidad;

    /// <summary>La cantidad de la línea en milésimas, o cero si la línea no se detecta: de un artículo por importe,
    /// cantidad no positiva, que su unidad de venta no admite, o fuera de tope. El tope es <see cref="int.MaxValue"/> unidades para una línea por
    /// unidad y <see cref="int.MaxValue"/> milésimas para una pesable; así las veces que se aplica un combo en una
    /// vuelta caben en un entero de 32 bits.</summary>
    private static long MilesimasDetectables(LineaParaCombos linea)
    {
        if (!ReglaDeCombos.ParticipaEnCombos(linea.UnidadVenta)
            || linea.Cantidad <= 0
            || linea.Cantidad > int.MaxValue
            || !ReglaDeCantidadDeVenta.EsCantidadAdmitida(linea.UnidadVenta, linea.Cantidad))
        {
            return 0;
        }

        var milesimas = (long)(linea.Cantidad * MilesimasPorUnidad);

        return linea.UnidadVenta == UnidadVenta.Peso && milesimas > int.MaxValue ? 0 : milesimas;
    }

    /// <summary>Un combo con una cantidad que <see cref="ReglaDeCombos"/> no admite no se aplica. Que la cantidad sea
    /// la que admite la unidad de venta de cada línea candidata lo decide <see cref="Intentar"/>.</summary>
    private static bool EsAplicable(ComboCandidato combo)
    {
        if (combo.Precio <= 0 || !ReglaDeCombos.AlcanzaElMinimo(combo.Componentes))
        {
            return false;
        }

        foreach (var componente in combo.Componentes)
        {
            if ((componente.IdArticulo is null) == (componente.IdFamilia is null)
                || !ReglaDeCombos.EsCantidadDeComponenteValida(componente.Cantidad))
            {
                return false;
            }
        }

        return true;
    }

    private static Aplicacion? Intentar(
        ComboCandidato combo, IReadOnlyList<LineaParaCombos> lineas, long[] restantes)
    {
        var disponibles = (long[])restantes.Clone();
        var tomado = new Dictionary<int, long>();

        var ordenados = combo.Componentes
            .OrderBy(c => c.IdArticulo is null ? 1 : 0);

        foreach (var componente in ordenados)
        {
            var faltan = (long)(componente.Cantidad * MilesimasPorUnidad);
            for (var i = 0; i < lineas.Count && faltan > 0; i++)
            {
                var coincide = componente.IdArticulo is { } idArticulo
                    ? lineas[i].IdArticulo == idArticulo
                    : lineas[i].IdFamilia == componente.IdFamilia;

                if (!coincide
                    || disponibles[i] == 0
                    || !ReglaDeCantidadDeVenta.EsCantidadAdmitida(lineas[i].UnidadVenta, componente.Cantidad))
                {
                    continue;
                }

                var milesimas = Math.Min(faltan, disponibles[i]);
                if (lineas[i].UnidadVenta == UnidadVenta.Unidad)
                {
                    milesimas -= milesimas % MilesimasPorUnidad;
                }

                if (milesimas == 0)
                {
                    continue;
                }

                disponibles[i] -= milesimas;
                faltan -= milesimas;
                tomado[i] = tomado.GetValueOrDefault(i) + milesimas;
            }

            if (faltan > 0)
            {
                return null;
            }
        }

        var posiciones = tomado.Keys.Order().ToList();
        var importesDeLista = posiciones
            .Select(p => Math.Round(ACantidad(tomado[p]) * lineas[p].PrecioLista, 2, MidpointRounding.AwayFromZero))
            .ToArray();
        var totalDeLista = importesDeLista.Sum();
        var costoConOfertas = posiciones.Sum(
            p => ACantidad(tomado[p]) * (lineas[p].PrecioLista - lineas[p].DescuentoUnitarioDeOferta));

        var cobro = RedondeoDePeso.APesoEntero(combo.Precio);
        if (cobro >= costoConOfertas || cobro >= totalDeLista)
        {
            return null;
        }

        var topes = importesDeLista.Select(decimal.Floor).ToArray();
        var partes = new decimal[posiciones.Count];
        for (var k = 0; k < partes.Length; k++)
        {
            var proporcional = RedondeoDePeso.APesoEntero(cobro * importesDeLista[k] / totalDeLista);
            partes[k] = Math.Clamp(proporcional, 0m, topes[k]);
        }

        var resto = cobro - partes.Sum();
        for (var k = partes.Length - 1; k >= 0 && resto != 0; k--)
        {
            var ajuste = resto > 0
                ? Math.Min(resto, topes[k] - partes[k])
                : -Math.Min(-resto, partes[k]);
            partes[k] += ajuste;
            resto -= ajuste;
        }

        if (resto != 0)
        {
            return null;
        }

        var reparto = new List<(int Posicion, long Milesimas, decimal Ahorro)>(posiciones.Count);
        for (var k = 0; k < partes.Length; k++)
        {
            reparto.Add((posiciones[k], tomado[posiciones[k]], importesDeLista[k] - partes[k]));
        }

        return new Aplicacion(combo, costoConOfertas - cobro, totalDeLista - cobro, reparto);
    }
}
