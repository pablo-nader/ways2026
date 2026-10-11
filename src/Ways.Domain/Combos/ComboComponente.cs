namespace Ways.Domain.Combos;

/// <summary>
/// Un componente de <see cref="Combo"/>: exactamente uno entre <see cref="IdArticulo"/> e
/// <see cref="IdFamilia"/>, con su <see cref="Cantidad"/>. PK propia sin auditoría ni baja
/// lógica: el conjunto de componentes se reemplaza completo al editar el combo.
///
/// No hereda de <see cref="Common.EntidadTenant"/>, así que <see cref="IdTenant"/> NO se
/// auto-estampa: quien arme la fila lo asigna, y el <c>WITH CHECK</c> de RLS rechaza el INSERT
/// con SQLSTATE 42501 si falta o es ajeno.
/// </summary>
public class ComboComponente
{
    public int Id { get; set; }
    public int IdTenant { get; set; }
    public int IdCombo { get; set; }
    public int? IdArticulo { get; set; }
    public int? IdFamilia { get; set; }
    public decimal Cantidad { get; set; }
}
