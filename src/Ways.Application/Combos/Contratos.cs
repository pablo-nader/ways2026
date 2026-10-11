namespace Ways.Application.Combos;

/// <summary>
/// Un componente tal como lo lee un cliente. <see cref="Nombre"/> es el del artículo o de la familia; si esa fila
/// fue dada de baja después de armar el combo (la baja de una familia o de un artículo que un combo vivo usa se
/// rechaza, pero un dato heredado o una baja fuera de banda puede dejar la referencia colgando) llega <c>null</c> y
/// <see cref="Disponible"/> es <c>false</c>. El id se devuelve igual, para que el cliente sepa qué componente es
/// el que hay que quitar o reemplazar: un PUT que lo repita se rechaza con <c>referencia_invalida</c>.
/// </summary>
public sealed record ComponenteDeComboListado(
    int? IdArticulo, int? IdFamilia, string? Nombre, decimal Cantidad, bool Disponible);

public sealed record ComboListado(
    int Id,
    int Codigo,
    string Nombre,
    decimal Precio,
    int? IdEmpresa,
    bool Activo,
    IReadOnlyList<ComponenteDeComboListado> Componentes);

/// <summary>Un componente del pedido: exactamente uno entre <see cref="IdArticulo"/> e
/// <see cref="IdFamilia"/>, con su <see cref="Cantidad"/>.</summary>
public sealed record ComponenteDeSolicitudDeCombo(int? IdArticulo, int? IdFamilia, decimal Cantidad);

/// <summary>
/// Alta de un combo. <see cref="Codigo"/> omitido ⇒ el servidor asigna el siguiente libre del tenant;
/// informado, tiene que ser un entero entre 1 y 999999 sin uso en otro combo vivo. <see cref="IdEmpresa"/> nulo ⇒
/// aplica a todo el tenant. <see cref="Componentes"/> son el conjunto completo.
/// </summary>
public sealed record AltaCombo(
    int? Codigo,
    string Nombre,
    decimal Precio,
    int? IdEmpresa,
    IReadOnlyList<ComponenteDeSolicitudDeCombo>? Componentes,
    bool Activo = true);

/// <summary>
/// Edición completa de un combo: reemplaza nombre, precio, empresa, estado y el conjunto de componentes.
/// <see cref="Codigo"/> omitido ⇒ conserva el actual; informado, aplica las mismas reglas que en el alta.
/// <see cref="Activo"/> es obligatorio (<c>400 activo_requerido</c>): es nullable para que su ausencia en el JSON se
/// rechace en vez de leerse como un valor por defecto y reactivar (o desactivar) el combo en silencio.
/// </summary>
public sealed record EdicionCombo(
    int? Codigo,
    string Nombre,
    decimal Precio,
    int? IdEmpresa,
    IReadOnlyList<ComponenteDeSolicitudDeCombo>? Componentes,
    bool? Activo);
