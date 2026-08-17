namespace Ways.Application.Exportacion;

/// <summary>
/// El encabezado del XLSX imprime la hora de pared junto a la etiqueta de zona
/// (<c>ContextoDeExportacion.ZonaHoraria</c>), así que el instante tiene que llegar ya expresado
/// en esa zona antes de guardarse en <c>GeneradoEl</c> — mismo criterio que
/// <see cref="Celda.FechaHora"/>: la Infrastructure no resuelve zonas horarias, solo escribe el
/// valor que ya le llega convertido.
/// </summary>
public static class InstanteDeGeneracion
{
    /// <summary>
    /// El fallback (instante intacto) existe porque <c>GET /api/reportes/compras/por-proveedor/export</c>
    /// pasa el centinela <c>"N/A"</c> como zona — ese reporte bucketea por <c>fecha_recepcion</c>
    /// sin exponer una zona propia. En ese caso el instante queda en UTC porque la etiqueta de al
    /// lado no afirma ninguna zona.
    /// </summary>
    public static DateTimeOffset En(DateTimeOffset instante, string zonaHoraria) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(zonaHoraria, out var zona)
            ? TimeZoneInfo.ConvertTime(instante, zona)
            : instante;
}
