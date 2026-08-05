using Ways.Domain.Catalogos;

namespace Ways.Domain.Caja;

/// <summary>
/// La derivación (design: The Derivation — binding, una sola fórmula, sin segunda copia): pura,
/// sin base de datos, mismo criterio que <see cref="Ventas.ValidadorDePagos"/>. Tanto
/// <c>ServicioDeResumenDeTurno</c> como <c>ServicioDeTurnos.CerrarAsync</c> (Slice 4) llaman a
/// esta misma clase con los mismos insumos — es la única garantía estructural de que el número
/// que el cajero vio a las 19:00 es el que el cierre compara a las 20:00 (spec: Resumen Parcial
/// Uses The Same Derivation As Cierre).
/// </summary>
public static class CalculadorDeArqueo
{
    /// <summary>Por medio con <c>Comportamiento ≠ CuentaCorriente</c>: <c>importe_esperado(m) =
    /// pagos(m) − gastos(m) + [m = ancla] × (fondo + refuerzos − retiros − vueltosTotales)</c>
    /// (design decisiones 2 y 9). <c>vueltosTotales</c> se deriva acá sumando
    /// <see cref="ActividadDeMedio.Vueltos"/> de TODOS los medios — el cambio sale físicamente
    /// del cajón sin importar con qué medio pagó el cliente, así que solo la línea del ancla lo
    /// absorbe (design decisión 2: no por medio).
    ///
    /// Arqueable por EXISTENCIA de fila, nunca por valor (<paramref name="insumos"/>.Actividad ya
    /// trae <see cref="ActividadDeMedio.TuvoFilas"/> resuelto): un medio puede netear exactamente
    /// 0 y seguir debiendo una declaración. El ancla entra igual sin <c>TuvoFilas</c> propio
    /// cuando el turno tuvo fondo/retiro/refuerzo/vuelto físico — el dinero se movió aunque nadie
    /// pagó con ese medio puntual. Cuenta corriente queda afuera siempre (proposal decisión 6):
    /// nada físico que contar.
    ///
    /// Devuelve SOLO los medios arqueables, en orden estable por <c>id_medio_pago</c> (Interfaces/
    /// Contracts).</summary>
    public static IReadOnlyList<LineaDeArqueo> Calcular(InsumosDeArqueo insumos, int idMedioAncla)
    {
        var vueltosTotales = insumos.Actividad.Sum(a => a.Vueltos);
        var hayMovimientoFisicoDelAncla =
            insumos.FondoInicial != 0m || insumos.Refuerzos != 0m || insumos.Retiros != 0m || vueltosTotales != 0m;

        var lineas = new List<LineaDeArqueo>();

        foreach (var actividad in insumos.Actividad.OrderBy(a => a.IdMedioPago))
        {
            if (actividad.Comportamiento == ComportamientoMedioPago.CuentaCorriente)
            {
                continue;
            }

            var esAncla = actividad.IdMedioPago == idMedioAncla;
            var esArqueable = actividad.TuvoFilas || (esAncla && hayMovimientoFisicoDelAncla);

            if (!esArqueable)
            {
                continue;
            }

            var importeEsperado = actividad.Pagos - actividad.Gastos;
            if (esAncla)
            {
                importeEsperado += insumos.FondoInicial + insumos.Refuerzos - insumos.Retiros - vueltosTotales;
            }

            lineas.Add(new LineaDeArqueo(actividad.IdMedioPago, importeEsperado));
        }

        return lineas;
    }
}
