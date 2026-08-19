using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Línea de un <see cref="Presupuesto"/> (proposal: Modelo de datos propuesto — §D). Child
/// scope: <c>id_tenant</c> únicamente, sin FK propia a <c>puntos_venta</c> — se deriva del
/// presupuesto padre, criterio verbatim de <c>ItemComprobanteVentaConfiguration.cs:13-15</c>.
/// <c>EntidadBase</c>: SÍ, como <c>ItemComprobanteVenta</c>/<c>ItemOrdenCompra</c> — el
/// replace-set del borrador las reescribe físicamente.
///
/// Deliberadamente MÁS ANGOSTA que <see cref="ItemComprobanteVenta"/> (proposal §D): sin
/// <c>IdArea</c>/<c>CodigoBarra</c> (ambos son atributos del artículo que la conversión
/// re-lee, que igual tiene que cargar), sin <c>CostoUnitario</c>/<c>CostoEsEstimado</c> (un
/// presupuesto nunca congela un costo — decisión 4 del proposal), sin <c>IdLote</c> (nada se
/// reserva — decisión 5 del proposal).
/// </summary>
public class ItemPresupuesto : EntidadTenant
{
    public int Id { get; set; }

    public int IdPresupuesto { get; set; }

    /// <summary>Asignado por el servidor en cada replace-set (mismo criterio que
    /// <c>ItemComprobanteVenta.Orden</c>/<c>ItemOrdenCompra.Orden</c>) — nunca input de
    /// cliente.</summary>
    public int Orden { get; set; }

    /// <summary><c>NOT NULL</c> (proposal §D): un presupuesto no tiene líneas de concepto
    /// libre — una línea sin artículo no puede convertirse en una línea de venta que mueve
    /// stock.</summary>
    public int IdArticulo { get; set; }

    /// <summary>Snapshot al momento del guardado del borrador — el cliente lee un nombre aunque
    /// el catálogo cambie después.</summary>
    public required string Descripcion { get; set; }

    /// <summary><c>numeric(12,3)</c> (proposal §D, doc-10 principio 5) — <c>&gt; 0</c>
    /// (<c>ck_items_presupuesto_cantidad_positiva</c>).</summary>
    public decimal Cantidad { get; set; }

    /// <summary>Congelado al momento del guardado (design decisión 2/proposal) — la provisión
    /// que la conversión reutiliza como autoridad de precio, en vez de re-resolver contra
    /// <c>ServicioDeOfertas</c>.</summary>
    public decimal PrecioUnitario { get; set; }

    public decimal Descuento { get; set; }

    public decimal Total { get; set; }

    /// <summary>Procedencia del precio ofrecido — congelada, nunca re-derivada al convertir
    /// (proposal §D, design decisión 2/tensión T3: un único <c>id_lista_precio</c> por
    /// presupuesto es un invariante de servicio, no una restricción de esquema).</summary>
    public int IdListaPrecio { get; set; }

    public int? IdOferta { get; set; }

    public int IdAlicuotaIva { get; set; }

    public decimal PorcentajeIva { get; set; }
}
