namespace Ways.Domain.Ventas;

/// <summary>
/// Billetes argentinos válidos y la regla de "vuelto justificado por billetes" (decisión del
/// dueño, 2026-09-16 — reemplaza el <c>vuelto_maximo</c> fijo del legacy para VENTAS EN
/// EFECTIVO, ver docs/01-features-existentes.md §B6 nota de paridad y
/// <see cref="ValidadorDePagos"/> regla 3). El legacy rechazaba directamente cualquier vuelto por
/// encima de un techo fijo ($20) — eso le impedía al cajero dar $4.500 de vuelto sobre un billete
/// de $10.000 contra un ticket de $5.500, un caso perfectamente válido.
///
/// La regla nueva: el vuelto es justificado si el efectivo entregado por el cliente puede
/// formarse con billetes válidos TODOS estrictamente mayores al vuelto — así el cliente nunca
/// entregó un billete que no necesitaba. Ejemplos del dueño: total 5500, entrega 10000 ⇒ vuelto
/// 4500, un solo billete de 10000 (&gt; 4500) alcanza, OK. Total 5500, entrega 5520 ⇒ vuelto 20,
/// ningún billete estrictamente mayor a 20 arma 5520 exacto (todos los billetes ≥ 50 sólo arman
/// múltiplos de 50), se rechaza.
/// </summary>
public static class BilletesArgentinos
{
    /// <summary>Denominaciones vigentes (lista del dueño, 2026-09-16) — única fuente; ningún
    /// llamador duplica estos literales.</summary>
    public static readonly IReadOnlyList<decimal> Denominaciones =
        new[] { 10m, 20m, 50m, 100m, 200m, 500m, 1000m, 2000m, 10000m, 20000m };

    /// <summary>Techo del efectivo entregado que este cálculo acepta evaluar (DP acotada, nunca
    /// sobre un monto sin límite). Por encima de este monto el vuelto se considera directamente
    /// no-justificado.</summary>
    public const decimal EfectivoMaximo = 10_000_000m;

    /// <summary>
    /// ¿<paramref name="efectivoEntregado"/> puede formarse con billetes válidos, todos
    /// estrictamente mayores a <paramref name="vuelto"/>? Un <paramref name="vuelto"/> ⇐ 0 no
    /// tiene nada que justificar (siempre <c>true</c>). El efectivo entregado tiene que ser un
    /// múltiplo entero del billete más chico ($10): si trae centavos o no es múltiplo de 10,
    /// nunca es formable con billetes reales y se rechaza directamente.
    ///
    /// Implementación: coin-change reachability (oferta ilimitada por denominación) con
    /// programación dinámica booleana sobre <c>efectivoEntregado / 10</c> — <see
    /// cref="EfectivoMaximo"/> acota el tamaño de la tabla.
    /// </summary>
    public static bool EsVueltoJustificado(decimal efectivoEntregado, decimal vuelto)
    {
        if (vuelto <= 0m)
        {
            return true;
        }

        if (efectivoEntregado <= 0m || efectivoEntregado > EfectivoMaximo)
        {
            return false;
        }

        if (efectivoEntregado % 1m != 0m || efectivoEntregado % 10m != 0m)
        {
            // Tiene centavos, o no es múltiplo del billete más chico: no formable con billetes.
            return false;
        }

        // Mutation target (mutation-proof-tests): el filtro es "> vuelto", ESTRICTO — un billete
        // que vale exactamente lo mismo que el vuelto no cuenta (el cliente no necesitaba
        // entregarlo). Evidencia de mutación en BilletesArgentinosTests
        // (VueltoIgualAUnBilleteNoAlcanzaParaJustificarElEfectivo).
        var unidadesDeDenominacionesValidas = Denominaciones
            .Where(billete => billete > vuelto)
            .Select(billete => (int)(billete / 10m))
            .Distinct()
            .ToList();

        if (unidadesDeDenominacionesValidas.Count == 0)
        {
            return false;
        }

        var unidadesTotales = (int)(efectivoEntregado / 10m);
        var alcanzable = new bool[unidadesTotales + 1];
        alcanzable[0] = true;

        for (var unidad = 1; unidad <= unidadesTotales; unidad++)
        {
            foreach (var unidadDeBillete in unidadesDeDenominacionesValidas)
            {
                if (unidadDeBillete <= unidad && alcanzable[unidad - unidadDeBillete])
                {
                    alcanzable[unidad] = true;
                    break;
                }
            }
        }

        return alcanzable[unidadesTotales];
    }
}
