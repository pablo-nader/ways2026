namespace Ways.Domain.Combos;

/// <summary>Componente de un combo tal como lo consumen <see cref="ReglaDeCombos"/> y
/// <see cref="ResolvedorDeCombos"/>: exactamente uno entre artículo y familia.</summary>
public readonly record struct ComponenteDeCombo(int? IdArticulo, int? IdFamilia, decimal Cantidad);

/// <summary>Combo candidato para el resolvedor. <see cref="Precio"/> es el del conjunto
/// completo (una aplicación).</summary>
public readonly record struct ComboCandidato(
    int Id, string Nombre, decimal Precio, IReadOnlyList<ComponenteDeCombo> Componentes);

/// <summary>
/// Línea del documento para detectar combos. <see cref="Indice"/> identifica la línea en la
/// salida y define el orden del documento. <see cref="DescuentoUnitarioDeOferta"/> es el
/// descuento por unidad que las ofertas ya resolvieron para esa línea.
/// </summary>
public readonly record struct LineaParaCombos(
    int Indice,
    int IdArticulo,
    int? IdFamilia,
    decimal Cantidad,
    decimal PrecioLista,
    decimal DescuentoUnitarioDeOferta);

/// <summary>
/// Consumo de combos de una línea. El descuento de línea que debe grabar quien llama es
/// <c>DescuentoUnitarioDeOferta × (Cantidad − UnidadesConsumidas) + AhorroDeCombo</c>:
/// las unidades consumidas quedan a precio de lista menos su parte del combo.
/// </summary>
public readonly record struct ConsumoDeLinea(
    int Indice, int UnidadesConsumidas, decimal AhorroDeCombo, IReadOnlyList<int> IdsDeCombo);

/// <summary>Un combo y las veces que se aplicó; <see cref="Ahorro"/> es la suma de lo
/// repartido a las líneas por ese combo.</summary>
public readonly record struct ComboAplicado(int IdCombo, string Nombre, int Veces, decimal Ahorro);

/// <summary>Resultado de <see cref="ResolvedorDeCombos.Resolver"/>: solo figuran las líneas con
/// unidades consumidas, en orden de documento.</summary>
public readonly record struct ResolucionDeCombos(
    IReadOnlyList<ConsumoDeLinea> Lineas, IReadOnlyList<ComboAplicado> Combos)
{
    public static ResolucionDeCombos Vacia { get; } = new([], []);
}
