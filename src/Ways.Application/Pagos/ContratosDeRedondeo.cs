namespace Ways.Application.Pagos;

/// <summary>Respuesta de <c>POST /api/compras/{id}/ajuste-de-redondeo</c>: <c>redondeo</c> es lo que se perdonó de la compra
/// (positivo), <c>pagado</c> ya lo incluye y el saldo pendiente queda en 0.</summary>
public sealed record ResultadoDeRedondeoDeCompra(int IdCompra, decimal Redondeo, decimal Pagado, decimal SaldoPendiente);

/// <summary>Respuesta de <c>POST /api/pagos/{id}/ajuste-de-redondeo</c>: <c>redondeo</c> es lo que se absorbió del disponible
/// del pago (positivo) y <c>disponible</c> queda en 0.</summary>
public sealed record ResultadoDeRedondeoDePago(int IdPago, decimal Redondeo, decimal Disponible);
