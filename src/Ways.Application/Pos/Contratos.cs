using System.Text.Json.Serialization;
using Ways.Application.Ofertas;
using Ways.Domain.Catalogos;

namespace Ways.Application.Pos;

/// <summary>
/// Un artículo dentro de <see cref="InstantaneaDePos"/> (stage-pos-venta-offline-backend). Deriva
/// EXACTAMENTE lo que <c>Pos.tsx</c> lee hoy del checkout online — nunca un campo "por si acaso":
/// <see cref="IdArea"/> queda afuera a propósito porque <c>ServicioDeVentas.MaterializarItems</c>
/// lo resuelve del artículo FRESCO en el sync, nunca del request; lo mismo <c>EsProducto</c>/
/// <c>ControlaLote</c> (el default FEFO ya cubre a "un cliente que ni siquiera conoce el campo",
/// ver <c>LineaDeVenta.IdLote</c>).
///
/// <see cref="PrecioOriginal"/>/<see cref="PrecioFinal"/>/<see cref="DescuentoUnitario"/>/
/// <see cref="Aplicadas"/> son el mismo shape que <see cref="ResultadoDeResolucion"/> — el
/// resultado YA RESUELTO de <c>ServicioDeOfertas.ResolverConEscalonesAsync</c> a
/// <c>cantidad = 1</c>, nunca el motor de reglas reimplementado en TypeScript (decisión del dueño,
/// rechazada explícitamente: el dispositivo nunca vuelve a evaluar ofertas offline).
///
/// <see cref="Escalones"/> es la tabla de quiebres por cantidad del artículo, un escalón por cada
/// <c>cantidadMinima > 1</c> que CAMBIA el resultado, ascendente por
/// <see cref="EscalonDeCantidad.CantidadDesde"/> y calculada por el motor real en el servidor
/// (<c>Ways.Domain.Ofertas.EscalonesDeCantidad</c>): el dispositivo solo elige el último escalón
/// cuyo umbral entra en la cantidad del carrito, nunca reimplementa el matching. Así una oferta
/// por volumen SÍ se cobra offline — la limitación que este contrato documentaba antes (precio
/// congelado a cantidad unitaria) ya no existe.
///
/// La entrada de cantidad 1 NO viaja en <see cref="Escalones"/>: ESA es exactamente la que llevan
/// los campos planos de arriba, y repetirla sería la misma información dos veces. Por eso
/// <see cref="Escalones"/> ausente/<c>null</c>/vacío significa "el artículo no tiene ningún
/// escalón" y los campos planos aplican a CUALQUIER cantidad — byte por byte el comportamiento
/// anterior a esta etapa, que es lo que mantiene válido el snapshot de un dispositivo que quedó
/// offline cruzando el deploy (el IndexedDB del dispositivo no tiene versión de esquema ni
/// validación). El <see cref="JsonIgnoreAttribute"/> sostiene esa compatibilidad del lado del
/// payload: sin escalones la clave queda AUSENTE, no <c>"escalones":null</c> repetido ~6000 veces.
/// Ojo con el reparto de responsabilidades, porque el atributo NO cubre la lista vacía
/// (<see cref="JsonIgnoreCondition.WhenWritingNull"/> solo omite <c>null</c>): que un artículo sin
/// escalones llegue acá como <c>null</c> y no como <c>[]</c> lo garantiza el propio llamador
/// (<c>ServicioDeInstantaneaDePos</c>, <c>escalones.Count == 0 ? null : escalones</c>). Los tres
/// casos significan lo mismo para el dispositivo, así que un <c>[]</c> que se escapara sería ruido
/// de payload, nunca un precio mal cobrado.
///
/// Solo artículos con precio vigente HOY en la lista de <see cref="ReglaDeClientes.
/// NumeroConsumidorFinal"/> (mismo motivo que <c>MaterializarItems</c>: un artículo sin precio
/// vigente ya rechaza 400 <c>articulo_sin_precio_vigente</c> en el camino online, así que
/// ofrecerlo offline sin poder cobrarlo no tiene sentido) — <see cref="PrecioOriginal"/>/
/// <see cref="PrecioFinal"/> nunca son null en la lista que devuelve el servicio.
/// </summary>
public sealed record ArticuloDeInstantanea(
    int IdArticulo,
    string CodigoInterno,
    string Nombre,
    IReadOnlyList<string> CodigosBarra,
    decimal PrecioOriginal,
    decimal PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicadaDto> Aplicadas,
    int IdAlicuotaIva,
    decimal PorcentajeIva,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EscalonDeCantidad>? Escalones = null);

/// <summary>
/// Respuesta de <c>GET /api/pos/instantanea</c> (stage-pos-venta-offline-backend, Parte A) — todo
/// lo que el checkout de un punto de venta de escritorio necesita para vender SIN red, para SU
/// PROPIO punto de venta (<c>PoliticaDeModoDePuntoVenta</c>, reusada tal cual — nunca
/// reimplementada). Instantánea COMPLETA, no paginada (ver el doc-comment de
/// <c>ServicioDeInstantaneaDePos.ObtenerAsync</c> para la justificación) — <see cref="Momento"/> es
/// el único invariante real: cada artículo de <see cref="Articulos"/> quedó resuelto contra el
/// MISMO instante, nunca contra instantes distintos de páginas sucesivas. El dispositivo la
/// vuelve a pedir cada vez que tiene señal — la vejez del último pull exitoso, mostrada al
/// operador con <see cref="Momento"/>, acota el riesgo de precio, no el tamaño del corte offline.
/// </summary>
public sealed record InstantaneaDePos(
    DateTimeOffset Momento,
    int IdPuntoVenta,
    IReadOnlyList<ArticuloDeInstantanea> Articulos,
    IReadOnlyList<MedioPagoDeInstantanea> MediosDePago,
    decimal ToleranciaPago);

/// <summary>Recorte de <c>MedioPagoListado</c> (catálogo genérico) a lo que el checkout offline
/// necesita para armar <c>PagoDeVenta</c> y decidir vuelto/referencia sin ida y vuelta — mismo
/// criterio que <c>ComprobanteListado</c> vs. el detalle completo: nunca <c>IdEmpresa</c>/
/// <c>Orden</c>/<c>RecargoPorcentaje</c>, que el checkout no lee.</summary>
public sealed record MedioPagoDeInstantanea(
    int IdMedioPago,
    string Nombre,
    ComportamientoMedioPago Comportamiento,
    bool AdmiteVuelto,
    bool RequiereReferencia);
