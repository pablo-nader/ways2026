using Ways.Domain.Common;

namespace Ways.Application.Exportacion;

/// <summary>
/// Guarda del tope de filas de una exportación (design decisión 5-6). Corre sobre
/// <see cref="TablaExportable.Filas"/> ya mapeada — para un reporte agregado esto es el conteo
/// final, sin ninguna consulta adicional ("nada que contar" además de lo que el mapper ya
/// produjo). Los reportes de tipo listado (slice 3) corren un <c>COUNT(*)</c> antes de mapear y
/// llaman a esta misma guarda con ese conteo.
/// mutation-proof-tests: mutación aplicada (el <c>if</c> reemplazado por <c>if (false &amp;&amp;
/// ...)</c>) — <c>ReportesVentasResumenExportTests.UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal</c>
/// pasó de 400 esperado a 200 obtenido; revertida, vuelve a pasar.
/// </summary>
public static class GuardaDeTope
{
    public static void Exigir(TablaExportable tabla, int topeDeFilas)
    {
        if (tabla.Filas.Count > topeDeFilas)
        {
            throw new ErrorDominio(
                "exportacion_demasiado_grande",
                $"La exportación tiene {tabla.Filas.Count} filas; el tope es {topeDeFilas}. " +
                "Acotá el rango o los filtros e intentá de nuevo.",
                400);
        }
    }
}
