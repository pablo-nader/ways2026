namespace Ways.Application.MercadoPago;

public sealed record ComisionEnDiagnosticoMercadoPago(string? Tipo, decimal? Monto);

public sealed record ImpuestoDePagoMercadoPago(string? Tipo, decimal? Valor);

public sealed record CargoDePagoMercadoPago(string? Nombre, string? Tipo, decimal? MontoOriginal);

/// <summary>Campos de un pago de <c>GET /v1/payments/search</c> ya saneados: del pagador solo viaja el nombre para mostrar.</summary>
public sealed record PagoRecibidoMercadoPago(
    long Id,
    string? FechaCreacion,
    string? FechaAprobacion,
    string? Estado,
    string? DetalleDelEstado,
    string? TipoDeOperacion,
    string? TipoDePago,
    string? MedioDePago,
    decimal? Monto,
    decimal? NetoRecibido,
    decimal? TotalPagado,
    string? Moneda,
    string? Descripcion,
    string? ReferenciaExterna,
    string? TipoDeInteraccion,
    long? IdCobrador,
    string? NombreDelPagador,
    IReadOnlyList<ComisionEnDiagnosticoMercadoPago> Comisiones,
    IReadOnlyList<ImpuestoDePagoMercadoPago> Impuestos,
    IReadOnlyList<CargoDePagoMercadoPago> Cargos);

public sealed record ListadoDePagosRecibidosMercadoPago(IReadOnlyList<PagoRecibidoMercadoPago> Pagos, int Total);

public sealed record ReporteDeTransaccionesMercadoPago(
    string? NombreDeArchivo, string? FechaDesde, string? FechaHasta, string? FechaCreacion, string? Estado);

/// <summary>Respuesta del diagnóstico de solo lectura. <see cref="Reportes"/> es <c>null</c> cuando su consulta falló; el código del
/// fallo va en <see cref="ErrorReportes"/>.</summary>
public sealed record DiagnosticoDePagosRecibidosMercadoPago(
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    int Total,
    IReadOnlyList<PagoRecibidoMercadoPago> Pagos,
    IReadOnlyList<ReporteDeTransaccionesMercadoPago>? Reportes,
    string? ErrorReportes);
