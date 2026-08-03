namespace Ways.Application.Precios;

/// <summary>Establece el precio vigente de un artículo en una lista <c>fija</c>, efectivo
/// AHORA (<c>ServicioDePrecios.EstablecerPrecioAsync</c> resuelve <c>vigente_desde</c> desde el
/// reloj del sistema, nunca desde el cliente). <see cref="ConfirmarReemplazo"/> solo importa
/// cuando ya existe un precio PENDIENTE (programado a futuro) para el mismo par — sin
/// confirmación, esa fila pendiente se conserva y el alta se rechaza con
/// <c>precio_pendiente_existe</c> (spec: Programmable Future Prices, At Most One Pending; design
/// decision 4). Sin <c>Id</c>: no existe edición de una fila existente — el único camino de
/// escritura es abrir una fila nueva (design decision 3, "precios never has an entity-level
/// Update").</summary>
public record AltaPrecio(int IdListaPrecio, decimal Precio, bool ConfirmarReemplazo = false);

/// <summary>Programa un precio a futuro (spec: Programmable Future Prices) — <see
/// cref="VigenteDesde"/> tiene que ser una fecha futura (con una tolerancia de desfasaje de
/// reloj, ver <c>ServicioDePrecios.ToleranciaReloj</c>); si ya hay un precio pendiente para el
/// mismo par, <see cref="ConfirmarReemplazo"/> en <c>true</c> lo reemplaza, en <c>false</c>
/// rechaza con <c>precio_pendiente_existe</c> (409).</summary>
public record ProgramarPrecio(int IdListaPrecio, decimal Precio, DateTimeOffset VigenteDesde, bool ConfirmarReemplazo = false);

/// <summary>Precio resuelto de un artículo en una lista a una fecha dada (spec: Current-Price
/// Query Semantics By Date, Derived List Price Resolution At Read Time) — <see cref="Precio"/>
/// es <c>null</c> cuando no hay ninguna fila vigente a esa fecha (artículo sin precio cargado
/// todavía en esa lista, o lista derivada cuya base tampoco tiene precio a esa fecha).</summary>
public record PrecioVigente(int IdArticulo, int IdListaPrecio, decimal? Precio, DateTimeOffset Fecha);

/// <summary>Una fila de historial (spec: Price History Never Overwrites) — solo existe para
/// listas <c>fija</c>; una lista <c>derivada</c> nunca tiene filas propias en <c>precios</c>.
/// <see cref="VigenteHasta"/> <c>null</c> ⇒ es la fila actualmente abierta (vigente o
/// pendiente, según su propio <see cref="VigenteDesde"/> contra "ahora").</summary>
public record HistorialDePrecio(int Id, decimal Precio, DateTimeOffset VigenteDesde, DateTimeOffset? VigenteHasta);
