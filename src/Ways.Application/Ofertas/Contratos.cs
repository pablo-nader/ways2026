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

/// <summary>Una línea de entrada para <c>ServicioDeOfertas.ResolverAsync</c> (task 3.6, design:
/// Resolution Contract) — a diferencia de <c>Ways.Domain.Ofertas.LineaAResolver</c> (pura,
/// Domain), esta es la forma HTTP: no lleva ni la cadena de ancestros de categoría ni la
/// descomposición de fecha/hora local (ambas las arma <c>ServicioDeOfertas</c>), y sí lleva
/// <see cref="IdEmpresa"/> — el dato que <c>LineaAResolver</c> deliberadamente no lleva (spec:
/// resolucion-de-ofertas / Candidate Matching, "Empresa-scoped oferta excludes other
/// empresas"; ver <c>Ways.Domain.Ofertas.ReglaDeOfertas.CoincideEmpresa</c>).</summary>
public sealed record LineaDeResolucion(int IdArticulo, int? IdEmpresa, int IdListaPrecio, decimal Cantidad);

/// <summary>Cuerpo de <c>POST /api/ofertas/resolver</c> — un único <paramref name="Momento"/>
/// (design: Open Questions, "server-configured local time") para todo el lote entero, no uno
/// por línea: resolver un carrito es "ahora" (o un instante hipotético) para todas sus líneas a
/// la vez. <c>null</c> ⇒ <c>IRelojDelSistema.Ahora</c>. <see cref="Lineas"/> es nullable porque
/// System.Text.Json no valida miembros <c>required</c> en constructores marcados
/// <c>SetsRequiredMembers</c>: la clave "lineas" ausente o explícitamente <c>null</c> deserializa
/// igual, así que <c>ServicioDeOfertas.ResolverAsync</c> es quien distingue ese caso (400
/// <c>lineas_requeridas</c>) de un lote vacío legítimo (<c>[]</c> ⇒ resultado vacío).</summary>
public sealed record SolicitudDeResolucion(IReadOnlyList<LineaDeResolucion>? Lineas, DateTimeOffset? Momento = null);

/// <summary>Una oferta aplicada, forma HTTP de <c>Ways.Domain.Ofertas.OfertaAplicada</c>.</summary>
public sealed record OfertaAplicadaDto(int IdOferta, string Nombre, decimal DescuentoUnitario);

/// <summary>Resultado de resolver una línea (spec: resolucion-de-ofertas / Applied Ofertas Are
/// Reported, Never Persisted) — <see cref="PrecioOriginal"/>/<see cref="PrecioFinal"/> son
/// <c>null</c> cuando el lote de precios no tiene ningún precio vigente para el par
/// (artículo, lista) consultado (caso fuera de alcance de la spec de ofertas: sin precio no hay
/// nada que descontar, <see cref="Aplicadas"/> queda vacía).</summary>
public sealed record ResultadoDeResolucion(
    int IdArticulo,
    int IdListaPrecio,
    decimal? PrecioOriginal,
    decimal? PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicadaDto> Aplicadas);
