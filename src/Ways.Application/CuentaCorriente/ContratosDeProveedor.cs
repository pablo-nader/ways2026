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

/// <summary>
/// Cuerpo de <c>POST /api/proveedores/{id}/cuenta-corriente/ajustes</c> (design.md: API Surface;
/// Transactions — AJUSTE MANUAL; decisión 15). Deliberadamente SIN <c>tipo</c> ni
/// <c>saldoResultante</c> — <c>tipo</c> porque <c>apertura</c> es la única otra forma del enum y
/// solo la migración la escribe (un campo que solo puede valer una cosa legal no debería existir,
/// <c>dto-contract-honesty</c> rule 1); <c>saldoResultante</c> porque es SIEMPRE derivado del
/// <c>RETURNING</c> del writer, nunca aceptado del cliente (ningún endpoint de esta etapa acepta
/// un saldo o un delta ya calculado). <see cref="Importe"/> viaja con signo, decidido por el
/// llamador (spec: Manual Ajuste — importe con signo); <see cref="Detalle"/> es obligatorio
/// (<see cref="Domain.CuentaCorriente.ReglaDeAjusteDeCuenta"/>).
/// </summary>
public sealed record SolicitudDeAjusteDeProveedor(int IdPuntoVenta, decimal Importe, string? Detalle);
