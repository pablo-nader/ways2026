using Ways.Domain.Articulos;
using Ways.Domain.Precios;

namespace Ways.Application.Familias;

/// <summary>Fila de <c>GET /api/familias</c>: una familia viva del tenant. <see cref="CantidadArticulos"/> cuenta
/// solo los miembros vivos (<c>id_familia = F</c> y sin baja lógica), la misma definición de miembro que usan los
/// escritores (doc 10 §3, "Familias de artículos").</summary>
public sealed record FamiliaListado(int Id, string Nombre, bool Activo, int CantidadArticulos);

/// <summary>
/// Cuerpo de <c>PUT /api/familias/{id}</c>: lo único de una familia que se edita. Los dos campos son obligatorios y
/// se escriben tal cual —el nombre sin espacios en los extremos— (<c>400 nombre_requerido</c>,
/// <c>400 nombre_muy_largo</c>, <c>400 activo_requerido</c>): <see cref="Activo"/> es nullable para que su ausencia en
/// el JSON se rechace en vez de leerse como <c>false</c> y desactivar la familia en silencio. <see cref="Activo"/>
/// gobierna si la familia admite miembros nuevos: el alta de un artículo con <c>idFamilia</c> rechaza una familia
/// inactiva (<c>409 familia_inactiva</c>). El nombre es único entre las familias vivas del tenant sin distinguir
/// mayúsculas (<c>409 familia_nombre_duplicado</c>).
/// </summary>
public sealed record EdicionFamilia(string? Nombre, bool? Activo);

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

/// <summary>
/// Cuerpo de <c>POST /api/familias/previsualizacion</c>: qué pasaría si se agruparan <see cref="IdsArticulos"/> con
/// <see cref="IdArticuloReferencia"/> como modelo. Los dos campos se usan: la referencia es el artículo con el que se
/// alinean los demás, y los ids son los artículos que se alinean (<c>null</c> es una lista vacía; los repetidos y la
/// propia referencia no cuentan, y se admiten como máximo 100 además de ella:
/// <see cref="Ways.Domain.Articulos.ReglaDeAgrupacion"/>). Si la referencia ya es miembro de una familia, la agrupación
/// sería sumar los artículos a ESA familia; si no tiene familia, sería crear una nueva.
/// </summary>
public sealed record SolicitudDePrevisualizacion(int IdArticuloReferencia, IReadOnlyList<int>? IdsArticulos);

/// <summary>El cambio de UNA lista fija para un artículo que se alinea: su estado de precios actual y el de la
/// referencia, al que pasa.</summary>
public sealed record CambioDePreciosDeLista(int IdListaPrecio, EstadoDePrecios Actual, EstadoDePrecios Nuevo);

/// <summary>
/// Lo que cambia en UN artículo al alinearlo con la referencia: <see cref="Campos"/> son las columnas compartidas
/// (con el nombre de la columna de <c>articulos</c>, en el orden de declaración de
/// <see cref="ValoresCompartidosDeFamilia"/>) que difieren, y <see cref="Actual"/> y <see cref="Nuevo"/> los trece
/// valores guardados en el artículo y en la referencia, tal cual están en la base —también un id de catálogo que
/// apunte a una fila dada de baja: la alineación lo copia así—. <see cref="Precios"/> trae las listas fijas en las
/// que el estado de precios cambia; las que ya coinciden no figuran. Un artículo ya alineado trae las dos listas
/// vacías.
/// </summary>
public sealed record CambiosDeUnArticulo(
    int IdArticulo,
    IReadOnlyList<string> Campos,
    ValoresCompartidosDeFamilia Actual,
    ValoresCompartidosDeFamilia Nuevo,
    IReadOnlyList<CambioDePreciosDeLista> Precios);

/// <summary>
/// Algo que impediría agrupar. <see cref="Codigo"/> y <see cref="Mensaje"/> son los de la respuesta de error que el
/// pedido real daría por ese problema: <c>referencia_invalida</c> (el artículo no existe o está dado de baja),
/// <c>articulo_en_otra_familia</c>, <c>familia_precio_inalineable</c> (con <see cref="IdListaPrecio"/>),
/// <c>familia_inactiva</c> y <c>no_encontrado</c> (la familia de la referencia está dada de baja). Sin
/// <see cref="IdArticulo"/> es un problema de la familia.
/// </summary>
public sealed record ProblemaDeAgrupacion(string Codigo, string Mensaje, int? IdArticulo, int? IdListaPrecio);

/// <summary>
/// Respuesta de <c>POST /api/familias/previsualizacion</c>: <see cref="Articulos"/> trae, para cada artículo pedido
/// que se puede alinear y ascendente por id, lo que cambiaría; <see cref="Problemas"/>, todo lo que impediría
/// agrupar, en el orden en que el pedido real los rechaza —el pedido real falla con el primero—: los de la familia, los
/// artículos que no existen, los que ya están en otra familia y los de precios. <see cref="IdFamilia"/> es la familia a
/// la que se sumarían (la de la referencia) o <c>null</c> si se crearía una nueva. Es una foto sin locks: entre la
/// previsualización y el pedido real puede haber cambiado cualquier cosa, y el pedido real decide con lo que lee bajo
/// sus locks.
/// </summary>
public sealed record PrevisualizacionDeAgrupacion(
    int IdArticuloReferencia,
    int? IdFamilia,
    IReadOnlyList<CambiosDeUnArticulo> Articulos,
    IReadOnlyList<ProblemaDeAgrupacion> Problemas);

/// <summary>
/// Cuerpo de <c>POST /api/familias</c>: crea la familia <see cref="Nombre"/> con <see cref="IdArticuloReferencia"/> como
/// modelo y la agrupa con <see cref="IdsArticulos"/>, que se alinean con él. Los tres campos se usan: el nombre (sin
/// espacios en los extremos, hasta 150 caracteres, único entre las familias vivas sin distinguir mayúsculas), la
/// referencia, que es siempre miembro aunque no figure en la lista, y los artículos (<c>null</c> es una lista vacía: la
/// familia nace con la referencia sola; los repetidos y la propia referencia no cuentan, y se admiten como máximo 100
/// además de ella). La referencia y los artículos tienen que existir y no pertenecer a ninguna familia: agrupar no
/// mueve a nadie de la que ya tiene.
/// </summary>
public sealed record AltaDeFamilia(string? Nombre, int IdArticuloReferencia, IReadOnlyList<int>? IdsArticulos);

/// <summary>
/// Cuerpo de <c>POST /api/familias/{id}/articulos</c>: suma <see cref="IdsArticulos"/> a la familia, alineados con su
/// artículo de referencia (el miembro vivo de menor id). Los ids repetidos cuentan una vez, se admiten como máximo 100,
/// y la lista no puede estar vacía (<c>400 articulos_requeridos</c>). Un id que ya es miembro de la familia se alinea
/// igual y no cambia nada si ya está alineado; uno que es miembro de OTRA familia se rechaza.
/// </summary>
public sealed record AgregadoDeArticulos(IReadOnlyList<int>? IdsArticulos);

/// <summary>
/// Respuesta de <c>POST /api/familias</c> y de <c>POST /api/familias/{id}/articulos</c>: la familia, su artículo de
/// referencia y, por cada artículo pedido distinto de la referencia y ascendente por id, lo que la agrupación cambió en
/// él (<see cref="CambiosDeUnArticulo"/>, el mismo que la previsualización informa). Un artículo que ya estaba alineado
/// trae las listas vacías.
/// </summary>
public sealed record ResultadoDeAgrupacion(
    int IdFamilia, string Nombre, int IdArticuloReferencia, IReadOnlyList<CambiosDeUnArticulo> Articulos);
