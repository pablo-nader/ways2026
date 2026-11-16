using Ways.Domain.Common;
using Ways.Domain.Dinero;

namespace Ways.Domain.Ventas;

/// <summary>Un item de la venta original (cantidad positiva), con lo que se cobró por él: <see cref="Total"/> ya incluye
/// descuentos de línea, ofertas, la parte del combo y el ajuste manual. <see cref="IdLote"/> es el del snapshot del item.</summary>
public readonly record struct ItemVendido(
    int IdItem, int Orden, int IdArticulo, bool PorImporte, decimal Cantidad, decimal PrecioUnitario, decimal Total,
    int? IdCombo, int? IdOferta, int IdListaPrecio, int IdAlicuotaIva, decimal PorcentajeIva, int? IdLote);

/// <summary>Lo que ya devolvieron las notas de crédito emitidas de la misma venta. Con <see cref="IdItemOrigen"/> es lo devuelto de
/// UN item de la venta: <see cref="Cantidad"/> positiva y <see cref="Credito"/> positivo, lo que esas notas acreditaron de verdad
/// (suma de sus items guardados). Sin <see cref="IdItemOrigen"/> (notas anteriores a esa columna) es lo devuelto de un artículo, o de
/// un importe en un artículo por importe, y solo cuentan las unidades: se asignan a los items del conjunto en orden y el crédito se
/// reconstruye repartiéndolas, así que <see cref="Credito"/> se ignora.</summary>
public readonly record struct UnidadesYaDevueltas(
    int IdArticulo, bool PorImporte, decimal PrecioUnitario, decimal Cantidad, decimal Credito = 0m, int? IdItemOrigen = null);

/// <summary>Una línea que la nota de crédito devuelve: el artículo y las unidades (positivas). <see cref="Importe"/> solo
/// lo lleva la línea de un artículo por importe: el precio con que se vendió.</summary>
public readonly record struct LineaDevuelta(int IdArticulo, decimal Cantidad, decimal? Importe = null);

/// <summary>Las unidades de una línea devuelta que salen de UN item de la venta original, con el importe que se devuelve por
/// ellas (<see cref="Credito"/>, positivo).</summary>
public readonly record struct TramoDeDevolucion(ItemVendido Origen, decimal Cantidad, decimal Credito);

/// <summary>Lo que queda por devolver de un artículo de la venta original (o de un importe, en un artículo por importe).
/// <see cref="ImporteDeLoRestante"/> es lo que se acredita por TODAS las unidades <see cref="Devolvible"/>s y
/// <see cref="ImporteUnitario"/> el valor por unidad del primer item con unidades pendientes (informativo: lo que se acredita
/// de verdad lo fija <see cref="DevolucionDeTicket.Repartir"/>).</summary>
public readonly record struct SaldoDeDevolucion(
    int IdArticulo, bool PorImporte, decimal PrecioUnitario, decimal Vendida, decimal Devuelta, decimal Devolvible,
    decimal ImporteDeLoRestante, decimal ImporteUnitario);

/// <summary>
/// Reparte las unidades que devuelve una nota de crédito entre los items de la venta original, para devolver lo que se
/// cobró y no lo que cuesta hoy, y las limita a lo que la venta vendió menos lo que otras notas de crédito emitidas de la
/// misma venta ya devolvieron. Pura, sin base de datos.
///
/// <para>Cada artículo es un conjunto de unidades: los items del mismo artículo (en un artículo por importe, los del mismo
/// precio de importe). Para cada línea devuelta, en el orden en que viene, se toman unidades de los items del conjunto en
/// orden de <see cref="ItemVendido.Orden"/> (y de <see cref="ItemVendido.IdItem"/> si empatan) hasta agotar las de cada uno.
/// Lo ya devuelto por las notas anteriores se descuenta item por item: cada item de una nota emitida guarda de qué item de la venta
/// salió (<c>id_item_comprobante_venta_origen</c>), y las unidades y el crédito de cada item de la venta son la suma de esos items
/// guardados, no una repetición del reparto; anular una nota intermedia libera sus unidades y su crédito sin mover los de las demás.
/// Las notas sin esa columna (anteriores a ella) consumen en orden los items de su conjunto, como antes. Una línea que pide más de lo que queda no se acepta: 409
/// <c>devolucion_excede_lo_vendido</c>.</para>
///
/// <para>Importe: se acredita por unidades acumuladas. El crédito de una toma es
/// <c>round(Total × (unidades ya tomadas + unidades de esta toma) / Cantidad)</c> al peso entero
/// (<see cref="RedondeoDePeso"/>) menos lo ya acreditado de ese item. Devolver TODAS las unidades de un item acredita
/// exactamente su <c>Total</c> y, como el redondeo es monótono, ninguna toma resulta negativa ni la suma supera el
/// <c>Total</c>, sea cual sea el reparto en líneas o en notas de crédito: el reparto nunca acredita menos de cero ni más de lo
/// que le falta al item para su <c>Total</c>. El <c>Total</c> del item es lo que se cobró por él
/// (incluidos oferta, combo y ajuste manual), así que el precio de lista de hoy no interviene.</para>
/// </summary>
public static class DevolucionDeTicket
{
    public static IReadOnlyList<IReadOnlyList<TramoDeDevolucion>> Repartir(
        IReadOnlyList<ItemVendido> vendidos,
        IReadOnlyList<UnidadesYaDevueltas> yaDevueltas,
        IReadOnlyList<LineaDevuelta> devueltas)
    {
        var estado = new EstadoDeReparto(vendidos, yaDevueltas);
        var resultado = new List<IReadOnlyList<TramoDeDevolucion>>(devueltas.Count);

        foreach (var devuelta in devueltas)
        {
            var clave = new ClaveDeConjunto(devuelta.IdArticulo, devuelta.Importe);
            var tramos = new List<TramoDeDevolucion>();
            var faltan = devuelta.Cantidad;

            foreach (var item in estado.ItemsDe(clave))
            {
                if (faltan <= 0m)
                {
                    break;
                }

                var disponibles = estado.Restantes[item.IdItem];
                if (disponibles <= 0m)
                {
                    continue;
                }

                var unidades = Math.Min(faltan, disponibles);
                var credito = estado.Tomar(item, unidades);
                faltan -= unidades;
                tramos.Add(new TramoDeDevolucion(item, unidades, credito));
            }

            if (faltan > 0m)
            {
                throw new ErrorDominio(
                    "devolucion_excede_lo_vendido",
                    $"El artículo {devuelta.IdArticulo} no admite devolver {devuelta.Cantidad}: el ticket original " +
                    $"permite devolver {devuelta.Cantidad - faltan} más.",
                    409);
            }

            resultado.Add(tramos);
        }

        return resultado;
    }

    /// <summary>Lo vendido, lo ya devuelto y lo que queda por devolver de cada artículo (o importe) de la venta, en orden de
    /// aparición del primer item. Los items con cantidad no positiva no cuentan.</summary>
    public static IReadOnlyList<SaldoDeDevolucion> Saldos(
        IReadOnlyList<ItemVendido> vendidos, IReadOnlyList<UnidadesYaDevueltas> yaDevueltas)
    {
        var estado = new EstadoDeReparto(vendidos, yaDevueltas);
        var saldos = new List<SaldoDeDevolucion>();

        foreach (var clave in estado.Claves)
        {
            var items = estado.ItemsDe(clave);
            var vendida = items.Sum(i => i.Cantidad);
            var devolvible = items.Sum(i => estado.Restantes[i.IdItem]);
            var primero = items.FirstOrDefault(i => estado.Restantes[i.IdItem] > 0m);
            var unitario = primero.Cantidad > 0m ? Math.Round(primero.Total / primero.Cantidad, 2, MidpointRounding.AwayFromZero) : 0m;

            var restante = 0m;
            foreach (var item in items)
            {
                if (estado.Restantes[item.IdItem] > 0m)
                {
                    restante += estado.Tomar(item, estado.Restantes[item.IdItem]);
                }
            }

            saldos.Add(new SaldoDeDevolucion(
                clave.IdArticulo, clave.Importe is not null, clave.Importe ?? 0m, vendida, vendida - devolvible, devolvible,
                restante, unitario));
        }

        return saldos;
    }

    private readonly record struct ClaveDeConjunto(int IdArticulo, decimal? Importe);

    private sealed class EstadoDeReparto
    {
        private readonly Dictionary<int, decimal> _tomadas = [];
        private readonly Dictionary<int, decimal> _acreditado = [];
        private readonly Dictionary<ClaveDeConjunto, List<ItemVendido>> _porConjunto = [];

        public Dictionary<int, decimal> Restantes { get; } = [];

        public List<ClaveDeConjunto> Claves { get; } = [];

        public EstadoDeReparto(IReadOnlyList<ItemVendido> vendidos, IReadOnlyList<UnidadesYaDevueltas> yaDevueltas)
        {
            foreach (var item in vendidos.Where(v => v.Cantidad > 0m).OrderBy(v => v.Orden).ThenBy(v => v.IdItem))
            {
                var clave = new ClaveDeConjunto(item.IdArticulo, item.PorImporte ? item.PrecioUnitario : null);
                if (!_porConjunto.TryGetValue(clave, out var items))
                {
                    items = [];
                    _porConjunto[clave] = items;
                    Claves.Add(clave);
                }

                items.Add(item);
                Restantes[item.IdItem] = item.Cantidad;
                _tomadas[item.IdItem] = 0m;
                _acreditado[item.IdItem] = 0m;
            }

            // Primero lo que dice de qué item salió: es la verdad guardada. Después las notas sin esa columna, que se reparten
            // en orden sobre lo que quedó.
            foreach (var devueltas in yaDevueltas.Where(d => d.IdItemOrigen is { } id && Restantes.ContainsKey(id)))
            {
                var idItem = devueltas.IdItemOrigen!.Value;
                var unidades = Math.Min(devueltas.Cantidad, Restantes[idItem]);

                Restantes[idItem] -= unidades;
                _tomadas[idItem] += unidades;
                _acreditado[idItem] += devueltas.Credito;
            }

            foreach (var devueltas in yaDevueltas.Where(d => d.IdItemOrigen is not { } id || !Restantes.ContainsKey(id)))
            {
                var clave = new ClaveDeConjunto(devueltas.IdArticulo, devueltas.PorImporte ? devueltas.PrecioUnitario : null);
                var faltan = devueltas.Cantidad;

                foreach (var item in ItemsDe(clave))
                {
                    if (faltan <= 0m)
                    {
                        break;
                    }

                    var unidades = Math.Min(faltan, Restantes[item.IdItem]);
                    if (unidades > 0m)
                    {
                        Tomar(item, unidades);
                        faltan -= unidades;
                    }
                }
            }
        }

        public IReadOnlyList<ItemVendido> ItemsDe(ClaveDeConjunto clave) =>
            _porConjunto.TryGetValue(clave, out var items) ? items : [];

        /// <summary>Toma unidades del item y devuelve lo que se acredita por ellas: el acumulado menos lo ya acreditado, sin bajar de cero
        /// (lo acreditado por las notas que sobreviven a una anulación puede superar al acumulado de las unidades tomadas) ni pasar de
        /// lo que le falta al item para su Total.</summary>
        public decimal Tomar(ItemVendido item, decimal unidades)
        {
            var tomadas = _tomadas[item.IdItem] + unidades;
            var yaAcreditado = _acreditado[item.IdItem];
            var credito = Math.Clamp(
                RedondeoDePeso.APesoEntero(item.Total * tomadas / item.Cantidad) - yaAcreditado,
                0m,
                Math.Max(0m, item.Total - yaAcreditado));

            Restantes[item.IdItem] -= unidades;
            _tomadas[item.IdItem] = tomadas;
            _acreditado[item.IdItem] += credito;

            return credito;
        }
    }
}
