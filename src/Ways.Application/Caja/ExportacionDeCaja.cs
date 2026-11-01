using Ways.Application.Exportacion;
using Ways.Domain.Caja;
using Ways.Domain.Pagos;

namespace Ways.Application.Caja;

/// <summary>
/// Mappers puros de un listado de caja ya materializado a <see cref="TablaExportable"/> — la
/// etapa 11 nunca vuelve a consultar la base para exportar (design decisión 11). Hoy cubre el histórico
/// de cajas y el detalle de un turno.
/// </summary>
public static class ExportacionDeCaja
{
    private static readonly IReadOnlyList<ColumnaExportable> ColumnasHistoricoDeCajas =
    [
        new ColumnaExportable("Turno", TipoDeColumna.Entero),
        new ColumnaExportable("Punto de venta", TipoDeColumna.Entero),
        new ColumnaExportable("Apertura", TipoDeColumna.FechaHora),
        new ColumnaExportable("Cierre", TipoDeColumna.FechaHora),
        new ColumnaExportable("Esperado", TipoDeColumna.Moneda),
        new ColumnaExportable("Declarado", TipoDeColumna.Moneda),
        new ColumnaExportable("Diferencia", TipoDeColumna.Moneda),
        new ColumnaExportable("Retiros", TipoDeColumna.Moneda)
    ];

    /// <summary>G2 listado (spec historico-de-cajas: G2 Listing Export Figures Equal The JSON
    /// Listing) — una fila por turno cerrado, sin fila de totales: el listado ya es "una fila por
    /// turno" (design decisión 6), mismo criterio que <c>VentasPorPuntoVenta</c>/
    /// <c>VentasPorVendedor</c>. <c>Retiros</c> resume <see cref="EgresosDeTurno.Retiros"/>; el
    /// desglose por categoría/área no viaja en una fila plana.</summary>
    public static TablaExportable De(
        IReadOnlyList<FilaDeHistoricoDeCajas> respuesta, ContextoDeExportacion ctx, TimeZoneInfo zona) =>
        new(
            "Histórico de cajas", ctx, ColumnasHistoricoDeCajas,
            respuesta
                .Select(f => (IReadOnlyList<Celda>)
                [
                    Celda.Entero(f.IdTurnoCaja),
                    Celda.Entero(f.IdPuntoVenta),
                    Celda.FechaHora(f.FechaApertura, zona),
                    Celda.FechaHora(f.FechaCierre, zona),
                    Celda.Moneda(f.Esperado),
                    Celda.Moneda(f.Declarado),
                    Celda.Moneda(f.Diferencia),
                    Celda.Moneda(f.Egresos.Retiros)
                ])
                .ToList());

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasDetalleDeTurno =
    [
        new ColumnaExportable("Sección", TipoDeColumna.Texto),
        new ColumnaExportable("Detalle", TipoDeColumna.Texto),
        new ColumnaExportable("Fecha", TipoDeColumna.FechaHora),
        new ColumnaExportable("Importe", TipoDeColumna.Moneda)
    ];

    /// <summary>Z-report del turno (spec historico-de-cajas: G2 Detail Reuses ResumenDeTurno Plus
    /// Ticket And Pago Listings) — <see cref="TablaExportable"/> solo admite UNA hoja (design:
    /// Interfaces/Contracts), así que el detalle vive en una única hoja seccionada por
    /// <c>Sección</c> en vez de varias hojas: medios de pago (esperado por medio), egresos por
    /// categoría, egresos por área, retiros, tickets y pagos, en ese orden — mismo agrupamiento
    /// que <see cref="ResumenDeTurno.Egresos"/> ya expone. <c>CantidadTickets</c>/
    /// <c>PrimerTicket</c>/<c>UltimoTicket</c>/<c>IngresosPorArea</c> no se repiten como filas
    /// propias: son contenido derivable de la sección Tickets (o de las líneas de venta, fuera del
    /// alcance del detalle de turno), no un segundo dato a mantener igual al JSON.</summary>
    public static TablaExportable De(DetalleDeTurno respuesta, ContextoDeExportacion ctx, TimeZoneInfo zona)
    {
        var filas = new List<IReadOnlyList<Celda>>();

        if (respuesta.FechaRecalculo is not null)
        {
            // Primera fila: la marca de recálculo; la fecha viaja como celda de fecha (zona del comercio).
            filas.Add(
            [
                Celda.Texto("Arqueo recalculado"),
                Celda.Texto(respuesta.IdEmpleadoRecalculo is { } idEmpleado
                    ? $"Recalculado por empleado #{idEmpleado}"
                    : "Recalculado"),
                Celda.FechaHora(respuesta.FechaRecalculo, zona),
                Celda.Moneda(null)
            ]);
        }

        foreach (var medio in respuesta.Resumen.Medios)
        {
            filas.Add(
            [
                Celda.Texto("Medios de pago"),
                Celda.Texto($"Medio {medio.IdMedioPago}"),
                Celda.FechaHora(null, zona),
                Celda.Moneda(medio.ImporteEsperado)
            ]);
        }

        foreach (var categoria in respuesta.Resumen.Egresos.PorCategoria)
        {
            filas.Add(
            [
                Celda.Texto("Egresos por categoría"),
                Celda.Texto(categoria.Categoria.ToString()),
                Celda.FechaHora(null, zona),
                Celda.Moneda(categoria.Total)
            ]);
        }

        foreach (var area in respuesta.Resumen.Egresos.PorArea)
        {
            filas.Add(
            [
                Celda.Texto("Egresos por área"),
                Celda.Texto(area.NombreArea),
                Celda.FechaHora(null, zona),
                Celda.Moneda(area.Total)
            ]);
        }

        filas.Add(
        [
            Celda.Texto("Retiros"),
            Celda.Texto("Total retiros"),
            Celda.FechaHora(null, zona),
            Celda.Moneda(respuesta.Resumen.Egresos.Retiros)
        ]);

        foreach (var ticket in respuesta.Tickets)
        {
            filas.Add(
            [
                Celda.Texto("Tickets"),
                Celda.Texto(ticket.NumeroVisible),
                Celda.FechaHora(ticket.Fecha, zona),
                Celda.Moneda(ticket.Total)
            ]);
        }

        foreach (var pago in respuesta.Pagos)
        {
            // Los pagos de caja general no entran en los egresos por categoría/área del arqueo.
            filas.Add(
            [
                Celda.Texto(pago.OrigenFondos == OrigenFondosPago.Tesoreria ? "Gastos (caja general)" : "Gastos"),
                Celda.Texto(pago.Categoria.ToString()),
                Celda.FechaHora(pago.Fecha, zona),
                Celda.Moneda(pago.Importe)
            ]);
        }

        return new TablaExportable("Caja Z", ctx, ColumnasDetalleDeTurno, filas);
    }
}
