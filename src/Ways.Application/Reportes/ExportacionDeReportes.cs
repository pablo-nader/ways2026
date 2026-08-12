using Ways.Application.Exportacion;
using Ways.Domain.Reportes;

namespace Ways.Application.Reportes;

/// <summary>
/// Mappers puros de un response record de <c>Ways.Application.Reportes</c> ya materializado a
/// <see cref="TablaExportable"/> — la etapa 11 nunca vuelve a consultar la base para exportar
/// (design decisión 11, spec exportacion-de-reportes: "No Re-Query"). Uno por reporte:
/// <c>ventas/resumen</c> llegó en slice 1b; los ocho restantes de stage-10 (por-punto-venta,
/// por-vendedor, por-medio-pago, articulos/top, compras/por-proveedor, gastos/resumen,
/// rentabilidad, comisiones) se agregan acá en slice 2.
/// </summary>
public static class ExportacionDeReportes
{
    /// <summary>Etiqueta PROVISIONAL del export de comisiones, verbatim con la respuesta JSON
    /// (<see cref="Comisiones.Provisional"/> viaja siempre en <c>true</c> — spec
    /// rentabilidad-y-comisiones: The Comisiones Export Is Labelled PROVISIONAL). El endpoint la
    /// pasa como <c>Cobertura</c> del <see cref="ContextoDeExportacion"/> — mismo campo que
    /// rentabilidad usa para su bloque de cobertura de costo, reusado acá para la etiqueta, no un
    /// campo nuevo.</summary>
    public const string EtiquetaProvisionalComisiones =
        "PROVISIONAL: comisión calculada al momento de exportar, no persistida.";

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

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasPorPuntoVenta =
    [
        new ColumnaExportable("Punto de venta", TipoDeColumna.Entero),
        new ColumnaExportable("Neto", TipoDeColumna.Moneda),
        new ColumnaExportable("TX", TipoDeColumna.Entero),
        new ColumnaExportable("Ticket promedio", TipoDeColumna.Moneda)
    ];

    /// <summary>Sin fila de totales: <see cref="VentasPorPuntoVenta"/> no trae un agregado de
    /// nivel respuesta (cada fila ya reporta su propio subtotal, spec reportes-de-gestion).</summary>
    public static TablaExportable De(VentasPorPuntoVenta respuesta, ContextoDeExportacion ctx) =>
        new(
            "Ventas por punto de venta", ctx, ColumnasPorPuntoVenta,
            respuesta.Filas
                .Select(f => (IReadOnlyList<Celda>)
                [
                    Celda.Entero(f.IdPuntoVenta),
                    Celda.Moneda(f.Neto),
                    Celda.Entero(f.CantidadTx),
                    Celda.Moneda(f.TicketPromedio)
                ])
                .ToList());

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasPorVendedor =
    [
        new ColumnaExportable("Vendedor", TipoDeColumna.Entero),
        new ColumnaExportable("Neto", TipoDeColumna.Moneda),
        new ColumnaExportable("TX", TipoDeColumna.Entero),
        new ColumnaExportable("Ticket promedio", TipoDeColumna.Moneda)
    ];

    public static TablaExportable De(VentasPorVendedor respuesta, ContextoDeExportacion ctx) =>
        new(
            "Ventas por vendedor", ctx, ColumnasPorVendedor,
            respuesta.Filas
                .Select(f => (IReadOnlyList<Celda>)
                [
                    Celda.Entero(f.IdEmpleado),
                    Celda.Moneda(f.Neto),
                    Celda.Entero(f.CantidadTx),
                    Celda.Moneda(f.TicketPromedio)
                ])
                .ToList());

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasPorMedioPago =
    [
        new ColumnaExportable("Medio de pago", TipoDeColumna.Entero),
        new ColumnaExportable("Neto", TipoDeColumna.Moneda),
        new ColumnaExportable("Cantidad de pagos", TipoDeColumna.Entero)
    ];

    public static TablaExportable De(VentasPorMedioPago respuesta, ContextoDeExportacion ctx) =>
        new(
            "Ventas por medio de pago", ctx, ColumnasPorMedioPago,
            respuesta.Filas
                .Select(f => (IReadOnlyList<Celda>)
                [
                    Celda.Entero(f.IdMedioPago),
                    Celda.Moneda(f.Neto),
                    Celda.Entero(f.CantidadPagos)
                ])
                .ToList());

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasArticulosTop =
    [
        new ColumnaExportable("Artículo", TipoDeColumna.Entero),
        new ColumnaExportable("Descripción", TipoDeColumna.Texto),
        new ColumnaExportable("Cantidad", TipoDeColumna.Cantidad),
        new ColumnaExportable("Total", TipoDeColumna.Moneda)
    ];

    /// <summary>Sin costo ni margen — esos campos viven en <c>/rentabilidad</c> (design decisión
    /// 10 de stage-10, heredada acá).</summary>
    public static TablaExportable De(TopArticulos respuesta, ContextoDeExportacion ctx) =>
        new(
            "Artículos top", ctx, ColumnasArticulosTop,
            respuesta.Articulos
                .Select(a => (IReadOnlyList<Celda>)
                [
                    Celda.Entero(a.IdArticulo),
                    Celda.Texto(a.Descripcion),
                    Celda.Cantidad(a.Cantidad),
                    Celda.Moneda(a.Total)
                ])
                .ToList());

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasComprasPorProveedor =
    [
        new ColumnaExportable("Proveedor", TipoDeColumna.Texto),
        new ColumnaExportable("Total", TipoDeColumna.Moneda),
        new ColumnaExportable("Cantidad de compras", TipoDeColumna.Entero)
    ];

    /// <summary>Una fila por proveedor más una fila de totales con
    /// <see cref="ComprasPorProveedor.TotalGeneral"/> — mismo criterio de totales que
    /// <c>ventas/resumen</c>.</summary>
    public static TablaExportable De(ComprasPorProveedor respuesta, ContextoDeExportacion ctx)
    {
        var filas = respuesta.PorProveedor
            .Select(p => (IReadOnlyList<Celda>)
            [
                Celda.Texto(p.NombreProveedor),
                Celda.Moneda(p.Total),
                Celda.Entero(p.CantidadCompras)
            ])
            .ToList();

        filas.Add(
        [
            Celda.Texto("Total"),
            Celda.Moneda(respuesta.TotalGeneral),
            Celda.Entero(respuesta.PorProveedor.Sum(p => p.CantidadCompras))
        ]);

        return new TablaExportable("Compras por proveedor", ctx, ColumnasComprasPorProveedor, filas);
    }

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasGastosResumen =
    [
        new ColumnaExportable("Período", TipoDeColumna.Texto),
        new ColumnaExportable("Importe", TipoDeColumna.Moneda)
    ];

    /// <summary>Una fila por bucket de <see cref="ResumenDeGastos.Serie"/> más una fila de
    /// totales con <see cref="ResumenDeGastos.ImporteTotal"/> — mismo criterio de gap-fill y
    /// totales que <c>ventas/resumen</c>. Sin el desglose por categoría: la serie temporal es la
    /// figura que este export prueba igual al endpoint, mismo alcance que el resto de los ocho
    /// mappers de esta slice.</summary>
    public static TablaExportable De(ResumenDeGastos respuesta, ContextoDeExportacion ctx)
    {
        var filas = respuesta.Serie
            .Select(bucket => (IReadOnlyList<Celda>)
            [
                Celda.Texto(bucket.Etiqueta),
                Celda.Moneda(bucket.Importe)
            ])
            .ToList();

        filas.Add([Celda.Texto("Total"), Celda.Moneda(respuesta.ImporteTotal)]);

        return new TablaExportable("Gastos resumen", ctx, ColumnasGastosResumen, filas);
    }

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasRentabilidad =
    [
        new ColumnaExportable("Artículo", TipoDeColumna.Entero),
        new ColumnaExportable("Descripción", TipoDeColumna.Texto),
        new ColumnaExportable("Venta considerada", TipoDeColumna.Moneda),
        new ColumnaExportable("Costo considerado", TipoDeColumna.Moneda),
        new ColumnaExportable("Margen", TipoDeColumna.Moneda),
        new ColumnaExportable("Margen %", TipoDeColumna.Decimal)
    ];

    /// <summary>Una fila por artículo (<c>IdArticulo</c> nulo en una línea de concepto libre,
    /// design decisión 10) más una fila de totales con las figuras de nivel respuesta. El
    /// bloque de cobertura NO lo escribe este mapper — lo arma <see cref="ArmarTextoDeCobertura"/>
    /// y lo pasa el endpoint dentro del <see cref="ContextoDeExportacion.Cobertura"/> del
    /// <paramref name="ctx"/> recibido, mismo seam que <c>ExportadorXlsx</c> ya escribe en la fila
    /// 4 del encabezado (spec rentabilidad-y-comisiones: Rentabilidad And Comisiones Exports Stack
    /// LecturaDeRentabilidad And Carry Coverage).</summary>
    public static TablaExportable De(Rentabilidad respuesta, ContextoDeExportacion ctx)
    {
        var filas = respuesta.PorArticulo
            .Select(p => (IReadOnlyList<Celda>)
            [
                Celda.Entero(p.IdArticulo),
                Celda.Texto(p.Descripcion),
                Celda.Moneda(p.VentaConsiderada),
                Celda.Moneda(p.CostoConsiderado),
                Celda.Moneda(p.Margen),
                Celda.Decimal(p.MargenPorcentaje)
            ])
            .ToList();

        filas.Add(
        [
            Celda.Entero(null),
            Celda.Texto("Total"),
            Celda.Moneda(respuesta.VentaConsiderada),
            Celda.Moneda(respuesta.CostoConsiderado),
            Celda.Moneda(respuesta.Margen),
            Celda.Decimal(respuesta.MargenPorcentaje)
        ]);

        return new TablaExportable("Rentabilidad", ctx, ColumnasRentabilidad, filas);
    }

    /// <summary>Texto del bloque de cobertura que el endpoint de <c>/rentabilidad/export</c> pasa
    /// como <see cref="ContextoDeExportacion.Cobertura"/> — repite los mismos tres conteos y sus
    /// subtotales de venta que <see cref="CoberturaDeCosto"/> trae en la respuesta JSON (spec:
    /// An Admin's Rentabilidad Export Carries The Coverage Block), nunca un porcentaje calculado
    /// aparte que pudiera desviarse del JSON.</summary>
    public static string ArmarTextoDeCobertura(CoberturaDeCosto cobertura) =>
        $"Cobertura: {cobertura.LineasConCostoReal} líneas con costo real (venta ${cobertura.VentaConCostoReal:0.00}), " +
        $"{cobertura.LineasConCostoEstimado} con costo estimado (venta ${cobertura.VentaConCostoEstimado:0.00}), " +
        $"{cobertura.LineasSinCosto} con costo desconocido (venta ${cobertura.VentaSinCosto:0.00}), " +
        $"de {cobertura.LineasTotales} líneas totales.";

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasComisiones =
    [
        new ColumnaExportable("Vendedor", TipoDeColumna.Entero),
        new ColumnaExportable("Neto vendido", TipoDeColumna.Moneda),
        new ColumnaExportable("Comisión", TipoDeColumna.Moneda)
    ];

    /// <summary>PROVISIONAL en su totalidad (droppable, stage-10 slice 10) — la etiqueta viaja en
    /// <see cref="ContextoDeExportacion.Cobertura"/> vía <see cref="EtiquetaProvisionalComisiones"/>,
    /// no en una columna propia: <see cref="Comisiones.Provisional"/> es siempre <c>true</c>, así
    /// que no hay una fila "no provisional" que distinguir.</summary>
    public static TablaExportable De(Comisiones respuesta, ContextoDeExportacion ctx) =>
        new(
            "Comisiones", ctx, ColumnasComisiones,
            respuesta.Filas
                .Select(f => (IReadOnlyList<Celda>)
                [
                    Celda.Entero(f.IdEmpleado),
                    Celda.Moneda(f.NetoVendido),
                    Celda.Moneda(f.Comision)
                ])
                .ToList());
}
