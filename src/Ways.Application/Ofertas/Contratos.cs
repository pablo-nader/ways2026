using System.Text.Json.Serialization;

namespace Ways.Application.Ofertas;

/// <summary><see cref="IdsListas"/> (mismo criterio judgment-day que
/// <c>ArticuloListado.IdsEmpresas</c>, stage-3 Slice 2, item 2) expone el subconjunto ACTUAL de
/// <c>ofertas_listas</c> — vacío cuando la oferta aplica a todas las listas — para que un
/// cliente HTTP pueda armar un PUT de no-op sin perder el targeting. <c>ListarAsync</c> lo deja
/// vacío por fila (evita el N+1 de una query por oferta listada); el valor real solo se completa
/// en <c>ObtenerAsync</c>/<c>CrearAsync</c>/<c>ActualizarAsync</c>. <c>NombreArticulo</c> es el
/// caso inverso: solo lo completa <c>ListarAsync</c> (una query para todas las filas) y las
/// demás operaciones lo dejan nulo; también es nulo cuando la oferta no es de artículo o el
/// artículo no es visible (dado de baja).</summary>
public sealed record OfertaListado(
    int Id,
    string Nombre,
    int? IdEmpresa,
    int? IdArticulo,
    int? IdGrupo,
    int? IdCategoria,
    DateOnly? FechaDesde,
    DateOnly? FechaHasta,
    TimeOnly? HoraDesde,
    TimeOnly? HoraHasta,
    IReadOnlyList<int> DiasSemana,
    decimal? CantidadMinima,
    decimal? PrecioUnitario,
    decimal? Porcentaje,
    decimal? ImporteFijo,
    int Prioridad,
    bool Acumulable,
    bool Activo,
    IReadOnlyList<int> IdsListas,
    int? IdFamilia = null,
    string? NombreArticulo = null,
    bool DisponibleOnline = true);

/// <summary>Alcance (<see cref="IdArticulo"/>/<see cref="IdGrupo"/>/<see cref="IdCategoria"/>/
/// <see cref="IdFamilia"/>) y
/// beneficio (<see cref="PrecioUnitario"/>/<see cref="Porcentaje"/>/<see cref="ImporteFijo"/>)
/// viajan como las columnas nullable crudas de doc 10 (cuatro de alcance, tres de beneficio) (design decision 1) — la
/// exclusividad de cada grupo la valida <c>ReglaDeOfertas</c> del lado de
/// <c>ServicioDeOfertas</c>, no un tipo discriminado acá. <see cref="IdsListas"/>: vacío/omitido
/// ⇒ la oferta aplica a todas las listas del tenant (spec: Multi-Lista Targeting via
/// ofertas_listas).</summary>
public sealed record AltaOferta(
    string Nombre,
    int? IdEmpresa,
    int? IdArticulo,
    int? IdGrupo,
    int? IdCategoria,
    DateOnly? FechaDesde,
    DateOnly? FechaHasta,
    TimeOnly? HoraDesde,
    TimeOnly? HoraHasta,
    IReadOnlyList<int>? DiasSemana,
    decimal? CantidadMinima,
    decimal? PrecioUnitario,
    decimal? Porcentaje,
    decimal? ImporteFijo,
    int Prioridad,
    bool Acumulable,
    IReadOnlyList<int>? IdsListas = null,
    bool Activo = true,
    int? IdFamilia = null,
    bool DisponibleOnline = true);

/// <summary>Mismo shape que <see cref="AltaOferta"/> salvo <see cref="DisponibleOnline"/>, que acá es opcional y
/// <c>null</c> significa "sin cambios": es nullable para que su ausencia en el JSON conserve el valor guardado en vez de
/// leerse como <c>false</c> y sacar la oferta de la tienda en silencio (un cliente anterior al campo sigue editando).
/// A diferencia de
/// <c>EdicionArticulo</c>/<c>AltaArticulo</c>, acá no hay ninguna columna inmutable que excluir
/// (<c>codigo_interno</c> no tiene equivalente en <c>ofertas</c>), así que los dos contratos
/// llevan exactamente los mismos campos (task 2.3: contratos separados por nombre, sin
/// diferencia de forma).</summary>
public sealed record EdicionOferta(
    string Nombre,
    int? IdEmpresa,
    int? IdArticulo,
    int? IdGrupo,
    int? IdCategoria,
    DateOnly? FechaDesde,
    DateOnly? FechaHasta,
    TimeOnly? HoraDesde,
    TimeOnly? HoraHasta,
    IReadOnlyList<int>? DiasSemana,
    decimal? CantidadMinima,
    decimal? PrecioUnitario,
    decimal? Porcentaje,
    decimal? ImporteFijo,
    int Prioridad,
    bool Acumulable,
    IReadOnlyList<int>? IdsListas,
    bool Activo,
    int? IdFamilia = null,
    bool? DisponibleOnline = null);

/// <summary>Una línea de entrada para <c>ServicioDeOfertas.ResolverAsync</c> (task 3.6, design:
/// Resolution Contract) — a diferencia de <c>Ways.Domain.Ofertas.LineaAResolver</c> (pura,
/// Domain), esta es la forma HTTP: no lleva ni la cadena de ancestros de categoría ni la
/// descomposición de fecha/hora local (ambas las arma <c>ServicioDeOfertas</c>), y sí lleva
/// <see cref="IdEmpresa"/> — el dato que <c>LineaAResolver</c> deliberadamente no lleva (spec:
/// resolucion-de-ofertas / Candidate Matching, "Empresa-scoped oferta excludes other
/// empresas"; ver <c>Ways.Domain.Ofertas.ReglaDeOfertas.CoincideEmpresa</c>).
///
/// <para><see cref="Importe"/> es el monto de un artículo que se vende por importe (<c>UnidadVenta.Importe</c>): la línea se
/// resuelve a ese precio, sin lista, ofertas ni combos, y su cantidad es 1. Solo lo admite un artículo por importe (400
/// <c>importe_no_admitido</c>); inválido es 400 <c>importe_invalido</c>. La curva por cantidad de la instantánea no lo
/// admite.</para></summary>
public sealed record LineaDeResolucion(
    int IdArticulo, int? IdEmpresa, int IdListaPrecio, decimal Cantidad, decimal? Importe = null);

/// <summary>Cuerpo de <c>POST /api/ofertas/resolver</c> — un único <paramref name="Momento"/>
/// (design: Open Questions, "server-configured local time") para todo el lote entero, no uno
/// por línea: resolver un carrito es "ahora" (o un instante hipotético) para todas sus líneas a
/// la vez. <c>null</c> ⇒ <c>IRelojDelSistema.Ahora</c>. <see cref="Lineas"/> es nullable porque
/// System.Text.Json no valida miembros <c>required</c> en constructores marcados
/// <c>SetsRequiredMembers</c>: la clave "lineas" ausente o explícitamente <c>null</c> deserializa
/// igual, así que <c>ServicioDeOfertas.ResolverAsync</c> es quien distingue ese caso (400
/// <c>lineas_requeridas</c>) de un lote vacío legítimo (<c>[]</c> ⇒ resultado vacío).</summary>
public sealed record SolicitudDeResolucion(
    IReadOnlyList<LineaDeResolucion>? Lineas,
    DateTimeOffset? Momento = null,
    AgregacionDeResolucion Agregacion = AgregacionDeResolucion.Documento);

/// <summary>Cómo se interpreta el lote de <see cref="SolicitudDeResolucion"/>. El valor por omisión
/// es <see cref="Documento"/> porque los POS ya instalados envían su carrito sin este campo.</summary>
public enum AgregacionDeResolucion
{
    /// <summary>El lote es un documento: las ofertas de familia comparan su cantidad mínima contra
    /// la suma de las cantidades de la familia en todo el lote (carrito, venta).</summary>
    Documento = 0,

    /// <summary>Cada línea es una consulta independiente a su propia cantidad: nada se suma entre
    /// líneas (vista previa de un resultado de búsqueda, consulta de precio).</summary>
    PorLinea = 1,
}

/// <summary>Una oferta aplicada, forma HTTP de <c>Ways.Domain.Ofertas.OfertaAplicada</c>.</summary>
public sealed record OfertaAplicadaDto(int IdOferta, string Nombre, decimal DescuentoUnitario);

/// <summary>Resultado de resolver una línea (spec: resolucion-de-ofertas / Applied Ofertas Are
/// Reported, Never Persisted) — <see cref="PrecioOriginal"/>/<see cref="PrecioFinal"/> son
/// <c>null</c> cuando el lote de precios no tiene ningún precio vigente para el par
/// (artículo, lista) consultado (caso fuera de alcance de la spec de ofertas: sin precio no hay
/// nada que descontar, <see cref="Aplicadas"/> queda vacía).
///
/// <see cref="Combo"/> solo lo completa <c>POST /api/ofertas/resolver-con-combos</c> (vista previa de lo que la venta
/// online cobrará) y solo en las líneas con unidades consumidas por un combo; en cualquier otro camino es <c>null</c> y
/// el <see cref="JsonIgnoreAttribute"/> deja la clave ausente, así que la respuesta de <c>/resolver</c> es
/// byte por byte la de siempre.</summary>
public sealed record ResultadoDeResolucion(
    int IdArticulo,
    int IdListaPrecio,
    decimal? PrecioOriginal,
    decimal? PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicadaDto> Aplicadas,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ComboDeLineaDto? Combo = null);

/// <summary>Lo que los combos le hacen a una línea del documento, igual que lo grabará la venta online:
/// <see cref="UnidadesConsumidas"/> de la línea entran a un combo (las demás conservan su oferta unitaria),
/// <see cref="Ahorro"/> es la parte del combo como IMPORTE DE LÍNEA (no unitario) y <see cref="IdCombo"/> el combo que
/// la venta guardará en la línea (el de menor id entre los que consumieron unidades). El neto de la línea es
/// <c>round((cantidad − UnidadesConsumidas) × (precio − DescuentoUnitario)) + round(UnidadesConsumidas × precio − Ahorro)</c>
/// y el descuento es <c>bruto − neto</c>, la misma fórmula del checkout (<c>CalculadorDeTotales.NetoDeLinea</c>).</summary>
public sealed record ComboDeLineaDto(int UnidadesConsumidas, decimal Ahorro, int IdCombo);

/// <summary>Un combo aplicado en el documento: <see cref="Veces"/> que se aplicó y <see cref="Ahorro"/> total (suma de lo
/// repartido a las líneas por ese combo). <see cref="Codigo"/> es el número que se tipea tras la <c>C</c>.</summary>
public sealed record ComboAplicadoDto(int IdCombo, int Codigo, string Nombre, int Veces, decimal Ahorro);

/// <summary>Respuesta de <c>POST /api/ofertas/resolver-con-combos</c>: <see cref="Lineas"/> en el orden de la
/// solicitud (cada una con su <see cref="ResultadoDeResolucion.Combo"/> si un combo la consumió) y los
/// <see cref="Combos"/> aplicados al documento, ascendentes por id (vacío si ninguno).</summary>
public sealed record ResolucionConCombos(
    IReadOnlyList<ResultadoDeResolucion> Lineas, IReadOnlyList<ComboAplicadoDto> Combos);

/// <summary>Un escalón de la curva de precio por cantidad, forma HTTP de
/// <c>Ways.Domain.Ofertas.EscalonResuelto</c>: desde <see cref="CantidadDesde"/> unidades (y hasta
/// el siguiente escalón, si hay) la línea resuelve a estos valores. <c>PrecioOriginal</c> NO se
/// repite por escalón a propósito — la cantidad no entra en la resolución de precio de lista, así
/// que es constante para toda la curva y ya viaja una sola vez en el resultado plano; lo único que
/// varía con la cantidad es el descuento, el final y qué ofertas aplicaron.</summary>
public sealed record EscalonDeCantidad(
    decimal CantidadDesde,
    decimal PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicadaDto> Aplicadas);

/// <summary>Una celda de la grilla de familia, forma HTTP de <c>Ways.Domain.Ofertas.CeldaDeFamilia</c>:
/// con <see cref="CantidadDeFamiliaDesde"/> unidades de la familia en el carrito y
/// <see cref="CantidadDesde"/> del propio artículo, la línea resuelve a estos valores. Igual que
/// <see cref="EscalonDeCantidad"/>, no repite <c>PrecioOriginal</c>.</summary>
public sealed record EscalonDeFamilia(
    decimal CantidadDeFamiliaDesde,
    decimal CantidadDesde,
    decimal PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicadaDto> Aplicadas);

/// <summary>El <see cref="Resultado"/> de siempre (la resolución a la cantidad pedida, byte por
/// byte lo que devuelve <c>ServicioDeOfertas.ResolverAsync</c>) más la curva de precio por
/// cantidad POR ENCIMA de esa cantidad. <see cref="Escalones"/> vacío ⇒ la línea no tiene ningún
/// escalón: el resultado plano aplica a cualquier cantidad. <see cref="EscalonesDeFamilia"/> es la
/// grilla para cuando la cantidad de la familia viene de otros artículos; <c>null</c> ⇒ ninguna
/// oferta de familia con cantidad mínima llega a la línea.</summary>
public sealed record ResultadoDeResolucionConEscalones(
    ResultadoDeResolucion Resultado,
    IReadOnlyList<EscalonDeCantidad> Escalones,
    IReadOnlyList<EscalonDeFamilia>? EscalonesDeFamilia = null);
