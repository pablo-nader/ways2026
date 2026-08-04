namespace Ways.Domain.Ofertas;

/// <summary>
/// Contrato de resolución (design: Resolution Contract, task 3.1) — records puros, sin
/// dependencias, que <see cref="ResolvedorDeOfertas"/> consume. <see cref="IdsCategorias"/> ya
/// llega resuelto (categoría propia + ancestros, ver <see cref="CadenaDeCategorias"/>): el
/// resolver nunca hace su propia consulta jerárquica, solo compara contra el conjunto que le
/// entrega <c>ServicioDeOfertas.ResolverAsync</c>. <see cref="DiaSemana"/> es ISO-8601
/// (1 = lunes … 7 = domingo, spec: ofertas / Vigencia Window Semantics).
/// </summary>
public readonly record struct LineaAResolver(
    int IdArticulo,
    int? IdGrupo,
    IReadOnlyList<int> IdsCategorias,
    int IdListaPrecio,
    decimal Cantidad,
    decimal PrecioOriginal,
    DateOnly Fecha,
    TimeOnly Hora,
    int DiaSemana);

/// <summary>
/// Una oferta candidata para una <see cref="LineaAResolver"/>. Ya pasó el filtro grueso de SQL
/// (<c>activo</c>, alcance por ids, <c>id_empresa</c>) en <c>ServicioDeOfertas.ResolverAsync</c>
/// — lo que le falta matchear (ventana de vigencia, <c>cantidad_minima</c>, lista objetivo, día
/// de semana) lo evalúa <see cref="ResolvedorDeOfertas.Coincide"/> antes de aplicar la
/// aritmética. <see cref="DiasSemana"/>/<see cref="ListasObjetivo"/> vacíos ⇒ sin restricción
/// (spec: ofertas / Vigencia Window Semantics, Multi-Lista Targeting via ofertas_listas).
/// </summary>
public readonly record struct OfertaCandidata(
    int Id,
    string Nombre,
    int Prioridad,
    bool Acumulable,
    AlcanceDeOferta Alcance,
    BeneficioDeOferta Beneficio,
    decimal? CantidadMinima,
    DateOnly? FechaDesde,
    DateOnly? FechaHasta,
    TimeOnly? HoraDesde,
    TimeOnly? HoraHasta,
    IReadOnlySet<int> DiasSemana,
    IReadOnlySet<int> ListasObjetivo);

/// <summary>Una oferta efectivamente aplicada a una línea — solo para reporte (design: Resolution
/// Contract). El orden en que aparece en <see cref="PrecioConOfertas.Aplicadas"/> (descendente
/// <c>Prioridad</c>, luego ascendente <c>IdOferta</c>) no afecta el monto calculado.</summary>
public readonly record struct OfertaAplicada(int IdOferta, string Nombre, decimal DescuentoUnitario);

/// <summary>Resultado de resolver UNA línea — <see cref="DescuentoUnitario"/> es la suma ya
/// clampeada de todos los <see cref="OfertaAplicada.DescuentoUnitario"/> (design: arithmetic
/// table, "Total = min(Σ discounts, PrecioOriginal)").</summary>
public readonly record struct PrecioConOfertas(
    decimal PrecioOriginal,
    decimal PrecioFinal,
    decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicada> Aplicadas);
