namespace Ways.Application.Pagos;

/// <summary>Lo que un pago de personal necesita saber de su destino, resuelto antes de abrir la transacción:
/// la empresa del pago, el punto de venta (obligatorio solo con el cajón), el turno abierto cuando el origen es
/// el cajón, el día del pago, la fecha de negocio del pago de tesorería y el nombre de cada medio de pago usado.</summary>
public sealed record EntornoDePagoDeSueldo(
    int IdEmpresa,
    int? IdPuntoVenta,
    int? IdTurnoCaja,
    DateOnly FechaDelPago,
    DateTimeOffset FechaDeNegocio,
    IReadOnlyDictionary<int, string> NombresDeMedios);
