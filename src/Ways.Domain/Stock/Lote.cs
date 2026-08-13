using Ways.Domain.Common;

namespace Ways.Domain.Stock;

/// <summary>
/// Identidad de lote (doc 10 §6, proposal gate §A, gate amendment 1): catálogo tenant-wide
/// (<c>id_tenant</c>, SIN <c>id_empresa</c>) — sigue al artículo (<see cref="Articulos.Articulo"/>)
/// igual que <see cref="Precios.Precio"/>, no la categoría "catálogo" que carga
/// <c>id_empresa NULL</c>. Con auditoría completa (<see cref="EntidadTenant"/>): tiene identidad
/// y ciclo de vida propios, a diferencia de las cachés PK-only (<see cref="StockLote"/>).
///
/// <see cref="FechaVencimiento"/> es inmutable una vez creado el lote (proposal decisión 3 del
/// gate; <c>ServicioDeLotes.ResolverOCrearAsync</c>, slice 3): una segunda recepción del mismo
/// <c>(articulo, codigo)</c> con otra fecha se rechaza <c>409 lote_vencimiento_incompatible</c>
/// en vez de sobrescribir la fila.
///
/// <see cref="Codigo"/> se deriva server-side del vencimiento ISO cuando el llamador lo omite
/// (<c>ReglaDeLotes.DerivarCodigo</c>, slice 2). El lote "sin identificar"
/// (<see cref="EsSinIdentificar"/> = true) usa el código reservado
/// <c>ReglaDeLotes.CodigoSinIdentificar</c> — a lo sumo uno por artículo
/// (<c>ux_lotes_sin_identificar</c>).
/// </summary>
public class Lote : EntidadTenant
{
    public int Id { get; set; }

    public int IdArticulo { get; set; }

    public required string Codigo { get; set; }

    /// <summary><c>NULL</c> si y solo si <see cref="EsSinIdentificar"/> es <c>true</c>
    /// (<c>ck_lotes_vencimiento_segun_tipo</c>).</summary>
    public DateOnly? FechaVencimiento { get; set; }

    public bool EsSinIdentificar { get; set; }
}
