using Ways.Domain.Reportes;

namespace Ways.Application.Reportes;

/// <summary>
/// Un punto de la evolución de valoración. <see cref="Fecha"/> es el instante de corte: todo se calcula con lo ocurrido hasta ese
/// instante inclusive (movimientos de stock, precios vigentes, compras y fuentes de tesorería); el último punto es el ahora si el
/// rango llega a hoy. La mercadería suma solo stock positivo (cada par artículo-sucursal se acota a cero). Un artículo sin costo o
/// sin precio no se valora en cero: se cuenta en <see cref="ArticulosSinCosto"/> / <see cref="ArticulosSinPrecio"/>.
/// <see cref="ArticulosConCostoEstimado"/> y <see cref="ValorCostoEstimado"/> cuentan lo valorado con el costo actual por falta de
/// una compra anterior al corte (el punto actual usa el costo nominal vigente y no estima). <see cref="Efectivo"/> es la caja
/// general y no incluye el efectivo del cajón de un turno abierto, igual que el libro de tesorería.
/// </summary>
public sealed record PuntoDeValoracion(
    DateTimeOffset Fecha,
    decimal MercaderiaCosto,
    decimal MercaderiaVenta,
    decimal Efectivo,
    decimal Cuentas,
    decimal Dinero,
    decimal TotalACosto,
    decimal TotalAVenta,
    int ArticulosSinCosto,
    int ArticulosSinPrecio,
    int ArticulosConCostoEstimado,
    decimal ValorCostoEstimado);

public sealed record CuentaDeValoracion(int IdCuenta, string Nombre, decimal Saldo);

/// <summary><see cref="IdArea"/> es <c>null</c> para "Sin área": artículos sin área o cuya área fue dada de baja.</summary>
public sealed record AreaDeValoracion(int? IdArea, string Nombre, decimal MercaderiaCosto, decimal MercaderiaVenta);

/// <summary>Detalle del último punto. <see cref="IdListaPrecio"/> es la lista con que se valoró a precio de venta (la
/// predeterminada de la empresa, o la compartida); <c>null</c> si no hay ninguna.</summary>
public sealed record DetalleActualDeValoracion(
    IReadOnlyList<CuentaDeValoracion> PorCuenta,
    IReadOnlyList<AreaDeValoracion> PorArea,
    int? IdListaPrecio,
    string? NombreListaPrecio);

public sealed record ValoracionDeInventario(
    int IdEmpresa,
    DateOnly Desde,
    DateOnly Hasta,
    Granularidad Granularidad,
    string ZonaHoraria,
    IReadOnlyList<PuntoDeValoracion> Puntos,
    DetalleActualDeValoracion Actual);
