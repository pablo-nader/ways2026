using Ways.Domain.Articulos;

namespace Ways.Domain.Combos;

/// <summary>Componente de un combo tal como lo consumen <see cref="ReglaDeCombos"/> y
/// <see cref="ResolvedorDeCombos"/>: exactamente uno entre artículo y familia. <see cref="Cantidad"/> va en la unidad
/// de venta del artículo: unidades enteras, o kilos con hasta tres decimales si se vende por peso.</summary>
public readonly record struct ComponenteDeCombo(int? IdArticulo, int? IdFamilia, decimal Cantidad);

/// <summary>Combo candidato para el resolvedor. <see cref="Precio"/> es el del conjunto
/// completo (una aplicación). <see cref="Codigo"/> es el número que se tipea tras la <c>C</c>: el motor no lo usa,
/// viaja para quien presenta los combos aplicados (<c>0</c> si quien arma el candidato no lo conoce).</summary>
public readonly record struct ComboCandidato(
    int Id, string Nombre, decimal Precio, IReadOnlyList<ComponenteDeCombo> Componentes, int Codigo = 0);

/// <summary>
/// Línea del documento para detectar combos. El orden de entrada es el orden del documento;
/// <see cref="Indice"/> solo identifica la línea en la salida. <see cref="DescuentoUnitarioDeOferta"/> es el
/// descuento por unidad que las ofertas ya resolvieron para esa línea. <see cref="UnidadVenta"/> es la del artículo
/// y decide qué cantidades de la línea y de un componente valen para ella; una línea por importe no se detecta.
/// </summary>
public readonly record struct LineaParaCombos(
    int Indice,
    int IdArticulo,
    int? IdFamilia,
    decimal Cantidad,
    decimal PrecioLista,
    decimal DescuentoUnitarioDeOferta,
    UnidadVenta UnidadVenta = UnidadVenta.Unidad);

/// <summary>
/// Consumo de combos de una línea. El descuento de línea que debe grabar quien llama es
/// <c>DescuentoUnitarioDeOferta × (Cantidad − UnidadesConsumidas) + AhorroDeCombo</c>:
/// las unidades consumidas quedan a precio de lista menos su parte del combo. <see cref="UnidadesConsumidas"/> es
/// una cantidad en la unidad de venta de la línea (entera por unidad, con hasta tres decimales por peso) y
/// <see cref="AhorroDeCombo"/> lleva a lo sumo dos decimales.
/// </summary>
public readonly record struct ConsumoDeLinea(
    int Indice, decimal UnidadesConsumidas, decimal AhorroDeCombo, IReadOnlyList<int> IdsDeCombo);

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
