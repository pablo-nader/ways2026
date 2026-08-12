using Ways.Application.Exportacion;

namespace Ways.Application.Caja;

/// <summary>
/// Mappers puros de un listado de caja ya materializado a <see cref="TablaExportable"/> — la
/// etapa 11 nunca vuelve a consultar la base para exportar (design decisión 11). Slice 7 abre este
/// archivo con el mapper del libro de tesorería (G3); G2 (listado/detalle) lo extiende en una
/// slice de seguimiento (5a.4/5b.4, ver el APPLY-RUN NOTE de <c>tasks.md</c>).
/// </summary>
public static class ExportacionDeCaja
{
    private static readonly IReadOnlyList<ColumnaExportable> ColumnasTesoreria =
    [
        new ColumnaExportable("Inicio", TipoDeColumna.Moneda),
        new ColumnaExportable("Ingreso", TipoDeColumna.Moneda),
        new ColumnaExportable("Egreso", TipoDeColumna.Moneda),
        new ColumnaExportable("Final", TipoDeColumna.Moneda),
        new ColumnaExportable("Concepto", TipoDeColumna.Texto),
        new ColumnaExportable("Empleado", TipoDeColumna.Entero),
        new ColumnaExportable("Fecha", TipoDeColumna.FechaHora)
    ];

    /// <summary>Libro de tesorería (G3, spec tesoreria: The Book Has An Export Sibling Equal To
    /// Its JSON) — misma orden de columnas que la tabla del libro (design: Slice 7 task 7.5,
    /// "inicio/ingreso/egreso/final/concepto/empleado/fecha"), en el MISMO orden de filas que
    /// <see cref="ServicioDeTesoreria.ListarAsync"/> devuelve (cadena por <c>Id</c> ascendente,
    /// nunca re-ordenado acá).</summary>
    public static TablaExportable De(IReadOnlyList<MovimientoTesoreriaListado> filas, ContextoDeExportacion ctx, TimeZoneInfo zona)
    {
        var celdas = filas
            .Select(f => (IReadOnlyList<Celda>)
            [
                Celda.Moneda(f.Inicio),
                Celda.Moneda(f.Ingreso),
                Celda.Moneda(f.Egreso),
                Celda.Moneda(f.Final),
                Celda.Texto(f.Concepto),
                Celda.Entero(f.IdEmpleado),
                Celda.FechaHora(f.Fecha, zona)
            ])
            .ToList();

        return new TablaExportable("Tesorería", ctx, ColumnasTesoreria, celdas);
    }

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
    /// Ticket And Gasto Listings) — <see cref="TablaExportable"/> solo admite UNA hoja (design:
    /// Interfaces/Contracts), así que el detalle vive en una única hoja seccionada por
    /// <c>Sección</c> en vez de varias hojas: medios de pago (esperado por medio), egresos por
    /// categoría, egresos por área, retiros, tickets y gastos, en ese orden — mismo agrupamiento
    /// que <see cref="ResumenDeTurno.Egresos"/> ya expone. <c>CantidadTickets</c>/
    /// <c>PrimerTicket</c>/<c>UltimoTicket</c>/<c>IngresosPorArea</c> no se repiten como filas
    /// propias: son contenido derivable de la sección Tickets (o de las líneas de venta, fuera del
    /// alcance del detalle de turno), no un segundo dato a mantener igual al JSON.</summary>
    public static TablaExportable De(DetalleDeTurno respuesta, ContextoDeExportacion ctx, TimeZoneInfo zona)
    {
        var filas = new List<IReadOnlyList<Celda>>();

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

        foreach (var gasto in respuesta.Gastos)
        {
            filas.Add(
            [
                Celda.Texto("Gastos"),
                Celda.Texto(gasto.Categoria.ToString()),
                Celda.FechaHora(gasto.Fecha, zona),
                Celda.Moneda(gasto.Importe)
            ]);
        }

        return new TablaExportable("Caja Z", ctx, ColumnasDetalleDeTurno, filas);
    }
}
