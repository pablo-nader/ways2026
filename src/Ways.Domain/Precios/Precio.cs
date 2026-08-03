using Ways.Domain.Common;

namespace Ways.Domain.Precios;

/// <summary>
/// Historial de precio por <c>(articulo, lista_precio)</c> (doc 10 §3, design decision 3):
/// append-only — un cambio de precio cierra la fila vigente (<see cref="VigenteHasta"/>) e
/// inserta una nueva, nunca actualiza <see cref="Precio"/> de una fila existente. Sin
/// <c>Update</c> a nivel de entidad a propósito: el único punto de escritura legítimo es
/// <c>ServicioDePrecios.AbrirNuevoPrecioAsync</c> (Slice 3) — esta clase solo declara la forma
/// de la tabla para esta slice (schema/domain foundation), sin servicio ni endpoint todavía.
/// </summary>
public class Precio : EntidadTenant
{
    public int Id { get; set; }

    public int IdArticulo { get; set; }
    public int IdListaPrecio { get; set; }

    /// <summary>Columna <c>precio</c> (doc 10 §3). Nombrada <c>Monto</c> y no <c>Precio</c> —
    /// C# no permite que un miembro se llame igual que su tipo contenedor (CS0542) — pero es
    /// la misma propiedad que el resto de la documentación de esta etapa (design/spec) llama
    /// "<c>Precio.Precio</c>": ningún camino de código la expone como <c>set</c>-eable fuera
    /// de <c>ServicioDePrecios.AbrirNuevoPrecioAsync</c> (Slice 3).</summary>
    public decimal Monto { get; set; }

    public DateTimeOffset VigenteDesde { get; set; }

    /// <summary><c>NULL</c> ⇒ fila vigente. <c>ux_precios_vigente (id_articulo,
    /// id_lista_precio) WHERE vigente_hasta IS NULL AND deleted_at IS NULL</c> garantiza como
    /// mucho una fila abierta por par — el diseño ya lo alcanza sin una constraint sobre
    /// <c>now()</c> (design decision 4).</summary>
    public DateTimeOffset? VigenteHasta { get; set; }
}
