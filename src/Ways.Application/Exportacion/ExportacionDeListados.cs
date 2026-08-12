using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Ventas;

namespace Ways.Application.Exportacion;

/// <summary>
/// Mappers puros de un listado ya materializado (<see cref="Ventas.ServicioDeVentas.ListarParaExportacionAsync"/>,
/// <see cref="Compras.ServicioDeCompras.ListarParaExportacionAsync"/>,
/// <see cref="CuentaCorriente.ServicioDeCuentaCorriente.ObtenerEstadoDeCuentaParaExportacionAsync"/>)
/// a <see cref="TablaExportable"/> — la etapa 11 nunca vuelve a consultar la base para exportar
/// (design decisión 11). <paramref name="zona"/> viaja aparte de <see cref="ContextoDeExportacion"/>
/// porque este último solo lleva la ETIQUETA de zona para el encabezado (design: Interfaces/
/// Contracts) — <see cref="Celda.FechaHora"/> necesita el <see cref="TimeZoneInfo"/> resuelto.
/// </summary>
public static class ExportacionDeListados
{
    private static readonly IReadOnlyList<ColumnaExportable> ColumnasVentas =
    [
        new ColumnaExportable("Número", TipoDeColumna.Texto),
        new ColumnaExportable("Fecha", TipoDeColumna.FechaHora),
        new ColumnaExportable("Punto de venta", TipoDeColumna.Entero),
        new ColumnaExportable("Cliente", TipoDeColumna.Entero),
        new ColumnaExportable("Estado", TipoDeColumna.Texto),
        new ColumnaExportable("Total", TipoDeColumna.Moneda)
    ];

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasCompras =
    [
        new ColumnaExportable("Comprobante", TipoDeColumna.Texto),
        new ColumnaExportable("Proveedor", TipoDeColumna.Entero),
        new ColumnaExportable("Fecha de recepción", TipoDeColumna.FechaHora),
        new ColumnaExportable("Estado", TipoDeColumna.Texto),
        new ColumnaExportable("Total", TipoDeColumna.Moneda)
    ];

    private static readonly IReadOnlyList<ColumnaExportable> ColumnasEstadoDeCuenta =
    [
        new ColumnaExportable("Fecha", TipoDeColumna.FechaHora),
        new ColumnaExportable("Tipo", TipoDeColumna.Texto),
        new ColumnaExportable("Importe", TipoDeColumna.Moneda),
        new ColumnaExportable("Saldo", TipoDeColumna.Moneda),
        new ColumnaExportable("Detalle", TipoDeColumna.Texto)
    ];

    /// <summary>Una fila por comprobante — mismo shape que <c>GET /api/ventas</c> (sin items ni
    /// pagos, el listado nunca los trajo).</summary>
    public static TablaExportable De(IReadOnlyList<ComprobanteListado> filas, ContextoDeExportacion ctx, TimeZoneInfo zona)
    {
        var celdas = filas
            .Select(f => (IReadOnlyList<Celda>)
            [
                Celda.Texto(f.NumeroVisible),
                Celda.FechaHora(f.Fecha, zona),
                Celda.Entero(f.IdPuntoVenta),
                Celda.Entero(f.IdCliente),
                Celda.Texto(f.Estado.ToString()),
                Celda.Moneda(f.Total)
            ])
            .ToList();

        return new TablaExportable("Ventas", ctx, ColumnasVentas, celdas);
    }

    /// <summary>Una fila por comprobante de compra — <see cref="CompraListada.NumeroExterno"/>
    /// nulo (borrador sin numerar todavía) queda como <c>"-"</c>, nunca celda vacía: distingue
    /// "sin numerar" de "sin dato" para quien filtra la columna.</summary>
    public static TablaExportable De(IReadOnlyList<CompraListada> filas, ContextoDeExportacion ctx, TimeZoneInfo zona)
    {
        var celdas = filas
            .Select(f => (IReadOnlyList<Celda>)
            [
                Celda.Texto(f.NumeroExterno ?? "-"),
                Celda.Entero(f.IdProveedor),
                Celda.FechaHora(f.FechaRecepcion, zona),
                Celda.Texto(f.Estado.ToString()),
                Celda.Moneda(f.Total)
            ])
            .ToList();

        return new TablaExportable("Compras", ctx, ColumnasCompras, celdas);
    }

    /// <summary>Una fila por movimiento del ledger — mismo orden (newest-first) y misma fuente de
    /// saldo (<c>saldo_resultante</c> persistido, nunca recalculado) que <c>GET
    /// /api/clientes/{id}/cuenta-corriente</c>. <see cref="MovimientoDeCuentaCorriente.Detalle"/>
    /// nulo (todo movimiento salvo un ajuste manual) queda como celda vacía.</summary>
    public static TablaExportable De(IReadOnlyList<MovimientoDeCuentaCorriente> filas, ContextoDeExportacion ctx, TimeZoneInfo zona)
    {
        var celdas = filas
            .Select(f => (IReadOnlyList<Celda>)
            [
                Celda.FechaHora(f.Fecha, zona),
                Celda.Texto(f.Tipo.ToString()),
                Celda.Moneda(f.Importe),
                Celda.Moneda(f.SaldoResultante),
                Celda.Texto(f.Detalle)
            ])
            .ToList();

        return new TablaExportable("Estado de cuenta", ctx, ColumnasEstadoDeCuenta, celdas);
    }
}
