using Ways.Domain.Articulos;
using Ways.Domain.Precios;

namespace Ways.Application.Familias;

/// <summary>Fila de <c>GET /api/familias</c>: una familia viva del tenant. <see cref="CantidadArticulos"/> cuenta
/// solo los miembros vivos (<c>id_familia = F</c> y sin baja lógica), la misma definición de miembro que usan los
/// escritores (doc 10 §3, "Familias de artículos").</summary>
public sealed record FamiliaListado(int Id, string Nombre, bool Activo, int CantidadArticulos);

/// <summary>Un miembro vivo de una familia. <see cref="IdMarca"/> es <c>null</c> cuando el artículo no tiene
/// marca o la que tiene está dada de baja: un id colgante nunca se expone (el mismo criterio que la grilla de
/// artículos).</summary>
public sealed record MiembroDeFamilia(int Id, string CodigoInterno, string Nombre, int? IdMarca, bool Activo);

/// <summary>
/// Los trece campos compartidos (<see cref="ValoresCompartidosDeFamilia"/>) del artículo de referencia de una
/// familia, tal como los lee <c>GET /api/familias/{id}</c>. Los cuatro ids de catálogo
/// (<see cref="IdArea"/>, <see cref="IdCategoria"/>, <see cref="IdGrupo"/>, <see cref="IdProveedorHabitual"/>) viajan
/// como <c>null</c> cuando apuntan a una fila dada de baja, igual que <see cref="MiembroDeFamilia.IdMarca"/>:
/// <see cref="IdArea"/> es obligatorio en el artículo, pero un área dada de baja que conserva artículos se lee como
/// "sin asignar". Lo demás es el valor guardado.
/// </summary>
public sealed record ValoresCompartidosDeLaFamilia(
    int? IdArea,
    int? IdCategoria,
    int? IdGrupo,
    int? IdProveedorHabitual,
    int IdAlicuotaIva,
    UnidadVenta UnidadVenta,
    decimal? UnidadesPorBulto,
    bool EsProducto,
    bool ControlaLote,
    bool AcumulaEnVenta,
    decimal? CostoLista,
    decimal? DescuentoProveedor,
    decimal? CostoNominal);

/// <summary>El estado de precios del artículo de referencia en UNA lista fija, a "ahora": el precio vigente y, si
/// lo hay, el pendiente con su fecha. Sin ningún precio en esa lista, el estado viene vacío.</summary>
public sealed record EstadoDePreciosDeLista(int IdListaPrecio, EstadoDePrecios Estado);

/// <summary>
/// Respuesta de <c>GET /api/familias/{id}</c>: lo que hace falta para armar el alta de un artículo dentro de la
/// familia. <see cref="Articulos"/> son los miembros vivos ascendentes por id; el primero es el artículo de
/// referencia (el de menor id), de quien salen <see cref="Valores"/> y <see cref="Precios"/>. Una familia sin
/// ningún miembro vivo no tiene referencia: <see cref="Valores"/> es <c>null</c> y <see cref="Precios"/> viene
/// vacío. Con referencia, <see cref="Precios"/> trae una entrada por cada lista fija del tenant, ascendente por id
/// de lista, también las que la referencia no tiene precios.
/// </summary>
public sealed record FamiliaDetalle(
    int Id,
    string Nombre,
    bool Activo,
    IReadOnlyList<MiembroDeFamilia> Articulos,
    ValoresCompartidosDeLaFamilia? Valores,
    IReadOnlyList<EstadoDePreciosDeLista> Precios);
