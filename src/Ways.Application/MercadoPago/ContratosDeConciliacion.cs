namespace Ways.Application.MercadoPago;

/// <summary>Cuántas filas de una conciliación cayeron en un resultado y cuánto suman (importe, comisión y neto tal como los informa el reporte).</summary>
public sealed record ResumenDeResultadoDeConciliacion(string Resultado, int Cantidad, decimal Importe, decimal Comision, decimal Neto);

/// <summary>
/// Una conciliación (un reporte importado) con su resumen por resultado. <see cref="CantidadFilas"/> cuenta todas las filas del archivo, las
/// inválidas incluidas (<see cref="CantidadInvalidas"/>). El período es el de las fechas de las transacciones leídas.
/// </summary>
public sealed record ConciliacionListado(
    int Id,
    int IdEmpresa,
    string? NombreArchivo,
    DateTimeOffset? FechaDesde,
    DateTimeOffset? FechaHasta,
    int IdUsuario,
    string? Usuario,
    DateTimeOffset SubidaEl,
    int CantidadFilas,
    int CantidadInvalidas,
    IReadOnlyList<ResumenDeResultadoDeConciliacion> Resumen);

public sealed record FilaInvalidaDeConciliacion(int NumeroLinea, string Motivo);

/// <summary>Lo que devuelve subir un reporte: la conciliación creada y las filas que no se pudieron leer (a lo sumo
/// <see cref="ServicioDeConciliacionMercadoPago.MaximoDeInvalidasDevueltas"/>; <see cref="InvalidasTruncado"/> avisa si hay más).</summary>
public sealed record ConciliacionImportada(
    ConciliacionListado Conciliacion, IReadOnlyList<FilaInvalidaDeConciliacion> Invalidas, bool InvalidasTruncado);

/// <summary>Un pago de venta sin verificar que podría ser el de una fila sin cobro en Ways (mismo importe, a un día de la fecha, misma caja
/// cuando la fila trae el id). Es una sugerencia: solo un Admin la confirma.</summary>
public sealed record CandidatoDeConfirmacion(
    int IdCobro, int IdComprobanteVenta, string NumeroVisible, DateTimeOffset Fecha, decimal Importe, string? Referencia);

/// <summary>Una fila del reporte con su resultado. <see cref="Candidatos"/> solo viene en una fila <c>sin_cobro_en_ways</c> todavía no
/// confirmada; <see cref="IdCobro"/> es el pago de venta que un Admin confirmó contra ella.</summary>
public sealed record FilaDeConciliacion(
    int Id,
    int NumeroLinea,
    string Resultado,
    string? SourceId,
    string? ExternalReference,
    string? TipoTransaccion,
    decimal? Importe,
    decimal? Comision,
    decimal? Neto,
    DateTimeOffset? FechaTransaccion,
    DateTimeOffset? FechaLiberacion,
    string? ExternalPosId,
    string? Motivo,
    int? IdCobroMercadoPago,
    int? IdCobro,
    IReadOnlyList<CandidatoDeConfirmacion> Candidatos);

public sealed record DetalleDeConciliacion(
    ConciliacionListado Conciliacion, IReadOnlyList<FilaDeConciliacion> Filas, int Total, int Pagina, int Tamano);

/// <summary>El pago de venta sin verificar (<c>cobros.id_cobro</c>, el que lista <c>GET /api/mercadopago/pagos-sin-verificar</c>) que se
/// confirma contra la fila.</summary>
public sealed record SolicitudDeConfirmacionDeFila(int IdCobro);

public static class CodigosDeErrorDeConciliacion
{
    public const string ArchivoRequerido = "conciliacion_archivo_requerido";

    public const string ArchivoDemasiadoGrande = "conciliacion_archivo_demasiado_grande";

    public const string ReporteInvalido = "conciliacion_reporte_invalido";

    public const string YaImportada = "conciliacion_ya_importada";

    public const string ResultadoInvalido = "conciliacion_resultado_invalido";

    public const string PaginacionInvalida = "conciliacion_paginacion_invalida";

    public const string PagoInvalido = "conciliacion_pago_invalido";

    public const string FilaNoConfirmable = "conciliacion_fila_no_confirmable";

    public const string FilaYaConfirmada = "conciliacion_fila_ya_confirmada";

    public const string PagoNoEstaSinVerificar = "conciliacion_pago_no_esta_sin_verificar";

    public const string PagoYaVerificado = "conciliacion_pago_ya_verificado";
}
