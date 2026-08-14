using Ways.Domain.Stock;

namespace Ways.Application.Stock;

/// <summary>
/// Cuerpo de <c>POST /api/stock/ajustes</c> (design: API Surface; spec: stock / Manual Ajuste
/// Path Is Admin-Only). <see cref="Cantidad"/> es con signo (positiva carga, negativa descarga)
/// y nunca cero (<c>ck_movimientos_stock_cantidad_no_cero</c>). Sin campo de empleado, mismo
/// criterio que <c>Ways.Application.Ventas.SolicitudDeVenta</c>: <c>id_empleado</c> siempre sale
/// del actor autenticado.
/// </summary>
public sealed record SolicitudDeAjusteDeStock(int IdPuntoVenta, int IdArticulo, decimal Cantidad, string? Observaciones);

/// <summary>
/// Balance de <c>GET /api/stock</c> (design: API Surface — "balance for the POS badge").
/// <see cref="Cantidad"/> es <c>0</c> mientras no exista todavía una fila de <c>stock</c> para el
/// par (creación perezosa, mismo criterio que <c>numeraciones_comprobante</c>).
/// </summary>
public sealed record StockActual(int IdPuntoVenta, int IdArticulo, decimal Cantidad);

/// <summary>
/// Cuerpo de <c>POST /api/stock/transferencias</c> (stage-8-compras-transferencias-inventario,
/// Slice 3; design: API Surface; Interfaces/Contracts; decisión 9). <see cref="Observaciones"/>
/// es obligatoria, mismo criterio que <see cref="SolicitudDeAjusteDeStock"/>. Cada
/// <see cref="LineaDeTransferencia"/> lleva una cantidad siempre POSITIVA — el signo por punto de
/// venta (origen negativo, destino positivo) lo decide el servidor, nunca el cliente.
/// </summary>
public sealed record SolicitudDeTransferencia(
    int IdPuntoVentaOrigen, int IdPuntoVentaDestino, string Observaciones,
    IReadOnlyList<LineaDeTransferencia> Lineas);

/// <summary>
/// Etapa 12, slice 10 (design: Write site 3 — "the lot travels"): <see cref="IdLote"/> es
/// opcional para un artículo lote-efectivo — omitido, el servidor lo resuelve vía FEFO en la
/// misma fase de decisión que el checkout (<c>ServicioDeStock.ResolverLineasDeTransferenciaAsync</c>).
/// Para un artículo SIN control de lote efectivo, un <c>idLote</c> no tiene destino y se rechaza
/// (<c>400 lote_invalido</c>) en vez de ignorarse en silencio.
/// </summary>
public sealed record LineaDeTransferencia(int IdArticulo, decimal Cantidad, int? IdLote = null);

/// <summary>Resultado de una transferencia: el stock resultante de cada artículo en AMBOS puntos
/// de venta tras la transacción (design: Transactions — TRANSFERENCIA).</summary>
public sealed record ResultadoTransferencia(
    int IdPuntoVentaOrigen, int IdPuntoVentaDestino, IReadOnlyList<LineaTransferida> Lineas);

public sealed record LineaTransferida(int IdArticulo, decimal CantidadOrigen, decimal CantidadDestino);

/// <summary>
/// Cuerpo de <c>POST /api/stock/conteos</c> (stage-8-compras-transferencias-inventario, Slice 3;
/// design: API Surface; Interfaces/Contracts; decisión 10). <see cref="Contada"/> es el TOTAL
/// físicamente contado — nunca un delta (spec: conteo-de-inventario / Conteo Input Is The Counted
/// Total, Never A Delta): el servidor deriva el ajuste bajo el lock de la fila de <c>stock</c>.
/// </summary>
public sealed record SolicitudDeConteo(int IdPuntoVenta, int IdArticulo, decimal Contada, string Observaciones);

/// <summary>
/// Resultado de <c>POST /api/stock/conteos</c> (stage-8, judgment-day fix: la respuesta anterior
/// (<see cref="StockActual"/>) era idéntica para el no-op de diferencia cero y para la rama que sí
/// escribe un movimiento, así que el cliente no podía distinguir ambos casos sin volver a leer el
/// stock — y esa segunda lectura puede correr después de una venta concurrente y mentir en
/// cualquiera de las dos direcciones. Este contrato lleva la verdad tal como el servidor la
/// escribió, bajo el mismo lock de fila que calculó <see cref="Delta"/>: nunca hace falta que el
/// cliente adivine.
/// </summary>
public sealed record ResultadoConteo(
    int IdPuntoVenta, int IdArticulo, decimal Cantidad, decimal CantidadAnterior, decimal Delta, bool MovimientoRegistrado);

/// <summary>
/// Cuerpo de <c>POST /api/stock/lotes</c> (stage-12-lotes-vencimientos, Slice 3; design: API
/// Surface; Interfaces/Contracts). Alta manual de un lote FECHADO — <see cref="Codigo"/> es
/// opcional (se deriva del vencimiento cuando se omite, <c>ReglaDeLotes.DerivarCodigo</c>) y NO
/// puede ser el código reservado del lote sin identificar (<c>400
/// codigo_de_lote_reservado</c>): ese lote solo lo crea la reconciliación, nunca esta vía.
/// </summary>
public sealed record SolicitudDeLote(int IdArticulo, string? Codigo, DateOnly FechaVencimiento);

/// <summary>
/// Fila de <c>GET /api/stock/lotes</c> y resultado de <c>POST /api/stock/lotes</c>
/// (stage-12-lotes-vencimientos, Slice 3; design decisión 19). <see cref="Sugerido"/> es el pick
/// FEFO server-computed (<c>ReglaDeLotes.ElegirFefo</c>) — el picker del POS lo renderiza, nunca
/// lo recalcula.
/// </summary>
public sealed record LoteListado(
    int IdLote, int IdArticulo, string Codigo, DateOnly? FechaVencimiento, bool EsSinIdentificar,
    decimal Cantidad, EstadoDeVencimiento Estado, bool Sugerido);

/// <summary>
/// Cuerpo de <c>POST /api/stock/lotes/reconciliacion</c> (stage-12-lotes-vencimientos, Slice 4;
/// design: API Surface). Ambos campos son opcionales y acotan el alcance — <c>null</c> en los
/// dos significa "todo el tenant" (<see cref="Stock.ServicioDeLotes.ReconciliarAsync"/> ya filtra
/// a los pares con <c>controla_lote</c> efectivo, así que un re-run amplio es seguro, nunca
/// destructivo: cada par ya reconciliado es un no-op, design decisión 13).
/// </summary>
public sealed record SolicitudDeReconciliacion(int? IdArticulo, int? IdPuntoVenta);

/// <summary>
/// Resultado de <c>POST /api/stock/lotes/reconciliacion</c>. <see cref="ParesReconciliados"/>
/// cuenta los pares <c>(articulo, punto de venta)</c> que efectivamente escribieron el par neto
/// cero de <c>reclasificacion</c> (residuo distinto de cero); <see cref="ParesSinResiduo"/> los
/// que ya estaban al día (residuo cero, no-op — spec: "A second reconciliation run is a no-op").
/// La suma de ambos es el total de pares dentro del alcance pedido.
/// </summary>
public sealed record ResultadoDeReconciliacion(int ParesReconciliados, int ParesSinResiduo);
