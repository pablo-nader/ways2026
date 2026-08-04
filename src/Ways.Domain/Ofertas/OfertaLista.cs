namespace Ways.Domain.Ofertas;

/// <summary>
/// Junction de targeting multi-lista (doc 10 §Ofertas, deviado por stage-4 decisión 4:
/// reemplaza la columna única <c>id_lista_precio NULL</c> del doc). Cero filas para una
/// oferta ⇒ aplica a todas las listas del tenant, incluidas las <c>derivada</c>; una o más
/// filas ⇒ la restringe a exactamente esas listas (spec: Multi-Lista Targeting via
/// ofertas_listas).
///
/// PK-only, sin auditoría ni baja lógica — mismo criterio que <see cref="Articulos.ArticuloEmpresa"/>
/// (no hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>: agregar
/// o quitar una lista del subconjunto es un INSERT/DELETE físico, no una edición con
/// historial). La PK se nombra explícita <c>pk_ofertas_listas</c> — a diferencia de
/// <c>PK_articulos_empresas</c> (default de EF, PascalCase), esta corrige esa inconsistencia
/// en vez de copiarla.
///
/// <see cref="IdTenant"/> NO se auto-estampa (no hereda <see cref="Common.EntidadTenant"/>) —
/// quien la construya DEBE asignarlo a mano; el RLS <c>WITH CHECK</c> rechaza el INSERT con
/// SQLSTATE 42501 si falta. El camino de escritura real (<c>ServicioDeOfertas</c>, Slice 2)
/// tiene que asignarlo explícitamente al armar cada fila del replace-set.
/// </summary>
public class OfertaLista
{
    public int IdOferta { get; set; }
    public int IdListaPrecio { get; set; }
    public int IdTenant { get; set; }
}
