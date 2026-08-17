using System.Text.Json;
using Ways.Application.Auditoria;

namespace Ways.Application.Exportacion;

/// <summary>
/// Mapper puro del sibling de export de auditoría (Slice 6, design decisión 13) — mapea desde la
/// MISMA <see cref="FilaDeAuditoria"/> que <c>GET /api/auditoria</c> devuelve, nunca vuelve a
/// consultar la base (design decisión 11, etapa 11) ni redeclara un predicado propio: la paridad
/// JSON↔XLSX es estructural porque ambos consumen <c>ServicioDeConsultaDeAuditoria.ConstruirQuery</c>.
/// </summary>
public static class ExportacionDeAuditoria
{
    private static readonly IReadOnlyList<ColumnaExportable> Columnas =
    [
        new ColumnaExportable("Fecha", TipoDeColumna.FechaHora),
        new ColumnaExportable("Acción", TipoDeColumna.Texto),
        new ColumnaExportable("Entidad", TipoDeColumna.Texto),
        new ColumnaExportable("Id entidad", TipoDeColumna.Entero),
        new ColumnaExportable("Actor", TipoDeColumna.Texto),
        new ColumnaExportable("Punto de venta", TipoDeColumna.Entero),
        new ColumnaExportable("Valor anterior", TipoDeColumna.Texto),
        new ColumnaExportable("Valor nuevo", TipoDeColumna.Texto)
    ];

    /// <summary>Una fila por evento de auditoría, mismo orden que <c>ConstruirQuery</c>
    /// (newest-first). <see cref="FilaDeAuditoria.Actor"/> nulo (actor de plataforma, invisible
    /// para esta sesión) cae a <c>"#idActor"</c> — nunca una celda vacía, que se confundiría con
    /// "sin actor" (design decisión 14). <see cref="FilaDeAuditoria.IdPuntoVenta"/> nulo (evento
    /// tenant-wide) sí queda vacío. Los dos payloads viajan como texto JSON crudo — el cliente
    /// del export nunca reinterpreta el contenido, mismo criterio que la lectura JSON.</summary>
    public static TablaExportable De(IReadOnlyList<FilaDeAuditoria> filas, ContextoDeExportacion ctx, TimeZoneInfo zona)
    {
        var celdas = filas
            .Select(f => (IReadOnlyList<Celda>)
            [
                Celda.FechaHora(f.CreadoEl, zona),
                Celda.Texto(f.Accion),
                Celda.Texto(f.Entidad),
                Celda.Entero(f.IdEntidad),
                Celda.Texto(f.Actor ?? $"#{f.IdActor}"),
                Celda.Entero(f.IdPuntoVenta),
                Celda.Texto(JsonSerializer.Serialize(f.ValorAnterior)),
                Celda.Texto(JsonSerializer.Serialize(f.ValorNuevo))
            ])
            .ToList();

        return new TablaExportable("Auditoría", ctx, Columnas, celdas);
    }
}
