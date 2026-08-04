namespace Ways.Domain.Ventas;

/// <summary>
/// Contador atómico de <c>comprobantes_venta.numero</c> por punto de venta + tipo de
/// comprobante (design decision 8, doc 09 <c>numeraciones_comprobante</c> family): la PK es
/// <c>(IdPuntoVenta, TipoComprobante)</c> — <c>IdPuntoVenta</c> ya es una identidad global
/// (doc 09), así que agregar <c>IdTenant</c> a la PK sería redundante (mismo criterio que
/// <c>pk_ofertas_listas</c>: lo carga como columna no-key, solo para RLS/FKs).
///
/// PK-only, sin auditoría ni baja lógica — mismo criterio que <see cref="Articulos.ArticuloEmpresa"/>/
/// <see cref="Ofertas.OfertaLista"/>: no hereda de <see cref="Common.EntidadBase"/>/
/// <see cref="Common.EntidadTenant"/>, así que necesita filtro de tenant escrito a mano
/// (<c>WaysDbContext.AplicarFiltroDeTenantEnNumeracionComprobante</c>).
///
/// <see cref="Application.Ventas.AsignadorDeNumeroComprobante"/> es el único punto de
/// escritura legítimo: lo hace con SQL crudo, con creación perezosa de la fila dentro de la
/// transacción del llamador (design decision 9) — nunca vía <c>SaveChangesAsync</c>.
/// </summary>
public class NumeracionComprobante
{
    public int IdPuntoVenta { get; set; }

    /// <summary><c>tipos_comprobante.codigo</c> ("TX", "NCX", …) — <c>varchar(30)</c> en vez
    /// de una FK: nunca es input de cliente (siempre viene de un <c>TipoComprobante</c> ya
    /// cargado), y doc 09 numera en esta misma tabla conceptos que no son comprobante
    /// (<c>retiro</c>, <c>cierre_caja</c> — stage 6).</summary>
    public required string TipoComprobante { get; set; }

    public int IdTenant { get; set; }

    public long ProximoNumero { get; set; } = 1;
}
