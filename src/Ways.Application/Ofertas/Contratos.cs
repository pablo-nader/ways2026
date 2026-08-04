namespace Ways.Application.Ofertas;

/// <summary><see cref="IdsListas"/> (mismo criterio judgment-day que
/// <c>ArticuloListado.IdsEmpresas</c>, stage-3 Slice 2, item 2) expone el subconjunto ACTUAL de
/// <c>ofertas_listas</c> — vacío cuando la oferta aplica a todas las listas — para que un
/// cliente HTTP pueda armar un PUT de no-op sin perder el targeting. <c>ListarAsync</c> lo deja
/// vacío por fila (evita el N+1 de una query por oferta listada); el valor real solo se completa
/// en <c>ObtenerAsync</c>/<c>CrearAsync</c>/<c>ActualizarAsync</c>.</summary>
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
    IReadOnlyList<int> IdsListas);

/// <summary>Alcance (<see cref="IdArticulo"/>/<see cref="IdGrupo"/>/<see cref="IdCategoria"/>) y
/// beneficio (<see cref="PrecioUnitario"/>/<see cref="Porcentaje"/>/<see cref="ImporteFijo"/>)
/// viajan como las tres columnas nullable crudas de doc 10 (design decision 1) — la
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
    bool Activo = true);

/// <summary>Mismo shape que <see cref="AltaOferta"/> — a diferencia de
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
    bool Activo);
