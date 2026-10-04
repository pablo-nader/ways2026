using System.Text.Json.Serialization;
using Ways.Application.Ofertas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;

namespace Ways.Application.Pos;

/// <summary>
/// Precio resuelto de un artículo en UNA lista de precios, dentro de
/// <see cref="ArticuloDeInstantanea.PreciosPorLista"/>. Mismo shape que
/// <see cref="ResultadoDeResolucion"/>: el resultado YA RESUELTO de
/// <c>ServicioDeOfertas.ResolverConEscalonesAsync</c> a <c>cantidad = 1</c> contra esa lista y la
/// empresa del punto de venta. El dispositivo nunca vuelve a evaluar ofertas (decisión del dueño).
///
/// <see cref="Escalones"/> es la curva de precio por cantidad: un escalón por cada
/// <c>cantidadMinima &gt; 1</c> que cambia el resultado, ascendente por
/// <see cref="EscalonDeCantidad.CantidadDesde"/>; el dispositivo solo elige el último escalón cuyo
/// umbral entra en la cantidad del carrito. Sin escalones viaja <c>null</c> y el
/// <see cref="JsonIgnoreAttribute"/> deja la clave ausente: la mayoría de los artículos no tiene
/// oferta por volumen y repetir <c>"escalones":null</c> por artículo y por lista solo engorda el
/// payload. Que llegue <c>null</c> y no <c>[]</c> lo garantiza
/// <see cref="ArmadorDeInstantanea.ArmarArticulos"/>.
/// </summary>
public sealed record PrecioDeListaDeInstantanea(
    int IdListaPrecio,
    decimal PrecioOriginal,
    decimal PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicadaDto> Aplicadas,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EscalonDeCantidad>? Escalones = null);

/// <summary>
/// Un artículo dentro de <see cref="InstantaneaDePos"/>: identidad, códigos, IVA y su precio en
/// cada lista resuelta (<see cref="PreciosPorLista"/>, ascendente por lista). Un artículo sin
/// precio vigente en una lista no tiene entrada para esa lista; sin precio en ninguna, el artículo
/// no viaja (online ya rechazaría <c>articulo_sin_precio_vigente</c>). <see cref="UnidadVenta"/>
/// le dice al carrito offline si la línea admite fracciones; es parte del contenido de la
/// etiqueta de <see cref="InstantaneaDePos"/>, así que un dispositivo con una instantánea anterior
/// recibe el cambio como contenido nuevo. <c>IdArea</c>,
/// <c>EsProducto</c> y <c>ControlaLote</c> quedan afuera: el servidor los resuelve del artículo
/// fresco al sincronizar (<c>ServicioDeVentas.MaterializarItems</c>), nunca del request.
/// </summary>
public sealed record ArticuloDeInstantanea(
    int IdArticulo,
    string CodigoInterno,
    string Nombre,
    IReadOnlyList<string> CodigosBarra,
    int IdAlicuotaIva,
    decimal PorcentajeIva,
    bool AcumulaEnVenta,
    UnidadVenta UnidadVenta,
    IReadOnlyList<PrecioDeListaDeInstantanea> PreciosPorLista);

/// <summary>
/// Un cliente visible para el punto de venta: activo y compartido o de la empresa del punto de
/// venta, más el Consumidor Final siempre. <see cref="IdListaPrecio"/> es <c>null</c> cuando la
/// lista del cliente está dada de baja: una FK a una fila dada de baja se trata igual que una FK
/// nula. <see cref="Saldo"/> es el caché de cuenta corriente al momento de la instantánea.
/// </summary>
public sealed record ClienteDeInstantanea(
    int IdCliente,
    int Numero,
    string Nombre,
    string? Apellido,
    string? RazonSocial,
    TipoDocumento? TipoDocumento,
    string? NumeroDocumento,
    int IdCondicionFiscal,
    int? IdEmpresa,
    int? IdListaPrecio,
    bool EsConsumidorFinal,
    decimal Saldo,
    decimal LimiteCredito,
    bool CreditoIlimitado);

/// <summary>
/// Respuesta de <c>GET /api/pos/instantanea?version=2</c>: todo lo que el checkout de un punto de
/// venta de escritorio necesita para cotizar y vender SIN red, para SU PROPIO punto de venta
/// (<c>PoliticaDeModoDePuntoVenta</c>, reusada tal cual). Completa y sin paginar: cada precio quedó
/// resuelto contra el mismo <see cref="Momento"/>, nunca contra instantes distintos de páginas
/// sucesivas. <see cref="Momento"/> queda fuera de la etiqueta de contenido
/// (<see cref="EtiquetaDeInstantanea"/>), así que dos pedidos con el mismo contenido comparten ETag
/// y el segundo puede responder <c>304</c>.
/// </summary>
public sealed record InstantaneaDePos(
    DateTimeOffset Momento,
    int IdPuntoVenta,
    IReadOnlyList<ArticuloDeInstantanea> Articulos,
    IReadOnlyList<ClienteDeInstantanea> Clientes,
    IReadOnlyList<MedioPagoDeInstantanea> MediosDePago,
    decimal ToleranciaPago);

/// <summary>
/// Artículo del formato original de la instantánea (pedido sin <c>?version=2</c>): precio plano
/// de la lista del Consumidor Final. Lo siguen pidiendo los POS de escritorio ya instalados, que
/// traen la página bundleada y no se actualizan solos — un artículo sin estos campos les haría
/// cobrar importes indefinidos. Ver <see cref="ArmadorDeInstantanea.ProyectarLegada"/>.
/// </summary>
public sealed record ArticuloDeInstantaneaLegada(
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

/// <summary>Formato original de <c>GET /api/pos/instantanea</c> (sin <c>?version=2</c>), byte por
/// byte el que leen los POS de escritorio ya instalados.</summary>
public sealed record InstantaneaLegadaDePos(
    DateTimeOffset Momento,
    int IdPuntoVenta,
    IReadOnlyList<ArticuloDeInstantaneaLegada> Articulos,
    IReadOnlyList<MedioPagoDeInstantanea> MediosDePago,
    decimal ToleranciaPago);

/// <summary>
/// Cuerpo de <c>POST /api/pos/rendicion-de-cola</c> — el dispositivo declara el estado de su cola
/// local para que el cierre de turno pueda verificarlo (ver
/// <see cref="Ways.Domain.Ventas.ReglaDeRendicionDeCola"/>). Sin <c>idPuntoVenta</c> ni
/// <c>idDispositivo</c> a propósito, mismo criterio que <see cref="InstantaneaDePos"/>: los dos
/// salen de <c>IContextoDeUsuario.IdDispositivo</c>, nunca del request.
///
/// <see cref="EntregadoHasta"/> es el número más alto que el dispositivo ya le imprimió a un
/// cliente (<c>proximo - 1</c> de su puntero local); <c>desde - 1</c> del bloque vivo ⇒ todavía no
/// repartió ninguno. <see cref="Pendientes"/> es cuántas de esas ventas no llegaron al servidor
/// (outbox + rechazadas) — existe para que el rechazo del cierre pueda decir CUÁNTAS faltan.
/// </summary>
public sealed record SolicitudDeRendicionDeCola(string CodigoTipoComprobante, long EntregadoHasta, int Pendientes);

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
