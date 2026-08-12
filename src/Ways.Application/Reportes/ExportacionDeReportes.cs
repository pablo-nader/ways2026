using Ways.Application.Exportacion;

namespace Ways.Application.Reportes;

/// <summary>
/// Mappers puros de un response record de <c>Ways.Application.Reportes</c> ya materializado a
/// <see cref="TablaExportable"/> — la etapa 11 nunca vuelve a consultar la base para exportar
/// (design decisión 11, spec exportacion-de-reportes: "No Re-Query"). Uno por reporte,
/// arrancando acá con <c>ventas/resumen</c> (slice 1b); los ocho reportes restantes de stage-10
/// se agregan en slice 2.
/// </summary>
public static class ExportacionDeReportes
{
    private static readonly IReadOnlyList<ColumnaExportable> ColumnasResumenDeVentas =
    [
        new ColumnaExportable("Período", TipoDeColumna.Texto),
        new ColumnaExportable("Neto", TipoDeColumna.Moneda),
        new ColumnaExportable("TX", TipoDeColumna.Entero),
        new ColumnaExportable("Ticket promedio", TipoDeColumna.Moneda)
    ];

    /// <summary>Una fila por bucket de <see cref="ResumenDeVentas.Serie"/> más una fila de
    /// totales — mismo criterio que la respuesta JSON: <c>TicketPromedio</c> nulo (bucket o
    /// período sin ningún TX) queda como celda vacía, nunca <c>0</c>.</summary>
    public static TablaExportable De(ResumenDeVentas respuesta, ContextoDeExportacion ctx)
    {
        var filas = respuesta.Serie
            .Select(bucket => (IReadOnlyList<Celda>)
            [
                Celda.Texto(bucket.Etiqueta),
                Celda.Moneda(bucket.Neto),
                Celda.Entero(bucket.CantidadTx),
                Celda.Moneda(bucket.TicketPromedio)
            ])
            .ToList();

        filas.Add(
        [
            Celda.Texto("Total"),
            Celda.Moneda(respuesta.NetoVendido),
            Celda.Entero(respuesta.CantidadTx),
            Celda.Moneda(respuesta.TicketPromedio)
        ]);

        return new TablaExportable("Ventas resumen", ctx, ColumnasResumenDeVentas, filas);
    }
}
