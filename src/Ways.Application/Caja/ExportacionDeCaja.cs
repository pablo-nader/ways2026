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
}
