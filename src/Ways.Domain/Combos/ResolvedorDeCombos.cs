using Ways.Domain.Dinero;

namespace Ways.Domain.Combos;

/// <summary>
/// Motor de combos puro y determinista (D1-D6 del feature). Detecta conjuntos completos en las
/// líneas de un documento y reparte el ahorro de cada aplicación entre las líneas consumidas.
/// Es voraz y se define así: en cada vuelta se elige, entre los combos que caben con las unidades
/// restantes, el de mayor ganancia real por aplicación (costo de esas unidades con sus ofertas
/// menos lo que el combo cobra; empate: menor
/// id). Con la asignación de líneas de esa vuelta, el combo se aplica de una vez k veces, con
/// k = mínimo, entre las líneas asignadas, de (unidades restantes / unidades tomadas por
/// aplicación) redondeado hacia abajo, y recién entonces se reevalúa. El reparto por línea se
/// calcula para UNA aplicación y se multiplica por k, de modo que cada conjunto cobra exacto
/// el precio del combo redondeado al peso. Lo que cobra una aplicación es el precio del combo redondeado al peso entero,
/// y ese cobro se reparte según el importe de lista de cada línea en pesos enteros que suman
/// exactamente el cobro, cada parte entre cero y el importe de lista de las unidades que aporta
/// (hacia abajo al peso). El ahorro de cada línea es su importe de lista menos su parte del cobro y
/// puede llevar centavos si el precio de lista los tiene. Un combo que tras ese redondeo no cobra
/// menos que las ofertas, o cuyos topes no alcanzan a contener el cobro, no se aplica. Un componente
/// familia toma líneas en orden de documento (el orden de entrada es el orden del documento), así
/// que un combo puede descartarse aunque otro emparejamiento del documento lo completaría. No
/// busca el óptimo global entre combos que se pisan.
/// </summary>
public static class ResolvedorDeCombos
{
    private readonly record struct Toma(int Posicion, int Unidades);

    private readonly record struct Aplicacion(
        ComboCandidato Combo, decimal Ganancia, decimal Ahorro, IReadOnlyList<(int Posicion, int Unidades, decimal Ahorro)> Reparto);

    public static ResolucionDeCombos Resolver(
        IReadOnlyList<LineaParaCombos> lineas, IReadOnlyList<ComboCandidato> combos)
    {
        var restantes = new int[lineas.Count];
        for (var i = 0; i < lineas.Count; i++)
        {
            restantes[i] = UnidadesDetectables(lineas[i].Cantidad);
        }

        var aplicables = combos.Where(EsAplicable).ToList();
        var consumidas = new int[lineas.Count];
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

            var veces = int.MaxValue;
            foreach (var (posicion, unidades, _) in elegida.Reparto)
            {
                veces = Math.Min(veces, restantes[posicion] / unidades);
            }

            foreach (var (posicion, unidades, ahorro) in elegida.Reparto)
            {
                restantes[posicion] -= unidades * veces;
                consumidas[posicion] += unidades * veces;
                ahorroPorLinea[posicion] += ahorro * veces;
                (combosPorLinea[posicion] ??= []).Add(elegida.Combo.Id);
            }

            vecesPorCombo.TryGetValue(elegida.Combo.Id, out var acumulado);
            vecesPorCombo[elegida.Combo.Id] = (acumulado.Veces + veces, acumulado.Ahorro + (elegida.Ahorro * veces));
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
                resultadoLineas.Add(new ConsumoDeLinea(
                    lineas[i].Indice, consumidas[i], ahorroPorLinea[i], [.. combosPorLinea[i]]));
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

    private static int UnidadesDetectables(decimal cantidad) =>
        cantidad > 0 && cantidad <= int.MaxValue && cantidad == decimal.Truncate(cantidad) ? (int)cantidad : 0;

    private static bool EsAplicable(ComboCandidato combo)
    {
        if (combo.Precio <= 0 || combo.Componentes.Count == 0)
        {
            return false;
        }

        decimal unidades = 0;
        foreach (var componente in combo.Componentes)
        {
            if ((componente.IdArticulo is null) == (componente.IdFamilia is null)
                || componente.Cantidad <= 0
                || componente.Cantidad != decimal.Truncate(componente.Cantidad))
            {
                return false;
            }

            if (componente.Cantidad > ReglaDeCombos.CantidadMaxima)
            {
                return false;
            }

            unidades += componente.Cantidad;
        }

        return unidades >= ReglaDeCombos.UnidadesMinimas;
    }

    private static Aplicacion? Intentar(
        ComboCandidato combo, IReadOnlyList<LineaParaCombos> lineas, int[] restantes)
    {
        var disponibles = (int[])restantes.Clone();
        var tomado = new Dictionary<int, int>();

        var ordenados = combo.Componentes
            .OrderBy(c => c.IdArticulo is null ? 1 : 0);

        foreach (var componente in ordenados)
        {
            var faltan = (int)componente.Cantidad;
            for (var i = 0; i < lineas.Count && faltan > 0; i++)
            {
                var coincide = componente.IdArticulo is { } idArticulo
                    ? lineas[i].IdArticulo == idArticulo
                    : lineas[i].IdFamilia == componente.IdFamilia;

                if (!coincide || disponibles[i] == 0)
                {
                    continue;
                }

                var unidades = Math.Min(faltan, disponibles[i]);
                disponibles[i] -= unidades;
                faltan -= unidades;
                tomado[i] = tomado.GetValueOrDefault(i) + unidades;
            }

            if (faltan > 0)
            {
                return null;
            }
        }

        var posiciones = tomado.Keys.Order().ToList();
        var totalDeLista = posiciones.Sum(p => tomado[p] * lineas[p].PrecioLista);
        var costoConOfertas = posiciones.Sum(p => tomado[p] * (lineas[p].PrecioLista - lineas[p].DescuentoUnitarioDeOferta));

        var cobro = RedondeoDePeso.APesoEntero(combo.Precio);
        if (cobro >= costoConOfertas)
        {
            return null;
        }

        var importesDeLista = posiciones.Select(p => tomado[p] * lineas[p].PrecioLista).ToArray();
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

        var reparto = new List<(int Posicion, int Unidades, decimal Ahorro)>(posiciones.Count);
        for (var k = 0; k < partes.Length; k++)
        {
            reparto.Add((posiciones[k], tomado[posiciones[k]], importesDeLista[k] - partes[k]));
        }

        return new Aplicacion(combo, costoConOfertas - cobro, totalDeLista - cobro, reparto);
    }
}
