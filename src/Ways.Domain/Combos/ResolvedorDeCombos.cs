namespace Ways.Domain.Combos;

/// <summary>
/// Motor de combos puro y determinista (D1-D6 del feature). Detecta conjuntos completos en las
/// líneas de un documento y reparte el ahorro de cada aplicación entre las líneas consumidas.
/// Es voraz: en cada vuelta aplica el combo de mayor ahorro por aplicación (empate: menor id) y
/// repite hasta que ninguno cabe; no busca el óptimo global entre combos que se pisan.
/// </summary>
public static class ResolvedorDeCombos
{
    private readonly record struct Toma(int Posicion, int Unidades);

    private readonly record struct Aplicacion(ComboCandidato Combo, decimal Ahorro, IReadOnlyList<(int Posicion, int Unidades, decimal Ahorro)> Reparto);

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
                    || candidata.Ahorro > mejor.Value.Ahorro
                    || (candidata.Ahorro == mejor.Value.Ahorro && combo.Id < mejor.Value.Combo.Id))
                {
                    mejor = candidata;
                }
            }

            if (mejor is not { } elegida)
            {
                break;
            }

            foreach (var (posicion, unidades, ahorro) in elegida.Reparto)
            {
                restantes[posicion] -= unidades;
                consumidas[posicion] += unidades;
                ahorroPorLinea[posicion] += ahorro;
                (combosPorLinea[posicion] ??= []).Add(elegida.Combo.Id);
            }

            vecesPorCombo.TryGetValue(elegida.Combo.Id, out var acumulado);
            vecesPorCombo[elegida.Combo.Id] = (acumulado.Veces + 1, acumulado.Ahorro + elegida.Ahorro);
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

        if (combo.Precio >= costoConOfertas)
        {
            return null;
        }

        var ahorro = totalDeLista - combo.Precio;
        var reparto = new List<(int Posicion, int Unidades, decimal Ahorro)>(posiciones.Count);
        var repartido = 0m;
        for (var k = 0; k < posiciones.Count; k++)
        {
            var posicion = posiciones[k];
            var parte = k == posiciones.Count - 1
                ? ahorro - repartido
                : Math.Round(
                    ahorro * (tomado[posicion] * lineas[posicion].PrecioLista) / totalDeLista,
                    2,
                    MidpointRounding.AwayFromZero);
            repartido += parte;
            reparto.Add((posicion, tomado[posicion], parte));
        }

        return new Aplicacion(combo, ahorro, reparto);
    }
}
