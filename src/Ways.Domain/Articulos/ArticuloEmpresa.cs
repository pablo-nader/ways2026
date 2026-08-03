namespace Ways.Domain.Articulos;

/// <summary>
/// Fila de excepción de disponibilidad (doc 10 §3, spec: articulos_empresas Junction Schema):
/// solo existe cuando el artículo tiene <see cref="Articulo.DisponibleParaTodas"/> = <c>false</c>.
/// Task 1.4: PK-only, sin baja lógica — no hereda de <see cref="Common.EntidadBase"/>/
/// <see cref="Common.EntidadTenant"/> a propósito, mismo criterio que
/// <see cref="NumeracionArticulo"/>: agregar o quitar una empresa del subconjunto es un
/// INSERT/DELETE físico, no una edición con historial. <see cref="IdTenant"/> sigue siendo el
/// filtro de aislamiento (query filter manual en <c>WaysDbContext</c>, ver
/// <c>AplicarFiltroDeTenantEnArticuloEmpresa</c> — esta clase no hereda de
/// <see cref="Common.EntidadTenant"/>, así que el loop por convención no la alcanza).
/// </summary>
public class ArticuloEmpresa
{
    public int IdArticulo { get; set; }
    public int IdEmpresa { get; set; }
    public int IdTenant { get; set; }
}
