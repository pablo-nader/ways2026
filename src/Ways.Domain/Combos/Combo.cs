using Ways.Domain.Common;

namespace Ways.Domain.Combos;

/// <summary>
/// Conjunto de dos o más unidades que se vende a un precio fijo (doc 10 §Combos). Catálogo-scoped
/// como <see cref="Ofertas.Oferta"/>: <c>id_tenant</c> NOT NULL e <see cref="IdEmpresa"/> NULL =
/// todo el tenant. Los componentes viven en <see cref="ComboComponente"/>; el invariante de
/// forma (al menos dos unidades, un componente es artículo o familia) lo valida
/// <see cref="ReglaDeCombos"/> y lo respaldan las CHECKs de esquema.
/// </summary>
public class Combo : EntidadTenant
{
    public int Id { get; set; }

    /// <summary><c>NULL</c> ⇒ toda empresa del tenant.</summary>
    public int? IdEmpresa { get; set; }

    /// <summary>Número que se tipea tras la <c>C</c> en el POS; único entre los combos vivos del
    /// tenant (<c>ux_combos_codigo</c>).</summary>
    public int Codigo { get; set; }

    public required string Nombre { get; set; }

    /// <summary>Precio fijo del conjunto, independiente de la lista de precios.</summary>
    public decimal Precio { get; set; }

    public bool Activo { get; set; } = true;

    /// <summary><c>true</c> (default) ⇒ la tienda online ofrece el combo.</summary>
    public bool DisponibleOnline { get; set; } = true;
}
