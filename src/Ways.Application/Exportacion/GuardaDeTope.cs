using Ways.Domain.Common;

namespace Ways.Application.Exportacion;

/// <summary>
/// Guarda del tope de filas de una exportación (design decisión 5-6). Recibe la cantidad de filas
/// ya contada por el caller: los reportes agregados (slice 1b/2) pasan
/// <see cref="TablaExportable.Filas"/><c>.Count</c> ya mapeada, sin ninguna consulta adicional
/// ("nada que contar" además de lo que el mapper ya produjo). Los reportes de tipo listado (slice
/// 3) corren un <c>COUNT(*)</c> ANTES de materializar y llaman a esta misma guarda con ese
/// conteo, sin pagar el costo de mapear filas que después se van a rechazar.
/// mutation-proof-tests: mutación aplicada (el <c>if</c> reemplazado por <c>if (false &amp;&amp;
/// ...)</c>) — <c>ReportesVentasResumenExportTests.UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal</c>
/// pasó de 400 esperado a 200 obtenido; revertida, vuelve a pasar.
/// </summary>
public static class GuardaDeTope
{
    public static void Exigir(int cantidadDeFilas, int topeDeFilas)
    {
        if (cantidadDeFilas > topeDeFilas)
        {
            throw new ErrorDominio(
                "exportacion_demasiado_grande",
                $"La exportación tiene {cantidadDeFilas} filas; el tope es {topeDeFilas}. " +
                "Acotá el rango o los filtros e intentá de nuevo.",
                400);
        }
    }
}
