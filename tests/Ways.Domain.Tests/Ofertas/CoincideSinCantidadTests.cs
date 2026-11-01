using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary><see cref="ResolvedorDeOfertas.CoincideSinCantidad"/>: los mismos ejes que <c>Coincide</c> salvo la cantidad
/// mínima, y la lista objetivo opcional.</summary>
public class CoincideSinCantidadTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);

    private static LineaAResolver Linea(int idListaPrecio = 1, decimal cantidad = 1m, int? idFamilia = null) =>
        new(1, null, [], idListaPrecio, cantidad, 0m, Fecha, Hora, 1, idFamilia, cantidad);

    private static OfertaCandidata Candidata(
        AlcanceDeOferta? alcance = null, decimal? cantidadMinima = null, DateOnly? fechaHasta = null,
        TimeOnly? horaDesde = null, IReadOnlySet<int>? dias = null, IReadOnlySet<int>? listas = null) =>
        new(
            1, "oferta", 0, false, alcance ?? AlcanceDeOferta.DeArticulo(1), BeneficioDeOferta.DePorcentaje(10m),
            cantidadMinima, null, fechaHasta, horaDesde, null, dias ?? new HashSet<int>(), listas ?? new HashSet<int>());

    [Fact]
    public void IgnoraLaCantidadMinimaDeUnArticuloYDeUnaFamilia()
    {
        Assert.True(ResolvedorDeOfertas.CoincideSinCantidad(Linea(), Candidata(cantidadMinima: 5m)));

        var deFamilia = Candidata(AlcanceDeOferta.DeFamilia(7), cantidadMinima: 5m);
        Assert.True(ResolvedorDeOfertas.CoincideSinCantidad(Linea(idFamilia: 7), deFamilia));
        Assert.False(ResolvedorDeOfertas.Coincide(Linea(idFamilia: 7), deFamilia));
    }

    [Fact]
    public void ExigeElAlcanceLaVentanaDeFechaHoraYDia()
    {
        Assert.False(ResolvedorDeOfertas.CoincideSinCantidad(Linea(), Candidata(AlcanceDeOferta.DeArticulo(2))));
        Assert.False(ResolvedorDeOfertas.CoincideSinCantidad(Linea(), Candidata(fechaHasta: Fecha.AddDays(-1))));
        Assert.False(ResolvedorDeOfertas.CoincideSinCantidad(Linea(), Candidata(horaDesde: Hora.AddMinutes(1))));
        Assert.False(ResolvedorDeOfertas.CoincideSinCantidad(Linea(), Candidata(dias: new HashSet<int> { 2 })));
        Assert.True(ResolvedorDeOfertas.CoincideSinCantidad(Linea(), Candidata(fechaHasta: Fecha, dias: new HashSet<int> { 1 })));
    }

    [Fact]
    public void LaListaObjetivoRestringeSoloSiSeConsidera()
    {
        var deOtraLista = Candidata(listas: new HashSet<int> { 9 });

        Assert.False(ResolvedorDeOfertas.CoincideSinCantidad(Linea(idListaPrecio: 1), deOtraLista));
        Assert.True(ResolvedorDeOfertas.CoincideSinCantidad(Linea(idListaPrecio: 9), deOtraLista));
        Assert.True(ResolvedorDeOfertas.CoincideSinCantidad(Linea(idListaPrecio: 1), deOtraLista, considerarLista: false));
    }
}
