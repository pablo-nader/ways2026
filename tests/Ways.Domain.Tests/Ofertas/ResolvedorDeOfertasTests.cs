using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// stage-4-ofertas, Slice 3 (tasks 3.8/3.9; spec: resolucion-de-ofertas / Base Selection and
/// Tie-Break, Additive-Over-Original Stacking, Candidate Matching) — función pura, sin base de
/// datos, mismo criterio que <see cref="Precios.ResolvedorDePreciosTests"/>. Cubre, con los
/// números concretos del spec, tanto la aritmética (<see cref="ResolvedorDeOfertas.Resolver"/>)
/// como el matching (<see cref="ResolvedorDeOfertas.Coincide"/>) — <c>id_empresa</c> queda
/// afuera a propósito: <see cref="LineaAResolver"/> no lo lleva (design: Resolution Contract),
/// se prueba en <see cref="ReglaDeOfertasTests"/> vía <c>ReglaDeOfertas.CoincideEmpresa</c>.
/// </summary>
public class ResolvedorDeOfertasTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);
    private const int DiaSemana = 1; // lunes

    private static LineaAResolver CrearLinea(
        int idArticulo = 1, int? idGrupo = null, IReadOnlyList<int>? idsCategorias = null,
        int idListaPrecio = 1, decimal cantidad = 1m, decimal precioOriginal = 1000m,
        DateOnly? fecha = null, TimeOnly? hora = null, int? diaSemana = null) =>
        new(
            idArticulo, idGrupo, idsCategorias ?? [], idListaPrecio, cantidad, precioOriginal,
            fecha ?? Fecha, hora ?? Hora, diaSemana ?? DiaSemana);

    private static OfertaCandidata CrearCandidata(
        int id = 1, string nombre = "oferta", int prioridad = 0, bool acumulable = false,
        AlcanceDeOferta? alcance = null, BeneficioDeOferta? beneficio = null,
        decimal? cantidadMinima = null, DateOnly? fechaDesde = null, DateOnly? fechaHasta = null,
        TimeOnly? horaDesde = null, TimeOnly? horaHasta = null,
        IReadOnlySet<int>? diasSemana = null, IReadOnlySet<int>? listasObjetivo = null) =>
        new(
            id, nombre, prioridad, acumulable,
            alcance ?? AlcanceDeOferta.DeArticulo(1), beneficio ?? BeneficioDeOferta.DePorcentaje(10m),
            cantidadMinima, fechaDesde, fechaHasta, horaDesde, horaHasta,
            diasSemana ?? new HashSet<int>(), listasObjetivo ?? new HashSet<int>());

    // --- Base Selection and Tie-Break ---

    /// <summary>Spec: "Highest prioridad wins as base".</summary>
    [Fact]
    public void LaMayorPrioridadGanaComoBase()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var baja = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(10m));
        var alta = CrearCandidata(id: 2, prioridad: 20, beneficio: BeneficioDeOferta.DePorcentaje(15m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [baja, alta]);

        var aplicada = Assert.Single(resultado.Aplicadas);
        Assert.Equal(2, aplicada.IdOferta);
        Assert.Equal(150m, aplicada.DescuentoUnitario);
        Assert.Equal(850m, resultado.PrecioFinal);
    }

    /// <summary>Spec: "Equal prioridad ties break by greater discount" — $600 de línea, -$50 fijo
    /// vs -10% ($60): gana el de -10%.</summary>
    [Fact]
    public void UnEmpateDePrioridadSeRompePorMayorDescuento()
    {
        var linea = CrearLinea(precioOriginal: 600m);
        var fijo = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DeImporteFijo(50m));
        var porcentaje = CrearCandidata(id: 2, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(10m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [fijo, porcentaje]);

        var aplicada = Assert.Single(resultado.Aplicadas);
        Assert.Equal(2, aplicada.IdOferta);
        Assert.Equal(60m, aplicada.DescuentoUnitario);
    }

    /// <summary>Spec: "Remaining tie breaks by lower id_oferta" — mismo descuento, gana id 5.</summary>
    [Fact]
    public void UnEmpateRemanenteSeRompePorMenorIdOferta()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var idMayor = CrearCandidata(id: 9, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(10m));
        var idMenor = CrearCandidata(id: 5, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(10m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [idMayor, idMenor]);

        var aplicada = Assert.Single(resultado.Aplicadas);
        Assert.Equal(5, aplicada.IdOferta);
    }

    /// <summary>Spec: "Acumulable-only candidates apply with no base" — el precio original queda
    /// como base implícita.</summary>
    [Fact]
    public void CandidatasSoloAcumulablesAplicanSinBase()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var acumulable = CrearCandidata(id: 1, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(10m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [acumulable]);

        var aplicada = Assert.Single(resultado.Aplicadas);
        Assert.Equal(100m, aplicada.DescuentoUnitario);
        Assert.Equal(900m, resultado.PrecioFinal);
    }

    /// <summary>Spec: "No matching oferta leaves the price unchanged".</summary>
    [Fact]
    public void SinCandidatasQueMatcheenElPrecioQuedaSinCambios()
    {
        var linea = CrearLinea(precioOriginal: 1000m);

        var resultado = ResolvedorDeOfertas.Resolver(linea, []);

        Assert.Empty(resultado.Aplicadas);
        Assert.Equal(0m, resultado.DescuentoUnitario);
        Assert.Equal(1000m, resultado.PrecioFinal);
    }

    // --- Additive-Over-Original Stacking ---

    /// <summary>Spec: "Base plus one acumulable" — $1000, base -20% ($200), acc -10% ($100),
    /// suma $300, final $700.</summary>
    [Fact]
    public void BaseMasUnaAcumulableSumanContraElOriginal()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var basePorcentaje = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(20m));
        var acumulable = CrearCandidata(id: 2, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(10m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [basePorcentaje, acumulable]);

        Assert.Equal(300m, resultado.DescuentoUnitario);
        Assert.Equal(700m, resultado.PrecioFinal);
        Assert.Equal(2, resultado.Aplicadas.Count);
    }

    /// <summary>Spec: "Multiple acumulables stack on the base" — $1000, base -20% ($200), acc
    /// -10% ($100) y -$50 fijo, suma $350, final $650.</summary>
    [Fact]
    public void MultiplesAcumulablesSeApilanSobreLaBase()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var basePorcentaje = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(20m));
        var acumulablePorcentaje = CrearCandidata(id: 2, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(10m));
        var acumulableFijo = CrearCandidata(id: 3, acumulable: true, beneficio: BeneficioDeOferta.DeImporteFijo(50m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [basePorcentaje, acumulablePorcentaje, acumulableFijo]);

        Assert.Equal(350m, resultado.DescuentoUnitario);
        Assert.Equal(650m, resultado.PrecioFinal);
        Assert.Equal(3, resultado.Aplicadas.Count);
    }

    /// <summary>Spec: "precio_unitario as the base" — $1000, base precio_unitario=750 (descuento
    /// $250), acc -10% ($100), suma $350, final $650.</summary>
    [Fact]
    public void PrecioUnitarioComoBase()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var base_ = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePrecioUnitario(750m));
        var acumulable = CrearCandidata(id: 2, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(10m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [base_, acumulable]);

        Assert.Equal(350m, resultado.DescuentoUnitario);
        Assert.Equal(650m, resultado.PrecioFinal);
    }

    /// <summary>Spec: "precio_unitario as an acumulable" — $1000, base -10% ($100), acc
    /// precio_unitario=600 (descuento $400), suma $500, final $500.</summary>
    [Fact]
    public void PrecioUnitarioComoAcumulable()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var base_ = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(10m));
        var acumulable = CrearCandidata(id: 2, acumulable: true, beneficio: BeneficioDeOferta.DePrecioUnitario(600m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [base_, acumulable]);

        Assert.Equal(500m, resultado.DescuentoUnitario);
        Assert.Equal(500m, resultado.PrecioFinal);
    }

    /// <summary>Spec: "Combined discount over 100% clamps to zero" — $1000, base -80% ($800),
    /// acc -30% ($300) y -20% ($200): suma cruda $1300, clampeada a $1000, final $0.</summary>
    [Fact]
    public void UnDescuentoCombinadoQueSuperaElCienPorCientoClampeaACero()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var base_ = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(80m));
        var acumulable1 = CrearCandidata(id: 2, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(30m));
        var acumulable2 = CrearCandidata(id: 3, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(20m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [base_, acumulable1, acumulable2]);

        Assert.Equal(1000m, resultado.DescuentoUnitario);
        Assert.Equal(0m, resultado.PrecioFinal);
    }

    /// <summary>Spec: "Derivada lista price is the original base" — el resolver no sabe de dónde
    /// vino el precio original: si ya es el precio derivado resuelto ($180, 10% off de una base
    /// de $200) y hay un -10% acumulable, el descuento se calcula sobre $180 ($18), nunca sobre
    /// la base de $200.</summary>
    [Fact]
    public void ElPrecioOriginalDeUnaListaDerivadaEsLaBaseDelDescuento()
    {
        var linea = CrearLinea(precioOriginal: 180m);
        var acumulable = CrearCandidata(id: 1, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(10m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [acumulable]);

        Assert.Equal(18m, resultado.DescuentoUnitario);
        Assert.Equal(162m, resultado.PrecioFinal);
    }

    /// <summary>Spec: "Result lists all applied ofertas" — reporte ordenado descendente por
    /// prioridad, luego ascendente por id_oferta (sin afectar el monto).</summary>
    [Fact]
    public void LasAplicadasSeReportanOrdenadasPorPrioridadDescendenteLuegoIdAscendente()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var base_ = CrearCandidata(id: 5, prioridad: 20, beneficio: BeneficioDeOferta.DePorcentaje(20m));
        var acumulableAltaPrioridad = CrearCandidata(id: 3, prioridad: 30, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(5m));
        var acumulableBajaPrioridad = CrearCandidata(id: 1, prioridad: 1, acumulable: true, beneficio: BeneficioDeOferta.DePorcentaje(5m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [base_, acumulableAltaPrioridad, acumulableBajaPrioridad]);

        Assert.Equal([3, 5, 1], resultado.Aplicadas.Select(a => a.IdOferta));
    }

    // --- Candidate Matching: alcance ---

    /// <summary>Spec: "Categoria-scoped oferta reaches subcategoria articulos" — la línea ya
    /// llega con la cadena de ancestros expandida (categoría propia + ancestros).</summary>
    [Fact]
    public void UnaOfertaScopedACategoriaMatcheaViaLaCadenaDeAncestrosDeLaLinea()
    {
        var idBebidas = 1;
        var idGaseosas = 2;
        var linea = CrearLinea(idsCategorias: [idGaseosas, idBebidas]);
        var candidata = CrearCandidata(alcance: AlcanceDeOferta.DeCategoria(idBebidas));

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    [Fact]
    public void UnaOfertaScopedACategoriaFueraDeLaCadenaDeAncestrosNoMatchea()
    {
        var linea = CrearLinea(idsCategorias: [2, 1]);
        var candidata = CrearCandidata(alcance: AlcanceDeOferta.DeCategoria(99));

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    /// <summary>Spec: "Grupo-scoped oferta matches via the articulo's grupo".</summary>
    [Fact]
    public void UnaOfertaScopedAGrupoMatcheaViaElGrupoDelArticulo()
    {
        var linea = CrearLinea(idGrupo: 7);
        var candidata = CrearCandidata(alcance: AlcanceDeOferta.DeGrupo(7));

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    [Fact]
    public void UnaOfertaScopedAGrupoNoMatcheaOtroGrupo()
    {
        var linea = CrearLinea(idGrupo: 7);
        var candidata = CrearCandidata(alcance: AlcanceDeOferta.DeGrupo(8));

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    [Fact]
    public void UnaOfertaScopedAGrupoNoMatcheaUnArticuloSinGrupo()
    {
        var linea = CrearLinea(idGrupo: null);
        var candidata = CrearCandidata(alcance: AlcanceDeOferta.DeGrupo(7));

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    [Fact]
    public void UnaOfertaScopedAArticuloNoMatcheaOtroArticulo()
    {
        var linea = CrearLinea(idArticulo: 1);
        var candidata = CrearCandidata(alcance: AlcanceDeOferta.DeArticulo(2));

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    // --- Candidate Matching: lista objetivo ---

    /// <summary>Spec: ofertas / "No junction rows targets every lista".</summary>
    [Fact]
    public void UnaListaObjetivoVaciaMatcheaCualquierLista()
    {
        var linea = CrearLinea(idListaPrecio: 42);
        var candidata = CrearCandidata(listasObjetivo: new HashSet<int>());

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    /// <summary>Spec: ofertas / "Junction rows restrict targeting".</summary>
    [Fact]
    public void UnaListaObjetivoNoVaciaRestringeElMatch()
    {
        var linea = CrearLinea(idListaPrecio: 42);
        var candidata = CrearCandidata(listasObjetivo: new HashSet<int> { 1, 2 });

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    // --- Candidate Matching: vigencia (fecha/hora/dias_semana) ---

    /// <summary>Spec: ofertas / "All-NULL vigencia always matches".</summary>
    [Fact]
    public void VigenciaTodaNulaSiempreMatchea()
    {
        var linea = CrearLinea(fecha: new DateOnly(2099, 1, 1), hora: new TimeOnly(23, 59), diaSemana: 7);
        var candidata = CrearCandidata();

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    /// <summary>Spec: ofertas / "Boundary dates and hours are inclusive".</summary>
    [Fact]
    public void LosLimitesDeFechaYHoraSonInclusivos()
    {
        var linea = CrearLinea(fecha: new DateOnly(2026, 8, 3), hora: new TimeOnly(14, 0));
        var candidata = CrearCandidata(
            fechaDesde: new DateOnly(2026, 8, 1), fechaHasta: new DateOnly(2026, 8, 3),
            horaDesde: new TimeOnly(10, 0), horaHasta: new TimeOnly(14, 0));

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    /// <summary>Spec: ofertas / "Outside any single axis excludes the match" — un día después de
    /// fecha_hasta.</summary>
    [Fact]
    public void UnDiaDespuesDeFechaHastaExcluyeElMatch()
    {
        var linea = CrearLinea(fecha: new DateOnly(2026, 8, 4), hora: new TimeOnly(12, 0));
        var candidata = CrearCandidata(
            fechaDesde: new DateOnly(2026, 8, 1), fechaHasta: new DateOnly(2026, 8, 3),
            horaDesde: new TimeOnly(10, 0), horaHasta: new TimeOnly(14, 0));

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    [Fact]
    public void UnDiaAntesDeFechaDesdeExcluyeElMatch()
    {
        var linea = CrearLinea(fecha: new DateOnly(2026, 7, 31));
        var candidata = CrearCandidata(fechaDesde: new DateOnly(2026, 8, 1));

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    [Fact]
    public void UnaHoraFueraDeLaVentanaExcluyeElMatch()
    {
        var linea = CrearLinea(hora: new TimeOnly(9, 59));
        var candidata = CrearCandidata(horaDesde: new TimeOnly(10, 0), horaHasta: new TimeOnly(14, 0));

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    /// <summary>Spec: ofertas / "dias_semana restricts to listed weekdays" — sábado y domingo,
    /// evaluado un miércoles.</summary>
    [Fact]
    public void DiasSemanaRestringeALosDiasListados()
    {
        var miercoles = 3;
        var linea = CrearLinea(diaSemana: miercoles);
        var candidata = CrearCandidata(diasSemana: new HashSet<int> { 6, 7 });

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    [Fact]
    public void DiasSemanaVacioMatcheaCualquierDia()
    {
        var linea = CrearLinea(diaSemana: 3);
        var candidata = CrearCandidata(diasSemana: new HashSet<int>());

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    // --- Candidate Matching: cantidad_minima ---

    /// <summary>Spec: ofertas / "NULL cantidad_minima always matches".</summary>
    [Fact]
    public void CantidadMinimaNulaSiempreMatchea()
    {
        var linea = CrearLinea(cantidad: 1m);
        var candidata = CrearCandidata(cantidadMinima: null);

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    /// <summary>Spec: ofertas / "Quantity below threshold excludes the match".</summary>
    [Fact]
    public void CantidadPorDebajoDelUmbralExcluyeElMatch()
    {
        var linea = CrearLinea(cantidad: 2m);
        var candidata = CrearCandidata(cantidadMinima: 3m);

        Assert.False(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    /// <summary>Spec: ofertas / "Quantity at threshold matches".</summary>
    [Fact]
    public void CantidadEnElUmbralMatchea()
    {
        var linea = CrearLinea(cantidad: 3m);
        var candidata = CrearCandidata(cantidadMinima: 3m);

        Assert.True(ResolvedorDeOfertas.Coincide(linea, candidata));
    }

    // --- Aritmética por beneficio: redondeo y clamp de precio_unitario > original ---

    /// <summary>Design: arithmetic table — <c>precio_unitario &gt; original</c> da descuento 0,
    /// no negativo (la oferta nunca sube el precio).</summary>
    [Fact]
    public void UnPrecioUnitarioMayorAlOriginalDaDescuentoCero()
    {
        var linea = CrearLinea(precioOriginal: 100m);
        var candidata = CrearCandidata(prioridad: 10, beneficio: BeneficioDeOferta.DePrecioUnitario(150m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [candidata]);

        Assert.Equal(0m, resultado.DescuentoUnitario);
        Assert.Equal(100m, resultado.PrecioFinal);
    }

    /// <summary>Design: arithmetic table — redondeo AwayFromZero a 2 decimales (no bankers'
    /// rounding), mismo criterio que <see cref="Precios.ResolvedorDePreciosTests"/>. Empate
    /// exacto de medio centavo vía <c>importe_fijo</c> (no se calcula por multiplicación, así
    /// que el tercer decimal exacto no depende de ningún redondeo intermedio) sobre un original
    /// holgado, para no interferir con el clamp <c>[0, original]</c>.</summary>
    [Fact]
    public void ElDescuentoRedondeaAwayFromZero()
    {
        var linea = CrearLinea(precioOriginal: 1000m);
        var candidata = CrearCandidata(prioridad: 10, beneficio: BeneficioDeOferta.DeImporteFijo(0.125m));

        var resultado = ResolvedorDeOfertas.Resolver(linea, [candidata]);

        Assert.Equal(0.13m, resultado.DescuentoUnitario);
        Assert.Equal(999.87m, resultado.PrecioFinal);
    }
}
