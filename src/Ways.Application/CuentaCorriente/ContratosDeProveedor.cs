using Ways.Domain.CuentaCorriente;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// DTOs del estado de cuenta de proveedores (design.md:139-149, task 4.2) —
/// <c>dto-contract-honesty</c>: cada campo se lee en algún lado, ninguno se acepta y descarta
/// (task 4.18). Distinta de <c>Ways.Application.Compras.SaldoDeProveedor</c> (Slice 4, /saldo):
/// esta es la lectura PAGINADA del ledger completo, aquella el resumen por-compra
/// (design decisión 9 — dos read models a propósito, ninguno amplía al otro).
/// </summary>
public sealed record MovimientoDeCuentaDeProveedor(
    int IdMovimiento, DateTimeOffset Fecha, TipoMovimientoCcProveedor Tipo, decimal Importe,
    decimal SaldoResultante, string? Detalle, int? IdComprobanteCompra, int? IdGasto,
    EtiquetaDeAjuste? Etiqueta);

/// <summary><see cref="Saldo"/> viene de <c>proveedores.saldo</c> (la caché de
/// <c>EscriturasDeCuentaCorrienteProveedor</c>) — NUNCA re-derivado de los movimientos de esta
/// misma página (design decisión 11).</summary>
public sealed record EstadoDeCuentaDeProveedorHeader(int IdProveedor, decimal Saldo);

/// <summary>Forma PAGINADA (design decisión 10 / <c>state.yaml</c> OD9 — reconciliación de tasks.md
/// decisión 5): <c>OFFSET</c>, desempate <c>id_movimiento DESC</c>, <c>Total</c> es el
/// <c>COUNT(*)</c> sin paginar que habilita "Página N de M" en el web (Slice 6).</summary>
public sealed record PaginaDeEstadoDeCuentaDeProveedor(
    EstadoDeCuentaDeProveedorHeader Header, IReadOnlyList<MovimientoDeCuentaDeProveedor> Items,
    int Total, int Pagina, int Tamanio, bool Historico, DateTimeOffset? Desde, DateTimeOffset? Hasta);
