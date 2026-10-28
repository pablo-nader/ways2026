namespace Ways.Application.Pagos;

/// <summary>Una parte del pago que se aplica a una compra.</summary>
public sealed record AplicacionSolicitada(int IdComprobanteCompra, decimal Importe);

/// <summary>Parte del disponible de un pago ya existente que se aplica a la compra que se está pagando.</summary>
public sealed record AplicacionDePagoExistente(int IdPago, decimal Importe);

/// <summary>Comando interno de pagar una compra con líneas nuevas, pagos existentes o ambos.</summary>
public sealed record ComandoDePagoDeCompraConAplicaciones(
    DateOnly Fecha,
    IReadOnlyList<Ways.Domain.Pagos.LineaDePago>? Lineas,
    string? Concepto,
    IReadOnlyList<AplicacionDePagoExistente>? Aplicaciones);

/// <summary>Cuerpo de <c>POST /api/pagos/{id}/aplicaciones</c>: de 1 a 20 compras de UN mismo proveedor, cada una una vez.
/// Todo campo se usa: <c>idComprobanteCompra</c> elige la compra y <c>importe</c> es lo que se aplica.</summary>
public sealed record SolicitudDeAplicarPago(IReadOnlyList<AplicacionSolicitada>? Aplicaciones);

/// <summary>Un pago con importe disponible que se puede aplicar. <c>idProveedor</c> es <c>null</c> en un pago sin
/// proveedor (de caja, «en concepto de»), que adopta el proveedor de la compra la primera vez que se aplica.</summary>
public sealed record PagoAplicable(
    int IdPago,
    DateTimeOffset Fecha,
    string Concepto,
    decimal Importe,
    decimal Disponible,
    int? IdProveedor);

/// <summary>Respuesta de <c>GET /api/compras/{id}/pagos-aplicables</c> y <c>GET /api/proveedores/{id}/pagos-disponibles</c>.</summary>
public sealed record PagosAplicables(IReadOnlyList<PagoAplicable> Pagos);
